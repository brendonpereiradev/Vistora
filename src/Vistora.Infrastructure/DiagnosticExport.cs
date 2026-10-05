using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistora.Core;

namespace Vistora.Infrastructure;

public sealed record RunDiagnosticSummary(string RunId, AttemptSummary[] Attempts, double? MeasuredDurationMs,
    bool Partial, string[] Notes);
public sealed record DiagnosticExportResult(string Path, long Bytes, bool Partial);
public sealed record DiagnosticSession(string Id, DateTimeOffset StartedAtUtc)
{ public string Label => StartedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"); }

public sealed partial class DiagnosticService
{
    private static readonly HashSet<string> EnvironmentFields = new(StringComparer.Ordinal)
    { "schemaVersion", "capturedAtUtc", "environmentId", "sessionId", "appVersion", "windowsVersion", "osArchitecture",
        "processArchitecture", "dotnetVersion", "runtime", "culture", "timeZone", "viewportWidth", "viewportHeight",
        "timeoutSeconds", "loginTimeoutMinutes", "diagnosticLevel", "selectorOverrideKeys", "edgeVersion", "playwrightVersion" };

    private async Task<AttemptSummary[]> LoadSummariesAsync(string runId, List<string> notes, bool includeMemory = true)
    {
        var directory = Path.Combine(Root, "diagnostics", SafeId(runId), "attempts");
        var result = new Dictionary<string, AttemptSummary>();
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                var id = Path.GetFileName(path);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                try
                {
                    var summary = JsonSerializer.Deserialize<AttemptSummary>(await File.ReadAllTextAsync(Path.Combine(path, "summary.json")), JsonOptions);
                    if (summary is null || summary.SchemaVersion != 1 || summary.RunId != runId || summary.AttemptId != id)
                        throw new InvalidDataException();
                    result[id] = summary;
                }
                catch
                {
                    notes.Add($"Resumo da tentativa {id} indisponível.");
                    result[id] = new AttemptSummary { RunId = runId, AttemptId = id, Outcome = "Unavailable", Partial = true };
                }
            }
        if (includeMemory)
            foreach (var summary in summaries.Values.Where(s => s.RunId == runId)) result[summary.AttemptId] = summary;
        return result.Values.Select(SanitizeSummary).Select(s => s.EndedAtUtc is null && !active.ContainsKey(s.AttemptId)
                ? s with { Outcome = "EndedUnexpectedly", Partial = true } : s)
            .OrderBy(s => s.StartedAtUtc).ToArray();
    }
    private static AttemptSummary SanitizeSummary(AttemptSummary summary) => summary with
    {
        SessionId = SafeIdentifier(summary.SessionId) ?? "", EnvironmentId = SafeIdentifier(summary.EnvironmentId) ?? "",
        Kind = SafeKey(summary.Kind), Outcome = SafeKey(summary.Outcome), ErrorCode = summary.ErrorCode is null ? null : SafeKey(summary.ErrorCode),
        ErrorId = SafeIdentifier(summary.ErrorId), FailedStep = summary.FailedStep is null ? null : SafeKey(summary.FailedStep),
        FailedFloorId = SafeIdentifier(summary.FailedFloorId),
        Floors = summary.Floors.Select(f => f with { FloorId = SafeIdentifier(f.FloorId) ?? "", IssueKey = SafeIssueKey(f.IssueKey) }).ToArray(),
        StepDurationsMs = summary.StepDurationsMs.Where(p => double.IsFinite(p.Value) && p.Value >= 0 && SafeKey(p.Key) == p.Key).ToDictionary(),
        Warnings = summary.Warnings.Select(w => new DiagnosticWarning(SafeKey(w.Code), SafeIdentifier(w.ErrorId) ?? "")).ToArray(),
        DurationMs = summary.DurationMs is { } ms && double.IsFinite(ms) && ms >= 0 ? ms : null
    };

