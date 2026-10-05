using System.Text.Json;

namespace Vistora.Infrastructure;

internal static class VisitDataPaths
{
    public static void Check(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("O caminho de limpeza está fora dos dados locais do Vistora.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("A limpeza não pode remover dados em pastas ou arquivos redirecionados.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    public static void CheckTree(string root, string directory)
    {
        Check(root, directory);
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            Check(root, entry);
            if (Directory.Exists(entry)) CheckTree(root, entry);
        }
    }
}

public sealed partial class DiagnosticService
{
    private bool clearingVisits;

    public async Task ClearVisitsAsync()
    {
        lock (enqueueGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (clearingVisits || !active.IsEmpty)
                throw new InvalidOperationException("Aguarde a conclusão da execução antes de limpar o histórico.");
            clearingVisits = true;
        }
        try
        {
            // A command in the writer queue runs after all earlier writes, under filesGate.
            // FlushAsync is intentionally insufficient here: it tolerates timeouts and failures.
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await queue.Writer.WriteAsync(new(Completion: done, Operation: ClearVisitFilesAsync));
            await done.Task;
        }
        finally { lock (enqueueGate) clearingVisits = false; }
    }

    private async Task ClearVisitFilesAsync()
    {
        var directory = Path.Combine(Root, "diagnostics");
        var logs = Path.Combine(Root, "logs");
        VisitDataPaths.CheckTree(Root, directory);
        VisitDataPaths.Check(Root, logs);
        var changes = new List<(string Path, string Text)>();
        if (Directory.Exists(logs))
            foreach (var path in Directory.EnumerateFiles(logs, "*.jsonl"))
            {
                VisitDataPaths.Check(Root, path);
                var kept = new List<string>();
                var changed = false;
                foreach (var line in await File.ReadAllLinesAsync(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) { kept.Add(line); continue; }
                    using var json = JsonDocument.Parse(line);
                    var entry = json.RootElement;
                    var hasRun = entry.TryGetProperty("runId", out var runId) || entry.TryGetProperty("RunId", out runId);
                    if (hasRun && runId.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(runId.GetString()))
                        changed = true;
                    else kept.Add(line);
                }
                if (changed) changes.Add((path, string.Join('\n', kept) + (kept.Count > 0 ? "\n" : "")));
            }
        foreach (var change in changes)
        {
            if (change.Text.Length == 0) File.Delete(change.Path);
            else await AtomicTextAsync(change.Path, change.Text);
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        lock (enqueueGate)
        {
            foreach (var id in summaries.Keys) sequences.Remove(id);
            summaries.Clear(); environments.Clear();
        }
        storageLimited = DiagnosticBytes() > options.MaxStorageMegabytes * 1024L * 1024;
    }
}
