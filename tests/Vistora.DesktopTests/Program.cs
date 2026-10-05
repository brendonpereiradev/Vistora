using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vistora.Core;
using Vistora.Desktop;
using Vistora.Infrastructure;

internal static class Program
{
    private static string? screenshotDirectory;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        if (args.Length == 2 && args[0] == "--screenshots") screenshotDirectory = Path.GetFullPath(args[1]);
        var tests = new (string Name, Action Test)[]
        {
            ("Janela abre dentro da tela com controles de mover e redimensionar", StartupWindowFitsScreen),
            ("Abertura respeita a escala e permite aumentar o tamanho depois", StartupSizingAcrossScales),
            ("Telas removem os avisos e ações solicitados", RemovedElements),
            ("Várias visitas exigem escolha na tela principal", AmbiguousVisits),
            ("Perfil excluído continua permitindo retomada", DeletedProfile),
            ("Retomada preserva os dados originais e prioriza progresso", OriginalData),
            ("Trocar perfil e bloquear a tela preservam a seleção", ProfileAndBusy),
            ("Conclusão remove a pendência e permite nova visita", CompletedVisit),
            ("Selecionar o histórico não altera a retomada", HistoryIsReadOnly),
            ("Cancelar a limpeza preserva dados e a confirmação informa seu alcance", CancelHistoryCleanup),
            ("Limpeza esvazia histórico e pendências e restaura o perfil", ClearHistory),
            ("Limpeza respeita carregamento, execução, exportação e prévia", CleanupGuards),
            ("Falha parcial de limpeza mantém registros restantes na tela", PartialHistoryCleanup)
        };
        var failed = 0;
        foreach (var (name, test) in tests)
            try { test(); Console.WriteLine($"PASS: {name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL: {name}: {ex}"); }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} verificações da interface aprovadas.");
        app.Shutdown();
        return failed == 0 ? 0 : 1;
    }

    private static VisitProfile Profile(string name = "Unidade A") => new()
    {
        Name = name, Unit = name, ReporterName = "Solicitante de exemplo", ReporterEmail = "pessoa@example.com",
        FullName = "Pessoa Exemplo", Extension = "123", Floors = [new Floor { Name = "Pavimento 1", Resolution = "Equipamentos conferidos." }]
    };
    private static VisitRun Run(VisitProfile profile, string key = "SD-1001")
    {
        var run = ProfileValidation.CreateRun(profile, new AppSettings());
        run.Floors[0].IssueKey = key; run.Floors[0].Stage = FloorStage.Created; run.Message = "Chamado aberto";
        return run;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T Control<T>(Window window, string name) where T : class => (T)window.FindName(name);
    private static void StartupWindowFitsScreen()
    {
        using var fixture = new Fixture([Profile()], []);
        var window = fixture.Window;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        Check(GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) && GetWindowRect(handle, out var bounds), "Não foi possível conferir a área útil do monitor.");
        Check(GetWindowRect(handle, out bounds) && bounds.Left >= monitor.Work.Left && bounds.Top >= monitor.Work.Top &&
            bounds.Right <= monitor.Work.Right && bounds.Bottom <= monitor.Work.Bottom,
            $"A janela ultrapassa a área útil: ({bounds.Left}, {bounds.Top}, {bounds.Right}, {bounds.Bottom}).");
        var style = GetWindowLong(handle, -16);
        const int controls = 0x00C00000 | 0x00080000 | 0x00040000 | 0x00020000 | 0x00010000;
        Check((style & controls) == controls && window.Icon is not null, "Barra de título, ícone ou controles de janela ausentes.");
        var original = bounds;
        window.Left += 8; window.Top += 8;
        window.Width -= 12;
        window.UpdateLayout();
        Check(GetWindowRect(handle, out bounds) && bounds.Left > original.Left && bounds.Top > original.Top &&
            bounds.Right - bounds.Left < original.Right - original.Left, "A janela não permite movimentação e redução de tamanho.");
        window.Width += 24; window.UpdateLayout();
        Check(GetWindowRect(handle, out bounds) && bounds.Right - bounds.Left > original.Right - original.Left,
            "A janela não permite aumentar o tamanho.");
        var editor = new ProfileWindow(Profile()) { Owner = window, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            editor.Show(); editor.UpdateLayout();
            var editorHandle = new WindowInteropHelper(editor).Handle;
            Check(GetMonitorInfo(MonitorFromWindow(editorHandle, 2), ref monitor) && GetWindowRect(editorHandle, out var editorBounds) &&
                editorBounds.Left >= monitor.Work.Left && editorBounds.Top >= monitor.Work.Top &&
                editorBounds.Right <= monitor.Work.Right && editorBounds.Bottom <= monitor.Work.Bottom && editor.Icon is not null,
                "A janela de perfil abre com a barra de título fora da tela.");
        }
        finally { editor.Close(); }
    }
    private static void StartupSizingAcrossScales()
    {
        var placement = typeof(MainWindow).Assembly.GetType("Vistora.Desktop.WindowPlacement");
        var fit = placement?.GetMethod("FitToWorkArea", BindingFlags.Static | BindingFlags.NonPublic);
        Check(fit is not null, "Ajuste da janela à área útil não implementado.");
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            using var fixture = new Fixture([Profile()], []);
            var window = fixture.Window;
            var work = new Rect(-1920 / scale, -80 / scale, 1920 / scale, 1040 / scale);
            fit!.Invoke(null, [window, work]);
            Check(work.Contains(new Rect(window.Left, window.Top, window.Width, window.Height)),
                $"Abertura fora da área útil em {scale * 100}%.");
            Check(window.MinWidth <= window.Width && window.MinHeight <= window.Height,
                $"Tamanho mínimo impede a janela de caber em {scale * 100}%.");
            var width = window.Width; var height = window.Height;
            window.Width += 24; window.Height += 24;
            Check(window.Width == width + 24 && window.Height == height + 24 &&
                double.IsPositiveInfinity(window.MaxWidth) && double.IsPositiveInfinity(window.MaxHeight),
                "O ajuste inicial criou uma restrição permanente de tamanho.");
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
    private static object? Invoke(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, PrivateInstance)!.Invoke(window, args);
    private static object Choice(MainWindow window, VisitRun run) => Control<ComboBox>(window, "PendingVisitPicker").Items.Cast<object>()
        .Single(choice => ReferenceEquals(choice.GetType().GetProperty("Run")!.GetValue(choice), run));
    private static VisitRun? SelectedRun(MainWindow window)
    {
        var choice = Control<ComboBox>(window, "PendingVisitPicker").SelectedItem;
        return choice?.GetType().GetProperty("Run")!.GetValue(choice) as VisitRun;
    }

    private static void RemovedElements()
    {
        var profile = Profile();
        using var fixture = new Fixture([profile], []); var window = fixture.Window;
        foreach (var name in new[] { "DuplicateButton", "ResumeButton", "LinkButton", "ClosingTeamSummary", "EditActiveButton", "ProfileSummary", "ResolutionPreview", "TimeoutBox", "DetailedLogsBox" })
            Check(window.FindName(name) is null, $"Controle removido ainda existe: {name}.");
        var texts = Descendants(window).OfType<TextBlock>().Select(text => text.Text).ToList();
        Check(!texts.Any(text => text.Contains("Microsoft Edge") || text.Contains("salvos neste") || text.Contains("Equipe de fechamento:") || text.Contains("Sobre o login") || text.Contains("Texto de fechamento") || text.Contains("Tempo de espera") || text.Contains("Ative os detalhes")), "Aviso removido continua na interface.");
        var buttons = Descendants(window).OfType<Button>().Select(button => button.Content as string).ToList();
        Check(!buttons.Intersect(new[] { "Duplicar", "Retomar execução", "Vincular chamado", "Abrir chamado no Jira", "Abrir diagnóstico", "Editar perfil", "Abrir pasta de dados", "Exportar log do aplicativo" }).Any(), "Ação removida continua na interface.");
        var pages = Control<TabControl>(window, "Pages").Items.Cast<TabItem>().ToList();
        Check(buttons.Count(text => text == "Exportar log") == 1 && Descendants(pages[2]).OfType<Button>().Any(button => button.Content.Equals("Exportar log")), "Exportação de logs não ficou somente no Histórico.");
        Check(Descendants(pages[3]).OfType<Button>().Single().Content.Equals("Salvar configurações"), "Configurações contém ações removidas.");
        Screenshot(window, "executar");
        foreach (var (page, name) in new[] { (1, "perfis"), (2, "historico"), (3, "configuracoes") })
        { Invoke(window, "SelectPage", page); Screenshot(window, name); }
        var editor = new ProfileWindow(profile);
        Check(!Descendants(editor).OfType<TextBlock>().Any(text => text.Text.Contains("salvos neste computador")), "Aviso continua no editor de perfil.");
        Screenshot(editor, "editar-perfil"); editor.Close();
    }

    private static void AmbiguousVisits()
    {
        var profile = Profile(); var first = Run(profile); var second = Run(profile, "SD-1002");
        using var fixture = new Fixture([profile], [first, second]); var window = fixture.Window;
        Check(SelectedRun(window) is null && !Control<Button>(window, "ExecuteButton").IsEnabled, "Escolheu uma visita ambígua automaticamente.");
        Screenshot(window, "pendencias");
        Control<ComboBox>(window, "PendingVisitPicker").SelectedItem = Choice(window, second);
        Check(SelectedRun(window) == second && Control<Button>(window, "ExecuteButton").IsEnabled, "Seleção explícita não habilitou a retomada.");
        Check(VisitRunSelection.ForResume([first, second], SelectedRun(window)!) == second, "A seleção foi substituída.");
        Screenshot(window, "retomar");
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        Screenshot(window, "retomar-tamanho-minimo");
    }

    private static void DeletedProfile()
    {
        var run = Run(Profile("Unidade excluída")); var before = JsonSerializer.Serialize(run, Serialization.Options);
        using var fixture = new Fixture([], [run]); var window = fixture.Window;
        var choice = Choice(window, run);
        Check(choice.GetType().GetProperty("Label")!.GetValue(choice)!.ToString()!.Contains("perfil excluído"), "Perfil excluído não foi identificado.");
        Control<ComboBox>(window, "PendingVisitPicker").SelectedItem = choice;
        Check(Control<ComboBox>(window, "ProfilePicker").SelectedItem is null && Control<Button>(window, "ExecuteButton").IsEnabled, "Retomada exigiu um perfil cadastrado.");
        Check(Control<DataGridColumn>(window, "VisitColumn").Visibility == Visibility.Collapsed, "Dados originais permitiram edição.");
        Check(before == JsonSerializer.Serialize(run, Serialization.Options), "Seleção alterou a execução salva.");
        Screenshot(window, "perfil-excluido");
    }

    private static void OriginalData()
    {
        var profile = Profile(); var run = Run(profile);
        var empty = ProfileValidation.CreateRun(profile, new AppSettings()); empty.CreatedAt = run.CreatedAt.AddHours(1);
        var before = JsonSerializer.Serialize(run, Serialization.Options);
        profile.Unit = "Unidade alterada"; profile.Floors[0].Resolution = "Texto alterado";
        using var fixture = new Fixture([profile], [empty, run]); var window = fixture.Window;
        Check(SelectedRun(window) == run, "Uma tentativa vazia ocultou a visita com chamados.");
        Check(before == JsonSerializer.Serialize(run, Serialization.Options), "Seleção alterou os dados originais da retomada.");
        Check(ReferenceEquals(Control<DataGrid>(window, "FloorGrid").Items[0], run.Floors[0].Floor), "Mostrou pavimentos de outra visita.");
    }

    private static void ProfileAndBusy()
    {
        var first = Profile(); var second = Profile("Unidade B"); var run = Run(first);
        using var fixture = new Fixture([first, second], [run]); var window = fixture.Window;
        Control<ComboBox>(window, "ProfilePicker").SelectedItem = second;
        Check(SelectedRun(window) is null && Control<Button>(window, "ExecuteButton").Content.Equals("Executar visita preventiva"), "Trocar de perfil manteve uma visita de outra unidade selecionada.");
        Check(Control<StackPanel>(window, "PendingVisitPanel").Visibility == Visibility.Visible, "Pendências de outros perfis ficaram inacessíveis.");
        Control<ComboBox>(window, "PendingVisitPicker").SelectedItem = Choice(window, run);
        Check(Control<ComboBox>(window, "ProfilePicker").SelectedItem == first, "Retomada não identificou o perfil selecionado.");
        Invoke(window, "SetBusy", true);
        Check(!Control<ComboBox>(window, "PendingVisitPicker").IsEnabled && !Control<Button>(window, "ExecuteButton").IsEnabled, "Execução permitiu trocar a visita em andamento.");
        Check(!Control<Button>(window, "ConnectButton").IsEnabled && Control<Button>(window, "StopButton").IsEnabled && Control<Button>(window, "StopButton").Visibility == Visibility.Visible, "O botão de parar ficou indisponível junto com as outras ações durante a execução.");
        Screenshot(window, "executando");
        Invoke(window, "SetBusy", false);
        Check(Control<Button>(window, "StopButton").Visibility == Visibility.Collapsed && !Control<Button>(window, "StopButton").IsEnabled, "O botão de parar permaneceu disponível depois da execução.");
        Check(SelectedRun(window) == run && Control<Button>(window, "ExecuteButton").IsEnabled && Control<DataGridColumn>(window, "VisitColumn").Visibility == Visibility.Collapsed, "Desbloquear a tela perdeu a retomada ou permitiu editar seus dados.");
    }

    private static void CompletedVisit()
    {
        var profile = Profile(); var run = Run(profile);
        using var fixture = new Fixture([profile], [run]); var window = fixture.Window;
        run.State = RunState.Completed; Invoke(window, "RefreshPendingVisits", null, false);
        Check(SelectedRun(window) is null && Control<StackPanel>(window, "PendingVisitPanel").Visibility == Visibility.Collapsed, "Visita concluída continuou disponível para retomada.");
        Check(Control<Button>(window, "ExecuteButton").IsEnabled, "Conclusão impediu uma nova visita.");
        Check(Control<DataGridColumn>(window, "VisitColumn").Visibility == Visibility.Visible, "Nova visita não permitiu selecionar pavimentos.");
    }

    private static void HistoryIsReadOnly()
    {
        var profile = Profile(); var first = Run(profile); var second = Run(profile, "SD-1002");
        using var fixture = new Fixture([profile], [first, second]); var window = fixture.Window;
        Control<ComboBox>(window, "PendingVisitPicker").SelectedItem = Choice(window, first);
        var before = JsonSerializer.Serialize(new[] { first, second }, Serialization.Options);
        Invoke(window, "SelectPage", 2); Control<DataGrid>(window, "HistoryGrid").SelectedItem = second;
        var history = (TabItem)Control<TabControl>(window, "Pages").Items[2];
        Check(Descendants(history).OfType<Button>().Select(button => button.Content).SequenceEqual(new[] { "Exportar log", "Limpar histórico e pendências" }), "Histórico contém ações inesperadas.");
        Check(SelectedRun(window) == first && before == JsonSerializer.Serialize(new[] { first, second }, Serialization.Options), "Consulta ao histórico alterou a retomada.");
        Screenshot(window, "historico-com-visitas");
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        Screenshot(window, "historico-tamanho-minimo");
    }

    private static void SetField(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
    private static void Clean(MainWindow window, Func<string, bool> confirm) => Await((Task)Invoke(window, "ClearHistoryWithConfirmationAsync", confirm)!);
    private static LocalStore Store(MainWindow window) => (LocalStore)typeof(MainWindow).GetField("store", PrivateInstance)!.GetValue(window)!;

    private static void CancelHistoryCleanup()
    {
        var profile = Profile(); var run = Run(profile);
        using var fixture = new Fixture([profile], [run], allowCleanup: true); var window = fixture.Window; var store = Store(window);
        Await(store.SaveRunAsync(run)); var before = File.ReadAllBytes(Path.Combine(store.Root, "runs", run.Id + ".json"));
        string? message = null; Clean(window, text => { message = text; return false; });
        Check(message is not null && message.Contains("1 visita(s)") && message.Contains("1 pendente(s)") && message.Contains("logs e capturas") && message.Contains("Jira"), "Confirmação omitiu o alcance da exclusão.");
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(store.Root, "runs", run.Id + ".json"))) && SelectedRun(window) == run, "Cancelar alterou os dados.");
        var dialog = new HistoryClearWindow(message!); var buttons = Descendants(dialog).OfType<Button>().ToArray();
        Check(buttons.Single(b => b.IsDefault).Content.Equals("Cancelar") && buttons.Single(b => b.IsCancel).Content.Equals("Cancelar"), "Confirmação não usa Cancelar por padrão.");
        Screenshot(dialog, "confirmar-limpeza");
        dialog.Close();
    }

    private static void ClearHistory()
    {
        var profile = Profile(); var deleted = Run(Profile("Perfil excluído")); var completed = Run(profile); completed.State = RunState.Completed;
        using var fixture = new Fixture([profile], [deleted, completed], allowCleanup: true); var window = fixture.Window; var store = Store(window);
        Await(store.SaveRunAsync(deleted)); Await(store.SaveRunAsync(completed));
        Control<ComboBox>(window, "PendingVisitPicker").SelectedItem = Choice(window, deleted);
        Clean(window, _ => true);
        Check(Control<DataGrid>(window, "HistoryGrid").Items.Count == 0 && Control<DataGrid>(window, "RunFloorGrid").Items.Count == 0 && SelectedRun(window) is null, "Limpeza deixou dados nas tabelas.");
        Check(Control<StackPanel>(window, "PendingVisitPanel").Visibility == Visibility.Collapsed && Control<ProgressBar>(window, "RunProgress").Value == 0, "Limpeza deixou pendências ou progresso.");
        Check(Control<ComboBox>(window, "ProfilePicker").SelectedItem == profile && Control<Button>(window, "ExecuteButton").IsEnabled && Control<Button>(window, "ExecuteButton").Content.Equals("Executar visita preventiva"), "Limpeza não restaurou o perfil e a nova execução.");
        Check(Control<Button>(window, "ClearHistoryButton").IsEnabled && Control<TextBlock>(window, "HistoryStatus").Text.Contains("removidos"), "Limpeza não informou sucesso ou liberou a tela.");
    }

    private static void CleanupGuards()
    {
        var profile = Profile(); var run = Run(profile); var confirmations = 0;
        using var fixture = new Fixture([profile], [run], allowCleanup: true); var window = fixture.Window;
        foreach (var field in new[] { "loading", "cleaningHistory", "exportingLog", "preview" })
        {
            SetField(window, field, true); Invoke(window, "UpdateInteraction");
            Check(!Control<Button>(window, "ClearHistoryButton").IsEnabled, "Botão de limpeza habilitado durante " + field);
            Clean(window, _ => { confirmations++; return true; });
            if (field == "cleaningHistory")
                Check(!Control<Button>(window, "ExecuteButton").IsEnabled && !Control<Button>(window, "ExportLogButton").IsEnabled && Control<Button>(window, "StopButton").Visibility == Visibility.Collapsed, "Limpeza não bloqueou ações ou exibiu Parar.");
            SetField(window, field, false);
        }
        Invoke(window, "SetBusy", true); Clean(window, _ => { confirmations++; return true; });
        Check(!Control<Button>(window, "ClearHistoryButton").IsEnabled && confirmations == 0, "Limpeza aceitou uma operação bloqueada.");
        Invoke(window, "SetBusy", false);
        Check(Control<Button>(window, "ClearHistoryButton").IsEnabled && SelectedRun(window) == run, "Bloqueio alterou a seleção ou não liberou a limpeza.");
    }

    private static void PartialHistoryCleanup()
    {
        var profile = Profile(); var first = Run(profile); first.Id = "00000000000000000000000000000001";
        var second = Run(profile, "SD-1002"); second.Id = "00000000000000000000000000000002";
        using var fixture = new Fixture([profile], [first, second], allowCleanup: true); var window = fixture.Window; var store = Store(window);
        Await(store.SaveRunAsync(first)); Await(store.SaveRunAsync(second));
        using (var locked = File.Open(Path.Combine(store.Root, "runs", second.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Clean(window, _ => true);
            var remaining = Control<DataGrid>(window, "HistoryGrid").Items.Cast<VisitRun>().ToArray();
            Check(Control<TextBlock>(window, "HistoryStatus").Text.Contains("incompleta") && remaining.Length == 1 && remaining[0].Id == second.Id, "Falha parcial declarou sucesso ou mostrou registros incorretos.");
        }
        Clean(window, _ => true);
        Check(Control<DataGrid>(window, "HistoryGrid").Items.Count == 0, "Falha parcial impediu nova tentativa.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void Screenshot(Window window, string name)
    {
        if (screenshotDirectory is null) return;
        Directory.CreateDirectory(screenshotDirectory);
        if (!window.IsVisible)
        {
            ProfileDocument? profiles = null; List<VisitRun>? runs = null;
            var pendingId = window is MainWindow main ? SelectedRun(main)?.Id : null;
            if (window is MainWindow)
            {
                var original = (ProfileDocument)typeof(MainWindow).GetField("profiles", PrivateInstance)!.GetValue(window)!;
                profiles = new ProfileDocument { Profiles = original.Profiles.ToList(), ActiveProfileId = original.ActiveProfileId };
                runs = ((List<VisitRun>)typeof(MainWindow).GetField("runs", PrivateInstance)!.GetValue(window)!).ToList();
            }
            window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000; window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (window is MainWindow shown)
            {
                typeof(MainWindow).GetField("profiles", PrivateInstance)!.SetValue(shown, profiles);
                typeof(MainWindow).GetField("runs", PrivateInstance)!.SetValue(shown, runs);
                Invoke(shown, "RefreshProfiles", profiles!.ActiveProfileId);
                Invoke(shown, "RefreshPendingVisits", pendingId, false);
                Invoke(shown, "RefreshHistory", (object?)null);
            }
        }
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(screenshotDirectory, name + ".png")); encoder.Save(file);
    }
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private sealed class Fixture : IDisposable
    {
        public MainWindow Window { get; }
        public Fixture(List<VisitProfile> profiles, List<VisitRun> runs, bool allowCleanup = false)
        {
            var diagnostics = allowCleanup ? new DiagnosticService(Path.Combine(Path.GetTempPath(), "VistoraDesktopTests", Guid.NewGuid().ToString("N"))) : null;
            Window = new MainWindow(preview: !allowCleanup, diagnostics: diagnostics);
            if (allowCleanup) SetField(Window, "loading", false);
            typeof(MainWindow).GetField("profiles", PrivateInstance)!.SetValue(Window, new ProfileDocument { Profiles = profiles, ActiveProfileId = profiles.FirstOrDefault()?.Id });
            typeof(MainWindow).GetField("runs", PrivateInstance)!.SetValue(Window, runs);
            Invoke(Window, "RefreshProfiles", (object?)null); Invoke(Window, "RefreshHistory", (object?)null);
            Invoke(Window, "UpdateInteraction");
        }
        public void Dispose()
        {
            Window.Close();
            Await(((DiagnosticService)typeof(MainWindow).GetField("diagnostics", PrivateInstance)!.GetValue(Window)!).DisposeAsync().AsTask());
        }
    }
}
