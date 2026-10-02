using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Playwright;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Automation.Edge;

public sealed class EdgeJiraAutomation(LocalStore store) : IJiraAutomation
{
    private IPlaywright? playwright;
    private IBrowserContext? context;
    private IPage? page;
    private DomControls? controls;
    private ILocator? closingDialog;
    private AppSettings settings = new();
    private DiagnosticAttempt? diagnostics;
    public void SetDiagnostics(DiagnosticAttempt value) => diagnostics = value;
    private IPage Page => page ?? throw new InvalidOperationException("O navegador não está conectado.");
    private DomControls Ui => controls ?? throw new InvalidOperationException("O navegador não está conectado.");
    private static readonly Regex IssueKeyPattern = new(@"\b([A-Z][A-Z0-9]*-\d+)\b");

    public async Task ConnectAsync(AppSettings appSettings, Action<string> report, CancellationToken cancellationToken, string? initialIssueKey = null)
    {
        settings = appSettings;
        playwright = await Playwright.CreateAsync();
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(store.BrowserDirectory,
                new() { Channel = "msedge", Headless = false, ChromiumSandbox = true, ViewportSize = new() { Width = 1440, Height = 960 } });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException("Não foi possível abrir o Microsoft Edge. Confira se ele está instalado e se a máquina permite abrir a janela de automação.", ex);
        }
        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        page.SetDefaultTimeout(settings.TimeoutSeconds * 1000);
        page.SetDefaultNavigationTimeout(settings.TimeoutSeconds * 1000);
        controls = new DomControls(page, settings.SelectorOverrides, diagnostics);
        if (diagnostics is not null && store.Diagnostics is not null)
            store.Diagnostics.UpdateBrowserVersion(diagnostics.Id, context.Browser?.Version,
                typeof(Playwright).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(Playwright).Assembly.GetName().Version?.ToString());
        page.RequestFailed += (_, request) => diagnostics?.Event("navigation.failed", "Uma requisição do navegador falhou.",
            DiagnosticLevel.Warning, new() { ["pageKind"] = PageKind(request.Url), ["resourceType"] = request.ResourceType });
        page.Response += (_, response) =>
        {
            if (response.Status >= 400) diagnostics?.Event("navigation.http_error", "Resposta HTTP de erro recebida.", DiagnosticLevel.Warning,
                new() { ["status"] = response.Status, ["pageKind"] = PageKind(response.Url) });
        };
        var initialUrl = initialIssueKey is null ? settings.PortalUrl : IssueUrl(initialIssueKey);
        await NavigateAsync(initialUrl);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(settings.LoginTimeoutMinutes);
        DateTimeOffset? formLoadingSince = null;
        string? previousMessage = null;
        string? waitingKind = null;
        var waitingSince = Stopwatch.GetTimestamp();
        diagnostics?.RecordDuration("browser.login_wait", 0);
        diagnostics?.RecordDuration("browser.form_loading", 0);
        void WaitChanged(string? kind)
        {
            if (kind == waitingKind) return;
            if (waitingKind is not null)
            {
                var duration = Stopwatch.GetElapsedTime(waitingSince).TotalMilliseconds;
                diagnostics?.RecordDuration(waitingKind == "login" ? "browser.login_wait" : "browser.form_loading", duration);
                diagnostics?.Event("browser.wait_finished", "Espera concluída.", details: new() { ["waitKind"] = waitingKind }, durationMs: duration);
            }
            waitingKind = kind; waitingSince = Stopwatch.GetTimestamp();
            if (kind is not null) diagnostics?.Event("browser.wait_started", "Aguardando o navegador.", details: new() { ["waitKind"] = kind });
        }
        try
        {
        while (!(initialIssueKey is null ? await PortalReadyAsync() : await IssueReadyAsync(initialUrl)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var onForm = IsTargetUrl(Page.Url, initialUrl);
            WaitChanged(onForm ? "form_loading" : "login");
            string message;
            if (onForm)
            {
                formLoadingSince ??= DateTimeOffset.UtcNow;
                message = initialIssueKey is null ? "Carregando o formulário de visita preventiva…" : $"Carregando o chamado {initialIssueKey} para retomar…";
                if (DateTimeOffset.UtcNow - formLoadingSince > TimeSpan.FromSeconds(settings.TimeoutSeconds))
                {
                    var timeoutError = new InvalidOperationException(initialIssueKey is null
                        ? "O formulário de visita preventiva abriu, mas seus campos ainda não foram reconhecidos. Confira a página e o diagnóstico da execução."
                        : $"O chamado {initialIssueKey} abriu, mas seu status ainda não carregou. Retome pelo histórico quando estiver pronto.");
                    timeoutError.Data["DiagnosticCode"] = "FORM_LOAD_TIMEOUT"; throw timeoutError;
                }
            }
            else
            {
                formLoadingSince = null;
                message = "Faça login no Jira na janela do Edge. A execução continuará após o login.";
            }
            if (message != previousMessage) { report(message); previousMessage = message; }
            if (DateTimeOffset.UtcNow > deadline)
            { var timeoutError = new InvalidOperationException("O tempo para login terminou. Retome a execução quando estiver pronto."); timeoutError.Data["DiagnosticCode"] = "LOGIN_TIMEOUT"; throw timeoutError; }
            await Task.Delay(600, cancellationToken);
        }
        }
        finally { WaitChanged(null); }
        diagnostics?.Event("browser.ready", "Página pronta para execução.");
        report(initialIssueKey is null ? "Login confirmado. Preparando os chamados." : $"Login confirmado. Retomando os chamados já registrados a partir de {initialIssueKey}.");
    }

