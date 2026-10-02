using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Vistora.Core;

namespace Vistora.Infrastructure;

public sealed partial class DiagnosticService : IDiagnosticSink, IAsyncDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    private sealed record Work(DiagnosticEvent? Event = null, string? Relative = null, string? Json = null, TaskCompletionSource? Completion = null);
    private readonly Channel<Work> queue;
    private readonly int queueCapacity;
    private readonly ConcurrentQueue<DiagnosticEvent> fallback = new();
    private readonly ConcurrentDictionary<string, AttemptSummary> summaries = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> environments = new();
    private readonly ConcurrentDictionary<string, byte> active = new();
    private readonly Dictionary<string, long> sequences = [];
    private readonly object enqueueGate = new();
    private readonly SemaphoreSlim filesGate = new(1, 1);
    private readonly Task writer;
    private readonly DiagnosticSanitizer sanitizer = new();
    private static readonly HashSet<string> DetailFields = new(StringComparer.Ordinal)
    { "stageBefore", "statePersisted", "closed", "started", "assignedToCurrentUser", "resolutionMatches", "publicCommentMatches",
        "teamMatches", "reason", "pageKind", "resourceType", "waitKind", "status", "count", "source", "controlKey", "strategy", "matches" };
    private DiagnosticOptions options = new();
    private long dropped;
    private int disposed;
    private volatile bool storageLimited;
    public string Root { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string EnvironmentId { get; private set; } = Guid.NewGuid().ToString("N");
    public string? Warning { get; private set; }
    public long DroppedEvents => Interlocked.Read(ref dropped);
    public event Action<string>? WarningRaised;
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistora");

    public DiagnosticService(string? root = null, int queueCapacity = 1024)
    {
        Root = Path.GetFullPath(root ?? DefaultRoot);
        this.queueCapacity = Math.Max(8, queueCapacity);
        queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(this.queueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        try
        {
            Directory.CreateDirectory(Root);
            var identity = Path.Combine(Root, "diagnostic-installation.json");
            if (File.Exists(identity))
            {
                var text = File.ReadAllText(identity).Trim();
                string? saved = text;
                if (text.StartsWith('{'))
                { using var json = JsonDocument.Parse(text); saved = json.RootElement.GetProperty("environmentId").GetString(); }
                if (Guid.TryParseExact(saved, "N", out var parsed)) EnvironmentId = parsed.ToString("N");
            }
            File.WriteAllText(identity, JsonSerializer.Serialize(new { schemaVersion = 1, environmentId = EnvironmentId }, JsonOptions), Encoding.UTF8);
        }
        catch { Warn("Não foi possível preparar a pasta de logs. O diagnóstico pode ficar incompleto."); }
        writer = Task.Run(WriteLoopAsync);
        AppEvent("app.started", "Aplicativo iniciado.");
        Enqueue(new(Relative: Path.Combine("logs", $"environment-{SessionId}.json"), Json: JsonSerializer.Serialize(EnvironmentSnapshot(null), JsonOptions)));
    }

    public void Configure(DiagnosticOptions? value)
    {
        options = new DiagnosticOptions
        {
            MinimumLevel = value is not null && Enum.IsDefined(value.MinimumLevel) ? value.MinimumLevel : DiagnosticLevel.Information,
            RetentionDays = Math.Clamp(value?.RetentionDays ?? 30, 1, 365),
            MaxStorageMegabytes = Math.Clamp(value?.MaxStorageMegabytes ?? 200, 1, 2000),
            RotationMegabytes = Math.Clamp(value?.RotationMegabytes ?? 10, 1, 100)
        };
    }
    public void Register(VisitRun run) => sanitizer.Register(run);
    public void Register(ProfileDocument profiles)
    { foreach (var profile in profiles.Profiles) sanitizer.Register(new VisitRun { Profile = profile, Floors = profile.Floors.Select(f => new FloorRun { Floor = f }).ToList() }); }

    private void Warn(string message)
    {
        var first = Warning is null;
        Warning ??= message;
        if (first) { try { WarningRaised?.Invoke(message); } catch { } }
    }
    private void Lost()
    { Interlocked.Increment(ref dropped); Warn("Alguns registros não puderam ser gravados. O diagnóstico está incompleto."); }

    public void AppEvent(string name, string message, DiagnosticLevel level = DiagnosticLevel.Information,
        Exception? exception = null, string? runId = null, string? code = null)
    {
        var error = exception is null ? null : sanitizer.Error(exception, Guid.NewGuid().ToString("N"));
        Write(new DiagnosticEvent { Component = "app", EventName = name, Message = sanitizer.Clean(message), Level = level,
            Exception = error, RunId = runId, ErrorCode = code });
    }
    public void Write(DiagnosticEvent entry)
    {
        if (entry.Level < options.MinimumLevel || (storageLimited && entry.Level == DiagnosticLevel.Debug)) return;
        // No arbitrary application object is retained by the background writer.
        lock (enqueueGate)
        {
            if (disposed != 0) return;
            if (entry.Level == DiagnosticLevel.Debug && queue.Reader.Count >= queueCapacity * 3 / 4) { Lost(); return; }
            var key = entry.AttemptId ?? SessionId;
            sequences[key] = sequences.GetValueOrDefault(key) + 1;
            entry = SanitizeEvent(entry) with { Sequence = sequences[key], SessionId = SessionId, EnvironmentId = EnvironmentId };
            if (!queue.Writer.TryWrite(new(Event: entry)))
            {
                Lost();
                if (entry.Level != DiagnosticLevel.Debug && fallback.Count < 128) fallback.Enqueue(entry);
            }
        }
    }
    private DiagnosticEvent SanitizeEvent(DiagnosticEvent entry) => entry with
    {
        EventName = SafeKey(entry.EventName), Component = SafeKey(entry.Component), Step = entry.Step is null ? null : SafeKey(entry.Step),
        RunId = SafeIdentifier(entry.RunId), AttemptId = SafeIdentifier(entry.AttemptId), ProfileId = SafeIdentifier(entry.ProfileId),
        FloorId = SafeIdentifier(entry.FloorId), SessionId = SafeIdentifier(entry.SessionId) ?? "", EnvironmentId = SafeIdentifier(entry.EnvironmentId) ?? "",
        OperationId = SafeIdentifier(entry.OperationId), IssueKey = SafeIssueKey(entry.IssueKey),
        Outcome = entry.Outcome is null ? null : SafeKey(entry.Outcome), ErrorCode = entry.ErrorCode is null ? null : SafeKey(entry.ErrorCode),
        StageBefore = entry.StageBefore is null ? null : SafeKey(entry.StageBefore), StageAfter = entry.StageAfter is null ? null : SafeKey(entry.StageAfter),
        PendingAction = entry.PendingAction is null ? null : SafeKey(entry.PendingAction),
        Message = sanitizer.Clean(entry.Message),
        Exception = CleanError(entry.Exception),
        Details = entry.Details?.Where(x => DetailFields.Contains(x.Key)).ToDictionary(x => x.Key, x => SafeDetail(x.Value)),
        ArtifactPaths = entry.ArtifactPaths?.Where(SafeArtifact).ToArray()
    };
    private static string? SafeIdentifier(string? value) => Guid.TryParseExact(value, "N", out var id) ? id.ToString("N") : null;
    private static string? SafeIssueKey(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Z][A-Z0-9]*-\d+$") ? value : null;
    private static string SafeKey(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z][a-zA-Z0-9_.-]{0,100}$") ? value : "unavailable";
    private object? SafeDetail(object? value)
    {
        if (value is null || value is bool || value is int || value is long || value is double) return value;
        if (value is string text) return sanitizer.Clean(text);
        if (value is JsonElement element)
            return element.ValueKind switch
            {
                JsonValueKind.String => sanitizer.Clean(element.GetString()), JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out var number) ? number : element.GetDouble(), _ => null
            };
        return null;
    }
    private DiagnosticError? CleanError(DiagnosticError? error) => error is null ? null : error with
    { Message = sanitizer.Clean(error.Message), StackTrace = sanitizer.Clean(error.StackTrace), Inner = CleanError(error.Inner) };
    private static bool SafeArtifact(string path) => System.Text.RegularExpressions.Regex.IsMatch(path, @"^artifacts/[a-zA-Z0-9_-]+\.png$");
    private void Enqueue(Work work) { if (!queue.Writer.TryWrite(work)) Lost(); }

    public void StartAttempt(AttemptSummary summary, VisitRun run)
    {
        Register(run);
        active[summary.AttemptId] = 0;
        summaries[summary.AttemptId] = summary;
        var environment = EnvironmentSnapshot(run.Settings);
        environments[summary.AttemptId] = environment;
        var directory = AttemptDirectory(summary.RunId, summary.AttemptId);
        Enqueue(new(Relative: Path.Combine(directory, "environment.json"), Json: JsonSerializer.Serialize(environment, JsonOptions)));
        Enqueue(new(Relative: Path.Combine(directory, "summary.json"), Json: JsonSerializer.Serialize(summary, JsonOptions)));
    }
    public async Task FinishAttemptAsync(AttemptSummary summary)
    {
        summary = summary with { Partial = summary.Partial || Warning is not null, DroppedEvents = DroppedEvents };
        summaries[summary.AttemptId] = summary;
        var directory = AttemptDirectory(summary.RunId, summary.AttemptId);
        Enqueue(new(Relative: Path.Combine(directory, "summary.json"), Json: JsonSerializer.Serialize(summary, JsonOptions)));
        await FlushAsync();
        active.TryRemove(summary.AttemptId, out _);
        await RefreshRunSummaryAsync(summary.RunId);
        await CleanAsync();
    }
    public void UpdateAttempt(AttemptSummary summary)
    {
        summaries[summary.AttemptId] = summary;
        Enqueue(new(Relative: Path.Combine(AttemptDirectory(summary.RunId, summary.AttemptId), "summary.json"),
            Json: JsonSerializer.Serialize(summary, JsonOptions)));
    }
    public void UpdateBrowserVersion(string attemptId, string? version, string? playwrightVersion)
    {
        if (!environments.TryGetValue(attemptId, out var value) || !summaries.TryGetValue(attemptId, out var summary)) return;
        var copy = new Dictionary<string, object?>(value) { ["edgeVersion"] = version ?? "indisponível", ["playwrightVersion"] = playwrightVersion ?? "indisponível" };
        environments[attemptId] = copy;
        Enqueue(new(Relative: Path.Combine(AttemptDirectory(summary.RunId, attemptId), "environment.json"), Json: JsonSerializer.Serialize(copy, JsonOptions)));
    }
    public string ArtifactDirectory(string runId, string attemptId)
    {
        if (storageLimited) throw new IOException("O orçamento de diagnóstico foi atingido; capturas suspensas.");
        return Path.Combine(Root, AttemptDirectory(runId, attemptId), "artifacts");
    }
    private static string SafeId(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new ArgumentException("Identificador inválido.");
    private static string AttemptDirectory(string runId, string attemptId) => Path.Combine("diagnostics", SafeId(runId), "attempts", SafeId(attemptId));

    private Dictionary<string, object?> EnvironmentSnapshot(AppSettings? settings) => new()
    {
        ["schemaVersion"] = 1, ["capturedAtUtc"] = DateTimeOffset.UtcNow, ["environmentId"] = EnvironmentId,
        ["sessionId"] = SessionId, ["appVersion"] = AppVersion, ["windowsVersion"] = Environment.OSVersion.Version.ToString(),
        ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(), ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["dotnetVersion"] = Environment.Version.ToString(), ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["culture"] = CultureInfo.CurrentCulture.Name, ["timeZone"] = TimeZoneInfo.Local.Id,
        ["viewportWidth"] = 1440, ["viewportHeight"] = 960, ["timeoutSeconds"] = settings?.TimeoutSeconds,
        ["loginTimeoutMinutes"] = settings?.LoginTimeoutMinutes, ["diagnosticLevel"] = options.MinimumLevel.ToString(),
        ["selectorOverrideKeys"] = settings?.SelectorOverrides.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"^[a-zA-Z][a-zA-Z0-9.]{0,80}$")).Order().ToArray() ?? [],
        ["edgeVersion"] = "indisponível", ["playwrightVersion"] = "indisponível"
    };
    public static string AppVersion => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "indisponível";

    public async Task FlushAsync()
    {
        if (disposed != 0) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await queue.Writer.WriteAsync(new(Completion: done), timeout.Token); await done.Task.WaitAsync(timeout.Token); }
        catch { Warn("Não foi possível concluir a gravação dos logs no tempo esperado. O diagnóstico pode estar parcial."); }
    }
    private async Task WriteLoopAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync())
        {
            await filesGate.WaitAsync();
            try
            {
                if (work.Event is not null) await AppendAsync(work.Event);
                if (work.Relative is not null && work.Json is not null) await AtomicTextAsync(Path.Combine(Root, work.Relative), work.Json);
                while (fallback.TryDequeue(out var entry)) await AppendAsync(entry);
            }
            catch
            {
                Lost();
                if (work.Event is not null && fallback.Count < 128) fallback.Enqueue(work.Event);
            }
            finally { filesGate.Release(); work.Completion?.TrySetResult(); }
        }
    }
    private async Task AppendAsync(DiagnosticEvent entry)
    {
        var relative = entry.AttemptId is not null && entry.RunId is not null ? Path.Combine(AttemptDirectory(entry.RunId, entry.AttemptId), "events.jsonl")
            : Path.Combine("logs", $"app-{DateTime.UtcNow:yyyyMMdd}-{SessionId}.jsonl");
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var maxBytes = options.RotationMegabytes * 1024L * 1024;
        if (File.Exists(path) && new FileInfo(path).Length >= maxBytes)
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".jsonl"));
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry, JsonOptions) + "\n", new UTF8Encoding(false));
    }
    internal static async Task AtomicTextAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed != 0) return;
        AppEvent("app.finished", "Aplicativo encerrado.");
        await FlushAsync();
        Interlocked.Exchange(ref disposed, 1);
        queue.Writer.TryComplete();
        try { await writer.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        // Do not dispose the semaphore if the writer is still finishing after a bounded wait.
    }
}
