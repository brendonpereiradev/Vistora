using System.Text.Json;
using Vistora.Core;
using Vistora.Infrastructure;

var tests = new (string Name, Func<Task> Test)[]
{
    ("Seis pavimentos conservam seus campos e textos", CompleteRun),
    ("O registro usa uma cópia dos dados do perfil", SnapshotIsolation),
    ("Uma abertura incerta nunca é reenviada na retomada", UncertainCreate),
    ("Um fechamento aceito é conferido sem novo comentário", UncertainClose),
    ("Cancelamento preserva o chamado já enviado", CancelAfterCreate),
    ("Resolução divergente impede a conclusão", WrongResolution),
    ("A vinculação confere o pavimento antes de salvar", LinkReconciliation),
    ("Uma execução concluída permanece concluída", CompletedGuard),
    ("Duas execuções simultâneas são bloqueadas", ConcurrentGuard),
    ("Validação exige dados e pavimentos corretos", ValidateProfile),
    ("Perfis e histórico persistem sem mistura", LocalPersistence),
    ("Arquivo corrompido recupera a cópia anterior", BackupRecovery),
    ("Versões de dados desconhecidas são preservadas", UnknownSchema)
    ,("Falha antes do envio do fechamento permite retomada", PrepareCloseFailure),
    ("Executar e retomar priorizam os seis chamados sobre tentativas vazias", ResumeSelection),
    ("Visitas de outros perfis e seleções ambíguas não são misturadas", SelectionGuards),
    ("Abertura incerta em qualquer pavimento impede outras criações", UncertainPreflight),
    ("Retomada de etapas diferentes pula todas as ações já concluídas", ResumeStages),
    ("A lista de pendências inclui perfis excluídos e preserva a seleção explícita", PendingVisits),
    ("Retomada confere todas as aberturas incertas antes de continuar", ResumeWithReconciliation),
    ("Cancelar a conferência mantém os vínculos salvos sem abrir chamados", CancelResumeReconciliation),
    ("Chamado divergente impede a continuação da retomada", WrongResumeIssue),
    ("Cancelar após informar o número impede a vinculação e a retomada", CancelResumeToken)
    ,("Fechamento incerto só é repetido após conferência explícita", RetryUnconfirmedClose),
    ("Conferência recusada ou cancelada preserva o fechamento pendente", DeclineCloseRetry),
    ("Status ou responsável divergente impede autorizar novo fechamento", UnsafeCloseRetry)
}.Concat(DiagnosticTests.Cases).Concat(HistoryCleanupTests.Cases).ToArray();
var failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL: {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} verificações aprovadas.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static VisitProfile Profile(int count = 2) => new()
{
    Name = "Unidade teste", ReporterName = "Pessoa Teste", ReporterEmail = "pessoa@example.com", FullName = "Pessoa Teste",
    Extension = "123", Unit = "Unidade A", Floors = Enumerable.Range(0, count).Select(i => new Floor
    { Name = $"Pavimento {i}", Sector = $"Setor {i}", Room = $"Sala {i}", Resolution = $"Visita {i}\nEquipamento {i}: OK" }).ToList()
};
static VisitRun Run(int count = 2) => ProfileValidation.CreateRun(Profile(count), new AppSettings());
static async Task CompleteRun()
{
    var run = Run(6); var memory = new MemoryStore(); var fake = new FakeJira();
    await new ExecutionEngine(memory, () => fake).ExecuteAsync(run, default);
    Check(run.State == RunState.Completed, run.Message);
    Check(fake.Creates == 6 && fake.Closes == 6, "Quantidade incorreta.");
    Check(fake.Issues.Values.All(i => i.Closed && i.Floor.Resolution == i.Comment), "Texto errado.");
    Check(memory.Saves.Any(r => r.Floors.Any(f => f.PendingAction == PendingAction.Create)), "Intenção de abertura ausente.");
    var firstClose = fake.Actions.FindIndex(a => a.StartsWith("close:"));
    Check(fake.Actions.Take(firstClose).Count(a => a.StartsWith("start:")) == 6, "Fechamento antecipado.");
}
static Task SnapshotIsolation()
{
    var profile = Profile(); var run = ProfileValidation.CreateRun(profile, new AppSettings());
    profile.Floors.Reverse(); profile.Floors[0].Resolution = "Mudou"; profile.Unit = "Outra unidade";
    Check(run.Profile.Unit == "Unidade A" && run.Floors[0].Floor.Resolution.StartsWith("Visita 0"), "Dados alteraram a execução.");
    return Task.CompletedTask;
}
static async Task UncertainCreate()
{
    var run = Run(1); var fake = new FakeJira { FailAfterCreate = true }; var engine = new ExecutionEngine(new MemoryStore(), () => fake);
    await engine.ExecuteAsync(run, default);
    Check(run.State == RunState.NeedsReconciliation && fake.Creates == 1, "Abertura incerta não registrada.");
    fake.FailAfterCreate = false;
    await engine.ExecuteAsync(run, default);
    Check(fake.Creates == 1 && run.State == RunState.NeedsReconciliation, "Abertura foi repetida.");
}
static async Task UncertainClose()
{
    var run = Run(2); var fake = new FakeJira { FailAfterClose = true }; var engine = new ExecutionEngine(new MemoryStore(), () => fake);
    await engine.ExecuteAsync(run, default);
    Check(fake.Closes == 1 && run.State != RunState.Completed, "Falha não registrada.");
    fake.FailAfterClose = false;
    await engine.ExecuteAsync(run, default);
    Check(run.State == RunState.Completed && fake.Closes == 2, "Fechamento foi reenviado.");
}
static (VisitRun Run, FakeJira Jira) PendingCloseRun()
{
    var run = Run(1);
    var floor = run.Floors[0]; floor.IssueKey = "SD-5000"; floor.Stage = FloorStage.Started; floor.PendingAction = PendingAction.Close;
    run.State = RunState.NeedsReconciliation;
    var fake = new FakeJira(); fake.Issues.Add(floor.IssueKey, new(floor.Floor) { Assigned = true, Started = true });
    return (run, fake);
}
static async Task RetryUnconfirmedClose()
{
    var (run, fake) = PendingCloseRun(); var memory = new MemoryStore(); var engine = new ExecutionEngine(memory, () => fake);
    await engine.ResumeAsync(run, _ => throw new Exception("Solicitou abertura."), default);
    Check(run.State == RunState.NeedsReconciliation && fake.Closes == 0, "Repetiu fechamento sem conferência.");
    var confirmations = 0;
    await engine.ResumeAsync(run, _ => throw new Exception("Solicitou abertura."), default, (floor, snapshot) =>
    {
        confirmations++;
        Check(floor.IssueKey == "SD-5000" && snapshot.Started && snapshot.AssignedToCurrentUser, "Conferiu outro chamado ou estado.");
        return Task.FromResult(true);
    });
    Check(run.State == RunState.Completed && confirmations == 1 && fake.Creates == 0 && fake.Closes == 1, run.Message);
    Check(memory.Saves.Any(saved => saved.Message.Contains("autorizado após conferência") && saved.Floors[0].PendingAction == PendingAction.None), "Autorização não foi salva.");
}
static async Task DeclineCloseRetry()
{
    foreach (var cancel in new[] { false, true })
    {
        var (run, fake) = PendingCloseRun(); using var cancellation = new CancellationTokenSource();
        await new ExecutionEngine(new MemoryStore(), () => fake).ResumeAsync(run, _ => Task.FromResult<string?>(null), cancellation.Token, (_, _) =>
        {
            if (cancel) cancellation.Cancel();
            return Task.FromResult(cancel);
        });
        Check(fake.Closes == 0 && run.Floors[0].PendingAction == PendingAction.Close &&
            run.State == (cancel ? RunState.Interrupted : RunState.NeedsReconciliation), "Perdeu a pendência ou enviou após recusa/cancelamento.");
    }
}
static async Task UnsafeCloseRetry()
{
    foreach (var unsafeState in new[] { "status", "assignee", "previously_closed" })
    {
        var (run, fake) = PendingCloseRun(); var confirmations = 0;
        if (unsafeState == "status") fake.Issues["SD-5000"].Started = false;
        if (unsafeState == "assignee") fake.Issues["SD-5000"].Assigned = false;
        if (unsafeState == "previously_closed") run.Floors[0].Stage = FloorStage.Closed;
        await new ExecutionEngine(new MemoryStore(), () => fake).ResumeAsync(run, _ => Task.FromResult<string?>(null), default, (_, _) =>
        { confirmations++; return Task.FromResult(true); });
        Check(confirmations == 0 && fake.Closes == 0 && run.State == RunState.NeedsReconciliation, "Liberou fechamento com estado divergente.");
    }
}
static async Task CancelAfterCreate()
{
    using var cts = new CancellationTokenSource(); var run = Run(3); var fake = new FakeJira { AfterCreate = cts.Cancel };
    await new ExecutionEngine(new MemoryStore(), () => fake).ExecuteAsync(run, cts.Token);
    Check(run.State == RunState.Interrupted && fake.Creates == 1 && run.Floors[0].IssueKey is not null, "Progresso perdido.");
}
static async Task WrongResolution()
{
    var run = Run(1); var fake = new FakeJira { WrongResolution = true };
    await new ExecutionEngine(new MemoryStore(), () => fake).ExecuteAsync(run, default);
    Check(run.State == RunState.NeedsReconciliation && run.Floors[0].PendingAction == PendingAction.Close, "Fechamento divergente aceito.");
}
static async Task LinkReconciliation()
{
    var run = Run(2); var fake = new FakeJira { FailAfterCreate = true }; var engine = new ExecutionEngine(new MemoryStore(), () => fake);
    await engine.ExecuteAsync(run, default);
    await engine.AttachIssueAsync(run, run.Floors[0], "SD-1001", default);
    Check(run.Floors[0].IssueKey == "SD-1001", "Vínculo não salvo.");
    run.Floors[1].PendingAction = PendingAction.Create;
    try { await engine.AttachIssueAsync(run, run.Floors[1], "SD-1001", default); throw new Exception("Duplicado aceito."); }
    catch (InvalidOperationException) { }
    fake.FailAfterCreate = false;
    run.Floors[1].PendingAction = PendingAction.None;
    await engine.ExecuteAsync(run, default);
    Check(run.State == RunState.Completed && fake.Creates == 2, run.Message);
}
static async Task CompletedGuard()
{
    var run = Run(1); var fake = new FakeJira(); var engine = new ExecutionEngine(new MemoryStore(), () => fake);
    await engine.ExecuteAsync(run, default);
    try { await engine.ExecuteAsync(run, default); throw new Exception("Execução concluída foi repetida."); } catch (InvalidOperationException) { }
    Check(run.State == RunState.Completed && fake.Closes == 1, "Histórico concluído alterado.");
}
static async Task ConcurrentGuard()
{
    var fake = new FakeJira { ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    var engine = new ExecutionEngine(new MemoryStore(), () => fake);
    var task = engine.ExecuteAsync(Run(1), default);
    try { await engine.ExecuteAsync(Run(1), default); throw new Exception("Execução concorrente aceita."); } catch (InvalidOperationException) { }
    fake.ConnectGate.SetResult(); await task;
}
static Task ValidateProfile()
{
    var profile = Profile(); profile.Floors[0].Selected = false; profile.Floors[1].Selected = false;
    Check(ProfileValidation.Validate(profile, new()).Any(e => e.Contains("Selecione")), "Seleção vazia aceita.");
    profile.Floors[0].Selected = true; profile.Floors[0].Resolution = "";
    Check(ProfileValidation.Validate(profile, new()).Any(e => e.Contains("resolução")), "Texto vazio aceito.");
    profile.ReporterEmail = "inválido"; Check(ProfileValidation.Validate(profile, new()).Any(e => e.Contains("e-mail válido")), "E-mail inválido aceito.");
    var settings = new AppSettings { PortalUrl = "https://outro.example.com" };
    Check(ProfileValidation.Validate(Profile(), settings).Any(e => e.Contains("HTTPS")), "Outro domínio aceito.");
    return Task.CompletedTask;
}
static string TestDirectory() => Path.Combine(Path.GetTempPath(), "VistoraTests", Guid.NewGuid().ToString("N"));
static async Task LocalPersistence()
{
    var store = new LocalStore(TestDirectory()); var a = Profile(); var b = Profile(); b.Name = "Unidade B"; b.Unit = "Unidade B";
    await store.SaveProfilesAsync(new() { Profiles = [a, b], ActiveProfileId = b.Id });
    var read = await new LocalStore(store.Root).LoadProfilesAsync();
    Check(read.Profiles.Count == 2 && read.ActiveProfileId == b.Id && read.Profiles[0].Unit != read.Profiles[1].Unit, "Perfis misturados.");
    var run = Run(); run.Floors[0].IssueKey = "SD-1"; await store.SaveRunAsync(run);
    var loaded = (await store.LoadRunsAsync()).Single(); Check(loaded.Floors[0].IssueKey == "SD-1", "Chamado não persistido.");
}
static async Task BackupRecovery()
{
    var store = new LocalStore(TestDirectory()); var profile = Profile();
    await store.SaveProfilesAsync(new() { Profiles = [profile] }); profile.Name = "Alterado";
    await store.SaveProfilesAsync(new() { Profiles = [profile] });
    await File.WriteAllTextAsync(Path.Combine(store.Root, "profiles.json"), "{");
    var recovered = await store.LoadProfilesAsync(); Check(recovered.Profiles.Single().Name == "Unidade teste", "Backup não recuperado.");
}
static async Task UnknownSchema()
{
    var store = new LocalStore(TestDirectory()); await store.SaveProfilesAsync(new() { Profiles = [Profile()] });
    await File.WriteAllTextAsync(Path.Combine(store.Root, "profiles.json"), "{\"SchemaVersion\":99}");
    try { await store.LoadProfilesAsync(); throw new Exception("Versão desconhecida aceita."); } catch (InvalidDataException) { }
    Check((await File.ReadAllTextAsync(Path.Combine(store.Root, "profiles.json"))).Contains("99"), "Arquivo sobrescrito.");
}
static async Task PrepareCloseFailure()
{
    var run = Run(1); var fake = new FakeJira { FailPrepareClose = true }; var engine = new ExecutionEngine(new MemoryStore(), () => fake);
    await engine.ExecuteAsync(run, default);
    Check(fake.Closes == 0 && run.Floors[0].PendingAction != PendingAction.Close, "Preparação foi tratada como envio.");
    fake.FailPrepareClose = false; await engine.ExecuteAsync(run, default);
    Check(run.State == RunState.Completed && fake.Closes == 1, run.Message);
}

static async Task ResumeSelection()
{
    using var cancellation = new CancellationTokenSource();
    var run = Run(6); var fake = new FakeJira(); var engine = new ExecutionEngine(new MemoryStore(), () => fake);
    fake.AfterCreate = () => { if (fake.Creates == 6) cancellation.Cancel(); };
    await engine.ExecuteAsync(run, cancellation.Token);
    Check(run.State == RunState.Interrupted && fake.Creates == 6, "Não preservou a criação inicial.");
    var keys = run.Floors.Select(f => f.IssueKey).ToArray();
    var empty = ProfileValidation.CreateRun(run.Profile, run.Settings); empty.CreatedAt = run.CreatedAt.AddMinutes(8);
    var older = ProfileValidation.CreateRun(run.Profile, run.Settings); older.CreatedAt = run.CreatedAt.AddMinutes(-8); older.Floors[0].PendingAction = PendingAction.Create;
    var history = new[] { empty, run, older };
    Check(ReferenceEquals(VisitRunSelection.PendingForProfile(history, run.Profile.Id), run), "Executar escolheu uma tentativa vazia.");
    Check(ReferenceEquals(VisitRunSelection.ForResume(history, empty), run), "Retomar escolheu uma tentativa vazia.");
    fake.AfterCreate = null;
    await engine.ExecuteAsync(Serialization.Copy(VisitRunSelection.ForResume(history, empty)), default);
    Check(fake.Creates == 6 && fake.Closes == 6 && fake.InitialIssueKey == keys[0], "A retomada gerou novas aberturas ou começou pelo formulário.");
    Check(run.Floors.Select(f => f.IssueKey).SequenceEqual(keys) && empty.Floors.All(f => f.IssueKey is null), "O histórico foi misturado.");
}

static Task SelectionGuards()
{
    var run = Run(1); run.Floors[0].IssueKey = "SD-1";
    var otherProfile = Run(1); otherProfile.Floors[0].IssueKey = "SD-2";
    Check(VisitRunSelection.PendingForProfile([run, otherProfile], run.Profile.Id) == run, "Outro perfil interferiu.");
    var another = ProfileValidation.CreateRun(run.Profile, run.Settings); another.Floors[0].IssueKey = "SD-3";
    try { VisitRunSelection.PendingForProfile([run, another], run.Profile.Id); throw new Exception("Visitas distintas foram escolhidas automaticamente."); }
    catch (InvalidOperationException) { }
    Check(VisitRunSelection.ForResume([run, another], another) == another, "Seleção explícita de uma visita foi ignorada.");
    run.State = RunState.Completed;
    Check(VisitRunSelection.PendingForProfile([run, otherProfile], run.Profile.Id) is null, "Visita concluída foi retomada.");
    try { VisitRunSelection.ForResume([run], run); throw new Exception("Concluída aceitou retomada."); } catch (InvalidOperationException) { }
    return Task.CompletedTask;
}

static async Task UncertainPreflight()
{
    var run = Run(3); run.Floors[2].PendingAction = PendingAction.Create;
    var fake = new FakeJira(); await new ExecutionEngine(new MemoryStore(), () => fake).ExecuteAsync(run, default);
    Check(run.State == RunState.NeedsReconciliation && fake.Creates == 0 && fake.Connections == 0, "Abriu outro pavimento antes de conferir a abertura incerta.");
}

static async Task ResumeStages()
{
    var run = Run(6); var fake = new FakeJira();
    for (var i = 0; i < run.Floors.Count; i++)
    {
        var floor = run.Floors[i]; floor.IssueKey = $"SD-{3000 + i}";
        floor.Stage = i == 0 ? FloorStage.Closed : i == 1 ? FloorStage.Started : i == 2 ? FloorStage.Assigned : FloorStage.Created;
        fake.Issues.Add(floor.IssueKey, new(floor.Floor) { Assigned = i < 3, Started = i < 2, Closed = i == 0, Comment = i == 0 ? floor.Floor.Resolution : "" });
    }
    await new ExecutionEngine(new MemoryStore(), () => fake).ExecuteAsync(run, default);
    Check(run.State == RunState.Completed && fake.Creates == 0 && fake.Closes == 5, run.Message);
    Check(fake.Actions.Count(a => a.StartsWith("assign:")) == 3 && fake.Actions.Count(a => a.StartsWith("start:")) == 4, "Repetiu atribuição ou início concluído.");
    Check(fake.InitialIssueKey == run.Floors[1].IssueKey, "Não abriu o primeiro pavimento ainda pendente.");
}

static Task PendingVisits()
{
    var first = Run(1); first.Floors[0].IssueKey = "SD-1";
    var second = ProfileValidation.CreateRun(first.Profile, first.Settings); second.Floors[0].PendingAction = PendingAction.Create;
    var empty = ProfileValidation.CreateRun(first.Profile, first.Settings); empty.CreatedAt = first.CreatedAt.AddHours(1);
    var deletedProfile = Run(1); deletedProfile.Floors[0].IssueKey = "SD-2";
    var completed = Run(1); completed.State = RunState.Completed;
    var history = new[] { empty, completed, deletedProfile, first, second };
    var pending = VisitRunSelection.Pending(history);
    Check(pending.Count == 4 && !pending.Contains(completed) && pending.Contains(deletedProfile), "Uma pendência ficou inacessível.");
    Check(pending.Last() == empty, "Tentativa vazia ocultou as visitas com progresso.");
    Check(VisitRunSelection.ForResume(history, second) == second, "A abertura incerta selecionada foi substituída.");
    Check(VisitRunSelection.ForResume(history, deletedProfile) == deletedProfile, "A retomada dependeu do cadastro do perfil.");
    return Task.CompletedTask;
}

static async Task ResumeWithReconciliation()
{
    var run = Run(2); var fake = new FakeJira(); var memory = new MemoryStore();
    foreach (var (floor, index) in run.Floors.Select((floor, index) => (floor, index)))
    {
        floor.PendingAction = PendingAction.Create;
        fake.Issues.Add($"SD-{5000 + index}", new(floor.Floor));
    }
    var requested = 0;
    var resumed = await new ExecutionEngine(memory, () => fake).ResumeAsync(run, floor =>
    {
        Check(fake.Creates == 0 && fake.Closes == 0, "Executou antes de conferir todos os chamados.");
        requested++;
        return Task.FromResult<string?>($" sd-{5000 + run.Floors.IndexOf(floor)} ");
    }, default);
    Check(resumed && requested == 2 && run.State == RunState.Completed, "A conferência não levou à conclusão.");
    Check(fake.Creates == 0 && fake.Closes == 2, "Reabriu chamados já vinculados.");
    Check(memory.Saves.Any(r => r.Floors.All(f => f.IssueKey is not null) && r.State != RunState.Running), "Não salvou os vínculos antes de executar.");
}

static async Task CancelResumeReconciliation()
{
    var run = Run(2); var fake = new FakeJira(); var memory = new MemoryStore();
    run.State = RunState.NeedsReconciliation;
    foreach (var floor in run.Floors) floor.PendingAction = PendingAction.Create;
    fake.Issues.Add("SD-5000", new(run.Floors[0].Floor));
    var requested = 0;
    var resumed = await new ExecutionEngine(memory, () => fake).ResumeAsync(run,
        _ => Task.FromResult<string?>(requested++ == 0 ? "SD-5000" : null), default);
    Check(!resumed && requested == 2 && run.State != RunState.Completed, "O cancelamento iniciou a execução.");
    Check(run.Floors[0].IssueKey == "SD-5000" && run.Floors[1].IssueKey is null && run.Floors[1].PendingAction == PendingAction.Create, "O cancelamento perdeu a pendência ou o vínculo.");
    Check(fake.Creates == 0 && fake.Closes == 0 && memory.Saves.Count == 1, "Executou após cancelar.");
    var saved = memory.Saves.Single();
    Check(saved.Floors[0].IssueKey == "SD-5000" && saved.Floors[1].PendingAction == PendingAction.Create, "O vínculo não ficou salvo.");
}

static async Task WrongResumeIssue()
{
    var run = Run(1); run.State = RunState.NeedsReconciliation; run.Floors[0].PendingAction = PendingAction.Create;
    var fake = new FakeJira(); var memory = new MemoryStore(); fake.Issues.Add("SD-5000", new(Profile(1).Floors[0]));
    try
    {
        await new ExecutionEngine(memory, () => fake).ResumeAsync(run, _ => Task.FromResult<string?>("SD-5000"), default);
        throw new Exception("Aceitou um chamado de outro pavimento.");
    }
    catch (InvalidOperationException) { }
    Check(run.Floors[0].IssueKey is null && run.Floors[0].PendingAction == PendingAction.Create && run.State == RunState.NeedsReconciliation, "Alterou a pendência após divergência.");
    Check(fake.Creates == 0 && fake.Closes == 0 && memory.Saves.Count == 0, "Executou após divergência.");
}

static async Task CancelResumeToken()
{
    using var cancellation = new CancellationTokenSource();
    var run = Run(1); run.Floors[0].PendingAction = PendingAction.Create;
    var fake = new FakeJira();
    try
    {
        await new ExecutionEngine(new MemoryStore(), () => fake).ResumeAsync(run, _ =>
        {
            cancellation.Cancel(); return Task.FromResult<string?>("SD-5000");
        }, cancellation.Token);
        throw new Exception("Ignorou o cancelamento.");
    }
    catch (OperationCanceledException) { }
    Check(fake.Connections == 0 && fake.Creates == 0 && run.Floors[0].IssueKey is null, "Vinculou após cancelar.");
}

sealed class MemoryStore : IRunStore
{
    public List<VisitRun> Saves { get; } = [];
    public Task SaveRunAsync(VisitRun run) { Saves.Add(Serialization.Copy(run)); return Task.CompletedTask; }
}
sealed class FakeIssue(Floor floor)
{
    public Floor Floor { get; } = Serialization.Copy(floor);
    public bool Assigned, Started, Closed;
    public string Comment = "";
}
sealed class FakeJira : IJiraAutomation
{
    public Dictionary<string, FakeIssue> Issues { get; } = [];
    public List<string> Actions { get; } = [];
    public int Creates, Closes, Connections;
    public bool FailAfterCreate, FailAfterClose, WrongResolution, FailPrepareClose, FailCapture, FailDispose;
    public Action? AfterCreate;
    public TaskCompletionSource? ConnectGate;
    private string? current;
    public string? InitialIssueKey;
    public async Task ConnectAsync(AppSettings settings, Action<string> report, CancellationToken cancellationToken, string? initialIssueKey = null) { Connections++; InitialIssueKey = initialIssueKey; if (ConnectGate is not null) await ConnectGate.Task; }
    public Task PrepareCreateAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<string> SubmitCreateAsync(VisitRun run, FloorRun floor)
    {
        Creates++; var key = $"SD-{1000 + Creates}"; Issues.Add(key, new(floor.Floor)); Actions.Add("create:" + key); AfterCreate?.Invoke();
        if (FailAfterCreate) throw new IOException("A resposta de abertura foi perdida.");
        return Task.FromResult(key);
    }
    public Task<IssueSnapshot> ReadIssueAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        current = floor.IssueKey; var issue = Issues[current!];
        if (issue.Floor.Id != floor.Floor.Id) throw new InvalidOperationException("Pavimento diferente.");
        return Task.FromResult(new IssueSnapshot(current!, issue.Closed ? "Fechado" : issue.Started ? "Em andamento N2" : "Aguardando N2",
            issue.Assigned, issue.Closed, issue.Started, !WrongResolution, issue.Comment == floor.Floor.Resolution, true));
    }
    public Task AssignAsync(VisitRun run, FloorRun floor) { CheckPage(floor); Issues[floor.IssueKey!].Assigned = true; Actions.Add("assign:" + current); return Task.CompletedTask; }
    public Task StartAsync(VisitRun run, FloorRun floor) { CheckPage(floor); Issues[floor.IssueKey!].Started = true; Actions.Add("start:" + current); return Task.CompletedTask; }
    public Task PrepareCloseAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        CheckPage(floor); if (FailPrepareClose) throw new InvalidOperationException("Campo de fechamento não encontrado.");
        return Task.CompletedTask;
    }
    public Task SubmitCloseAsync(VisitRun run, FloorRun floor)
    {
        CheckPage(floor); var issue = Issues[floor.IssueKey!]; issue.Closed = true; issue.Comment = floor.Floor.Resolution;
        Closes++; Actions.Add("close:" + current);
        if (FailAfterClose) throw new IOException("A resposta de fechamento foi perdida.");
        return Task.CompletedTask;
    }
    private void CheckPage(FloorRun floor) { if (current != floor.IssueKey) throw new Exception("A página de outro pavimento seria alterada."); }
    public Task CaptureFailureAsync(VisitRun run) => FailCapture ? Task.FromException(new IOException("Falha ao capturar.")) : Task.CompletedTask;
    public ValueTask DisposeAsync() => FailDispose ? ValueTask.FromException(new IOException("Falha ao fechar.")) : ValueTask.CompletedTask;
}
