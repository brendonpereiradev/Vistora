using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistora.Core;

public static class Serialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public sealed class Floor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Novo pavimento";
    public bool Selected { get; set; } = true;
    public string Sector { get; set; } = "Todos os setores.";
    public string Room { get; set; } = "Todas as salas.";
    public string Resolution { get; set; } = "";
}

public sealed class VisitProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Minha unidade";
    public string ReporterName { get; set; } = "";
    public string ReporterEmail { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Title { get; set; } = "Visita preventiva";
    public string Description { get; set; } = "Solicito visita preventiva.";
    public List<string> Attachments { get; set; } = [];
    public List<Floor> Floors { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class ProfileDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveProfileId { get; set; }
    public List<VisitProfile> Profiles { get; set; } = [];
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    // Endereços fictícios; a configuração real permanece nos dados locais do usuário.
    public string JiraUrl { get; set; } = "https://jira.example.com";
    public string PortalUrl { get; set; } = "https://jira.example.com/servicedesk/customer/portal/1/group/1/create/1";
    public string QueueUrl { get; set; } = "https://jira.example.com/jira/servicedesk/projects/SD/queues/custom/1";
    public int TimeoutSeconds { get; set; } = 60;
    public int LoginTimeoutMinutes { get; set; } = 15;
    public Dictionary<string, string> SelectorOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ClosingTeam { get; set; } = "Field Services";
    public DiagnosticOptions Diagnostics { get; set; } = new();
}

public enum FloorStage { Pending, Created, Assigned, Started, Closed }
public enum PendingAction { None, Create, Assign, Start, Close }
public enum RunState { Running, Interrupted, Failed, NeedsReconciliation, Completed }

public sealed class FloorRun
{
    public Floor Floor { get; set; } = new();
    public string? IssueKey { get; set; }
    public FloorStage Stage { get; set; }
    public PendingAction PendingAction { get; set; }
    public string Message { get; set; } = "Aguardando";
    [JsonIgnore] public string Name => Floor.Name;
    [JsonIgnore] public string StageLabel => Stage switch
    {
        FloorStage.Pending => "Pendente", FloorStage.Created => "Aberto", FloorStage.Assigned => "Atribuído",
        FloorStage.Started => "Em atendimento", FloorStage.Closed => "Fechado", _ => "Pendente"
    };
}

public sealed class VisitRun
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public VisitProfile Profile { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public string TechnicianName { get; set; } = "";
    public List<FloorRun> Floors { get; set; } = [];
    public RunState State { get; set; } = RunState.Interrupted;
    public string Message { get; set; } = "Pronta para iniciar";
    [JsonIgnore] public string ProfileName => Profile.Name;
    [JsonIgnore] public string DateLabel => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    [JsonIgnore] public string StateLabel => State switch
    {
        RunState.Completed => "Concluída", RunState.Running => "Em execução", RunState.Interrupted => "Interrompida",
        RunState.NeedsReconciliation => "Vincular chamado", _ => "Requer atenção"
    };
    [JsonIgnore] public string ProgressLabel => $"{Floors.Count(f => f.Stage == FloorStage.Closed)} de {Floors.Count} fechados";
}

public sealed record IssueSnapshot(string Key, string Status, bool AssignedToCurrentUser, bool Closed,
    bool Started, bool ResolutionMatches, bool PublicCommentMatches, bool TeamMatches);

public sealed class ReconciliationException(string message) : Exception(message);

public static class ProfileValidation
{
    public static List<string> Validate(VisitProfile profile, AppSettings settings)
    {
        List<string> errors = [];
        foreach (var (label, value) in new[] { ("Nome do perfil", profile.Name), ("Solicitante", profile.ReporterName),
                     ("E-mail do solicitante", profile.ReporterEmail), ("Nome completo", profile.FullName),
                     ("Ramal", profile.Extension), ("Unidade", profile.Unit), ("Título", profile.Title),
                     ("Detalhes / justificativas", profile.Description) })
            if (string.IsNullOrWhiteSpace(value)) errors.Add($"Preencha {label}.");
        if (!string.IsNullOrWhiteSpace(profile.ReporterEmail) &&
            !System.Net.Mail.MailAddress.TryCreate(profile.ReporterEmail, out _)) errors.Add("Informe um e-mail válido.");
        if (profile.Floors.Select(f => f.Id).Distinct().Count() != profile.Floors.Count)
            errors.Add("Os identificadores dos pavimentos estão duplicados.");
        if (profile.Floors.Select(f => f.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Floors.Count)
            errors.Add("Use nomes diferentes para os pavimentos.");
        var floors = profile.Floors.Where(f => f.Selected).ToList();
        if (floors.Count == 0) errors.Add("Selecione pelo menos um pavimento.");
        foreach (var floor in floors)
        {
            if (string.IsNullOrWhiteSpace(floor.Name) || string.IsNullOrWhiteSpace(floor.Sector) || string.IsNullOrWhiteSpace(floor.Room))
                errors.Add("Preencha nome, setor e sala de todos os pavimentos selecionados.");
            if (string.IsNullOrWhiteSpace(floor.Resolution)) errors.Add($"Preencha a resolução de {floor.Name}.");
        }
        foreach (var path in profile.Attachments)
            if (!File.Exists(path)) errors.Add($"Anexo não encontrado: {Path.GetFileName(path)}.");
        if (!Uri.TryCreate(settings.JiraUrl, UriKind.Absolute, out var jira) || jira.Scheme != Uri.UriSchemeHttps)
            errors.Add("O endereço do Jira deve começar com https://.");
        foreach (var url in new[] { settings.PortalUrl, settings.QueueUrl })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
                jira is null || !string.Equals(parsed.Authority, jira.Authority, StringComparison.OrdinalIgnoreCase))
                errors.Add("O formulário e a fila devem pertencer ao mesmo endereço HTTPS do Jira.");
        if (settings.TimeoutSeconds is < 10 or > 180) errors.Add("O tempo de espera deve estar entre 10 e 180 segundos.");
        return errors.Distinct().ToList();
    }

    public static VisitRun CreateRun(VisitProfile profile, AppSettings settings)
    {
        var errors = Validate(profile, settings);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        var copy = Serialization.Copy(profile);
        return new VisitRun { Profile = copy, Settings = Serialization.Copy(settings),
            Floors = copy.Floors.Where(f => f.Selected).Select(f => new FloorRun { Floor = f }).ToList() };
    }
}
