using Vistora.Core;
using Vistora.Infrastructure;

internal static class HistoryCleanupTests
{
    public static (string Name, Func<Task> Test)[] Cases =>
    [
        ("Limpeza remove todas as visitas, backups, logs e capturas sem alterar outros dados", Complete),
        ("Gravações pendentes e resumos em memória não recriam visitas apagadas", QueuedWrites),
        ("Limpeza recusa uma tentativa em andamento e permite nova tentativa após concluir", ActiveAttempt),
        ("Arquivo de visita bloqueado informa falha parcial e permite repetir a limpeza", LockedRun),
        ("Captura bloqueada preserva registros e permite repetir a limpeza", LockedCapture),
        ("Log compartilhado bloqueado preserva registros e eventos gerais", LockedLog)
    ];
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "VistoraCleanupTests", Guid.NewGuid().ToString("N"));
    private static VisitRun Run() => ProfileValidation.CreateRun(new VisitProfile
    {
        Name = "Unidade de teste", Unit = "Unidade A", ReporterName = "Pessoa Exemplo", ReporterEmail = "pessoa@example.com",
        FullName = "Pessoa Exemplo", Extension = "123", Floors = [new Floor { Name = "Pavimento 1", Resolution = "Equipamentos conferidos." }]
    }, new AppSettings());

    private static async Task Complete()
    {
        var root = NewRoot();
        await using var diagnostics = new DiagnosticService(root);
        var store = new LocalStore(root, diagnostics);
        var profile = Run().Profile;
        await store.SaveProfilesAsync(new ProfileDocument { Profiles = [profile], ActiveProfileId = profile.Id });
        await store.SaveSettingsAsync(new AppSettings());
        Directory.CreateDirectory(store.BrowserDirectory);
        var browser = Path.Combine(store.BrowserDirectory, "session.dat");
        await File.WriteAllTextAsync(browser, "Login preservado");
        var preservedPaths = new[] { browser, Path.Combine(root, "profiles.json"), Path.Combine(root, "settings.json"),
            Path.Combine(root, "diagnostic-installation.json") };
        var preserved = await Task.WhenAll(preservedPaths.Select(path => File.ReadAllBytesAsync(path)));
        var runs = new List<VisitRun>();
        foreach (var state in Enum.GetValues<RunState>())
        {
            var run = Run(); run.State = state;
            run.Floors[0].IssueKey = "SD-1001";
            await store.SaveRunAsync(run); await store.SaveRunAsync(run); runs.Add(run);
            var attempt = new DiagnosticAttempt(diagnostics, run);
            attempt.Event("cleanup.visit", "Evento da visita.");
            await attempt.FinishAsync(state == RunState.Completed);
            var artifacts = diagnostics.ArtifactDirectory(run.Id, attempt.Id);
            Directory.CreateDirectory(artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "falha.png"), "captura");
            diagnostics.AppEvent("cleanup.related", "Evento compartilhado da visita.", runId: run.Id);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "runs", runs[0].Id + ".json.tmp"), "temporário");
        var orphan = Guid.NewGuid().ToString("N");
        var orphanPath = Path.Combine(root, "diagnostics", orphan);
        Directory.CreateDirectory(orphanPath); await File.WriteAllTextAsync(Path.Combine(orphanPath, "legado.png"), "captura antiga");
        diagnostics.AppEvent("cleanup.orphan", "Evento de visita sem registro.", runId: orphan);
        diagnostics.AppEvent("cleanup.general", "Evento geral preservado.");
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        await diagnostics.ExportAsync(zip, runs[0]); var zipBytes = await File.ReadAllBytesAsync(zip);
        await store.ClearHistoryAsync(); await diagnostics.FlushAsync();
        Check((await new LocalStore(root).LoadRunsAsync()).Count == 0 && !Directory.EnumerateFiles(Path.Combine(root, "runs")).Any(), "Uma visita ou cópia permaneceu no disco.");
        Check(!Directory.Exists(Path.Combine(root, "diagnostics")), "Capturas ou diagnósticos permaneceram no disco.");
        foreach (var run in runs) Check(await diagnostics.ReadOverviewAsync(run.Id) is null, "Resumo antigo permaneceu em memória.");
        for (var i = 0; i < preserved.Length; i++)
        {
            var after = await File.ReadAllBytesAsync(preservedPaths[i]);
            Check(preserved[i].SequenceEqual(after), "A limpeza alterou dados preservados.");
        }
        var zipAfter = await File.ReadAllBytesAsync(zip);
        Check(zipBytes.SequenceEqual(zipAfter), "A limpeza alterou o ZIP exportado.");
        var lines = (await Task.WhenAll(Directory.EnumerateFiles(Path.Combine(root, "logs"), "*.jsonl").Select(path => File.ReadAllLinesAsync(path)))).SelectMany(x => x).ToArray();
        Check(lines.Any(line => line.Contains("cleanup.general")) && !lines.Any(line => line.Contains("cleanup.related") || line.Contains("cleanup.orphan")), "Logs compartilhados não foram filtrados corretamente.");
        await store.ClearHistoryAsync();
        var next = Run(); await store.SaveRunAsync(next);
        var nextAttempt = new DiagnosticAttempt(diagnostics, next); nextAttempt.Event("cleanup.new", "Nova visita."); await nextAttempt.FinishAsync(false);
        Check((await store.LoadRunsAsync()).Single().Id == next.Id && await diagnostics.ReadOverviewAsync(next.Id) is not null, "A limpeza impediu uma nova visita.");
    }

    private static async Task QueuedWrites()
    {
        await using var diagnostics = new DiagnosticService(NewRoot(), queueCapacity: 64);
        var store = new LocalStore(diagnostics.Root, diagnostics); var run = Run(); await store.SaveRunAsync(run);
        var attempt = new DiagnosticAttempt(diagnostics, run); await attempt.FinishAsync(false);
        for (var i = 0; i < 40; i++) diagnostics.AppEvent("cleanup.queued", "Evento anterior à limpeza.", runId: run.Id);
        diagnostics.UpdateAttempt(new AttemptSummary { RunId = run.Id, AttemptId = attempt.Id });
        await store.ClearHistoryAsync(); await diagnostics.FlushAsync();
        Check(!Directory.Exists(store.DiagnosticsDirectory(run.Id)) && await diagnostics.ReadOverviewAsync(run.Id) is null, "A fila ou o cache recriou dados apagados.");
        Check(!Directory.EnumerateFiles(Path.Combine(diagnostics.Root, "logs"), "*.jsonl").Any(path => File.ReadAllText(path).Contains(run.Id)), "A fila recriou eventos apagados.");
    }

    private static async Task ActiveAttempt()
    {
        await using var diagnostics = new DiagnosticService(NewRoot());
        var store = new LocalStore(diagnostics.Root, diagnostics); var run = Run(); await store.SaveRunAsync(run);
        var attempt = new DiagnosticAttempt(diagnostics, run);
        try { await store.ClearHistoryAsync(); throw new Exception("Tentativa ativa aceitou limpeza."); }
        catch (InvalidOperationException) { }
        Check((await store.LoadRunsAsync()).Single().Id == run.Id, "A recusa alterou a visita.");
        await attempt.FinishAsync(false); await store.ClearHistoryAsync();
        Check((await store.LoadRunsAsync()).Count == 0, "A recusa não liberou a limpeza.");
    }

    private static async Task LockedRun()
    {
        await using var diagnostics = new DiagnosticService(NewRoot()); var store = new LocalStore(diagnostics.Root, diagnostics);
        var first = Run(); first.Id = "00000000000000000000000000000001";
        var second = Run(); second.Id = "00000000000000000000000000000002";
        await store.SaveRunAsync(first); await store.SaveRunAsync(second);
        using (var locked = File.Open(Path.Combine(store.Root, "runs", second.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await store.ClearHistoryAsync(); throw new Exception("Arquivo bloqueado declarou sucesso."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var remaining = await new LocalStore(store.Root).LoadRunsAsync();
            Check(remaining.Count == 1 && remaining.Single().Id == second.Id, "Os registros restantes não refletem a exclusão parcial.");
        }
        await store.ClearHistoryAsync(); Check((await store.LoadRunsAsync()).Count == 0, "Não permitiu repetir a limpeza.");
    }

    private static async Task LockedCapture()
    {
        await using var diagnostics = new DiagnosticService(NewRoot()); var store = new LocalStore(diagnostics.Root, diagnostics);
        var run = Run(); await store.SaveRunAsync(run);
        var path = Path.Combine(store.DiagnosticsDirectory(run.Id), "falha.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "captura");
        using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await store.ClearHistoryAsync(); throw new Exception("Captura bloqueada declarou sucesso."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Check((await store.LoadRunsAsync()).Count == 1, "Falha no diagnóstico apagou o registro de retomada.");
        }
        await store.ClearHistoryAsync(); Check((await store.LoadRunsAsync()).Count == 0, "A falha de captura não liberou a limpeza.");
    }

    private static async Task LockedLog()
    {
        await using var diagnostics = new DiagnosticService(NewRoot()); var store = new LocalStore(diagnostics.Root, diagnostics);
        var run = Run(); await store.SaveRunAsync(run);
        diagnostics.AppEvent("cleanup.related", "Visita.", runId: run.Id); diagnostics.AppEvent("cleanup.general", "Geral."); await diagnostics.FlushAsync();
        var path = Directory.EnumerateFiles(Path.Combine(store.Root, "logs"), "*.jsonl").Single();
        using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await store.ClearHistoryAsync(); throw new Exception("Log bloqueado declarou sucesso."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Check((await store.LoadRunsAsync()).Count == 1 && File.ReadAllText(path).Contains("cleanup.general"), "Falha no log apagou dados preservados.");
        }
        await store.ClearHistoryAsync(); Check((await store.LoadRunsAsync()).Count == 0, "A falha de log não liberou a limpeza.");
    }
}
