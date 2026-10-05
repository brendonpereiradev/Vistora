using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vistora.Core;

public enum DiagnosticLevel { Debug, Information, Warning, Error }

public sealed class DiagnosticOptions
{
    public DiagnosticLevel MinimumLevel { get; set; } = DiagnosticLevel.Debug;
    public int RetentionDays { get; set; } = 30;
    public int MaxStorageMegabytes { get; set; } = 200;
    public int RotationMegabytes { get; set; } = 10;
}

public sealed record DiagnosticError(string Id, string Type, string Message, string? StackTrace, DiagnosticError? Inner);

public sealed record DiagnosticEvent
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public long Sequence { get; init; }
    public DiagnosticLevel Level { get; init; } = DiagnosticLevel.Information;
    public string EventName { get; init; } = "";
    public string Component { get; init; } = "";
    public string Message { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string? RunId { get; init; }
    public string? AttemptId { get; init; }
    public string? ProfileId { get; init; }
    public string? FloorId { get; init; }
    public string? IssueKey { get; init; }
    public string? OperationId { get; init; }
    public string? Step { get; init; }
    public string? StageBefore { get; init; }
    public string? StageAfter { get; init; }
    public string? PendingAction { get; init; }
    public string? Outcome { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public DiagnosticError? Exception { get; init; }
    public Dictionary<string, object?>? Details { get; init; }
    public string[]? ArtifactPaths { get; init; }
}

public sealed record DiagnosticFloor(string FloorId, string? IssueKey, FloorStage Stage, PendingAction PendingAction);
public sealed record DiagnosticWarning(string Code, string ErrorId);

public sealed record AttemptSummary
{
    public int SchemaVersion { get; init; } = 1;
    public string RunId { get; init; } = "";
    public string AttemptId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string Kind { get; init; } = "execute";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public double? DurationMs { get; init; }
    public string Outcome { get; init; } = "Running";
    public RunState State { get; init; }
    public bool StatePersisted { get; init; }
    public int CreatedThisAttempt { get; init; }
    public int ReusedIssues { get; init; }
    public int PlannedFloors { get; init; }
    public DiagnosticFloor[] Floors { get; init; } = [];
    public Dictionary<string, double> StepDurationsMs { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? ErrorId { get; init; }
    public string? FailedStep { get; init; }
    public string? FailedFloorId { get; init; }
    public bool Partial { get; init; }
    public long DroppedEvents { get; init; }
    public DiagnosticWarning[] Warnings { get; init; } = [];
}

public interface IDiagnosticSink
{
    string SessionId { get; }
    string EnvironmentId { get; }
    void Write(DiagnosticEvent entry);
    Task FlushAsync();
    void StartAttempt(AttemptSummary summary, VisitRun run);
    void UpdateAttempt(AttemptSummary summary) { }
    Task FinishAttemptAsync(AttemptSummary summary);
}

public sealed class NullDiagnosticSink : IDiagnosticSink
{
    public static readonly NullDiagnosticSink Instance = new();
    public string SessionId => "";
    public string EnvironmentId => "";
    public void Write(DiagnosticEvent entry) { }
    public Task FlushAsync() => Task.CompletedTask;
    public void StartAttempt(AttemptSummary summary, VisitRun run) { }
    public Task FinishAttemptAsync(AttemptSummary summary) => Task.CompletedTask;
}

// Explicitly sanitize before serialization, including Playwright call logs and exception causes.
public sealed class DiagnosticSanitizer
{
    private readonly List<string> sensitive = [];
    private readonly object gate = new();
    public void Register(VisitRun run)
    {
        var p = run.Profile;
        Register([p.Name, p.ReporterName, p.ReporterEmail, p.FullName, p.Extension, p.Unit, p.Phone,
            p.Title, p.Description, run.TechnicianName, run.Settings.ClosingTeam, .. p.Attachments,
            .. run.Settings.SelectorOverrides.Values, .. run.Floors.SelectMany(f =>
                new[] { f.Floor.Name, f.Floor.Sector, f.Floor.Room, f.Floor.Resolution })]);
    }
    public void Register(IEnumerable<string> values)
    {
        lock (gate)
        {
            foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
                if (!sensitive.Contains(value, StringComparer.OrdinalIgnoreCase)) sensitive.Add(value);
            sensitive.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }
    public string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value;
        lock (gate)
            foreach (var secret in sensitive)
                text = text.Replace(secret, "[removido]", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)https?://[^\s<>""']+", "[endereço]");
        text = Regex.Replace(text, @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[e-mail]");
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [removido]");
        text = Regex.Replace(text, @"(?i)(password|passwd|senha|token|secret|authorization|cookie)\s*[=:]\s*[^\r\n,;]+", "$1=[removido]");
        text = Regex.Replace(text, @"(?i)\bin\s+[A-Z]:\\[^\r\n]*\\([^\\\r\n]+\.cs):line\s+(\d+)", "em $1:linha $2");
        text = Regex.Replace(text, @"(?i)\b[A-Z]:\\[^\r\n""<>]+", "[caminho]");
        text = Regex.Replace(text, @"\\\\[^\s\\]+\\[^\r\n""<>]+", "[caminho]");
        text = Regex.Replace(text, @"(?<!\w)(?:\+?\d[\s().-]*){8,15}(?!\w)", "[telefone]");
        // Raw locator logs can contain arbitrary input, not only values known in the profile.
        var callLog = text.IndexOf("Call log:", StringComparison.OrdinalIgnoreCase);
        if (callLog >= 0) text = text[..callLog] + "Detalhes do Playwright omitidos; consulte a chave do controle nos eventos.";
        return text.Length > 12000 ? text[..12000] + "…" : text;
    }
    public DiagnosticError Error(Exception error, string id, int depth = 0) => new(id,
        error.GetType().FullName ?? error.GetType().Name, Clean(error.Message), Clean(error.StackTrace),
        depth < 5 && error.InnerException is not null ? Error(error.InnerException, id, depth + 1) : null);
}

public static class DiagnosticErrors
{
    public static string Code(Exception error, string? step = null)
    {
        if (error is OperationCanceledException) return "CANCELLED";
        if (error.Data["DiagnosticCode"] is string explicitCode) return explicitCode;
        if (step == "store.save") return "STORE_WRITE_FAILED";
        if (step == "browser.create") return "BROWSER_START_FAILED";
        if (step == "browser.dispose") return "BROWSER_CLOSE_FAILED";
        if (step == "create.submit") return "CREATE_RESULT_UNCERTAIN";
        if (step == "close.submit") return "CLOSE_RESULT_UNCERTAIN";
        if (error is ReconciliationException) return step == "close.verify" ? "CLOSE_VERIFICATION_MISMATCH" : "RECONCILIATION_REQUIRED";
        if (error.Message.Contains("única", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("mais de um", StringComparison.OrdinalIgnoreCase)) return "FIELD_AMBIGUOUS";
        if (error is TimeoutException || error.Message.Contains("tempo", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)) return "STATUS_TIMEOUT";
        if (error.Message.Contains("campo", StringComparison.OrdinalIgnoreCase)) return "FIELD_NOT_FOUND";
        return "OPERATION_FAILED";
    }
}

public sealed class DiagnosticAttempt
{
    private readonly IDiagnosticSink sink;
    private readonly VisitRun run;
    private readonly DiagnosticSanitizer sanitizer = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, double> durations = [];
    private readonly Dictionary<Exception, string> errors = new(ReferenceEqualityComparer.Instance);
    private readonly List<DiagnosticWarning> warnings = [];
    private readonly int initialIssues;
    private readonly AttemptSummary initial;
    private string? operationId;
    private string? step;
    private FloorRun? floor;
    private string? stageBefore;
    private bool finished;
    public string Id => initial.AttemptId;
    public string? CurrentOperationId => operationId;
    public string? FailedStep { get; private set; }
    public string? FailedFloorId { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorId { get; private set; }

    public DiagnosticAttempt(IDiagnosticSink? sink, VisitRun run, string kind = "execute")
    {
        this.sink = sink ?? NullDiagnosticSink.Instance;
        this.run = run;
        initialIssues = run.Floors.Count(f => f.IssueKey is not null);
        sanitizer.Register(run);
        initial = Snapshot(false) with { RunId = run.Id, AttemptId = Guid.NewGuid().ToString("N"),
            SessionId = this.sink.SessionId, EnvironmentId = this.sink.EnvironmentId, Kind = kind,
            StartedAtUtc = DateTimeOffset.UtcNow, ReusedIssues = initialIssues };
        try { this.sink.StartAttempt(initial, run); } catch { /* Diagnostics never changes the business flow. */ }
        Event("attempt.started", "Tentativa iniciada.");
    }

    public void Event(string name, string message, DiagnosticLevel level = DiagnosticLevel.Information,
        Dictionary<string, object?>? details = null, string? outcome = null, string[]? artifacts = null,
        DiagnosticError? error = null, double? durationMs = null, string? errorCode = null)
    {
        sanitizer.Register([run.TechnicianName]);
        var safeDetails = details?.ToDictionary(x => x.Key, x => x.Value is string s ? (object?)sanitizer.Clean(s) : x.Value);
        try
        {
            sink.Write(new DiagnosticEvent { Level = level, EventName = name, Component = "execution",
                Message = sanitizer.Clean(message), RunId = run.Id, AttemptId = Id, ProfileId = run.Profile.Id,
                FloorId = floor?.Floor.Id, IssueKey = floor?.IssueKey, OperationId = operationId, Step = step,
                StageBefore = stageBefore, StageAfter = floor?.Stage.ToString(), PendingAction = floor?.PendingAction.ToString(),
                Outcome = outcome, Details = safeDetails, ArtifactPaths = artifacts, Exception = error,
                DurationMs = durationMs, ErrorCode = errorCode });
        }
        catch { /* A broken sink cannot trigger a repeated Jira action. */ }
    }

    public void Error(Exception error, string? failedStep = null, bool secondary = false)
    {
        if (errors.ContainsKey(error)) return;
        var id = Guid.NewGuid().ToString("N"); errors.Add(error, id);
        var code = DiagnosticErrors.Code(error, failedStep ?? step);
        secondary = secondary || ErrorId is not null;
        if (!secondary && ErrorId is null)
        { ErrorId = id; ErrorCode = code; FailedStep = failedStep ?? step; FailedFloorId = floor?.Floor.Id; }
        else warnings.Add(new(code, id));
        Event(secondary ? "error.secondary" : "operation.failed", secondary ? "Falha secundária no diagnóstico ou encerramento." : "A operação falhou.",
            error is ReconciliationException || secondary ? DiagnosticLevel.Warning : DiagnosticLevel.Error,
            error: sanitizer.Error(error, id), errorCode: code, outcome: "failed");
    }

    public async Task<T> StepAsync<T>(string name, FloorRun? currentFloor, Func<Task<T>> operation)
    {
        var previous = (step, floor, operationId, stageBefore);
        step = name; floor = currentFloor; operationId = Guid.NewGuid().ToString("N");
        stageBefore = floor?.Stage.ToString();
        var begin = Stopwatch.GetTimestamp();
        Event("operation.started", "Etapa iniciada.", details: new() { ["stageBefore"] = floor?.Stage.ToString() });
        try
        {
            var result = await operation();
            var elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            Event("operation.finished", "Etapa executada.", outcome: "observed", durationMs: elapsed);
            if (name != "browser.connect" && elapsed > Math.Max(1, run.Settings.TimeoutSeconds) * 800)
                Event("operation.slow", "Etapa demorou próximo ao limite de espera.", DiagnosticLevel.Warning, durationMs: elapsed);
            return result;
        }
        catch (OperationCanceledException)
        { Event("operation.cancelled", "Etapa interrompida.", outcome: "cancelled"); throw; }
        catch (Exception ex) { Error(ex); throw; }
        finally
        {
            RecordDuration(name, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            (step, floor, operationId, stageBefore) = previous;
        }
    }
    public Task StepAsync(string name, FloorRun? currentFloor, Func<Task> operation) => StepAsync(name, currentFloor, async () => { await operation(); return true; });
    public void RecordDuration(string name, double milliseconds) => durations[name] = durations.GetValueOrDefault(name) + milliseconds;
    public Task FlushAsync() => SafeFlushAsync();
    private async Task SafeFlushAsync() { try { await sink.FlushAsync(); } catch { } }
    public void Confirm(FloorRun currentFloor, string name, IssueSnapshot? snapshot = null)
    {
        var previous = floor; floor = currentFloor;
        Event(name, "Resultado conferido e progresso salvo.", outcome: "confirmed", details: snapshot is null ? null : new()
        {
            ["closed"] = snapshot.Closed, ["started"] = snapshot.Started, ["assignedToCurrentUser"] = snapshot.AssignedToCurrentUser,
            ["resolutionMatches"] = snapshot.ResolutionMatches, ["publicCommentMatches"] = snapshot.PublicCommentMatches, ["teamMatches"] = snapshot.TeamMatches
        });
        floor = previous;
    }
    public void SnapshotEvent(FloorRun currentFloor, IssueSnapshot snapshot)
    {
        var previous = floor; floor = currentFloor;
        Event("issue.observed", "Estado do chamado observado.", details: new()
        {
            ["closed"] = snapshot.Closed, ["started"] = snapshot.Started, ["assignedToCurrentUser"] = snapshot.AssignedToCurrentUser,
            ["resolutionMatches"] = snapshot.ResolutionMatches, ["publicCommentMatches"] = snapshot.PublicCommentMatches, ["teamMatches"] = snapshot.TeamMatches
        }); floor = previous;
    }
    public void Skip(FloorRun currentFloor, string reason)
    { var previous = floor; floor = currentFloor; Event("operation.skipped", "Etapa já registrada ou conferida.", details: new() { ["reason"] = reason }, outcome: "skipped"); floor = previous; }
    private AttemptSummary Snapshot(bool persisted) => new()
    {
        State = run.State, StatePersisted = persisted, PlannedFloors = run.Floors.Count,
        Floors = run.Floors.Select(f => new DiagnosticFloor(f.Floor.Id, f.IssueKey, f.Stage, f.PendingAction)).ToArray()
    };
    public void RecordState(bool persisted)
    {
        var snapshot = Snapshot(persisted);
        try { sink.UpdateAttempt(initial with { State = snapshot.State, StatePersisted = persisted, Floors = snapshot.Floors,
            CreatedThisAttempt = Math.Max(0, run.Floors.Count(f => f.IssueKey is not null) - initialIssues),
            StepDurationsMs = new(durations) }); } catch { }
    }
    public async Task FinishAsync(bool persisted, string? outcome = null)
    {
        if (finished) return; finished = true;
        var summary = Snapshot(persisted) with
        {
            RunId = run.Id, AttemptId = Id, SessionId = sink.SessionId, EnvironmentId = sink.EnvironmentId,
            Kind = initial.Kind, StartedAtUtc = initial.StartedAtUtc, EndedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, Outcome = outcome ?? run.State.ToString(),
            CreatedThisAttempt = Math.Max(0, run.Floors.Count(f => f.IssueKey is not null) - initialIssues), ReusedIssues = initialIssues,
            StepDurationsMs = new(durations), ErrorCode = ErrorCode, ErrorId = ErrorId, FailedStep = FailedStep,
            FailedFloorId = FailedFloorId, Warnings = warnings.ToArray()
        };
        Event("attempt.finished", "Tentativa encerrada.", outcome: summary.Outcome,
            details: new() { ["statePersisted"] = persisted }, durationMs: summary.DurationMs);
        try { await sink.FinishAttemptAsync(summary).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await SafeFlushAsync();
    }
}
