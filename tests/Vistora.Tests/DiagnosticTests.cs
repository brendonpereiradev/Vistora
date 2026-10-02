using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistora.Core;
using Vistora.Infrastructure;

internal static class DiagnosticTests
{
    public static (string Name, Func<Task> Test)[] Cases =>
    [
        ("Logs preservam etapas, correlação, resultado e tempos", Complete),
        ("Logs de retomada preservam tentativas sem duplicar ações", Resume),
        ("Cancelamento não gera falsa falha técnica", Cancel),
        ("Falhas da fábrica e limpeza liberam a execução e preservam a causa", Lifecycle),
        ("Falha ao salvar impede envio e não declara progresso persistido", PersistenceFailure),
        ("ZIP é portátil, íntegro e contém somente a visita selecionada", PortableExport),
        ("Exportação reaplica sanitização de dados e exceções", Privacy),
        ("Capturas exigem inclusão explícita e caminhos permitidos", Screenshots),
        ("Exportação durante execução informa corte e resultado parcial", LiveExport),
        ("Exportação histórica preserva ambiente e ignora linha truncada", HistoricalExport),
        ("Falha de destino preserva fontes e não deixa ZIP incompleto", ExportFailure),
        ("Fila limitada e disco indisponível não interrompem a visita", UnavailableLogger),
        ("Retenção protege visitas pendentes e a sessão atual", Retention),
        ("Diagnóstico geral pode ser exportado sem visita criada", ApplicationExport),
        ("Eventos recebem cópias e sequências seguras em concorrência", ConcurrentLogging),
        ("Rotação conserva todos os eventos na exportação", Rotation),
        ("Exportação permite escolher uma sessão anterior do aplicativo", PreviousSession),
        ("Versão do Windows permanece legível na exportação", EnvironmentVersion)
    ];
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "VistoraDiagnosticTests", Guid.NewGuid().ToString("N"));
    private static VisitRun Run(int count = 2) => ProfileValidation.CreateRun(new VisitProfile
    {
        Name = "Perfil privado", ReporterName = "Solicitante Sentinel", ReporterEmail = "privado.sentinel@example.com",
        FullName = "Nome Sentinel", Extension = "987654", Phone = "11987654321", Unit = "Unidade Sentinel",
        Title = "Visita privada", Description = "Descrição confidencial sentinel",
        Floors = Enumerable.Range(1, count).Select(i => new Floor { Name = $"Andar privado {i}", Sector = "Setor privado", Room = "Sala privada",
            Resolution = $"Resolução confidencial {i}\nComentário reservado sentinel" }).ToList()
    }, new AppSettings());
    private static async Task<DiagnosticEvent[]> Events(string root, VisitRun run) =>
        (await Task.WhenAll(Directory.EnumerateFiles(Path.Combine(root, "diagnostics", run.Id), "*.jsonl", SearchOption.AllDirectories)
            .Select(path => File.ReadAllLinesAsync(path)))).SelectMany(lines => lines).Where(s => s.Length > 0)
            .Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s, Serialization.Options)!).ToArray();
    private static async Task<AttemptSummary[]> Summaries(string root, VisitRun run) =>
        (await Task.WhenAll(Directory.EnumerateFiles(Path.Combine(root, "diagnostics", run.Id), "summary.json", SearchOption.AllDirectories)
            .Select(path => File.ReadAllTextAsync(path)))).Select(s => JsonSerializer.Deserialize<AttemptSummary>(s, Serialization.Options)!).OrderBy(s => s.StartedAtUtc).ToArray();
    private static async Task<Dictionary<string, byte[]>> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path); var files = new Dictionary<string, byte[]>();
        foreach (var entry in zip.Entries)
        { await using var source = entry.Open(); using var buffer = new MemoryStream(); await source.CopyToAsync(buffer); files[entry.FullName] = buffer.ToArray(); }
        return files;
    }
    private static JsonDocument Json(Dictionary<string, byte[]> files, string path) => JsonDocument.Parse(files[path]);
    private static async Task Complete()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var run = Run();
        var store = new LocalStore(diagnostics.Root, diagnostics);
        await new ExecutionEngine(store, () => new FakeJira(), diagnostics).ExecuteAsync(run, default);
        var entries = await Events(diagnostics.Root, run); var summary = (await Summaries(diagnostics.Root, run)).Single();
        Check(run.State == RunState.Completed && summary.StatePersisted && summary.CreatedThisAttempt == 2, "Resumo incorreto.");
        Check(entries.Count(e => e.EventName == "attempt.finished") == 1, "Evento terminal duplicado.");
        Check(entries.All(e => e.RunId == run.Id && e.AttemptId == summary.AttemptId && e.SessionId == diagnostics.SessionId), "Correlação perdida.");
        Check(entries.Select(e => e.Sequence).Distinct().Count() == entries.Length, "Sequências duplicadas.");
        Check(entries.Where(e => e.EventName == "operation.started").All(e => entries.Any(x => x.EventName == "operation.finished" && x.OperationId == e.OperationId)), "Operação sem resultado.");
        Check(entries.Any(e => e.EventName == "close.confirmed" && e.Outcome == "confirmed" && e.FloorId is not null), "Conferência final ausente.");
        Check(summary.DurationMs >= 0 && summary.StepDurationsMs.ContainsKey("create.prepare") && summary.StepDurationsMs.Values.All(t => t >= 0), "Tempos ausentes.");
        Check(File.Exists(Path.Combine(diagnostics.Root, "diagnostics", run.Id, "resumo.txt")), "Resumo legível ausente.");
    }
    private static async Task Resume()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var run = Run();
        var fake = new FakeJira { FailAfterClose = true }; var store = new LocalStore(diagnostics.Root, diagnostics);
        var engine = new ExecutionEngine(store, () => fake, diagnostics);
        await engine.ExecuteAsync(run, default); fake.FailAfterClose = false; await engine.ExecuteAsync(run, default);
        var summaries = await Summaries(diagnostics.Root, run);
        Check(summaries.Length == 2 && summaries[0].AttemptId != summaries[1].AttemptId, "Tentativas sobrescritas.");
        Check(summaries[0].ErrorCode == "CLOSE_RESULT_UNCERTAIN" && summaries[1].Outcome == "Completed", "Resultados incorretos.");
        Check(fake.Creates == 2 && fake.Closes == 2 && summaries[1].ReusedIssues == 2 && summaries[1].CreatedThisAttempt == 0, "Retomada duplicou ações.");
        Check((await Events(diagnostics.Root, run)).Any(e => e.EventName == "operation.skipped"), "Decisão de reaproveitamento ausente.");
    }
    private static async Task Cancel()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); using var cancel = new CancellationTokenSource();
        var run = Run(); var fake = new FakeJira { AfterCreate = cancel.Cancel };
        await new ExecutionEngine(new LocalStore(diagnostics.Root, diagnostics), () => fake, diagnostics).ExecuteAsync(run, cancel.Token);
        var summary = (await Summaries(diagnostics.Root, run)).Single(); var entries = await Events(diagnostics.Root, run);
        Check(summary.Outcome == "Interrupted" && summary.StatePersisted && run.Floors[0].IssueKey is not null, "Cancelamento perdeu progresso.");
        Check(entries.All(e => e.Level != DiagnosticLevel.Error), "Interrupção classificada como erro.");
    }
    private static async Task Lifecycle()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var store = new LocalStore(diagnostics.Root, diagnostics);
        var failFactory = true; var fake = new FakeJira { FailDispose = true };
        var engine = new ExecutionEngine(store, () => { if (failFactory) throw new IOException("Falha da fábrica."); return fake; }, diagnostics);
        var failed = Run(1); await engine.ExecuteAsync(failed, default); failFactory = false;
        var good = Run(1); await engine.ExecuteAsync(good, default);
        Check((await Summaries(diagnostics.Root, failed)).Single().ErrorCode == "BROWSER_START_FAILED", "Falha da fábrica ausente.");
        Check(good.State == RunState.Completed, "Falha de limpeza transformou sucesso em falha ou manteve bloqueio.");
        Check((await Events(diagnostics.Root, good)).Any(e => e.EventName == "error.secondary" && e.ErrorCode == "BROWSER_CLOSE_FAILED"), "Falha de limpeza ausente.");
        var primary = Run(1); fake.FailPrepareClose = true; fake.FailCapture = true;
        await engine.ExecuteAsync(primary, default);
        var events = await Events(diagnostics.Root, primary);
        Check(events.Count(e => e.EventName == "error.secondary") == 2, "Falhas de captura/limpeza não conservadas.");
        Check((await Summaries(diagnostics.Root, primary)).Single().FailedStep == "close.prepare", "Causa original perdida.");
    }
    private static async Task PersistenceFailure()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var fake = new FakeJira(); var run = Run();
        await new ExecutionEngine(new BrokenStore(), () => fake, diagnostics).ExecuteAsync(run, default);
        var summary = (await Summaries(diagnostics.Root, run)).Single();
        Check(fake.Creates == 0 && !summary.StatePersisted && summary.ErrorCode == "STORE_WRITE_FAILED", "Falha de persistência permitiu envio.");
        Check(run.Message.Contains("Não foi possível salvar"), "Falha de gravação não apresentada.");
    }
    private static async Task PortableExport()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var store = new LocalStore(diagnostics.Root, diagnostics);
        var run = Run(); var other = Run(1);
        var engine = new ExecutionEngine(store, () => new FakeJira(), diagnostics);
        await engine.ExecuteAsync(run, default); await engine.ExecuteAsync(other, default);
        diagnostics.AppEvent("other.failure", "Falha de outra visita.", DiagnosticLevel.Error, runId: other.Id);
        var destination = Path.Combine(DirectoryPath(), "logs.zip"); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var result = await diagnostics.ExportAsync(destination, run); var files = await ReadZip(destination);
        Check(result.Bytes > 0 && !result.Partial, "Exportação completa marcada parcial.");
        Check(files.ContainsKey("resumo.txt") && files.ContainsKey("LEIA-ME.txt"), "Arquivos legíveis ausentes.");
        using var manifest = Json(files, "manifest.json");
        Check(manifest.RootElement.GetProperty("runId").GetString() == run.Id, "Escopo errado.");
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var data = files[item.GetProperty("path").GetString()!];
            Check(item.GetProperty("bytes").GetInt32() == data.Length && item.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), "Checksum inválido.");
        }
        Check(!string.Join("", files.Values.Select(Encoding.UTF8.GetString)).Contains(other.Id), "Outra visita foi exportada.");
        Check(files.Keys.All(k => !k.Contains("profiles") && !k.Contains("browser") && !k.Contains("runs/")), "Dados de origem incluídos.");
        // Copying the archive alone must suffice to read its event timeline.
        var copy = Path.Combine(Path.GetDirectoryName(destination)!, "outra-maquina.zip"); File.Copy(destination, copy);
        Check((await ReadZip(copy)).Keys.SequenceEqual(files.Keys), "Pacote depende do diretório original.");
    }
    private static async Task Privacy()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var run = Run(1); var attempt = new DiagnosticAttempt(diagnostics, run);
        attempt.Error(new InvalidOperationException($"{run.Profile.ReporterEmail} {run.Profile.FullName} {run.Profile.Description} {run.Floors[0].Floor.Resolution} https://host.test/path?token=secretvalue C:\\Users\\privado\\file.txt Bearer topsecret",
            new IOException("senha=ultrasecret; contato telefone 11987654321")), "create.prepare");
        await attempt.FinishAsync(false);
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        var files = await ReadZip((await diagnostics.ExportAsync(zip, run)).Path);
        var text = string.Concat(files.Values.Select(Encoding.UTF8.GetString));
        foreach (var value in new[] { run.Profile.ReporterEmail, run.Profile.FullName, run.Profile.Description, "Comentário reservado sentinel", "secretvalue", "topsecret", "ultrasecret", "11987654321", "Users\\privado" })
            Check(!text.Contains(value, StringComparison.OrdinalIgnoreCase), "Valor sensível exportado: " + value);
        Check(text.Contains("System.InvalidOperationException") && text.Contains("System.IO.IOException"), "Tipos e causas internas perdidos.");
    }
    private static async Task Screenshots()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var run = Run(1); var attempt = new DiagnosticAttempt(diagnostics, run);
        var directory = diagnostics.ArtifactDirectory(run.Id, attempt.Id); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "captura.png"), [137, 80, 78, 71]);
        attempt.Event("artifact.saved", "Captura disponível.", artifacts: ["artifacts/captura.png", "artifacts/../../profiles.json", "C:/private.png"]);
        await attempt.FinishAsync(true);
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        var normal = await ReadZip((await diagnostics.ExportAsync(zip, run)).Path);
        Check(normal.Keys.All(k => !k.EndsWith(".png")), "Captura incluída sem opção.");
        var included = await ReadZip((await diagnostics.ExportAsync(zip, run, true)).Path);
        Check(included.Keys.Count(k => k.EndsWith(".png")) == 1 && included.Keys.All(k => !k.Contains("..")), "Captura ausente ou caminho externo permitido.");
    }
    private static async Task LiveExport()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var run = Run(1);
        var fake = new FakeJira { ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var task = new ExecutionEngine(new LocalStore(diagnostics.Root, diagnostics), () => fake, diagnostics).ExecuteAsync(run, default);
        try
        {
            var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
            var files = await ReadZip((await diagnostics.ExportAsync(zip, run)).Path);
            using var manifest = Json(files, "manifest.json"); using var summary = Json(files, "resumo.json");
            Check(manifest.RootElement.GetProperty("partial").GetBoolean(), "Execução ativa apresentada como definitiva.");
            Check(summary.RootElement.GetProperty("attempts")[0].GetProperty("outcome").GetString() == "Running", "Resultado ativo incorreto.");
            Check(fake.Creates == 0, "Exportação interferiu no Jira.");
        }
        finally { fake.ConnectGate.TrySetResult(); await task; }
    }
    private static async Task HistoricalExport()
    {
        var root = DirectoryPath(); var run = Run(1); string environmentPath;
        await using (var first = new DiagnosticService(root))
        {
            var attempt = new DiagnosticAttempt(first, run);
            attempt.Event("before.crash", "Evento válido antes do encerramento."); await first.FlushAsync();
            environmentPath = Path.Combine(root, "diagnostics", run.Id, "attempts", attempt.Id, "environment.json");
        }
        var events = Directory.EnumerateFiles(Path.Combine(root, "diagnostics", run.Id), "events.jsonl", SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(events, "{\"linhaTruncada\":");
        var env = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(environmentPath))!;
        env["appVersion"] = JsonSerializer.SerializeToElement("0.1.0+original"); await File.WriteAllTextAsync(environmentPath, JsonSerializer.Serialize(env));
        await using var second = new DiagnosticService(root);
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"); var result = await second.ExportAsync(zip, run); var files = await ReadZip(zip);
        using var summary = Json(files, "resumo.json");
        Check(result.Partial && summary.RootElement.GetProperty("attempts")[0].GetProperty("outcome").GetString() == "EndedUnexpectedly", "Encerramento sem conclusão não detectado.");
        var versionPreserved = files.Where(f => f.Key.EndsWith("environment.json")).Any(f =>
        { using var original = JsonDocument.Parse(f.Value); return original.RootElement.GetProperty("appVersion").GetString() == "0.1.0+original"; });
        Check(versionPreserved, "Ambiente antigo substituído pelo exportador.");
        Check(files.Where(f => f.Key.EndsWith("events.jsonl")).Any(f => Encoding.UTF8.GetString(f.Value).Contains("before.crash")), "Linha válida perdida.");
    }
    private static async Task ExportFailure()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var run = Run(1);
        await new ExecutionEngine(new LocalStore(diagnostics.Root, diagnostics), () => new FakeJira(), diagnostics).ExecuteAsync(run, default);
        var source = Directory.EnumerateFiles(Path.Combine(diagnostics.Root, "diagnostics", run.Id), "events.jsonl", SearchOption.AllDirectories).Single();
        var before = await File.ReadAllBytesAsync(source); var destination = Path.Combine(DirectoryPath(), "missing", "logs.zip");
        try { await diagnostics.ExportAsync(destination, run); throw new Exception("Destino inexistente aceito."); } catch (DirectoryNotFoundException) { }
        var after = await File.ReadAllBytesAsync(source);
        Check(before.SequenceEqual(after) && !File.Exists(destination), "Fonte alterada ou ZIP incompleto publicado.");
        try { await diagnostics.ExportAsync(Path.Combine(diagnostics.Root, "profiles.json"), run); throw new Exception("Exportação sobre dados permitida."); }
        catch (InvalidOperationException) { }
    }
    private static async Task UnavailableLogger()
    {
        var root = DirectoryPath(); Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, "diagnostics"), "bloqueio");
        await using var diagnostics = new DiagnosticService(root, 8);
        diagnostics.Configure(new DiagnosticOptions { MinimumLevel = DiagnosticLevel.Debug });
        for (var i = 0; i < 500; i++) diagnostics.AppEvent("queue.stress", "Registro de teste.", DiagnosticLevel.Debug);
        var run = Run(1); var fake = new FakeJira();
        await new ExecutionEngine(new MemoryStore(), () => fake, diagnostics).ExecuteAsync(run, default);
        Check(run.State == RunState.Completed && fake.Closes == 1 && diagnostics.Warning is not null && diagnostics.DroppedEvents > 0, "Gravador indisponível interferiu no fluxo.");
    }
    private static async Task Retention()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath()); var store = new LocalStore(diagnostics.Root, diagnostics);
        var complete = Run(1); await new ExecutionEngine(store, () => new FakeJira(), diagnostics).ExecuteAsync(complete, default);
        var pending = Run(1); var fake = new FakeJira { FailAfterCreate = true };
        await new ExecutionEngine(store, () => fake, diagnostics).ExecuteAsync(pending, default);
        var completedPath = Path.Combine(diagnostics.Root, "diagnostics", complete.Id); var pendingPath = Path.Combine(diagnostics.Root, "diagnostics", pending.Id);
        Directory.SetLastWriteTimeUtc(completedPath, DateTime.UtcNow.AddDays(-60)); Directory.SetLastWriteTimeUtc(pendingPath, DateTime.UtcNow.AddDays(-60));
        var progress = await File.ReadAllBytesAsync(Path.Combine(diagnostics.Root, "runs", pending.Id + ".json"));
        await diagnostics.CleanAsync();
        Check(!Directory.Exists(completedPath) && Directory.Exists(pendingPath), "Retenção removeu pendência ou não limpou diagnóstico antigo.");
        var after = await File.ReadAllBytesAsync(Path.Combine(diagnostics.Root, "runs", pending.Id + ".json"));
        Check(progress.SequenceEqual(after), "Retenção alterou progresso.");
        Check(Directory.EnumerateFiles(Path.Combine(diagnostics.Root, "logs"), "*.jsonl").Any(), "Sessão atual removida.");
    }
    private static async Task ApplicationExport()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath());
        diagnostics.AppEvent("app.startup_failed", "Falha ao iniciar.", DiagnosticLevel.Error, new IOException("Sem dados locais."));
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"); var files = await ReadZip((await diagnostics.ExportAsync(zip)).Path);
        Check(Encoding.UTF8.GetString(files["app.jsonl"]).Contains("app.startup_failed") && files.ContainsKey("environment.json"), "Falha inicial não exportada.");
    }
    private static async Task ConcurrentLogging()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath());
        var details = new Dictionary<string, object?> { ["count"] = 1 };
        diagnostics.Write(new DiagnosticEvent { EventName = "snapshot", Details = details }); details["count"] = 99;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        { for (var i = 0; i < 20; i++) diagnostics.AppEvent("parallel", "Evento concorrente."); })));
        await diagnostics.FlushAsync();
        var lines = (await Task.WhenAll(Directory.EnumerateFiles(Path.Combine(diagnostics.Root, "logs"), "*.jsonl").Select(path => File.ReadAllLinesAsync(path)))).SelectMany(x => x).ToArray();
        var events = lines.Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s, Serialization.Options)!).ToArray();
        Check(events.Select(e => e.Sequence).Distinct().Count() == events.Length, "Sequências concorrentes duplicadas.");
        Check(((JsonElement)events.Single(e => e.EventName == "snapshot").Details!["count"]!).GetInt32() == 1, "Evento manteve objeto mutável.");
    }
    private static async Task Rotation()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath());
        diagnostics.Configure(new DiagnosticOptions { RotationMegabytes = 1 });
        var run = Run(1); var attempt = new DiagnosticAttempt(diagnostics, run);
        for (var i = 0; i < 145; i++) attempt.Event("rotation.test", new string('x', 9000));
        await attempt.FinishAsync(false);
        Check(Directory.EnumerateFiles(Path.Combine(diagnostics.Root, "diagnostics", run.Id), "*.jsonl", SearchOption.AllDirectories).Count() > 1, "Rotação não ocorreu.");
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"); var files = await ReadZip((await diagnostics.ExportAsync(zip, run)).Path);
        var events = Encoding.UTF8.GetString(files.Single(f => f.Key.EndsWith("events.jsonl")).Value).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => JsonSerializer.Deserialize<DiagnosticEvent>(s, Serialization.Options)!).ToArray();
        Check(events.Count(e => e.EventName == "rotation.test") == 145 && events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).Order()), "Partes rotacionadas perdidas ou fora de ordem.");
    }
    private static async Task PreviousSession()
    {
        var root = DirectoryPath(); string previousSession; string environmentId;
        await using (var first = new DiagnosticService(root))
        { previousSession = first.SessionId; environmentId = first.EnvironmentId; first.AppEvent("old.session", "Registro antigo."); }
        await using var second = new DiagnosticService(root);
        Check(second.EnvironmentId == environmentId && (await second.ListApplicationSessionsAsync()).Any(s => s.Id == previousSession), "Origem ou lista de sessões perdeu identidade.");
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"); var files = await ReadZip((await second.ExportAsync(zip, applicationSessionId: previousSession)).Path);
        using var manifest = Json(files, "manifest.json");
        Check(manifest.RootElement.GetProperty("sessionId").GetString() == previousSession && Encoding.UTF8.GetString(files["app.jsonl"]).Contains("old.session"), "Sessão antiga não exportada.");
        Check(!Encoding.UTF8.GetString(files["app.jsonl"]).Contains(second.SessionId), "Sessões misturadas.");
    }
    private static async Task EnvironmentVersion()
    {
        await using var diagnostics = new DiagnosticService(DirectoryPath());
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip"); var files = await ReadZip((await diagnostics.ExportAsync(zip)).Path);
        using var environment = Json(files, "environment.json");
        Check(environment.RootElement.GetProperty("windowsVersion").GetString() == Environment.OSVersion.Version.ToString(), "Versão numérica mascarada como dado pessoal.");
    }
    private sealed class BrokenStore : IRunStore
    { public Task SaveRunAsync(VisitRun run) => Task.FromException(new IOException("Falha de gravação.")); }
}
