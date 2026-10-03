using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Vistora.Core;

namespace Vistora.Desktop;

/// <summary>Cor do texto de um RunState: concluída = teal, em execução = ink, interrompida = âmbar, demais = vermelho.</summary>
public sealed class RunStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RunState.Completed => Application.Current.Resources["Teal"],
        RunState.Running => Application.Current.Resources["Ink"],
        RunState.Interrupted => Application.Current.Resources["Amber"],
        _ => Application.Current.Resources["Red"]
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Segmento n (1 a 4) do indicador de etapa: teal quando o pavimento já alcançou a etapa.</summary>
public sealed class StageSegmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FloorStage stage && (int)stage >= int.Parse((string)parameter, CultureInfo.InvariantCulture)
            ? Application.Current.Resources["Teal"] : Application.Current.Resources["Line"];
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Path.GetFileName(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
