using System.Text;
using System.Text.Json;
using Vistora.Core;

namespace Vistora.Infrastructure;

public sealed class LocalStore : IRunStore
{
    public string Root { get; }
    public DiagnosticService? Diagnostics { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    public LocalStore(string? root = null, DiagnosticService? diagnostics = null)
    {
        Diagnostics = diagnostics;
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistora");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "runs"));
    }
    public string BrowserDirectory => Path.Combine(Root, "browser");
    public string DiagnosticsDirectory(string id) => Path.Combine(Root, "diagnostics", SafeId(id));
    private static string SafeId(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new ArgumentException("Identificador inválido.");

    public Task<ProfileDocument> LoadProfilesAsync() => ReadAsync("profiles.json", () => new ProfileDocument());
    public Task<AppSettings> LoadSettingsAsync() => ReadAsync("settings.json", () => new AppSettings());
    public Task SaveProfilesAsync(ProfileDocument value) { Diagnostics?.Register(value); return WriteAsync("profiles.json", value); }
    public Task SaveSettingsAsync(AppSettings value) => WriteAsync("settings.json", value);
    public Task SaveRunAsync(VisitRun run) { Diagnostics?.Register(run); return WriteAsync(Path.Combine("runs", SafeId(run.Id) + ".json"), run); }

    private async Task<T> ReadAsync<T>(string relative, Func<T> create)
    {
        var path = Path.Combine(Root, relative);
        if (!File.Exists(path)) return create();
        try
        {
            T value;
            try { value = await ReadFileAsync<T>(path); }
            catch (JsonException ex)
            {
                if (!File.Exists(path + ".bak")) throw new InvalidDataException($"O arquivo {relative} não pôde ser lido. Os dados foram preservados.", ex);
                value = await ReadFileAsync<T>(path + ".bak");
                Diagnostics?.AppEvent("store.backup_recovered", "Dados recuperados da cópia anterior.", DiagnosticLevel.Warning);
            }
            if (value is ProfileDocument profiles) Diagnostics?.Register(profiles);
            if (value is VisitRun run) Diagnostics?.Register(run);
            return value;
        }
        catch (Exception ex)
        { Diagnostics?.AppEvent("store.read_failed", "Não foi possível ler os dados locais.", DiagnosticLevel.Error, ex, code: "STORE_READ_FAILED"); throw; }
    }

    private static async Task<T> ReadFileAsync<T>(string path)
    {
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() != 1)
            throw new InvalidDataException("Versão de dados incompatível. Use a versão apropriada do aplicativo.");
        return JsonSerializer.Deserialize<T>(json, Serialization.Options) ?? throw new JsonException("Arquivo vazio.");
    }

    private async Task WriteAsync<T>(string relative, T value)
    {
        await gate.WaitAsync();
        var path = Path.Combine(Root, relative);
        var temp = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(value, Serialization.Options);
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await file.WriteAsync(bytes);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        catch (Exception ex)
        {
            Diagnostics?.AppEvent("store.write_failed", "Não foi possível salvar os dados locais.", DiagnosticLevel.Error, ex,
                value is VisitRun run ? run.Id : null, "STORE_WRITE_FAILED");
            throw;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            finally { gate.Release(); }
        }
    }

    public async Task<List<VisitRun>> LoadRunsAsync()
    {
        List<VisitRun> result = [];
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "runs"), "*.json"))
        {
            var run = await ReadAsync(Path.Combine("runs", Path.GetFileName(path)), () => new VisitRun());
            if (run.State == RunState.Running)
            {
                run.State = RunState.Interrupted;
                run.Message = "A execução anterior foi interrompida. Confira e retome.";
                Diagnostics?.AppEvent("run.recovered_after_shutdown", "Execução sem conclusão observada recuperada para conferência.", DiagnosticLevel.Warning, runId: run.Id);
            }
            result.Add(run);
        }
        return result.OrderByDescending(r => r.CreatedAt).ToList();
    }
}
