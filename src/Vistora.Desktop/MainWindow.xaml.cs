using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vistora.Automation.Edge;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly ExecutionEngine engine;
    private readonly DiagnosticService diagnostics;
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

    private sealed record PageInfo(string Name, string Title, string Subtitle);
    private static readonly PageInfo[] PageList =
    [
        new("Executar visita", "Executar visita preventiva", "Escolha a unidade e os pavimentos que você visitou."),
        new("Perfis e pavimentos", "Perfis e pavimentos", "Organize os dados de cada solicitante e unidade."),
        new("Histórico", "Histórico de visitas", "Confira os chamados e retome uma execução interrompida."),
        new("Configurações", "Configurações", "Defina onde a automação deve abrir os chamados.")
    ];
    // Controles que ficam bloqueados durante uma execução; só o botão Parar permanece ativo.
    private UIElement[] BusyLocked => [RunSetup, RunActions, ProfileActions, HistoryActions, SettingsPanel];

    public MainWindow(bool preview = false, bool pendingPreview = false, DiagnosticService? diagnostics = null, int startPage = 0)
    {
        this.preview = preview;
        this.pendingPreview = pendingPreview;
        InitializeComponent();
        RailItems.ItemsSource = PageList;
        SelectPage(startPage);
        this.diagnostics = diagnostics ?? new DiagnosticService(preview ? Path.Combine(Path.GetTempPath(), "VistoraPreview", Guid.NewGuid().ToString("N")) : null);
        store = new LocalStore(this.diagnostics.Root, this.diagnostics);
        engine = new ExecutionEngine(store, () => new EdgeJiraAutomation(store), this.diagnostics);
        this.diagnostics.WarningRaised += ShowDiagnosticWarning;
        if (this.diagnostics.Warning is { } warning) ShowDiagnosticWarning(warning);
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
                {
                    var stages = new[] { FloorStage.Closed, FloorStage.Closed, FloorStage.Started, FloorStage.Assigned, FloorStage.Created, FloorStage.Pending };
                    runs.Add(new VisitRun
                    {
                        Profile = Serialization.Copy(example), State = RunState.Interrupted,
                        CreatedAt = DateTimeOffset.Now.AddMinutes(-8), Message = "Execução interrompida. Retome para continuar.",
                        Floors = example.Floors.Select((f, i) => new FloorRun
                        { Floor = Serialization.Copy(f), IssueKey = stages[i % stages.Length] == FloorStage.Pending ? null : $"SD-{1001 + i}", Stage = stages[i % stages.Length], Message = "Chamado aberto" }).ToList()
                    });
                    // Outros estados no histórico, de perfis distintos para não gerar pendência no perfil ativo.
                    foreach (var (state, label, minutes) in new[] { (RunState.Completed, "Concluída", -1500), (RunState.NeedsReconciliation, "Vincular", -2900), (RunState.Failed, "Falha", -4400) })
                    {
                        var other = Serialization.Copy(example); other.Id = Guid.NewGuid().ToString("N"); other.Name = $"Unidade {label}";
                        runs.Add(new VisitRun { Profile = other, State = state, CreatedAt = DateTimeOffset.Now.AddMinutes(minutes), Message = label,
                            Floors = other.Floors.Select(f => new FloorRun { Floor = f, Stage = state == RunState.Completed ? FloorStage.Closed : FloorStage.Pending }).ToList() });
                    }
                }
            }
            else
            {
                profiles = await store.LoadProfilesAsync(); settings = await store.LoadSettingsAsync(); runs = await store.LoadRunsAsync();
                settings.Diagnostics ??= new DiagnosticOptions();
                diagnostics.Configure(settings.Diagnostics);
                diagnostics.Register(profiles);
                if (profiles.Profiles.Count == 0)
                {
                    var first = NewTemplate(); profiles.Profiles.Add(first); profiles.ActiveProfileId = first.Id;
                    await store.SaveProfilesAsync(profiles);
                }
            }
            RefreshProfiles(); RefreshHistory();
            JiraUrlBox.Text = settings.JiraUrl; PortalUrlBox.Text = settings.PortalUrl;
            QueueUrlBox.Text = settings.QueueUrl; TimeoutBox.Text = settings.TimeoutSeconds.ToString();
            DetailedLogsBox.IsChecked = settings.Diagnostics.MinimumLevel == DiagnosticLevel.Debug;
            ClosingTeamSummary.Text = $"Equipe de fechamento: {settings.ClosingTeam}";
            ExecutionMessage.Text = preview ? "Escolha os pavimentos e confira os textos antes de executar." : "Configure o perfil da unidade e faça login no Edge quando solicitado.";
            loading = false;
            await diagnostics.CleanAsync();
        }
        catch (Exception ex)
        {
            diagnostics.AppEvent("app.initialize_failed", "Não foi possível carregar os dados locais.", DiagnosticLevel.Error, ex, code: "INITIALIZATION_FAILED");
            ExecuteButton.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            ExecutionMessage.Text = "Não foi possível carregar os dados locais. Os arquivos foram preservados.";
            MessageBox.Show(this, ex.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowDiagnosticWarning(string message) => Dispatcher.InvokeAsync(() =>
    { DiagnosticWarning.Text = message; DiagnosticWarning.Visibility = Visibility.Visible; });

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
        ShowRunFloors(null);
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
            ShowRunFloors(run);
        }
        catch (InvalidOperationException ex) { PendingVisitNotice.Text = ex.Message; }
    }
    // Lista compacta de pavimentos com etapa e chamado da execução em foco (ativa, concluída ou pendente).
    private void ShowRunFloors(VisitRun? run)
    {
        RunFloorList.ItemsSource = null; RunFloorList.ItemsSource = run?.Floors; // reatribui para redesenhar as etapas
        RunFloorsScroll.Visibility = run is { Floors.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        RunProgress.Value = run is null || run.Floors.Count == 0 ? 0 : run.Floors.Sum(f => (int)f.Stage) * 100d / (run.Floors.Count * 4);
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
    private void RailItemLoaded(object sender, RoutedEventArgs e)
    {
        var item = (RadioButton)sender;
        if (ReferenceEquals(item.DataContext, PageList[Pages.SelectedIndex])) item.IsChecked = true;
    }
    private void PageChecked(object sender, RoutedEventArgs e)
    {
        var page = (PageInfo)((RadioButton)sender).DataContext;
        SelectPage(Array.IndexOf(PageList, page));
    }
    internal void SelectPage(int index)
    {
        index = Math.Clamp(index, 0, PageList.Length - 1);
        Pages.SelectedIndex = index;
        var page = PageList[index];
        PageTitle.Text = page.Title; PageSubtitle.Text = page.Subtitle;
        if (RailItems.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter container &&
            VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is RadioButton item)
            item.IsChecked = true;
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
        foreach (var control in BusyLocked) control.IsEnabled = !value;
        StopButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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
        ShowRunFloors(run);
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
        diagnostics.AppEvent("access.started", "Verificação de acesso iniciada.");
        await using var browser = new EdgeJiraAutomation(store);
        await browser.ConnectAsync(settings, message => Dispatcher.Invoke(() => ExecutionMessage.Text = message), token);
        ExecutionMessage.Text = "Acesso ao formulário confirmado. A sessão do Edge foi salva.";
        diagnostics.AppEvent("access.confirmed", "Acesso ao formulário confirmado.");
    }
    private void RefreshHistory(string? selectedId = null)
    {
        var id = selectedId ?? (HistoryGrid.SelectedItem as VisitRun)?.Id;
        HistoryGrid.ItemsSource = null; HistoryGrid.ItemsSource = runs;
        HistoryGrid.SelectedItem = runs.FirstOrDefault(r => r.Id == id)
            ?? runs.FirstOrDefault(r => r.Profile.Id == ActiveProfile?.Id && r.State != RunState.Completed && r.Floors.Any(f => f.IssueKey is not null))
            ?? runs.FirstOrDefault();
    }
    private async void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        var run = HistoryGrid.SelectedItem as VisitRun;
        RunFloorGrid.ItemsSource = run?.Floors; RunFloorGrid.SelectedIndex = run is null ? -1 : 0;
        HistoryMessage.Text = run?.Message ?? "Selecione uma execução para ver os chamados.";
        DiagnosticSummary.Text = "";
        if (run is not null)
            try
            {
                var overview = await diagnostics.ReadOverviewAsync(run.Id);
                if (HistoryGrid.SelectedItem == run) DiagnosticSummary.Text = overview ?? "Diagnóstico detalhado indisponível para esta visita.";
            }
            catch { DiagnosticSummary.Text = "Diagnóstico indisponível."; }
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
        var dialog = new IssueKeyWindow(floor.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var key = dialog.IssueKey;
        await SafeAsync(async () =>
        {
            SetBusy(true); cancellation = new CancellationTokenSource();
            try { activeTask = engine.AttachIssueAsync(run, floor, key, cancellation.Token); await activeTask; }
            finally { activeTask = null; cancellation.Dispose(); cancellation = null; SetBusy(false); RefreshHistory(run.Id); }
        });
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
    private async void ExportLog(object sender, RoutedEventArgs e)
    { if (HistoryGrid.SelectedItem is VisitRun run) await DiagnosticExportUi.ExportAsync(this, diagnostics, run); }
    private async void ExportAppLog(object sender, RoutedEventArgs e) => await DiagnosticExportUi.ExportAsync(this, diagnostics);
    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var copy = Serialization.Copy(settings);
        copy.JiraUrl = JiraUrlBox.Text.Trim().TrimEnd('/'); copy.PortalUrl = PortalUrlBox.Text.Trim(); copy.QueueUrl = QueueUrlBox.Text.Trim();
        if (!int.TryParse(TimeoutBox.Text, out var timeout)) { ShowSettingsStatus("Informe o tempo de espera em segundos.", true); return; }
        copy.TimeoutSeconds = timeout;
        copy.Diagnostics.MinimumLevel = DetailedLogsBox.IsChecked == true ? DiagnosticLevel.Debug : DiagnosticLevel.Information;
        var errors = ProfileValidation.ValidateSettings(copy);
        if (errors.Count > 0) { ShowSettingsStatus(string.Join(Environment.NewLine, errors), true); return; }
        await SafeAsync(async () =>
        {
            await store.SaveSettingsAsync(copy); settings = copy;
            diagnostics.Configure(copy.Diagnostics);
            ShowSettingsStatus("Configurações salvas.", false);
        });
    }
    private void ShowSettingsStatus(string text, bool error)
    {
        SettingsStatus.Text = text;
        SettingsStatus.Foreground = (Brush)FindResource(error ? "Red" : "Teal");
    }
    private void SettingsEdited(object sender, TextChangedEventArgs e) => SettingsStatus.Text = "";
    private void OpenData(object sender, RoutedEventArgs e) => OpenPath(store.Root);
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { ExecutionMessage.Text = "Operação interrompida."; }
        catch (Exception exception)
        {
            diagnostics.AppEvent("ui.operation_failed", "Uma operação da interface falhou.", DiagnosticLevel.Error, exception);
            MessageBox.Show(this, exception.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!busy || closing) return;
        e.Cancel = true; closing = true;
        cancellation?.Cancel(); ExecutionMessage.Text = "Registrando o progresso antes de fechar…";
        try { if (activeTask is not null) await activeTask; }
        catch (Exception ex) { diagnostics.AppEvent("app.shutdown_failed", "Falha ao aguardar a execução no encerramento.", DiagnosticLevel.Error, ex); }
        await diagnostics.FlushAsync();
        Close();
    }
}