    private static RunDiagnosticSummary Consolidate(string runId, AttemptSummary[] attempts, List<string> notes)
    {
        if (attempts.Length == 0) notes.Add("Esta visita não possui registros no formato de diagnóstico atual.");
        if (attempts.Any(a => a.DurationMs is null)) notes.Add("Há tentativas sem medição de tempo disponível.");
        if (attempts.Any(a => a.EndedAtUtc is null)) notes.Add("Há tentativa sem término observado; o resultado não é definitivo.");
        return new(runId, attempts, attempts.Any(a => a.DurationMs.HasValue) ? attempts.Sum(a => a.DurationMs ?? 0) : null,
            notes.Count > 0 || attempts.Any(a => a.Partial || a.DroppedEvents > 0), notes.Distinct().ToArray());
    }
    private static string OutcomeLabel(string outcome) => outcome switch
    {
        "Completed" => "Concluída", "Interrupted" => "Interrompida", "Failed" => "Falha", "NeedsReconciliation" => "Precisa de conferência",
        "Running" => "Em andamento", "EndedUnexpectedly" => "Encerrada sem conclusão observada", "Linked" => "Chamado vinculado",
        "Unavailable" => "Indisponível", _ => outcome
    };
    private static string SummaryText(RunDiagnosticSummary summary)
    {
        var text = new StringBuilder($"Diagnóstico Vistora\nVisita: {summary.RunId}\nTentativas: {summary.Attempts.Length}\n");
        text.AppendLine($"Tempo medido acumulado: {(summary.MeasuredDurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString("g") : "indisponível")}");
        text.AppendLine($"Diagnóstico parcial: {(summary.Partial ? "sim" : "não")}");
        foreach (var attempt in summary.Attempts)
        {
            text.AppendLine($"\nTentativa: {attempt.AttemptId}\nAmbiente: {attempt.EnvironmentId}\nResultado: {OutcomeLabel(attempt.Outcome)}");
            text.AppendLine($"Início UTC: {attempt.StartedAtUtc:O}\nFim UTC: {attempt.EndedAtUtc:O}\nEstado final salvo: {(attempt.StatePersisted ? "sim" : "não confirmado")}");
            text.AppendLine($"Previstos: {attempt.PlannedFloors}; criados nesta tentativa: {attempt.CreatedThisAttempt}; reaproveitados: {attempt.ReusedIssues}");
            text.AppendLine($"Fechados: {attempt.Floors.Count(f => f.Stage == FloorStage.Closed)}; ações pendentes: {attempt.Floors.Count(f => f.PendingAction != PendingAction.None)}");
            if (attempt.ErrorCode is not null) text.AppendLine($"Causa: {attempt.ErrorCode}; etapa: {attempt.FailedStep}; pavimento: {attempt.FailedFloorId}; erro: {attempt.ErrorId}");
            foreach (var warning in attempt.Warnings) text.AppendLine($"Aviso técnico: {warning.Code}; erro: {warning.ErrorId}");
            foreach (var stage in Enum.GetValues<FloorStage>()) text.AppendLine($"{stage}: {attempt.Floors.Count(f => f.Stage == stage)}");
            foreach (var timing in attempt.StepDurationsMs) text.AppendLine($"Tempo {timing.Key}: {timing.Value:F0} ms");
            foreach (var floor in attempt.Floors) text.AppendLine($"Pavimento {floor.FloorId}; chamado {floor.IssueKey ?? "não identificado"}; etapa {floor.Stage}; pendência {floor.PendingAction}");
        }
        foreach (var note in summary.Notes) text.AppendLine("Observação: " + note);
        return text.ToString();
    }
    private async Task RefreshRunSummaryAsync(string runId)
    {
        await filesGate.WaitAsync();
        try
        {
            var notes = new List<string>();
            if (Warning is not null) notes.Add(Warning);
            var attempts = await LoadSummariesAsync(runId, notes);
            if (attempts.Length == 0) return;
            var summary = Consolidate(runId, attempts, notes);
            var directory = Path.Combine(Root, "diagnostics", SafeId(runId));
            await AtomicTextAsync(Path.Combine(directory, "resumo.json"), JsonSerializer.Serialize(summary, JsonOptions));
            await AtomicTextAsync(Path.Combine(directory, "resumo.txt"), SummaryText(summary));
        }
        catch { Warn("O resumo do diagnóstico não pôde ser atualizado."); }
        finally { filesGate.Release(); }
    }
    public async Task<string?> ReadOverviewAsync(string runId)
    {
        await FlushAsync();
        await filesGate.WaitAsync();
        try
        {
            var notes = new List<string>(); var attempts = await LoadSummariesAsync(runId, notes);
            if (attempts.Length == 0) return null;
            var last = attempts.Last();
            return $"{OutcomeLabel(last.Outcome)} · {attempts.Length} tentativa(s) · " +
                (last.DurationMs is { } ms ? $"{TimeSpan.FromMilliseconds(ms):g}" : "tempo indisponível") +
                $" · {last.Floors.Count(f => f.Stage == FloorStage.Closed)}/{last.PlannedFloors} fechados" +
                (last.Warnings.Length > 0 ? $" · {last.Warnings.Length} aviso(s) técnico(s)" : "") +
                (notes.Count > 0 || last.Partial ? " · diagnóstico parcial" : "");
        }
        finally { filesGate.Release(); }
    }

