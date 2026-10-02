using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Desktop;

internal static class DiagnosticExportUi
{
    public static async Task ExportAsync(Window owner, DiagnosticService diagnostics, VisitRun? run = null)
    {
        var dialog = new Window { Owner = owner, Title = "Exportar log", Width = 510, Height = run is null ? 450 : 380,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = run is null
            ? "Salve os registros de uma abertura do aplicativo para análise em outro computador."
            : "Salve os registros desta visita e suas retomadas para análise em outro computador.", TextWrapping = TextWrapping.Wrap });
        ComboBox? sessions = null;
        if (run is null)
        {
            panel.Children.Add(new TextBlock { Text = "Abertura do aplicativo", Margin = new Thickness(0, 14, 0, 5) });
            var available = await diagnostics.ListApplicationSessionsAsync();
            sessions = new ComboBox { ItemsSource = available, DisplayMemberPath = nameof(DiagnosticSession.Label),
                SelectedItem = available.FirstOrDefault(s => s.Id == diagnostics.SessionId) };
            panel.Children.Add(sessions);
        }
        panel.Children.Add(new TextBlock { Text = "Identificação do teste (opcional)", Margin = new Thickness(0, 18, 0, 5) });
        var label = new TextBox { MaxLength = 80 }; panel.Children.Add(label);
        var screenshots = new CheckBox { Content = "Incluir capturas de falha", Margin = new Thickness(0, 12, 0, 5),
            Visibility = run is null ? Visibility.Collapsed : Visibility.Visible };
        panel.Children.Add(screenshots);
        panel.Children.Add(new TextBlock { Text = "Capturas podem conter dados visíveis do chamado. Credenciais e sessão do navegador ficam fora do pacote.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 16) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Escolher onde salvar" };
        save.Click += (_, _) => dialog.DialogResult = true; buttons.Children.Add(save);
        var cancel = new Button { Content = "Cancelar", IsCancel = true }; buttons.Children.Add(cancel);
        panel.Children.Add(buttons); dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        var file = new SaveFileDialog { Title = "Salvar pacote de logs", Filter = "Pacote de diagnóstico (*.zip)|*.zip", DefaultExt = ".zip",
            AddExtension = true, OverwritePrompt = true, FileName = $"Vistora-diagnostico-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{run?.Id ?? (sessions?.SelectedItem as DiagnosticSession)?.Id ?? diagnostics.SessionId}-{Guid.NewGuid():N}.zip" };
        if (file.ShowDialog(owner) != true) return;
        try
        {
            var result = await diagnostics.ExportAsync(file.FileName, run, screenshots.IsChecked == true, label.Text.Trim(),
                (sessions?.SelectedItem as DiagnosticSession)?.Id);
            MessageBox.Show(owner, $"Log exportado para:\n{result.Path}\n\nTamanho: {result.Bytes / 1024d:F1} KB.\n" +
                (result.Partial ? "O pacote indica registros ausentes ou uma execução ainda em andamento.\n" : "") +
                "Você pode anexar este ZIP na conversa para análise.", "Exportar log", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            diagnostics.AppEvent("export.failed", "Não foi possível exportar o diagnóstico.", DiagnosticLevel.Error, ex, run?.Id, "EXPORT_FAILED");
            MessageBox.Show(owner, "Não foi possível salvar o pacote. Os logs originais foram preservados.\n" + ex.Message,
                "Exportar log", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
