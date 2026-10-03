using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Vistora.Desktop;

public partial class IssueKeyWindow : Window
{
    private static readonly Regex KeyPattern = new(@"^[A-Z][A-Z0-9]*-\d+$", RegexOptions.Compiled);
    public string IssueKey { get; private set; } = "";

    public IssueKeyWindow(string floorName)
    {
        InitializeComponent();
        Intro.Text = $"Informe o chamado já criado para {floorName}. Os dados serão conferidos no Jira antes do vínculo.";
    }

    private void KeyChanged(object sender, TextChangedEventArgs e)
    {
        var text = KeyBox.Text.Trim().ToUpperInvariant();
        var valid = KeyPattern.IsMatch(text);
        ConfirmButton.IsEnabled = valid;
        KeyError.Text = text.Length > 0 && !valid ? "Use o formato do Jira, por exemplo SD-123." : "";
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        IssueKey = KeyBox.Text.Trim().ToUpperInvariant();
        DialogResult = true;
    }
}