    private static string PageKind(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "unknown";
        if (Regex.IsMatch(uri.AbsolutePath, "login|signin|auth", RegexOptions.IgnoreCase)) return "authentication";
        if (uri.AbsolutePath.Contains("/create/", StringComparison.Ordinal)) return "create_form";
        if (uri.AbsolutePath.Contains("/portal/", StringComparison.Ordinal)) return "customer_portal";
        if (uri.AbsolutePath.Contains("/issue/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/browse/", StringComparison.Ordinal)) return "issue";
        return "other";
    }
    private async Task NavigateAsync(string url)
    {
        var begin = Stopwatch.GetTimestamp();
        diagnostics?.Event("navigation.started", "Navegação iniciada.", DiagnosticLevel.Debug, new() { ["pageKind"] = PageKind(url) });
        await Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        diagnostics?.Event("navigation.finished", "Documento carregado.", DiagnosticLevel.Debug,
            new() { ["pageKind"] = PageKind(url) }, durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
    }

    private async Task<bool> IssueReadyAsync(string initialUrl)
    {
        if (Page.IsClosed) throw new InvalidOperationException("A janela do Edge foi fechada. Retome a execução para continuar.");
        if (!IsTargetUrl(Page.Url, initialUrl)) return false;
        return await DomControls.IsUniqueVisibleAsync(await StatusButtonLocatorAsync());
    }

    private async Task<bool> PortalReadyAsync()
    {
        if (Page.IsClosed) throw new InvalidOperationException("A janela do Edge foi fechada. Retome a execução para continuar.");
        if (!IsPortalUrl(Page.Url)) return false;
        if (settings.SelectorOverrides.TryGetValue("portal.ready", out var selector))
            return await DomControls.IsUniqueVisibleAsync(Page.Locator(selector).Filter(new() { Visible = true }));
        foreach (var (key, label) in new[] { ("portal.reporter", "Abrir esta requisição em nome de"), ("portal.title", "Título"), ("portal.extension", "Ramal do solicitante") })
        {
            var field = await Ui.TryFieldAsync(key, [label]);
            if (field is null || !await field.IsEnabledAsync()) return false;
        }
        return true;
    }

    private bool IsPortalUrl(string url) => IsTargetUrl(url, settings.PortalUrl);

    private bool IsTargetUrl(string url, string targetUrl) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Authority, new Uri(settings.JiraUrl).Authority, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath.TrimEnd('/'), new Uri(targetUrl).AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    public async Task PrepareCreateAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Page.Url != settings.PortalUrl) await NavigateAsync(settings.PortalUrl);
        var profile = run.Profile;
        await Ui.ChooseAsync("portal.reporter", ["Abrir esta requisição em nome de"], profile.ReporterEmail,
            new Regex($"(?:^|[\\s(<]){Regex.Escape(profile.ReporterEmail)}(?=$|[\\s)>])", RegexOptions.IgnoreCase), profile.ReporterEmail);
        cancellationToken.ThrowIfCancellationRequested();
        await Ui.FillAsync("portal.title", ["Título"], profile.Title);
        await Ui.FillAsync("portal.extension", ["Ramal do solicitante"], profile.Extension);
        await Ui.FillAsync("portal.fullName", ["Nome completo do usuário"], profile.FullName);
        await Ui.ChooseAsync("portal.unit", ["Unidade - Usuário", "Unidade - usuário"], profile.Unit,
            new Regex($"^{Regex.Escape(profile.Unit)}$", RegexOptions.IgnoreCase), profile.Unit);
        await Ui.FillAsync("portal.floor", ["Andar"], floor.Floor.Name);
        await Ui.FillAsync("portal.sector", ["Setor", "Setor/Local"], floor.Floor.Sector);
        await Ui.FillAsync("portal.room", ["Sala"], floor.Floor.Room);
        await Ui.FillAsync("portal.phone", ["Telefone"], profile.Phone);
        await Ui.FillAsync("portal.description", ["Detalhes / justificativas", "Detalhes / justificativas:"], profile.Description);
        if (profile.Attachments.Count > 0)
        {
            diagnostics?.Event("attachments.prepared", "Anexos preparados.", details: new() { ["count"] = profile.Attachments.Count });
            var upload = settings.SelectorOverrides.TryGetValue("portal.attachments", out var selector)
                ? Page.Locator(selector) : Page.Locator("input[type=file]");
            if (await upload.CountAsync() != 1) throw new InvalidOperationException("Não foi possível identificar onde incluir os anexos.");
            await upload.SetInputFilesAsync(profile.Attachments.ToArray());
            foreach (var attachment in profile.Attachments)
                await Page.GetByText(Path.GetFileName(attachment), new() { Exact = true }).First.WaitForAsync();
        }
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<string> SubmitCreateAsync(VisitRun run, FloorRun floor)
    {
        var oldUrl = Page.Url;
        await Ui.ClickAsync("portal.submit", ["Enviar"]);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Page.Url != oldUrl)
            {
                var match = IssueKeyPattern.Match(Page.Url);
                if (match.Success) return match.Groups[1].Value;
            }
            var links = Page.Locator("a[href*='/issue/'],a[href*='/browse/'],a[href*='/portal/']");
            var keys = new HashSet<string>();
            for (var i = 0; i < await links.CountAsync(); i++)
            {
                var link = links.Nth(i);
                if (!await link.IsVisibleAsync()) continue;
                var text = await link.InnerTextAsync();
                var match = IssueKeyPattern.Match(text);
                if (match.Success) keys.Add(match.Groups[1].Value);
            }
            if (keys.Count == 1 && Page.Url != oldUrl) return keys.Single();
            await Task.Delay(300);
        }
        throw new ReconciliationException($"O formulário de {floor.Name} foi enviado, mas o número do chamado não foi identificado. Confira o Jira e vincule o número pelo histórico.");
    }

    private string IssueUrl(string key) => $"{settings.JiraUrl.TrimEnd('/')}/jira/servicedesk/projects/{key.Split('-')[0]}/queues/issue/{key}";

    public async Task<IssueSnapshot> ReadIssueAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = floor.IssueKey ?? throw new InvalidOperationException("Chamado ainda não identificado.");
        await NavigateAsync(IssueUrl(key));
        var title = settings.SelectorOverrides.TryGetValue("issue.title", out var titleSelector)
            ? Page.Locator(titleSelector) : Page.GetByRole(AriaRole.Heading, new() { Name = run.Profile.Title, Exact = true });
        await title.First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        if (DomControls.Normalize(await (await DomControls.UniqueAsync(title, "Título")).InnerTextAsync()) != DomControls.Normalize(run.Profile.Title))
            throw new InvalidOperationException("O título do chamado não corresponde à visita configurada.");
        var body = await Page.Locator("body").InnerTextAsync();
        if (!Regex.IsMatch(body, $"\\b{Regex.Escape(key)}\\b")) throw new InvalidOperationException("O número exibido não corresponde ao chamado registrado.");
        foreach (var (field, labels, expected) in new (string, string[], string)[]
        {
            ("issue.floor", ["Andar"], floor.Floor.Name), ("issue.unit", ["Unidade - Usuário"], run.Profile.Unit),
            ("issue.sector", ["Setor/Local", "Setor"], floor.Floor.Sector), ("issue.room", ["Sala"], floor.Floor.Room),
            ("issue.fullName", ["Nome completo do usuário"], run.Profile.FullName),
            ("issue.extension", ["Ramal do solicitante"], run.Profile.Extension),
            ("issue.requestType", ["Request Type", "Tipo de solicitação"], "Visita Preventiva")
        })
        {
            var actual = await Ui.ValueAsync(field, labels);
            var matches = string.Equals(DomControls.Normalize(actual), DomControls.Normalize(expected), StringComparison.OrdinalIgnoreCase);
            diagnostics?.Event("field.verified", "Campo conferido.", matches ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
                new() { ["controlKey"] = field, ["matches"] = matches });
            if (!matches)
                throw new InvalidOperationException($"{key}: o campo “{labels[0]}” não corresponde a {floor.Name}. Confira os dados antes de retomar.");
        }
        var reporter = await Ui.ValueAsync("issue.reporter", ["Reporter", "Relator", "Solicitante"]);
        if (!string.Equals(DomControls.NormalizePersonName(reporter), DomControls.NormalizePersonName(run.Profile.ReporterName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{key}: o solicitante configurado não foi encontrado na página.");
        var status = await StatusAsync(cancellationToken);
        var closed = Regex.IsMatch(status, @"^(Fechado|Closed|Concluído|Concluido)$", RegexOptions.IgnoreCase);
        var started = Regex.IsMatch(status, @"^Em andamento N2$", RegexOptions.IgnoreCase);
        var assignee = await Ui.ValueAsync("issue.assignee", ["Assignee", "Responsável"]);
        var assignLink = Page.GetByText(new Regex(@"^(Assign to me|Atribuir a mim)$", RegexOptions.IgnoreCase)).Filter(new() { Visible = true });
        var self = !string.IsNullOrEmpty(run.TechnicianName) && DomControls.Normalize(assignee) == DomControls.Normalize(run.TechnicianName) && await assignLink.CountAsync() == 0;
        var resolutionMatches = false;
        var publicMatches = false;
        var teamMatches = false;
        if (closed)
        {
            teamMatches = DomControls.Normalize(await Ui.ValueAsync("issue.team", ["Equipe"])) == run.Settings.ClosingTeam;
            resolutionMatches = await ResolutionMatchesAsync(floor.Floor.Resolution);
            publicMatches = await PublicCommentMatchesAsync(key, floor.Floor.Resolution, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(key, status, self, closed, started, resolutionMatches, publicMatches, teamMatches);
    }

    private async Task<ILocator> StatusButtonAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var button = await StatusButtonLocatorAsync();
            var count = await button.CountAsync();
            if (count > 1)
            {
                diagnostics?.Event("control.ambiguous", "Mais de um controle de status foi encontrado.", DiagnosticLevel.Warning,
                    new() { ["controlKey"] = "issue.status", ["count"] = count }, errorCode: "FIELD_AMBIGUOUS");
                throw new InvalidOperationException("O Jira exibiu mais de um controle de status do chamado. Confira a página antes de retomar.");
            }
            if (count == 1 && await button.IsEnabledAsync()) return button;
            await Task.Delay(200, cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new InvalidOperationException("O controle de status do chamado não carregou no tempo esperado. Confira a página e retome pelo histórico.");
    }

    private async Task<ILocator> StatusButtonLocatorAsync()
    {
        ILocator button;
        if (settings.SelectorOverrides.TryGetValue("issue.status", out var selector)) button = Page.Locator(selector);
        else
        {
            button = Page.Locator("[data-testid='issue-field-status.ui.status-view.status-button.status-button']");
            if (await button.Filter(new() { Visible = true }).CountAsync() == 0)
                button = Page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^(Aguardando N2|Em andamento N2|Fechado|Closed|Concluído|Concluido)(?:\s*-\s*(?:Change status|Alterar status))?$", RegexOptions.IgnoreCase) });
        }
        return button.Filter(new() { Visible = true });
    }

    private async Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        DomControls.Normalize(await (await StatusButtonAsync(cancellationToken)).InnerTextAsync());

    public async Task AssignAsync(VisitRun run, FloorRun floor)
    {
        await Ui.ClickAsync("issue.assignToMe", ["Assign to me", "Atribuir a mim"]);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var link = Page.GetByText(new Regex(@"^(Assign to me|Atribuir a mim)$", RegexOptions.IgnoreCase)).Filter(new() { Visible = true });
            var assigned = await Ui.ValueAsync("issue.assignee", ["Assignee", "Responsável"]);
            if (await link.CountAsync() == 0 && !Regex.IsMatch(assigned, @"^(Unassigned|Não atribuído|None)$", RegexOptions.IgnoreCase) && assigned.Length > 0)
            {
                run.TechnicianName = assigned;
                return;
            }
            await Task.Delay(300);
        }
        throw new InvalidOperationException($"Não foi possível confirmar a atribuição de {floor.IssueKey}.");
    }

    private async Task OpenTransitionAsync(string action)
    {
        var direct = Page.GetByText(action, new() { Exact = true }).Filter(new() { Visible = true });
        if (await DomControls.IsUniqueVisibleAsync(direct)) { await direct.ClickAsync(); return; }
        await (await StatusButtonAsync()).ClickAsync();
        await Ui.ClickAsync(action == "Iniciar Atendimento" ? "issue.start" : "issue.close", [action]);
    }

    public async Task StartAsync(VisitRun run, FloorRun floor)
    {
        var status = await StatusAsync();
        if (!string.Equals(status, "Aguardando N2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{floor.IssueKey} está em “{status}”. O início de atendimento esperado é a partir de Aguardando N2.");
        await OpenTransitionAsync("Iniciar Atendimento");
        var dialog = Page.GetByRole(AriaRole.Dialog).Filter(new() { Visible = true });
        if (await DomControls.IsUniqueVisibleAsync(dialog)) await Ui.ClickAsync("start.confirm", ["Iniciar Atendimento", "Confirm", "Confirmar"], dialog);
        await WaitStatusAsync("Em andamento N2");
    }

    public async Task PrepareCloseAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(await StatusAsync(), "Em andamento N2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{floor.IssueKey}: confira o início do atendimento antes do fechamento.");
        await OpenTransitionAsync("Transitar para Fechado");
        var dialog = Page.GetByRole(AriaRole.Dialog).Filter(new() { Visible = true });
        await dialog.First.WaitForAsync();
        dialog = await DomControls.UniqueAsync(dialog, "Fechamento");
        await Ui.FillAsync("close.resolution", ["Resolução do chamado"], floor.Floor.Resolution, dialog);
        // Seleciona explicitamente o comentário público antes de preencher o editor.
        var reply = dialog.GetByText("Reply to customer", new() { Exact = true });
        if (await DomControls.IsUniqueVisibleAsync(reply)) await reply.ClickAsync();
        else await Ui.ClickAsync("close.publicMode", ["Reply to customer", "Responder ao cliente"], dialog);
        await Ui.FillAsync("close.publicComment", ["Comentário público", "Reply to customer", "Responder ao cliente", "Comentário", "Comment"], floor.Floor.Resolution, dialog);
        var team = run.Settings.ClosingTeam;
        await Ui.ChooseAsync("close.team", ["Equipe"], team, new Regex($"^{Regex.Escape(team)}$"), team, dialog);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        closingDialog = dialog;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task SubmitCloseAsync(VisitRun run, FloorRun floor)
    {
        if (closingDialog is null) throw new InvalidOperationException("O formulário de fechamento ainda não foi preparado.");
        await Ui.ClickAsync("close.submit", ["Transitar para Fechado"], closingDialog);
        closingDialog = null;
        await WaitStatusAsync("Fechado", "Closed", "Concluído");
    }

    private async Task WaitStatusAsync(params string[] expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { var status = await StatusAsync(); if (expected.Contains(status, StringComparer.OrdinalIgnoreCase)) return; }
            catch (InvalidOperationException) { /* A página pode estar substituindo os controles. */ }
            await Task.Delay(300);
        }
        throw new InvalidOperationException("A mudança de status ainda não foi confirmada. O progresso foi salvo para conferência.");
    }

    private async Task<bool> ResolutionMatchesAsync(string expected)
    {
        var directValue = await Ui.TryValueAsync("issue.resolution", ["Resolução do chamado"]);
        if (directValue is not null)
        { diagnostics?.Event("verification.source", "Origem da conferência da resolução.", DiagnosticLevel.Debug, new() { ["source"] = "issue_field" }); return DomControls.Normalize(directValue) == DomControls.Normalize(expected); }
        // Esse campo aparece no modal, mas não está no layout principal do Jira da unidade.
        // O histórico registra separadamente os valores anteriores e os novos valores salvos.
        foreach (var name in new[] { "History", "Histórico" })
        {
            var tab = Page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true }).Filter(new() { Visible = true });
            if (!await DomControls.IsUniqueVisibleAsync(tab)) continue;
            await tab.ClickAsync();
            diagnostics?.Event("verification.source", "Origem da conferência da resolução.", DiagnosticLevel.Debug, new() { ["source"] = "issue_history" });
            var updates = Page.Locator("[data-testid='issue-history.ui.history-items.generic-history-item.history-item']")
                .Filter(new() { Has = Page.GetByText(DomControls.LabelPattern("Resolução do chamado")), Visible = true });
            try { await updates.First.WaitForAsync(new() { State = WaitForSelectorState.Visible }); }
            catch (TimeoutException) { return false; }
            var savedValue = await updates.First.EvaluateAsync<string>("e => e.lastElementChild?.lastElementChild?.lastElementChild?.innerText || ''");
            // A aba History mostra a atualização mais recente primeiro. Nunca aceitar um valor antigo.
            return DomControls.Normalize(savedValue) == DomControls.Normalize(expected);
        }
        return false;
    }

    private async Task<bool> PublicCommentMatchesAsync(string key, string expected, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "Comments", "Comentários" })
        {
            var tab = Page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true });
            if (await DomControls.IsUniqueVisibleAsync(tab)) { await tab.ClickAsync(); break; }
        }
        if (settings.SelectorOverrides.TryGetValue("issue.publicComment", out var selector))
        {
            diagnostics?.Event("verification.source", "Origem da conferência do comentário público.", DiagnosticLevel.Debug, new() { ["source"] = "public_comment_override" });
            var comments = Page.Locator(selector);
            for (var i = 0; i < await comments.CountAsync(); i++)
                if (DomControls.Normalize(await comments.Nth(i).InnerTextAsync()) == DomControls.Normalize(expected)) return true;
            return false;
        }
        var portalLink = Page.GetByRole(AriaRole.Link, new() { NameRegex = new(@"^(View request in portal|Ver solicitação no portal)$", RegexOptions.IgnoreCase) }).Filter(new() { Visible = true });
        if (await DomControls.IsUniqueVisibleAsync(portalLink))
            return await PortalCommentMatchesAsync(key, expected, (await portalLink.GetAttributeAsync("href"))!, cancellationToken);
        var markers = Page.GetByText(new Regex(@"^(Comentário público|Public comment|Replied to customer|Reply to customer|Compartilhado com o cliente)$", RegexOptions.IgnoreCase));
        for (var i = 0; i < await markers.CountAsync(); i++)
        {
            var node = markers.Nth(i);
            var contentsInComment = await node.EvaluateAsync<string[]>("e => Array.from(e.closest('article,[role=article],[data-testid*=comment]')?.querySelectorAll('pre,p,[data-testid*=body]') || []).map(n => n.innerText || '')");
            if (contentsInComment.Any(text => DomControls.Normalize(text) == DomControls.Normalize(expected))) return true;
            // A leitura fica restrita ao comentário; o corpo do chamado também contém a resolução.
            for (var depth = 0; depth < 2; depth++)
            {
                node = node.Locator("..");
                var tag = await node.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
                if (tag is "body" or "main" or "section") break;
                var contents = node.Locator("pre,p,[data-testid*=body]");
                for (var j = 0; j < await contents.CountAsync(); j++)
                    if (DomControls.Normalize(await contents.Nth(j).InnerTextAsync()) == DomControls.Normalize(expected)) return true;
            }
        }
        return false;
    }

    private async Task<bool> PortalCommentMatchesAsync(string key, string expected, string href, CancellationToken cancellationToken)
    {
        var originalUrl = Page.Url;
        var target = new Uri(new Uri(originalUrl), href);
        if (target.Authority != new Uri(settings.JiraUrl).Authority ||
            !Regex.IsMatch(target.AbsolutePath, $"^/servicedesk/customer/portal/\\d+/{Regex.Escape(key)}/?$"))
            throw new InvalidOperationException("O link do portal não corresponde ao chamado em conferência.");
        try
        {
            diagnostics?.Event("verification.source", "Origem da conferência do comentário público.", DiagnosticLevel.Debug, new() { ["source"] = "customer_portal" });
            await NavigateAsync(target.AbsoluteUri);
            await Page.GetByText(key, new() { Exact = true }).First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
            // No portal do cliente, ler apenas comentários publicados; descrições e editores ficam excluídos.
            var bodies = Page.Locator(".ak-renderer-wrapper.is-comment .ak-renderer-document").Filter(new() { Visible = true });
            var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var body in await bodies.AllInnerTextsAsync())
                    if (DomControls.Normalize(body) == DomControls.Normalize(expected)) return true;
                await Task.Delay(300, cancellationToken);
            } while (DateTimeOffset.UtcNow < deadline);
            return false;
        }
        finally { if (!Page.IsClosed) await NavigateAsync(originalUrl); }
    }

    public async Task CaptureFailureAsync(VisitRun run)
    {
        if (page is null || page.IsClosed)
        { diagnostics?.Event("artifact.skipped", "Captura indisponível: navegador fechado ou não conectado.", DiagnosticLevel.Warning); return; }
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) || uri.Authority != new Uri(settings.JiraUrl).Authority ||
            PageKind(page.Url) == "authentication" || await page.Locator("input[type=password]").CountAsync() > 0)
        { diagnostics?.Event("artifact.skipped", "Captura não permitida nesta página.", DiagnosticLevel.Warning); return; }
        var attemptId = diagnostics?.Id ?? Guid.NewGuid().ToString("N");
        var directory = store.Diagnostics?.ArtifactDirectory(run.Id, attemptId) ?? Path.Combine(store.DiagnosticsDirectory(run.Id), "attempts", attemptId, "artifacts");
        Directory.CreateDirectory(directory);
        var filename = $"{Guid.NewGuid():N}-falha.png";
        var profile = run.Profile;
        string[] privateValues = [profile.ReporterName, profile.ReporterEmail, profile.FullName, profile.Extension, profile.Phone,
            profile.Unit, profile.Description, run.TechnicianName, .. run.Floors.Select(f => f.Floor.Resolution)];
        var masks = new List<ILocator> { page.Locator("input,textarea,[contenteditable=true]") };
        masks.AddRange(privateValues.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Select(v => page.GetByText(v, new() { Exact = false })));
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, filename), FullPage = true, Mask = masks, Timeout = 10000 });
        diagnostics?.Event("artifact.saved", "Captura de falha disponível.", artifacts: ["artifacts/" + filename]);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (context is not null) await context.CloseAsync(); }
        finally { playwright?.Dispose(); }
    }
}
