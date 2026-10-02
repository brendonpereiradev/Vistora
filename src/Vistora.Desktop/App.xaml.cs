using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Desktop;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private DiagnosticService? diagnostics;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preview = e.Args.Contains("--preview");
        if (!preview)
        {
            instanceMutex = new Mutex(true, "Local\\Vistora.Desktop", out var created);
            if (!created) { MessageBox.Show("O Vistora já está aberto.", "Vistora"); Shutdown(); return; }
        }
        diagnostics = new DiagnosticService(preview ? Path.Combine(Path.GetTempPath(), "VistoraPreview", Guid.NewGuid().ToString("N")) : null);
        DispatcherUnhandledException += (_, args) =>
        {
            diagnostics.AppEvent("app.unhandled_error", "Erro não tratado na interface.", DiagnosticLevel.Error, args.Exception, code: "UNHANDLED_UI_ERROR");
            MessageBox.Show(args.Exception.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            diagnostics.AppEvent("app.fatal_error", "Encerramento por erro não tratado.", DiagnosticLevel.Error,
                args.ExceptionObject as Exception, code: "FATAL_ERROR");
            Task.Run(() => diagnostics.FlushAsync()).GetAwaiter().GetResult();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            diagnostics.AppEvent("app.task_error", "Erro em tarefa de segundo plano.", DiagnosticLevel.Error, args.Exception);
        MainWindow window;
        try { window = new MainWindow(preview, preview && e.Args.Contains("--preview-pending"), diagnostics); }
        catch (Exception ex)
        {
            diagnostics.AppEvent("app.startup_failed", "Não foi possível iniciar o aplicativo.", DiagnosticLevel.Error, ex, code: "STARTUP_FAILED");
            var emergency = new Window { Title = "Vistora — diagnóstico", Width = 540, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "Não foi possível iniciar o Vistora. Você pode exportar os registros para análise. Os dados locais foram preservados.", TextWrapping = TextWrapping.Wrap });
            var export = new Button { Content = "Exportar log do aplicativo", Margin = new Thickness(0, 18, 0, 0) };
            export.Click += async (_, _) => await DiagnosticExportUi.ExportAsync(emergency, diagnostics);
            panel.Children.Add(export); emergency.Content = panel; MainWindow = emergency; emergency.Show(); return;
        }
        MainWindow = window;
        window.Show();
        if (preview)
        {
            if (e.Args.Contains("--preview-history")) window.SelectPage(2);
            if (e.Args.Contains("--preview-settings")) window.SelectPage(3);
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(500);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var pathIndex = Array.IndexOf(e.Args, "--preview");
                var path = e.Args.Length > pathIndex + 1 ? e.Args[pathIndex + 1] : "vistora-preview.png";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var file = File.Create(path);
                encoder.Save(file);
                var exportIndex = Array.IndexOf(e.Args, "--preview-export-log");
                if (exportIndex >= 0 && e.Args.Length > exportIndex + 1)
                    await diagnostics.ExportAsync(Path.GetFullPath(e.Args[exportIndex + 1]));
                Shutdown();
            };
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (diagnostics is not null) Task.Run(async () => await diagnostics.DisposeAsync()).GetAwaiter().GetResult();
        instanceMutex?.Dispose(); base.OnExit(e);
    }
}