    public async Task<DiagnosticExportResult> ExportAsync(string destination, VisitRun? run = null,
        bool includeScreenshots = false, string? testLabel = null, string? applicationSessionId = null)
    {
        if (run is not null) Register(run);
        var selectedSession = SafeId(applicationSessionId ?? SessionId);
        string? selectedEnvironment = null;
        await FlushAsync();
        var exportId = Guid.NewGuid().ToString("N");
        var cutoff = DateTimeOffset.UtcNow;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var notes = new List<string>();
        if (Warning is not null) notes.Add(Warning);
        await filesGate.WaitAsync();
        try
        {
            cutoff = DateTimeOffset.UtcNow;
            // The writer, rotation and retention share this lock: only complete persisted records are read.
            AttemptSummary[] attempts = run is null ? [] : await LoadSummariesAsync(run.Id, notes, includeMemory: false);
            foreach (var attempt in attempts)
            {
                var prefix = $"attempts/{attempt.AttemptId}/";
                var directory = Path.Combine(Root, AttemptDirectory(attempt.RunId, attempt.AttemptId));
                var events = await ReadEventsAsync(directory, notes);
                var matching = events.Where(e => e.RunId == run!.Id && e.AttemptId == attempt.AttemptId && e.TimestampUtc <= cutoff).OrderBy(e => e.Sequence).ToArray();
                var exported = attempt with { Partial = attempt.Partial || attempt.EndedAtUtc is null };
                files[prefix + "events.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(matching.Select(e => JsonSerializer.Serialize(SanitizeEvent(e), JsonOptions) + "\n")));
                files[prefix + "summary.json"] = JsonBytes(exported);
                Dictionary<string, object?> environment;
                environment = await ReadEnvironmentAsync(Path.Combine(directory, "environment.json"), notes);
                files[prefix + "environment.json"] = JsonBytes(environment);
                if (includeScreenshots)
                    foreach (var artifact in matching.SelectMany(e => e.ArtifactPaths ?? []).Where(SafeArtifact).Distinct())
                    {
                        var source = Path.Combine(directory, artifact.Replace('/', Path.DirectorySeparatorChar));
                        if (!artifact.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                        if (IsReparsePoint(source) || IsReparsePoint(Path.GetDirectoryName(source)!))
                        { notes.Add("Uma captura com redirecionamento de caminho não foi incluída."); continue; }
                        try { files[prefix + artifact] = await File.ReadAllBytesAsync(source); }
                        catch { notes.Add("Uma captura referenciada não está disponível."); }
                    }
            }
            var appEvents = await ReadEventsAsync(Path.Combine(Root, "logs"), notes);
            bool IncludeAppEvent(DiagnosticEvent entry)
            {
                if (entry.TimestampUtc > cutoff) return false;
                if (run is null) return entry.SessionId == selectedSession;
                if (entry.RunId is not null) return entry.RunId == run.Id;
                return attempts.Any(a => entry.SessionId == a.SessionId &&
                    (entry.EventName == "app.started" || entry.EventName == "app.finished" ||
                     entry.TimestampUtc >= a.StartedAtUtc && entry.TimestampUtc <= (a.EndedAtUtc ?? cutoff)));
            }
            var selected = appEvents.Where(IncludeAppEvent).OrderBy(e => e.TimestampUtc).ThenBy(e => e.Sequence).ToArray();
            foreach (var session in attempts.Select(a => a.SessionId).Where(s => s.Length > 0).Distinct())
                if (!selected.Any(e => e.SessionId == session)) notes.Add($"Registros do aplicativo da sessão {session} indisponíveis, possivelmente por retenção.");
            files["app.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(selected.Select(e => JsonSerializer.Serialize(SanitizeEvent(e), JsonOptions) + "\n")));
            if (run is null)
            {
                var environmentPath = Path.Combine(Root, "logs", $"environment-{selectedSession}.json");
                var environment = await ReadEnvironmentAsync(environmentPath, notes);
                selectedEnvironment = environment.GetValueOrDefault("environmentId") as string;
                files["environment.json"] = JsonBytes(environment);
                if (selected.Length == 0) notes.Add("Não há eventos disponíveis para a sessão selecionada.");
                files["resumo.txt"] = Encoding.UTF8.GetBytes($"Diagnóstico do aplicativo Vistora\nSessão: {selectedSession}\nAmbiente: {selectedEnvironment ?? "indisponível"}\nEventos disponíveis: {selected.Length}\n");
                files["resumo.json"] = JsonBytes(new { schemaVersion = 1, sessionId = selectedSession, environmentId = selectedEnvironment, events = selected.Length });
            }
            else
            {
                var summary = Consolidate(run.Id, attempts, notes);
                files["resumo.json"] = JsonBytes(summary);
                files["resumo.txt"] = Encoding.UTF8.GetBytes(SummaryText(summary));
            }
            var partial = notes.Count > 0 || DroppedEvents > 0 || attempts.Any(a => a.Partial || a.EndedAtUtc is null);
            files["LEIA-ME.txt"] = Encoding.UTF8.GetBytes("Pacote portátil de diagnóstico do Vistora.\n" +
                "Leia resumo.txt; examine os eventos JSONL e environment.json de cada tentativa. Datas em UTC, durações em milissegundos.\n" +
                "Os números dos chamados e identificadores técnicos foram conservados para correlação.\n" +
                "Perfis, configurações completas, anexos e sessão do navegador não estão incluídos.\n" +
                (includeScreenshots ? "Capturas incluídas por escolha do usuário; imagens podem conter dados visíveis do chamado.\n" : "Capturas não incluídas.\n") +
                $"Diagnóstico parcial: {(partial ? "sim" : "não")}.\n" + string.Join("\n", notes.Distinct()));
            var inventory = files.Select(f => new { path = f.Key, bytes = f.Value.Length, sha256 = Convert.ToHexString(SHA256.HashData(f.Value)).ToLowerInvariant() }).ToArray();
            files["manifest.json"] = JsonBytes(new
            {
                packageSchemaVersion = 1, exportId, exportedAtUtc = cutoff, exporterVersion = AppVersion,
                runId = run?.Id, scope = run is null ? "application-session" : "visit-all-available-attempts",
                sessionId = run is null ? selectedSession : null, environmentId = run is null ? selectedEnvironment : null,
                testLabel = sanitizer.Clean(testLabel), partial, droppedEvents = DroppedEvents,
                attempts = attempts.Select(a => new { a.AttemptId, a.SessionId, a.EnvironmentId, a.StartedAtUtc, a.EndedAtUtc,
                    lastIncludedSequence = files.ContainsKey($"attempts/{a.AttemptId}/events.jsonl") ? GetLastSequence(files[$"attempts/{a.AttemptId}/events.jsonl"]) : 0 }),
                screenshotsIncluded = includeScreenshots, notes = notes.Distinct().ToArray(), files = inventory
            });
        }
        finally { filesGate.Release(); }
        var target = Path.GetFullPath(destination);
        // Prevent an export from replacing any source file in the diagnostic/data tree.
        var rootPrefix = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;
        if (target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Escolha uma pasta fora dos dados locais do Vistora para salvar o ZIP.");
        var temporary = target + "." + exportId + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    foreach (var file in files)
                    {
                        var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                        await using var output = entry.Open(); await output.WriteAsync(file.Value);
                    }
                await stream.FlushAsync(); stream.Flush(true);
            }
            File.Move(temporary, target, true);
            using var manifest = JsonDocument.Parse(files["manifest.json"]);
            return new(target, new FileInfo(target).Length, manifest.RootElement.GetProperty("partial").GetBoolean());
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<DiagnosticSession[]> ListApplicationSessionsAsync()
    {
        await FlushAsync(); await filesGate.WaitAsync();
        try
        {
            var directory = Path.Combine(Root, "logs");
            var result = new List<DiagnosticSession>();
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "environment-*.json"))
                    try
                    {
                        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        var id = json.RootElement.GetProperty("sessionId").GetString();
                        if (SafeIdentifier(id) is { } valid)
                            result.Add(new(valid, json.RootElement.GetProperty("capturedAtUtc").GetDateTimeOffset()));
                    }
                    catch { }
            if (result.All(s => s.Id != SessionId)) result.Add(new(SessionId, DateTimeOffset.UtcNow));
            return result.OrderByDescending(s => s.StartedAtUtc).ToArray();
        }
        finally { filesGate.Release(); }
    }
    private static long GetLastSequence(byte[] bytes)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return 0;
        using var last = JsonDocument.Parse(lines.Last()); return last.RootElement.GetProperty("sequence").GetInt64();
    }
    private static byte[] JsonBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    private static bool IsReparsePoint(string path) => (File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    private async Task<List<DiagnosticEvent>> ReadEventsAsync(string directory, List<string> notes)
    {
        var result = new List<DiagnosticEvent>();
        if (!Directory.Exists(directory)) { notes.Add("Parte dos eventos não está disponível nesta instalação."); return result; }
        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            try
            {
                var text = await File.ReadAllTextAsync(path);
                if (text.Length > 0 && !text.EndsWith('\n')) notes.Add("Linha final incompleta foi ignorada.");
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length - 1; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<DiagnosticEvent>(lines[i], JsonOptions);
                        if (entry is null || entry.SchemaVersion != 1) { notes.Add("Evento com formato desconhecido não foi exportado."); continue; }
                        result.Add(entry);
                    }
                    catch { notes.Add("Um evento inválido foi ignorado."); }
                }
            }
            catch { notes.Add("Um arquivo de eventos não pôde ser lido."); }
        }
        return result;
    }
    private async Task<Dictionary<string, object?>> ReadEnvironmentAsync(string path, List<string> notes)
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            if (doc.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException();
            return doc.RootElement.EnumerateObject().Where(p => EnvironmentFields.Contains(p.Name)).ToDictionary(p => p.Name,
                p => EnvironmentValue(p.Name, p.Value));
        }
        catch { notes.Add("Metadados do ambiente de origem indisponíveis."); return new() { ["schemaVersion"] = 1, ["availability"] = "indisponível" }; }
    }
    private object? EnvironmentValue(string name, JsonElement value)
    {
        if (name == "selectorOverrideKeys") return value.EnumerateArray().Select(v => v.GetString()).OfType<string>().Where(k => SafeKey(k) == k).ToArray();
        if (name is "windowsVersion" or "dotnetVersion" or "edgeVersion" or "playwrightVersion" or "appVersion")
        {
            var version = value.GetString();
            // Version numbers are structured metadata, not phone numbers or profile values.
            return version is not null && System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+(?:\.\d+){1,4}(?:[-+][a-zA-Z0-9._+-]{1,120})?$")
                ? version : "indisponível";
        }
        if (name is "environmentId" or "sessionId") return SafeIdentifier(value.GetString());
        if (name == "capturedAtUtc") return value.GetDateTimeOffset().ToUniversalTime();
        return SafeDetail(value);
    }

    public async Task CleanAsync()
    {
        await filesGate.WaitAsync();
        try
        {
            var protectedRuns = new HashSet<string>(summaries.Values.Where(s => active.ContainsKey(s.AttemptId)).Select(s => s.RunId));
            var runsDirectory = Path.Combine(Root, "runs");
            var unknownRunState = false;
            if (Directory.Exists(runsDirectory))
                foreach (var path in Directory.EnumerateFiles(runsDirectory, "*.json"))
                    try
                    {
                        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                        var state = run.RootElement.GetProperty("State").GetString();
                        if (state != nameof(RunState.Completed)) protectedRuns.Add(Path.GetFileNameWithoutExtension(path));
                    }
                    catch { unknownRunState = true; }
            var candidates = new List<DirectoryInfo>();
            var diagnostics = Path.Combine(Root, "diagnostics");
            if (!unknownRunState && Directory.Exists(diagnostics))
                foreach (var directory in Directory.EnumerateDirectories(diagnostics))
                {
                    var id = Path.GetFileName(directory);
                    if (!Guid.TryParseExact(id, "N", out _) || protectedRuns.Contains(id)) continue;
                    var known = summaries.Values.Where(s => s.RunId == id).OrderBy(s => s.StartedAtUtc).LastOrDefault();
                    var runPath = Path.Combine(runsDirectory, id + ".json");
                    // Unknown/orphan diagnoses may describe pending actions: preserve them.
                    if (!File.Exists(runPath) && known?.State != RunState.Completed) continue;
                    candidates.Add(new DirectoryInfo(directory));
                }
            var maxBytes = options.MaxStorageMegabytes * 1024L * 1024;
            var total = DiagnosticBytes();
            foreach (var directory in candidates.OrderBy(d => d.LastWriteTimeUtc))
            {
                if (directory.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-options.RetentionDays) && total <= maxBytes) continue;
                var bytes = directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                if (IsReparsePoint(directory.FullName) || directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(f => f.Attributes.HasFlag(FileAttributes.ReparsePoint))) continue;
                directory.Delete(true); total -= bytes;
                foreach (var attempt in summaries.Values.Where(s => s.RunId == directory.Name).ToArray())
                { summaries.TryRemove(attempt.AttemptId, out _); environments.TryRemove(attempt.AttemptId, out _); }
            }
            var logs = Path.Combine(Root, "logs");
            var protectedSessions = summaries.Values.Where(s => protectedRuns.Contains(s.RunId)).Select(s => s.SessionId).ToHashSet();
            protectedSessions.Add(SessionId);
            // Discover sessions for pending visits saved in a previous application process.
            foreach (var id in protectedRuns)
            {
                var notes = new List<string>();
                foreach (var attempt in await LoadSummariesAsync(id, notes)) protectedSessions.Add(attempt.SessionId);
            }
            if (!unknownRunState && Directory.Exists(logs))
                foreach (var file in new DirectoryInfo(logs).EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (protectedSessions.Any(s => file.Name.Contains(s, StringComparison.Ordinal)) || file.Extension is not (".jsonl" or ".json")) continue;
                    if (file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-options.RetentionDays) && total <= maxBytes) continue;
                    var bytes = file.Length; file.Delete(); total -= bytes;
                }
            storageLimited = total > maxBytes;
            if (storageLimited) Warn("O limite de diagnóstico foi atingido. Visitas pendentes foram preservadas; detalhes e capturas estão limitados.");
        }
        catch { Warn("A limpeza dos diagnósticos não pôde ser concluída. Os dados de execução foram preservados."); }
        finally { filesGate.Release(); }
    }
    private long DiagnosticBytes() => new[] { "logs", "diagnostics" }.Select(name => Path.Combine(Root, name)).Where(Directory.Exists)
        .Sum(path => new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
}
