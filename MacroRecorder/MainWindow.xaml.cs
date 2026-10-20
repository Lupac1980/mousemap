using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacroRecorder.Models;
using MacroRecorder.Native;
using MacroRecorder.Services;

namespace MacroRecorder;

/// <summary>
/// Janela principal: lista de automações, fluxo de mapeamento (nome + hotkey
/// validada por RegisterHotKey), orquestração de gravação/reprodução e o
/// ícone da bandeja do sistema (System Tray).
/// </summary>
public partial class MainWindow : Window
{
    // ==================== SERVIÇOS / ESTADO ====================

    private readonly List<Automation> _automations = new();
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly HookManager _hooks = new();
    private readonly AutomationRecorder _recorder = new();
    private readonly AutomationPlayer _player = new();

    private TaskbarIconWrapper? _tray;

    // Estado do painel de configuração
    private Automation? _draft;                       // automação em edição
    private bool _hotkeyValidated;                    // true => BtnRecord habilitado

    // Cores da UI
    private static readonly Brush ColorOk       = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // verde
    private static readonly Brush ColorError    = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)); // vermelho
    private static readonly Brush ColorMuted    = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0xA0));

    public MainWindow()
    {
        InitializeComponent();
    }

    // ==================== CICLO DE VIDA ====================

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        try
        {
            // 1) Serviço de hotkeys globais (message-only window própria)
            _hotkeys.Start();
            _hotkeys.HotkeyPressed += OnSavedHotkeyPressed;

            // 2) Hooks LL permanentes para GRAVAÇÃO (mouse + teclado)
            _hooks.Install(keyboard: true, mouse: true);
            _hooks.MouseEvent  += OnHookMouse;
            _hooks.KeyboardEvent += OnHookKeyboard;

            // 3) Bandeja do sistema
            _tray = new TaskbarIconWrapper(this);

            // 4) Carrega automações salvas + registra hotkeys
            LoadAutomations();

            Logger.Info("MainWindow inicializada com sucesso.");
        }
        catch (Exception ex)
        {
            Logger.Error("Falha na inicialização dos serviços nativos", ex);
            MessageBox.Show(
                "Não foi possível iniciar os hooks/hotkeys globais.\n\n" +
                ex.Message + "\n\nO MacroRecorder precisa ser executado como Administrador.",
                "MacroRecorder - Erro fatal de inicialização",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>O botão "X" NÃO fecha o app: apenas esconde para a bandeja.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Se estivermos encerrando de verdade (menu Sair), deixa fechar.
        if (_tray?.IsExiting == true)
        {
            CleanupAll();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;                       // impede o fechamento real
        Hide();                                // some com a janela
        _tray?.ShowBalloon("MacroRecorder continua rodando",
            "Clique duplo no ícone da bandeja para reabrir a janela.");
    }

    /// <summary>Libera TODOS os recursos nativos na saída (requisito crítico).</summary>
    private void CleanupAll()
    {
        try
        {
            _player.Cancel();
            _hooks.Dispose();      // UnhookWindowsHookEx + fim da thread do message loop
            _hotkeys.Dispose();    // UnregisterHotKey de todas as hotkeys
            _tray?.Dispose();
            Logger.Info("Recursos nativos liberados (hooks e hotkeys).");
        }
        catch (Exception ex)
        {
            Logger.Error("Erro ao liberar recursos no encerramento", ex);
        }
    }

    // ==================== CARREGAMENTO / LISTA ====================

    private void LoadAutomations()
    {
        _automations.Clear();
        _automations.AddRange(AutomationStorage.Load());

        foreach (var auto in _automations)
        {
            var res = _hotkeys.TryRegister(auto.Modifiers, auto.KeyCode, auto.Id, permanent: true);
            if (!res.Success && res.IsConflict)
                Logger.Warn($"Hotkey '{auto.HotkeyText}' da automação '{auto.Name}' já está em uso por outro app.");
        }

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        GridAutomations.ItemsSource = null;
        GridAutomations.ItemsSource = _automations; // recarrega (lista pequena, sem virtualização complexa)
    }

    // ==================== FLUXO: MAPEAR NOVA AUTOMAÇÃO ====================

    private void BtnNewAutomation_Click(object sender, RoutedEventArgs e)
    {
        // Nova linha rascunho ativada para configuração inline
        _draft = new Automation { Name = "Minha Automação" };

        TxtName.Text = _draft.Name;
        TxtHotkey.Text = "Pressione um atalho... (ex: Ctrl+Shift+1)";
        TxtHotkey.Foreground = ColorMuted;
        TxtHotkeyHint.Text = "";
        BtnRecord.IsEnabled = false;   // regra: só habilita após hotkey válida
        _hotkeyValidated = false;

        ConfigPanel.Visibility = Visibility.Visible;
        BtnNewAutomation.IsEnabled = false;
        Dispatcher.InvokeAsync(() => TxtHotkey.Focus());
    }

    private void BtnCancelConfig_Click(object sender, RoutedEventArgs e) => CloseConfigPanel();

    private void CloseConfigPanel()
    {
        // Libera a hotkey temporária registrada durante a validação, se houver
        if (_draft != null && _hotkeyValidated)
            _hotkeys.Unregister(_draft.Id);

        _draft = null;
        ConfigPanel.Visibility = Visibility.Collapsed;
        BtnNewAutomation.IsEnabled = true;
    }

    // ==================== DEFINIÇÃO DO ATALHO (PREVIEW + VALIDAÇÃO) ====================

    private void TxtHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_draft == null) return;

        e.Handled = true; // consome a tecla: nada deve ser digitado no TextBox

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc cancela o mapeamento
        if (key == Key.Escape) { CloseConfigPanel(); return; }

        // Ignora quando o usuário pressiona apenas modificadores: mostra preview parcial
        if (HotkeyFormatter.IsModifierKey(key))
        {
            var previewMods = HotkeyFormatter.CurrentModifiers();
            ShowPreview(HotkeyFormatter.Format(previewMods, Key.None), ColorMuted, "Solte uma tecla final...");
            return;
        }

        var modsNow = HotkeyFormatter.CurrentModifiers();

        // REGRA: precisa de ao menos Ctrl/Alt/Win (Shift sozinho não é permitido pelo Windows)
        if (!HotkeyFormatter.HasRequiredModifier(modsNow))
        {
            ShowPreview(HotkeyFormatter.Format(modsNow, key), ColorError,
                "Use pelo menos Ctrl, Alt ou Win na combinação.");
            SetRecordEnabled(false);
            return;
        }

        uint mods = HotkeyFormatter.ToModifierFlags(modsNow);
        uint vk = HotkeyFormatter.KeyToVirtualKeyCode(key);

        // ---- TENTATIVA DE REGISTRO GLOBAL (RegisterHotKey) ----
        // Registra permanente sob o Id do rascunho: se for aceita, mantém-se viva
        // até virar hotkey oficial da automação gravada (ou ser liberada no cancelamento).
        var result = _hotkeys.TryRegister(mods, vk, _draft.Id, permanent: true);

        string text = HotkeyFormatter.Format(modsNow, key);

        if (result.Success)
        {
            // VERDE/PRETO: válido -> pode gravar
            _draft.Modifiers = mods;
            _draft.KeyCode = vk;
            _draft.HotkeyText = text;
            ShowPreview(text, ColorOk, "✓ Atalho válido!");
            SetRecordEnabled(true);
        }
        else if (result.IsConflict)
        {
            // VERMELHO + aviso: erro 1409 - hotkey já registrada por outro aplicativo
            ShowPreview(text, ColorError, "⚠ Atalho em uso, escolha outro.");
            SetRecordEnabled(false);
        }
        else
        {
            ShowPreview(text, ColorError, result.ErrorMessage ?? "Erro ao registrar atalho.");
            SetRecordEnabled(false);
        }
    }

    private void ShowPreview(string text, Brush color, string hint)
    {
        TxtHotkey.Text = text;
        TxtHotkey.Foreground = color;
        TxtHotkeyHint.Text = hint;
        TxtHotkeyHint.Foreground = color;
    }

    private void SetRecordEnabled(bool enabled)
    {
        _hotkeyValidated = enabled;
        BtnRecord.IsEnabled = enabled;
    }

    // ==================== INÍCIO DA GRAVAÇÃO ====================

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null || !_hotkeyValidated) return;

        // Valida nome
        _draft.Name = string.IsNullOrWhiteSpace(TxtName.Text) ? "Automação sem nome" : TxtName.Text.Trim();

        try
        {
            // A hotkey validada fica LIBERADA do RegisterHotKey durante a gravação:
            // assim o WH_KEYBOARD_LL consegue enxergá-la e usá-la para PARAR a gravação
            // (senão o RegisterHotKey a consumiria antes do hook).
            _hotkeys.Unregister(_draft.Id);

            _recorder.Start(_draft.Modifiers, _draft.KeyCode);

            // Esconde/minimiza para a bandeja
            Hide();
            _tray?.ShowBalloon("Gravação iniciada",
                $"Gravação iniciada. Pressione [{_draft.HotkeyText}] para parar.");

            Logger.Info($"Gravação iniciada: '{_draft.Name}' [{_draft.HotkeyText}]");
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao iniciar gravação", ex);
            MessageBox.Show("Não foi possível iniciar a gravação:\n" + ex.Message,
                "MacroRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            // Re-registra a hotkey para não perder o estado válido
            _hotkeys.TryRegister(_draft.Modifiers, _draft.KeyCode, _draft.Id, permanent: true);
        }
    }

    // ==================== HOOKS -> GRAVADOR ====================

    private void OnHookMouse(int wParam, NativeTypes.MSLLHOOKSTRUCT info)
    {
        // Encaminha direto: o recorder ignora se não estiver gravando.
        _recorder.HandleMouse(wParam, info);
    }

    private void OnHookKeyboard(uint vkCode, bool isKeyDown)
    {
        if (!_recorder.IsRecording) return;

        if (_recorder.CheckStopHotkey(vkCode, isKeyDown))
        {
            // FIM DA GRAVAÇÃO pela hotkey criada pelo usuário
            Dispatcher.BeginInvoke(new Action(FinishRecording), DispatcherPriority.Send);
        }
    }

    /// <summary>Executado na thread da UI quando a hotkey de parada é detectada.</summary>
    private void FinishRecording()
    {
        if (!_recorder.IsRecording || _draft == null) return;

        var events = _recorder.Stop();

        try
        {
            if (events.Count == 0)
            {
                // Nada gravado: devolve o foco e descarta o rascunho
                _hotkeys.TryRegister(_draft.Modifiers, _draft.KeyCode, _draft.Id, permanent: true);
                Show(); WindowState = WindowState.Normal; Activate();
                MessageBox.Show("Nenhum evento de mouse foi capturado durante a gravação.",
                    "MacroRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
                CloseConfigPanel();
                return;
            }

            _draft.Events = events.ToList();
            _draft.CreatedAt = DateTime.UtcNow;

            // Persistência JSON portable
            AutomationStorage.Upsert(_draft, _automations);

            // Agora sim a hotkey vira global definitiva da macro salva
            var res = _hotkeys.TryRegister(_draft.Modifiers, _draft.KeyCode, _draft.Id, permanent: true);
            if (!res.Success)
                Logger.Warn($"Macro '{_draft.Name}' salva mas hotkey indisponível: {res.ErrorMessage}");

            RefreshGrid();
            CloseConfigPanel();

            // RESTAURA a janela principal
            Show();
            WindowState = WindowState.Normal;
            Activate();

            _tray?.ShowBalloon("Gravação concluída",
                $"'{_draft.Name}' salva com {events.Count} eventos.");
            Logger.Info($"Gravação concluída: '{_draft.Name}' com {events.Count} eventos.");
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao finalizar/salvar gravação", ex);
            MessageBox.Show("Erro ao salvar a automação:\n" + ex.Message,
                "MacroRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            Show(); WindowState = WindowState.Normal;
        }
    }

    // ==================== EXECUÇÃO EM BACKGROUND (HOTKEY SALVA) ====================

    private void OnSavedHotkeyPressed(string automationId)
    {
        // WM_HOTKEY chega na thread da mensagem; garantimos marshaling p/ UI thread
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var auto = _automations.FirstOrDefault(a => a.Id == automationId);
            if (auto == null) return;

            if (_recorder.IsRecording) return; // não reproduz enquanto grava

            // Reprodução em Task separada (não trava a UI) com CancellationToken interno
            _ = _player.PlayAsync(auto);
            UpdateStatus($"Reproduzindo '{auto.Name}'...", ColorOk);
        }));
    }

    // Botões da grade
    private void BtnPlayRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Automation auto)
            _ = _player.PlayAsync(auto);
    }

    private void BtnDeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Automation auto) return;

        var confirm = MessageBox.Show(
            $"Excluir a automação \"{auto.Name}\"?\nO arquivo JSON será atualizado.",
            "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _hotkeys.Unregister(auto.Id);          // libera a hotkey global
        AutomationStorage.Delete(auto.Id, _automations); // remove do JSON
        RefreshGrid();
    }

    // ==================== STATUS RODAPÉ ====================

    private void UpdateStatus(string text, Brush color)
    {
        TxtStatus.Text = text;
        StatusDot.Fill = color;
    }

    // Chamado pelo wrapper da bandeja (clique no ícone / menu "Mostrar")
    internal void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // traz para frente mesmo minimizado
    }

    // Chamado pelo menu "Sair" da bandeja
    internal void ExitApplication()
    {
        CleanupAll();
        Application.Current.Shutdown();
    }
}
