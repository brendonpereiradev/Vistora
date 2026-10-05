using System.ComponentModel;
using System.IO;
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
    private bool cleaningHistory;
    private bool exportingLog;
    private bool closing;
    private bool updatingPendingVisits;
    private VisitProfile? ActiveProfile => ProfilePicker.SelectedItem as VisitProfile;
    private VisitRun? SelectedPendingRun => (PendingVisitPicker?.SelectedItem as PendingVisitChoice)?.Run;

    private sealed record PageInfo(string Name, string Title, string Subtitle);
    private static readonly PageInfo[] PageList =
    [
        new("Executar visita", "Executar visita preventiva", "Escolha a unidade e os pavimentos que você visitou."),
        new("Perfis e pavimentos", "Perfis e pavimentos", "Organize os dados de cada solicitante e unidade."),
        new("Histórico", "Histórico de visitas", "Confira os chamados e os resultados de cada visita."),
        new("Configurações", "Configurações", "Defina onde a automação deve abrir os chamados.")
    ];
    // Controles bloqueados durante execução ou limpeza das visitas.
    private UIElement[] BusyLocked => [RunSetup, RunActions, ProfileActions, SettingsPanel];

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.FitStartup(this);
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
                settings.Diagnostics.MinimumLevel = DiagnosticLevel.Debug;
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
            QueueUrlBox.Text = settings.QueueUrl;
            loading = false;
            UpdateInteraction();
            await diagnostics.CleanAsync();
        }
        catch (Exception ex)
        {
            diagnostics.AppEvent("app.initialize_failed", "Não foi possível carregar os dados locais.", DiagnosticLevel.Error, ex, code: "INITIALIZATION_FAILED");
            ExecuteButton.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            MessageBox.Show(this, ex.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowDiagnosticWarning(string message) => Dispatcher.InvokeAsync(() =>
    { DiagnosticWarning.Text = message; DiagnosticWarning.Visibility = Visibility.Visible; });

    private static VisitProfile NewTemplate() => new()
    {
        Floors = new[] { "Subsolo", "Térreo", "1° Pavimento", "2° Pavimento", "3° Pavimento", "4° Pavimento" }
            .Select(name => new Floor
            {
                Name = name,
                Resolution = $"Realizada visita preventiva {name}\n\nEquipamentos e serviços verificados.\nObservações: preencha o resultado da visita."
            }).ToList()
    };

    private void RefreshProfiles(string? selectedId = null)
    {
        var id = selectedId ?? profiles.ActiveProfileId;
        updatingPendingVisits = true;
        try
        {
            ProfilePicker.ItemsSource = null; ProfilePicker.ItemsSource = profiles.Profiles;
            ProfilePicker.SelectedItem = profiles.Profiles.FirstOrDefault(p => p.Id == id) ?? profiles.Profiles.FirstOrDefault();
        }
        finally { updatingPendingVisits = false; }
        ProfilesGrid.ItemsSource = null; ProfilesGrid.ItemsSource = profiles.Profiles;
        RefreshPendingVisits(preferProfile: true);
    }
    private void RefreshPendingVisits(string? selectedId = null, bool preferProfile = false)
    {
        var id = selectedId ?? (preferProfile ? null : SelectedPendingRun?.Id);
        var choices = VisitRunSelection.Pending(runs)
            .Select(run => new PendingVisitChoice(run, profiles.Profiles.All(p => p.Id != run.Profile.Id))).ToList();
        var selected = choices.FirstOrDefault(choice => choice.Run.Id == id);
        if (selected is null && ActiveProfile is not null)
        {
            try
            {
                var preferred = VisitRunSelection.PendingForProfile(runs, ActiveProfile.Id);
                selected = choices.FirstOrDefault(choice => choice.Run == preferred);
            }
            catch (InvalidOperationException) { /* A escolha explícita fica na tela principal. */ }
        }
        updatingPendingVisits = true;
        try { PendingVisitPicker.ItemsSource = choices; PendingVisitPicker.SelectedItem = selected; }
        finally { updatingPendingVisits = false; }
        PendingVisitPanel.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowExecutionProfile();
    }
    private void PendingVisitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPendingVisits) return;
        if (SelectedPendingRun is { } run)
        {
            updatingPendingVisits = true;
            try { ProfilePicker.SelectedItem = profiles.Profiles.FirstOrDefault(p => p.Id == run.Profile.Id); }
            finally { updatingPendingVisits = false; }
        }
        ShowExecutionProfile();
        if (SelectedPendingRun is { } selected) ShowProgress(selected);
    }
    private void ShowExecutionProfile()
    {
        var run = SelectedPendingRun;
        var profile = run?.Profile ?? ActiveProfile;
        var floors = run?.Floors.Select(f => f.Floor).ToList() ?? profile?.Floors;
        FloorGrid.ItemsSource = floors;
        FloorGrid.SelectedIndex = floors?.Count > 0 ? 0 : -1;
        ShowRunFloors(run);
        VisitColumn.Visibility = run is null ? Visibility.Visible : Visibility.Collapsed;
        var profileHasPending = ActiveProfile is not null && runs.Any(r => r.Profile.Id == ActiveProfile.Id && r.State != RunState.Completed);
        ExecuteButton.Content = run is not null || profileHasPending ? "Retomar visita preventiva" : "Executar visita preventiva";
        UpdateExecutionControls();
        PendingVisitNotice.Visibility = PendingVisitPanel.Visibility;
        if (run is not null)
        {
            var count = run.Floors.Count(f => f.IssueKey is not null);
            PendingVisitNotice.Text = $"Visita pendente de {run.DateLabel}: {count} chamados registrados. A retomada usará os dados originais e o progresso desta visita.";
        }
        else PendingVisitNotice.Text = profileHasPending
            ? "Selecione a visita pendente que deseja retomar. Nenhum chamado será aberto antes dessa escolha."
            : "Para retomar, escolha uma visita pendente acima. Para uma nova visita, use o perfil selecionado.";
        UpdateSelectionSummary();
    }
    private void ShowRunFloors(VisitRun? run)
    {
        RunFloorList.ItemsSource = null; RunFloorList.ItemsSource = run?.Floors;
        RunFloorsScroll.Visibility = run is { Floors.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        RunProgress.Value = run is null || run.Floors.Count == 0 ? 0 : run.Floors.Sum(f => (int)f.Stage) * 100d / (run.Floors.Count * 4);
    }
    private void UpdateExecutionControls()
    {
        var profileHasPending = ActiveProfile is not null && runs.Any(r => r.Profile.Id == ActiveProfile.Id && r.State != RunState.Completed);
        ExecuteButton.IsEnabled = !busy && !cleaningHistory && (SelectedPendingRun is not null || (ActiveProfile is not null && !profileHasPending));
    }
    private void UpdateSelectionSummary() => SelectionSummary.Text = SelectedPendingRun is { } run
        ? $"Pavimentos da visita: {run.Floors.Count}"
        : $"Pavimentos selecionados: {ActiveProfile?.Floors.Count(f => f.Selected) ?? 0}";
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPendingVisits || ActiveProfile is null) return;
        profiles.ActiveProfileId = ActiveProfile.Id;
        RefreshPendingVisits(preferProfile: true);
        if (SelectedPendingRun is { } run) ShowProgress(run);
        else RunProgress.Value = 0;
        if (!loading && !preview) await SafeAsync(() => store.SaveProfilesAsync(profiles));
    }
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
    }
    private async Task EditAsync(VisitProfile? profile, bool isNew = false)
    {
        if (busy || cleaningHistory || profile is null) return;
        var dialog = new ProfileWindow(profile) { Owner = this };
        if (dialog.ShowDialog() == true) await SaveProfileAsync(dialog.Profile, isNew);
    }
    private async void NewProfile(object sender, RoutedEventArgs e) => await SafeAsync(() => EditAsync(NewTemplate(), true));
    private async void EditProfile(object sender, RoutedEventArgs e) => await SafeAsync(() => EditAsync(ProfilesGrid.SelectedItem as VisitProfile));
    private async void DeleteProfile(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not VisitProfile profile || busy || cleaningHistory) return;
        if (MessageBox.Show(this, $"Excluir o perfil “{profile.Name}”? O histórico das visitas será mantido.", "Vistora", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await SafeAsync(async () => { profiles.Profiles.Remove(profile); profiles.ActiveProfileId = profiles.Profiles.FirstOrDefault()?.Id; await store.SaveProfilesAsync(profiles); RefreshProfiles(); });
    }

    private void SetBusy(bool value)
    {
        busy = value;
        UpdateInteraction();
    }
    private void UpdateInteraction()
    {
        foreach (var control in BusyLocked) control.IsEnabled = !busy && !cleaningHistory;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateExecutionControls();
        StopButton.IsEnabled = busy;
        ClearHistoryButton.IsEnabled = !loading && !busy && !cleaningHistory && !exportingLog && !preview;
        ExportLogButton.IsEnabled = !cleaningHistory && !exportingLog;
    }
    private async void Execute(object sender, RoutedEventArgs e)
    {
        if (busy || cleaningHistory || loading) return;
        await SafeAsync(async () =>
        {
            if (preview) throw new InvalidOperationException("A prévia é somente para conferir a tela.");
            SetBusy(true); cancellation = new CancellationTokenSource();
            try { activeTask = ExecuteVisitAsync(); await activeTask; }
            finally { activeTask = null; cancellation.Dispose(); cancellation = null; SetBusy(false); }
        });
    }
    private async Task ExecuteVisitAsync()
    {
        var pending = SelectedPendingRun is { } selected ? VisitRunSelection.ForResume(runs, selected)
            : ActiveProfile is not null ? VisitRunSelection.PendingForProfile(runs, ActiveProfile.Id) : null;
        if (pending is not null)
        {
            RefreshHistory(pending.Id);
            RefreshPendingVisits(pending.Id);
            await RunAsync(pending, resume: true);
            return;
        }
        if (ActiveProfile is null) throw new InvalidOperationException("Escolha um perfil ou uma visita pendente para continuar.");
        var errors = ProfileValidation.Validate(ActiveProfile, settings);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        await store.SaveProfilesAsync(profiles);
        var run = ProfileValidation.CreateRun(ActiveProfile, settings);
        await store.SaveRunAsync(run);
        runs.Insert(0, run); RefreshHistory();
        cancellation!.Token.ThrowIfCancellationRequested();
        await RunAsync(run);
    }
    private async Task RunAsync(VisitRun run, bool resume = false)
    {
        try
        {
            if (resume)
            {
                var token = cancellation!.Token;
                var task = engine.ResumeAsync(run, floor => Dispatcher.InvokeAsync(() => token.IsCancellationRequested ? null : AskForIssueKey(floor.Name)).Task, token,
                    (floor, snapshot) => Dispatcher.InvokeAsync(() => !token.IsCancellationRequested && MessageBox.Show(this,
                        $"O fechamento anterior de {floor.Name} ({floor.IssueKey}) ficou sem confirmação. No Jira, o chamado está em “{snapshot.Status}”.\n\n" +
                        "Confira na janela do Edge a resolução e os comentários deste chamado antes de continuar. Se o texto já foi enviado, escolha Não e conclua o fechamento no Jira para evitar duplicação.\n\n" +
                        "Você conferiu que a resolução e o comentário público não foram enviados e deseja tentar o fechamento novamente?",
                        "Conferir fechamento pendente", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes).Task);
                if (await task) ShowProgress(run);
            }
            else { await engine.ExecuteAsync(run, cancellation!.Token); ShowProgress(run); }
        }
        finally
        {
            RefreshHistory(run.Id);
            RefreshPendingVisits(run.State == RunState.Completed ? null : run.Id);
            if (run.State == RunState.Completed) ShowRunFloors(run);
        }
    }
    private void Stop(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel(); StopButton.IsEnabled = false;
    }
    private void ShowProgress(VisitRun run)
    {
        ShowRunFloors(run);
        HistoryGrid.Items.Refresh();
        if (HistoryGrid.SelectedItem is VisitRun selected && selected.Id == run.Id)
            RunFloorGrid.Items.Refresh();
    }
    private async void CheckAccess(object sender, RoutedEventArgs e)
    {
        if (busy || cleaningHistory || loading || preview) return;
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
        await browser.ConnectAsync(settings, message => diagnostics.AppEvent("access.progress", message), token);
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
    private void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        var run = HistoryGrid.SelectedItem as VisitRun;
        RunFloorGrid.ItemsSource = run?.Floors; RunFloorGrid.SelectedIndex = run is null ? -1 : 0;
    }
    private string? AskForIssueKey(string floorName)
    {
        var dialog = new IssueKeyWindow(floorName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.IssueKey : null;
    }
    private async void ExportLog(object sender, RoutedEventArgs e)
    {
        if (cleaningHistory || exportingLog) return;
        exportingLog = true; UpdateInteraction();
        try { await DiagnosticExportUi.ExportAsync(this, diagnostics, HistoryGrid.SelectedItem as VisitRun); }
        finally { exportingLog = false; UpdateInteraction(); }
    }
    private async void ClearHistory(object sender, RoutedEventArgs e) => await SafeAsync(() => ClearHistoryWithConfirmationAsync(message =>
        new HistoryClearWindow(message) { Owner = this }.ShowDialog() == true));

    private async Task ClearHistoryWithConfirmationAsync(Func<string, bool> confirm)
    {
        if (loading || busy || cleaningHistory || exportingLog || preview) return;
        var pending = VisitRunSelection.Pending(runs).Count;
        var message = $"Apagar todo o histórico deste computador?\n\n{runs.Count} visita(s), incluindo {pending} pendente(s), serão removidas de todas as unidades. Os logs e capturas das visitas também serão apagados.\n\nAs visitas pendentes não poderão mais ser retomadas pelo Vistora. Os chamados no Jira, perfis, configurações e login do Edge serão preservados.\n\nEsta ação não pode ser desfeita.";
        if (!confirm(message)) return;
        cleaningHistory = true; UpdateInteraction();
        HistoryStatus.Text = "Limpando histórico e pendências…";
        HistoryStatus.Foreground = (Brush)FindResource("Muted");
        Exception? failure = null;
        try { activeTask = store.ClearHistoryAsync(); await activeTask; }
        catch (Exception ex)
        {
            failure = ex;
            diagnostics.AppEvent("history.clear_failed", "A limpeza do histórico não pôde ser concluída.", DiagnosticLevel.Error, ex);
        }
        finally
        {
            try { runs = await store.LoadRunsAsync(); }
            catch (Exception ex)
            {
                failure ??= ex;
                runs.RemoveAll(run => !File.Exists(Path.Combine(store.Root, "runs", run.Id + ".json")));
            }
            RefreshProfiles(profiles.ActiveProfileId); RefreshHistory();
            activeTask = null; cleaningHistory = false; UpdateInteraction();
        }
        HistoryStatus.Foreground = (Brush)FindResource(failure is null ? "Teal" : "Red");
        HistoryStatus.Text = failure is null
            ? "Histórico, pendências, logs e capturas das visitas removidos."
            : "A limpeza ficou incompleta. Os registros restantes foram mantidos. Tente novamente. " + failure.Message;
        if (failure is null) diagnostics.AppEvent("history.cleared", "Histórico e diagnósticos das visitas removidos.");
    }
    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (busy || cleaningHistory) return;
        var copy = Serialization.Copy(settings);
        copy.JiraUrl = JiraUrlBox.Text.Trim().TrimEnd('/'); copy.PortalUrl = PortalUrlBox.Text.Trim(); copy.QueueUrl = QueueUrlBox.Text.Trim();
        copy.Diagnostics ??= new DiagnosticOptions();
        copy.Diagnostics.MinimumLevel = DiagnosticLevel.Debug;
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
    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { diagnostics.AppEvent("ui.operation_cancelled", "Operação interrompida."); }
        catch (Exception exception)
        {
            diagnostics.AppEvent("ui.operation_failed", "Uma operação da interface falhou.", DiagnosticLevel.Error, exception);
            MessageBox.Show(this, exception.Message, "Vistora", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if ((!busy && !cleaningHistory) || closing) return;
        e.Cancel = true; closing = true;
        cancellation?.Cancel();
        try { if (activeTask is not null) await activeTask; }
        catch (Exception ex) { diagnostics.AppEvent("app.shutdown_failed", "Falha ao aguardar a execução no encerramento.", DiagnosticLevel.Error, ex); }
        await diagnostics.FlushAsync();
        Close();
    }
}
