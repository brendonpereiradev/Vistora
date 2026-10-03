using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vistora.Desktop;

public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preview = e.Args.Contains("--preview");
        if (!preview)
        {
            instanceMutex = new Mutex(true, "Local\\Vistora.Desktop", out var created);
            if (!created) { MessageBox.Show("O Vistora já está aberto.", "Vistora"); Shutdown(); return; }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var pageIndex = Array.IndexOf(e.Args, "--preview-page");
        var page = preview && pageIndex >= 0 && e.Args.Length > pageIndex + 1 && int.TryParse(e.Args[pageIndex + 1], out var n) ? n : 0;
        var window = new MainWindow(preview, preview && e.Args.Contains("--preview-pending"), page);
        MainWindow = window;
        window.Show();
        if (preview)
        {
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(500);
                window.UpdateLayout();
                var client = (FrameworkElement)window.Content; // área útil, sem a moldura do Windows
                var bitmap = new RenderTargetBitmap((int)client.ActualWidth, (int)client.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var pathIndex = Array.IndexOf(e.Args, "--preview");
                var path = e.Args.Length > pathIndex + 1 ? e.Args[pathIndex + 1] : "vistora-preview.png";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var file = File.Create(path);
                encoder.Save(file);
                Shutdown();
            };
        }
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }
}
