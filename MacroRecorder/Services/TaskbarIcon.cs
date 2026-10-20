using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MacroRecorder.Native;

namespace MacroRecorder.Services;

/// <summary>
/// Implementação NATIVA do System Tray para WPF puro (sem NuGet externo).
/// Usa Shell_NotifyIcon (shell32.dll) + HwndSource.AddHook na janela principal.
///
/// Recursos:
///  - Ícone na bandeja carregado direto do app.ico embutido no .exe
///  - Tooltip dinâmico (UpdateTooltip)
///  - Menu de contexto: "Abrir MacroRecorder" / "Sair"
///  - Clique duplo restaura a janela principal
///  - Balloon tips (notificações) para início/fim de gravação
/// </summary>
public sealed class TaskbarIconWrapper : IDisposable
{
    // ==================== P/INVOKE Shell_NotifyIcon ====================

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    private const uint NIM_ADD    = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON    = 0x02;
    private const uint NIF_TIP     = 0x04;
    private const uint NIF_INFO    = 0x10;

    private const uint NIIF_INFO    = 0x01;
    private const uint NIIF_WARNING = 0x02;

    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYMOUSE = WM_USER + 1; // callback custom dos eventos do mouse

    // Mensagens de mouse que recebemos via lParam do callback
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP     = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    private const uint IMAGE_ICON    = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_SHARED       = 0x00008000;

    // ==================== ESTADO ====================

    private readonly MainWindow _owner;
    private readonly IntPtr _hwnd;         // handle nativo da janela WPF
    private readonly HwndSource? _source;  // message hook da janela principal
    private NOTIFYICONDATA _nid;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _iconIsShared;            // ícones LR_SHARED não devem ser destruídos
    private ContextMenu? _menu;
    private bool _iconAdded;

    /// <summary>Marca que o usuário escolheu "Sair" - autoriza o Close real da janela.</summary>
    public bool IsExiting { get; private set; }

    public TaskbarIconWrapper(MainWindow owner)
    {
        _owner = owner;

        var helper = new System.Windows.Interop.WindowInteropHelper(owner);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("A janela principal ainda não possui handle nativo.");

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _hIcon = LoadAppIcon();
        AddIcon();
        BuildContextMenu();
    }

    // ==================== ÍCONE ====================

    /// <summary>
    /// Estratégia robusta para obter o HICON do app.ico mesmo em single-file publish:
    /// 1) procura app.ico ao lado do executável (copiado no build/publish);
    /// 2) senão extrai o recurso embutido no assembly para um arquivo temporário;
    /// 3) senão usa o ícone padrão do sistema (nunca ficamos sem tray icon).
    /// </summary>
    private IntPtr LoadAppIcon()
    {
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(exeDir, "app.ico"),
                Path.Combine(exeDir, "Icons", "app.ico")
            };

            foreach (var file in candidates.Where(File.Exists))
            {
                IntPtr h = LoadImageW(IntPtr.Zero, file, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
                if (h != IntPtr.Zero) return h;
            }

            // Recurso embutido (.ico fica disponível via GetManifestResourceStream)
            var asm = Assembly.GetExecutingAssembly();
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));

            if (resName != null)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "macrorecorder_tray.ico");
                using (var stream = asm.GetManifestResourceStream(resName)!)
                using (var fs = File.Create(tmp))
                    stream.CopyTo(fs);

                IntPtr h = LoadImageW(IntPtr.Zero, tmp, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
                try { File.Delete(tmp); } catch { /* melhor esforço */ }
                if (h != IntPtr.Zero) return h;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Falha ao carregar ícone customizado da bandeja: " + ex.Message);
        }

        // Fallback compartilhado do Windows
        _iconIsShared = true;
        return LoadImageW(IntPtr.Zero, "IDI_APPLICATION", IMAGE_ICON, 32, 32, LR_SHARED);
    }

    private void AddIcon()
    {
        _nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYMOUSE,
            hIcon = _hIcon,
            szTip = "MacroRecorder — clique duplo para abrir",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        _iconAdded = Shell_NotifyIconW(NIM_ADD, ref _nid);
        if (!_iconAdded)
            Logger.Warn("Shell_NotifyIcon(NIM_ADD) falhou - o ícone pode não aparecer na bandeja.");
    }

    /// <summary>Atualiza o tooltip da bandeja (ex.: status de gravação).</summary>
    public void UpdateTooltip(string text)
    {
        _nid.uFlags = NIF_TIP;
        _nid.szTip = text.Length > 127 ? text[..127] : text;
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);
    }

    /// <summary>Exibe notificação balloon/toast na bandeja (usado nos toasts do fluxo).</summary>
    public void ShowBalloon(string title, string message, bool warning = false)
    {
        try
        {
            var data = _nid;
            data.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
            data.uFlags = NIF_INFO;
            data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = message;
            data.uTimeoutOrVersion = 5000; // ms de exibição
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            Logger.Warn("Falha ao exibir balloon: " + ex.Message);
        }
    }

    // ==================== MENU DE CONTEXTO ====================

    private void BuildContextMenu()
    {
        _menu = new ContextMenu();

        var miOpen = new MenuItem { Header = "🗔  Abrir MacroRecorder" };
        miOpen.Click += (_, _) => _owner.RestoreFromTray();

        var miExit = new MenuItem { Header = "❌  Sair" };
        miExit.Click += (_, _) =>
        {
            IsExiting = true;           // autoriza o OnClosing real
            _owner.ExitApplication();   // libera hooks/hotkeys e faz Shutdown
        };

        _menu.Items.Add(miOpen);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(miExit);
    }

    // ==================== CALLBACK DA BANDEJA ====================

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == WM_TRAYMOUSE)
        {
            int mouseMsg = unchecked((int)(long)lParam) & 0xFFFF;

            switch (mouseMsg)
            {
                case WM_LBUTTONDBLCLK:      // duplo clique: restaura a janela
                    _owner.RestoreFromTray();
                    handled = true;
                    break;

                case WM_RBUTTONUP:          // botão direito: menu de contexto
                    ShowContextMenuAtCursor();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenuAtCursor()
    {
        if (_menu == null) return;

        // Truque clássico: dá foco à janela antes, senão o menu "gruda" na tela.
        SetForegroundWindow(_hwnd);

        _menu.IsOpen = false;
        _menu.PlacementTarget = _owner;
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    // ==================== DISPOSE ====================

    public void Dispose()
    {
        if (_iconAdded)
        {
            _nid.uFlags = 0;
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _iconAdded = false;
        }

        try { _source?.RemoveHook(WndProc); } catch { /* janela já destruída */ }

        if (_hIcon != IntPtr.Zero && !_iconIsShared)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
