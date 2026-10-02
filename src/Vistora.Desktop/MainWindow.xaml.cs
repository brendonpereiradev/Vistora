using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Vistora.Automation.Edge;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly ExecutionEngine engine;
    private readonly bool preview;
    private readonly bool pendingPreview;
    private ProfileDocument profiles = new();
    private AppSettings settings = new();
    private List<VisitRun> runs = [];
    private CancellationTokenSource? cancellation;
    private Task? activeTask;
    private bool loading = true;
    private bool busy;
    private bool closing;
    private VisitProfile? ActiveProfile => ProfilePicker.SelectedItem as VisitProfile;

    public MainWindow(bool preview = false, bool pendingPreview = false)
    {
        this.preview = preview;
        this.pendingPreview = pendingPreview;
        InitializeComponent();
        store = new LocalStore(preview ? Path.Combine(Path.GetTempPath(), "VistoraPreview", Guid.NewGuid().ToString("N")) : null);
        engine = new ExecutionEngine(store, () => new EdgeJiraAutomation(store));
        engine.Progress += run => Dispatcher.Invoke(() => ShowProgress(run));
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (preview)
            {
                var example = NewTemplate();
                example.Name = "Unidade de exemplo"; example.ReporterName = "Administrador da unidade";
                example.Unit = "Configure o nome da sua unidade";
                profiles.Profiles.Add(example); profiles.ActiveProfileId = example.Id;
                if (pendingPreview)
                    runs.Add(new VisitRun
                    {
                        Profile = Serialization.Copy(example), State = RunState.Interrupted,
                        CreatedAt = DateTimeOffset.Now.AddMinutes(-8),
                        Floors = example.Floors.Select((f, i) => new FloorRun
                        { Floor = Serialization.Copy(f), IssueKey = $"SD-{1001 + i}", Stage = FloorStage.Created, Message = "Chamado aberto" }).ToList()
                    });
            }
            else
            {
                profiles = await store.LoadProfilesAsync(); settings = await store.LoadSettingsAsync(); runs = await store.LoadRunsAsync();
                if (profiles.Profiles.Count == 0)
                {
                    var first = NewTemplate(); profiles.Profiles.Add(first); profiles.ActiveProfileId = first.Id;
                    await store.SaveProfilesAsync(profiles);
                }
            }
            RefreshProfiles(); RefreshHistory();
            JiraUrlBox.Text = settings.JiraUrl; PortalUrlBox.Text = settings.PortalUrl;
            QueueUrlBox.Text = settings.QueueUrl; TimeoutBox.Text = settings.TimeoutSeconds.ToString();
            ClosingTeamSummary.Text = $"Equipe de fechamento: {settings.ClosingTeam}";
            ExecutionMessage.Text = preview ? "Escolha os pavimentos e confira os textos antes de executar." : "Configure o perfil da unidade e faça login no Edge quando solicitado.";
            loading = false;
        }
        catch (Exception ex)
        {
            ExecuteButton.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            ExecutionMessage.Text = "Não foi possível carregar os dados locais. Os arquivos foram preservados.";
            MessageBox.Show(this, ex.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static VisitProfile NewTemplate()
    {
        var result = new VisitProfile();
        var file = Path.Combine(AppContext.BaseDirectory, "assets", "resolucoes.txt");
        if (File.Exists(file))
            foreach (var block in Regex.Split(File.ReadAllText(file).Trim(), @"\r?\n\s*-\s*\r?\n"))
            {
                var text = block.Trim();
                var firstLine = text.Split('\n')[0].Trim();
                var name = firstLine.Replace("Realizada visita preventiva ", "", StringComparison.Ordinal);
                result.Floors.Add(new Floor { Name = name, Resolution = text });
            }
        return result;
    }

    private void RefreshProfiles(string? selectedId = null)
    {
        var id = selectedId ?? profiles.ActiveProfileId;
        ProfilePicker.ItemsSource = null; ProfilePicker.ItemsSource = profiles.Profiles;
        ProfilePicker.SelectedItem = profiles.Profiles.FirstOrDefault(p => p.Id == id) ?? profiles.Profiles.FirstOrDefault();
        ProfilesGrid.ItemsSource = null; ProfilesGrid.ItemsSource = profiles.Profiles;
        ShowProfile();
    }
    private void ShowProfile()
    {
        FloorGrid.ItemsSource = ActiveProfile?.Floors;
        FloorGrid.SelectedIndex = ActiveProfile?.Floors.Count > 0 ? 0 : -1;
        ProfileSummary.Text = ActiveProfile is { } p ? $"{(p.Unit.Length > 0 ? p.Unit : "Unidade a configurar")}\nSolicitante: {(p.ReporterName.Length > 0 ? p.ReporterName : "a configurar")}" : "Crie um perfil para começar.";
        UpdateSelectionSummary();
        UpdateResumeNotice();
    }
    private void UpdateResumeNotice()
    {
        if (PendingVisitNotice is null || ExecuteButton is null) return;
        var pending = ActiveProfile is not null && runs.Any(r => r.Profile.Id == ActiveProfile.Id && r.State != RunState.Completed);
        ExecuteButton.Content = pending ? "Retomar visita preventiva" : "Executar visita preventiva";
        PendingVisitNotice.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        if (!pending) return;
        try
        {
            var run = VisitRunSelection.PendingForProfile(runs, ActiveProfile!.Id)!;
            var count = run.Floors.Count(f => f.IssueKey is not null);
            PendingVisitNotice.Text = $"Visita pendente de {run.DateLabel}: {count} chamados registrados. A execução continuará com os dados e o progresso dessa visita.";
        }
        catch (InvalidOperationException ex) { PendingVisitNotice.Text = ex.Message; }
    }
    private void UpdateSelectionSummary() => SelectionSummary.Text = $"Pavimentos selecionados: {ActiveProfile?.Floors.Count(f => f.Selected) ?? 0}";
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActiveProfile is null) return;
        profiles.ActiveProfileId = ActiveProfile.Id;
        ShowProfile();
        if (!loading && !preview) await SafeAsync(() => store.SaveProfilesAsync(profiles));
    }
    private void FloorSelected(object sender, SelectionChangedEventArgs e) => ResolutionPreview.Text = (FloorGrid.SelectedItem as Floor)?.Resolution ?? "Selecione um pavimento para conferir seu texto.";
    private void SelectionChanged(object sender, RoutedEventArgs e) { if (SelectionSummary is not null) UpdateSelectionSummary(); }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        var index = int.Parse((string)((Button)sender).Tag);
        Pages.SelectedIndex = index;
        var titles = new[] { "Executar visita preventiva", "Perfis e pavimentos", "Histórico de visitas", "Configurações" };
        var subtitles = new[] { "Escolha a unidade e os pavimentos que você visitou.", "Organize os dados de cada solicitante e unidade.", "Confira os chamados e retome uma execução interrompida.", "Defina onde a automação deve abrir os chamados." };
        PageTitle.Text = titles[index]; PageSubtitle.Text = subtitles[index];
    }

    private async Task SaveProfileAsync(VisitProfile profile, bool newProfile)
    {
        if (profiles.Profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Já existe um perfil com este nome. Escolha outro nome.");
        if (newProfile) profiles.Profiles.Add(profile);
        else profiles.Profiles[profiles.Profiles.FindIndex(p => p.Id == profile.Id)] = profile;
        profiles.ActiveProfileId = profile.Id;
        await store.SaveProfilesAsync(profiles);
        RefreshProfiles(profile.Id);
        ExecutionMessage.Text = "Perfil salvo. Confira os pavimentos antes de executar.";
    }
    private async Task EditAsync(VisitProfile? profile, bool isNew = false)
    {
        if (busy || profile is null) return;
        var dialog = new ProfileWindow(profile) { Owner = this };
        if (dialog.ShowDialog() == true) await SaveProfileAsync(dialog.Profile, isNew);
    }
    private async void NewProfile(object sender, RoutedEventArgs e) => await SafeAsync(() => EditAsync(NewTemplate(), true));
    private async void EditActive(object sender, RoutedEventArgs e) => await SafeAsync(() => EditAsync(ActiveProfile));
    private async void EditProfile(object sender, RoutedEventArgs e) => await SafeAsync(() => EditAsync(ProfilesGrid.SelectedItem as VisitProfile));
    private async void DuplicateProfile(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VisitProfile source || busy) return;
        var copy = Serialization.Copy(source); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " (cópia)";
        foreach (var floor in copy.Floors) floor.Id = Guid.NewGuid().ToString("N");
        await SafeAsync(() => EditAsync(copy, true));
    }
    private async void DeleteProfile(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VisitProfile profile || busy) return;
        if (MessageBox.Show(this, $"Excluir o perfil “{profile.Name}”? O histórico das visitas será mantido.", "Vistora", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await SafeAsync(async () => { profiles.Profiles.Remove(profile); profiles.ActiveProfileId = profiles.Profiles.FirstOrDefault()?.Id; await store.SaveProfilesAsync(profiles); RefreshProfiles(); });
    }

    private void SetBusy(bool value)
    {
        busy = value;
        foreach (var control in new UIElement[] { ProfilePicker, FloorGrid, EditActiveButton, ExecuteButton, ConnectButton, NewProfileButton, EditProfileButton, DuplicateButton, DeleteButton, SettingsPanel, ResumeButton, LinkButton }) control.IsEnabled = !value;
        StopButton.IsEnabled = value;
    }
    private async void Execute(object sender, RoutedEventArgs e)
    {
        if (busy || ActiveProfile is null) return;
        await SafeAsync(async () =>
        {
            if (preview) throw new InvalidOperationException("A prévia é somente para conferir a tela.");
            var pending = VisitRunSelection.PendingForProfile(runs, ActiveProfile.Id);
            if (pending is not null)
            {
                RefreshHistory(pending.Id);
                await RunAsync(pending);
                return;
            }
            var errors = ProfileValidation.Validate(ActiveProfile, settings);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            await store.SaveProfilesAsync(profiles);
            var run = ProfileValidation.CreateRun(ActiveProfile, settings);
            await store.SaveRunAsync(run);
            runs.Insert(0, run); RefreshHistory();
            await RunAsync(run);
        });
    }
    private async Task RunAsync(VisitRun run)
    {
        SetBusy(true); cancellation = new CancellationTokenSource();
        try { activeTask = engine.ExecuteAsync(run, cancellation.Token); await activeTask; ShowProgress(run); }
        finally { activeTask = null; cancellation.Dispose(); cancellation = null; SetBusy(false); RefreshHistory(run.Id); UpdateResumeNotice(); }
    }
    private void Stop(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel(); StopButton.IsEnabled = false;
        ExecutionMessage.Text = "Parando após registrar o resultado da operação atual…";
    }
    private void ShowProgress(VisitRun run)
    {
        ClosingTeamSummary.Text = $"Equipe de fechamento: {run.Settings.ClosingTeam}";
        ExecutionMessage.Text = run.Message;
        var stages = run.Floors.Sum(f => (int)f.Stage);
        RunProgress.Value = run.Floors.Count == 0 ? 0 : stages * 100d / (run.Floors.Count * 4);
        HistoryGrid.Items.Refresh();
        if (HistoryGrid.SelectedItem is VisitRun selected && selected.Id == run.Id)
        { HistoryMessage.Text = run.Message; RunFloorGrid.Items.Refresh(); }
    }
    private async void CheckAccess(object sender, RoutedEventArgs e)
    {
        if (busy || preview) return;
        await SafeAsync(async () =>
        {
            SetBusy(true); cancellation = new CancellationTokenSource();
            try
            {
                activeTask = CheckAccessAsync(cancellation.Token);
                await activeTask;
            }
            finally { activeTask = null; cancellation.Dispose(); cancellation = null; SetBusy(false); }
        });
    }
    private async Task CheckAccessAsync(CancellationToken token)
    {
        await using var browser = new EdgeJiraAutomation(store);
        await browser.ConnectAsync(settings, message => Dispatcher.Invoke(() => ExecutionMessage.Text = message), token);
        ExecutionMessage.Text = "Acesso ao formulário confirmado. A sessão do Edge foi salva.";
    }
    private void RefreshHistory(string? selectedId = null)
    {
        var id = selectedId ?? (HistoryGrid.SelectedItem as VisitRun)?.Id;
        HistoryGrid.ItemsSource = null; HistoryGrid.ItemsSource = runs;
        HistoryGrid.SelectedItem = runs.FirstOrDefault(r => r.Id == id)
            ?? runs.FirstOrDefault(r => r.Profile.Id == ActiveProfile?.Id && r.State != RunState.Completed && r.Floors.Any(f => f.IssueKey is not null))
            ?? runs.FirstOrDefault();
    }
    private void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        var run = HistoryGrid.SelectedItem as VisitRun;
        RunFloorGrid.ItemsSource = run?.Floors; RunFloorGrid.SelectedIndex = run is null ? -1 : 0;
        HistoryMessage.Text = run?.Message ?? "Selecione uma execução para ver os chamados.";
    }
    private async void Resume(object sender, RoutedEventArgs e)
    {
        if (busy || HistoryGrid.SelectedItem is not VisitRun run) return;
        await SafeAsync(async () =>
        {
            var pending = VisitRunSelection.ForResume(runs, run);
            RefreshHistory(pending.Id);
            await RunAsync(pending);
        });
    }
    private async void LinkIssue(object sender, RoutedEventArgs e)
    {
        if (busy || HistoryGrid.SelectedItem is not VisitRun run || RunFloorGrid.SelectedItem is not FloorRun floor) return;
        if (floor.IssueKey is not null || floor.PendingAction != PendingAction.Create)
        { MessageBox.Show(this, "Selecione um pavimento cuja abertura foi enviada e cujo número ainda não foi identificado.", "Vistora"); return; }
        var key = AskForIssueKey(floor.Name);
        if (key is null) return;
        await SafeAsync(async () =>
        {
            SetBusy(true); cancellation = new CancellationTokenSource();
            try { activeTask = engine.AttachIssueAsync(run, floor, key.Trim().ToUpperInvariant(), cancellation.Token); await activeTask; }
            finally { activeTask = null; cancellation.Dispose(); cancellation = null; SetBusy(false); RefreshHistory(run.Id); }
        });
    }
    private string? AskForIssueKey(string floorName)
    {
        var dialog = new Window { Owner = this, Title = "Vincular chamado", Width = 470, Height = 250, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"Informe o chamado já criado para {floorName}.\nOs dados serão conferidos no Jira antes do vínculo.", TextWrapping = TextWrapping.Wrap });
        var field = new TextBox { Margin = new Thickness(0, 16, 0, 16) }; panel.Children.Add(field);
        var button = new Button { Content = "Conferir e vincular", Style = (Style)FindResource("Primary") };
        button.Click += (_, _) => { dialog.DialogResult = true; }; panel.Children.Add(button); dialog.Content = panel;
        return dialog.ShowDialog() == true ? field.Text : null;
    }
    private void OpenIssue(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is VisitRun run && RunFloorGrid.SelectedItem is FloorRun { IssueKey: { } key })
            OpenPath($"{run.Settings.JiraUrl.TrimEnd('/')}/jira/servicedesk/projects/{key.Split('-')[0]}/queues/issue/{key}");
    }
    private void OpenDiagnostics(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not VisitRun run) return;
        var path = store.DiagnosticsDirectory(run.Id);
        if (Directory.Exists(path)) OpenPath(path); else MessageBox.Show(this, "Esta execução não tem diagnóstico registrado.", "Vistora");
    }
    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await SafeAsync(async () =>
        {
            var copy = Serialization.Copy(settings);
            copy.JiraUrl = JiraUrlBox.Text.Trim().TrimEnd('/'); copy.PortalUrl = PortalUrlBox.Text.Trim(); copy.QueueUrl = QueueUrlBox.Text.Trim();
            if (!int.TryParse(TimeoutBox.Text, out var timeout)) throw new InvalidOperationException("Informe o tempo de espera em segundos.");
            copy.TimeoutSeconds = timeout;
            var validationProfile = new VisitProfile { Floors = [new Floor { Resolution = "Teste" }] };
            var errors = ProfileValidation.Validate(validationProfile, copy).Where(message => message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase) || message.Contains("https://", StringComparison.OrdinalIgnoreCase) || message.Contains("tempo de espera", StringComparison.OrdinalIgnoreCase)).ToList();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            await store.SaveSettingsAsync(copy); settings = copy;
            MessageBox.Show(this, "Configurações salvas.", "Vistora");
        });
    }
    private void OpenData(object sender, RoutedEventArgs e) => OpenPath(store.Root);
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { ExecutionMessage.Text = "Operação interrompida."; }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!busy || closing) return;
        e.Cancel = true; closing = true;
        cancellation?.Cancel(); ExecutionMessage.Text = "Registrando o progresso antes de fechar…";
        try { if (activeTask is not null) await activeTask; } catch { /* Os detalhes ficam no registro da execução. */ }
        Close();
    }
}
