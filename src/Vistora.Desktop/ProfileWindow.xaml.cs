using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vistora.Core;

namespace Vistora.Desktop;

public partial class ProfileWindow : Window
{
    public VisitProfile Profile { get; }
    private readonly ObservableCollection<Floor> floors;
    private readonly ObservableCollection<string> attachments;
    public ProfileWindow(VisitProfile profile)
    {
        InitializeComponent();
        Profile = Serialization.Copy(profile);
        DataContext = Profile;
        floors = new(Profile.Floors);
        FloorsGrid.ItemsSource = floors;
        FloorsGrid.SelectedIndex = floors.Count > 0 ? 0 : -1;
        attachments = new(Profile.Attachments);
        AttachmentsList.ItemsSource = attachments;
    }
    private void SelectedFloorChanged(object sender, SelectionChangedEventArgs e) => ResolutionBox.DataContext = FloorsGrid.SelectedItem;
    private void AddFloor(object sender, RoutedEventArgs e)
    {
        var floor = new Floor { Name = $"Pavimento {floors.Count + 1}", Resolution = $"Realizada visita preventiva Pavimento {floors.Count + 1}\n\n" };
        floors.Add(floor); FloorsGrid.SelectedItem = floor; FloorsGrid.ScrollIntoView(floor);
    }
    private void RemoveFloor(object sender, RoutedEventArgs e) { if (FloorsGrid.SelectedItem is Floor floor) floors.Remove(floor); }
    private void Move(int offset)
    {
        FloorsGrid.CommitEdit(DataGridEditingUnit.Cell, true); FloorsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (FloorsGrid.SelectedItem is not Floor floor) return;
        var index = floors.IndexOf(floor); var target = index + offset;
        if (target >= 0 && target < floors.Count) { floors.Move(index, target); FloorsGrid.SelectedItem = floor; }
    }
    private void MoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => Move(1);
    private void AddAttachments(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Anexos para os chamados" };
        if (dialog.ShowDialog(this) == true)
            foreach (var path in dialog.FileNames.Where(p => !attachments.Contains(p))) attachments.Add(path);
    }
    private void RemoveAttachments(object sender, RoutedEventArgs e)
    {
        foreach (var path in AttachmentsList.SelectedItems.Cast<string>().ToList()) attachments.Remove(path);
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        FloorsGrid.CommitEdit(DataGridEditingUnit.Cell, true); FloorsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (string.IsNullOrWhiteSpace(Profile.Name)) { MessageBox.Show(this, "Preencha o nome do perfil.", "Vistora"); return; }
        if (floors.Any(f => string.IsNullOrWhiteSpace(f.Name)) || floors.Select(f => f.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != floors.Count)
        { MessageBox.Show(this, "Use um nome diferente e preenchido para cada pavimento.", "Vistora"); return; }
        Profile.Floors = floors.ToList();
        Profile.Attachments = attachments.ToList();
        Profile.Name = Profile.Name.Trim(); Profile.ReporterEmail = Profile.ReporterEmail.Trim(); Profile.Unit = Profile.Unit.Trim();
        DialogResult = true;
    }
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
