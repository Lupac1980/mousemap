using System.Windows;
using MacroRecorder.Services;

namespace MacroRecorder;

/// <summary>
/// Classe raiz do aplicativo. Cria a MainWindow e trata exceções globais.
/// O ShutdownMode é OnExplicitShutdown (definido no App.xaml) porque o app
/// precisa sobreviver ao fechamento da janela, ficando na bandeja do sistema.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- Tratamento global de exceções: loga e avisa, nunca fecha silencioso ----
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Exceção não tratada na UI", args.Exception);
            MessageBox.Show(
                $"Ocorreu um erro inesperado:\n\n{args.Exception.Message}\n\n" +
                "Detalhes foram gravados em macrorecorder.log.",
                "MacroRecorder - Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true; // impede o crash do processo
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Error("Exceção não tratada fora da UI",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        };

        base.OnStartup(e);

        // Janela principal criada manualmente (sem StartupUri) para controle total
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("Aplicativo encerrado.");
        base.OnExit(e);
    }
}
