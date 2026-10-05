using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vistora.Desktop;

public sealed class HistoryClearWindow : Window
{
    public HistoryClearWindow(string message)
    {
        Title = "Limpar histórico e pendências";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, IsDefault = true };
        var clear = new Button { Content = "Limpar tudo", Background = (Brush)FindResource("Red"), Foreground = Brushes.White };
        clear.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(clear); panel.Children.Add(actions);
        Content = panel;
    }
}
