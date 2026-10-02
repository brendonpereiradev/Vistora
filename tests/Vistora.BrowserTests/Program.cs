using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Vistora.Automation.Edge;
using Vistora.Core;
using Vistora.Infrastructure;

if (args.Contains("--validate-saved-issues"))
{
    var savedStore = new LocalStore();
    var savedRuns = await savedStore.LoadRunsAsync();
    var savedRun = VisitRunSelection.ForResume(savedRuns, savedRuns.First());
    Console.WriteLine($"Retomada direcionada para {savedRun.DateLabel}, com {savedRun.Floors.Count(f => f.IssueKey is not null)} chamados registrados.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await using var automation = new EdgeJiraAutomation(savedStore);
    await automation.ConnectAsync(savedRun.Settings, Console.WriteLine, cancellation.Token, savedRun.Floors.First(f => f.IssueKey is not null).IssueKey);
    foreach (var floor in savedRun.Floors.Where(f => f.IssueKey is not null))
    {
        var snapshot = await automation.ReadIssueAsync(savedRun, floor, cancellation.Token);
        Check(!snapshot.Closed || (snapshot.ResolutionMatches && snapshot.PublicCommentMatches && snapshot.TeamMatches), $"{snapshot.Key}: resolução, comentário público ou equipe não conferidos.");
        Console.WriteLine($"PASS: {snapshot.Key} — dados conferidos, status {snapshot.Status}.");
    }
    Console.WriteLine("Apenas leitura; chamados e histórico preservados.");
    return 0;
}

if (args.Contains("--diagnose-saved-issue"))
{
    var savedStore = new LocalStore();
    var savedRun = (await savedStore.LoadRunsAsync()).First(r => r.Floors.Any(f => f.IssueKey is not null));
    var key = savedRun.Floors.First(f => f.IssueKey is not null).IssueKey!;
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchPersistentContextAsync(savedStore.BrowserDirectory,
        new() { Channel = "msedge", Headless = false, ChromiumSandbox = true });
    var page = browser.Pages[0];
    await page.GotoAsync($"{savedRun.Settings.JiraUrl.TrimEnd('/')}/jira/servicedesk/projects/{key.Split('-')[0]}/queues/issue/{key}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    await page.GetByRole(AriaRole.Heading, new() { Name = savedRun.Profile.Title, Exact = true }).WaitForAsync();
    Console.WriteLine("Controles com texto de status: " + await page.Locator("button,[role=button]").EvaluateAllAsync<string>("els=>JSON.stringify(els.filter(e=>/Aguardando N2|Em andamento N2|Fechado/i.test(e.innerText||'')).map(e=>({text:e.innerText,html:e.outerHTML})))"));
    await Task.Delay(1000);
    Console.WriteLine("Controles de leitura: " + await page.Locator("button,[role=button],[role=tab],h2,h3,label").EvaluateAllAsync<string>("els=>JSON.stringify(els.filter(e=>/Resolu|Equipe|Comment|Activity|Details|Show|More|Mostrar|Exibir|Expand|Recolher|View/i.test(e.innerText||e.getAttribute('aria-label')||'')).map(e=>({text:e.innerText.slice(0,200),label:e.getAttribute('aria-label'),testid:e.getAttribute('data-testid')})))"));
    var resolutionLabels = page.GetByText(new System.Text.RegularExpressions.Regex("Resolução do chamado", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    Console.WriteLine("Rótulos de resolução: " + await resolutionLabels.EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:e.outerHTML,parent:e.parentElement.outerHTML.slice(0,2400)})))"));
    Console.WriteLine("Trechos de fechamento: " + await page.Locator("body").EvaluateAsync<string>("e=>JSON.stringify(e.innerText.split('\\n').map((line,i,lines)=>/Resolu|Equipe|Comment|Activity|Details|Show|More|Mostrar|Exibir|Expand|Recolher|View/i.test(line)?lines.slice(Math.max(0,i-1),i+4).join(' | '):'').filter(Boolean))"));
    foreach (var tabName in new[] { "Comments", "History" })
    {
        var tab = page.GetByRole(AriaRole.Tab, new() { Name = tabName, Exact = true });
        Console.WriteLine($"Aba {tabName}: {await tab.CountAsync()}");
        if (await tab.CountAsync() != 1) continue;
        await tab.ClickAsync(); await Task.Delay(1500);
        Console.WriteLine($"Conteúdo {tabName}: " + await page.GetByRole(AriaRole.Tabpanel).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:(()=>{const clone=e.cloneNode(true);for(const node of clone.querySelectorAll('*')){node.removeAttribute('class');node.removeAttribute('style')}return clone.outerHTML.slice(0,18000)})()})))"));
    }
    var portalLink = page.GetByRole(AriaRole.Link, new() { Name = "View request in portal", Exact = true });
    Console.WriteLine("Link do portal: " + await portalLink.GetAttributeAsync("href"));
    await page.GotoAsync(new Uri(new Uri(page.Url), (await portalLink.GetAttributeAsync("href"))!).AbsoluteUri); await Task.Delay(1500);
    Console.WriteLine("Portal atual: " + new Uri(page.Url).GetLeftPart(UriPartial.Path));
    var commentOpening = savedRun.Floors[0].Floor.Resolution.Split('\n')[0].Trim();
    Console.WriteLine("Comentário no portal: " + await page.GetByText(commentOpening, new() { Exact = true }).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>{let node=e;const parents=[];for(let i=0;i<5&&node;i++,node=node.parentElement){const clone=node.cloneNode(true);for(const child of clone.querySelectorAll('*')){child.removeAttribute('class');child.removeAttribute('style')}parents.push(clone.outerHTML.slice(0,7000))}return parents}))"));
    Console.WriteLine("Controles do portal: " + await page.Locator("button,[role=button],article,[data-testid*=comment],[data-test-id*=comment]").EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({tag:e.tagName,id:e.id,class:e.className,testid:e.getAttribute('data-testid'),testId:e.getAttribute('data-test-id'),text:e.innerText?.slice(0,500)})))"));
    Console.WriteLine("Apenas leitura; nenhuma atribuição ou transição executada.");
    return 0;
}

if (args.Contains("--validate-saved-portal"))
{
    var savedStore = new LocalStore();
    var savedSettings = await savedStore.LoadSettingsAsync();
    var savedProfiles = await savedStore.LoadProfilesAsync();
    var activeProfile = Serialization.Copy(savedProfiles.Profiles.Single(p => p.Id == savedProfiles.ActiveProfileId));
    activeProfile.Attachments.Clear(); // A conferência não envia anexos nem o formulário.
    var validationRun = ProfileValidation.CreateRun(activeProfile, savedSettings);
    using var validationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    await using var automation = new EdgeJiraAutomation(savedStore);
    await automation.ConnectAsync(savedSettings, Console.WriteLine, validationCancellation.Token);
    await automation.PrepareCreateAsync(validationRun, validationRun.Floors[0], validationCancellation.Token);
    Console.WriteLine("PASS: formulário real reconhecido e campos preenchidos/conferidos. Nenhum chamado enviado.");
    return 0;
}

if (args.Contains("--probe-jira") || args.Contains("--diagnose-portal"))
{
    using var playwright = await Playwright.CreateAsync();
    var savedStore = new LocalStore();
    var savedSettings = await savedStore.LoadSettingsAsync();
    var probeRoot = args.Contains("--saved-session") ? savedStore.BrowserDirectory : Path.Combine(Path.GetTempPath(), "VistoraProbe", Guid.NewGuid().ToString("N"));
    await using var browser = await playwright.Chromium.LaunchPersistentContextAsync(probeRoot, new() { Channel = "msedge", Headless = false, ChromiumSandbox = true });
    var page = browser.Pages[0];
    await page.GotoAsync(savedSettings.PortalUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    await Task.Delay(1500);
    Console.WriteLine("Página: " + new Uri(page.Url).GetLeftPart(UriPartial.Path));
    Console.WriteLine("Título: " + await page.TitleAsync());
    if (args.Contains("--diagnose-portal"))
    {
        var marker = page.GetByText(new System.Text.RegularExpressions.Regex(@"^Abrir esta requisição em nome de\s*\*?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Console.WriteLine("Marcadores encontrados pela versão anterior: " + await marker.CountAsync());
        for (var i = 0; i < await marker.CountAsync(); i++)
            Console.WriteLine("Marcador: " + await marker.Nth(i).EvaluateAsync<string>("e => JSON.stringify({tag:e.tagName,text:e.textContent,visible:!!(e.offsetWidth||e.offsetHeight||e.getClientRects().length)})"));
        Console.WriteLine("Rótulos: " + await page.Locator("label").EvaluateAllAsync<string>("els => JSON.stringify(els.map(e=>({text:e.textContent,for:e.htmlFor})))"));
        Console.WriteLine("Campos: " + await page.Locator("input,select,textarea,[contenteditable=true]").EvaluateAllAsync<string>("els => JSON.stringify(els.map(e=>({tag:e.tagName,id:e.id,role:e.getAttribute('role'),label:e.getAttribute('aria-label'),labelledby:e.getAttribute('aria-labelledby'),type:e.getAttribute('type')})))"));
        if (args.Contains("--saved-session"))
        {
            var document = await savedStore.LoadProfilesAsync();
            var reporterEmail = document.Profiles.Single(p => p.Id == document.ActiveProfileId).ReporterEmail;
            var reporterField = page.Locator("#reporter");
            await reporterField.FocusAsync(); await reporterField.FillAsync(reporterEmail); await Task.Delay(2000);
            Console.WriteLine("Opções do solicitante: " + await page.GetByRole(AriaRole.Option).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:e.outerHTML})))"));
            Console.WriteLine("Listas: " + await page.GetByRole(AriaRole.Listbox).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:e.outerHTML})))"));
            var emailPattern = new System.Text.RegularExpressions.Regex($"(?:^|[\\s(<]){System.Text.RegularExpressions.Regex.Escape(reporterEmail)}(?=$|[\\s)>])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            await page.GetByRole(AriaRole.Option, new() { NameRegex = emailPattern }).ClickAsync();
            await Task.Delay(400);
            Console.WriteLine("Seleção: " + await reporterField.EvaluateAsync<string>("e=>JSON.stringify({value:e.value,describedby:e.getAttribute('aria-describedby'),refs:(e.getAttribute('aria-describedby')||'').split(/\\s+/).map(id=>({id,text:document.getElementById(id)?.textContent})),container:e.parentElement.parentElement.outerHTML})"));
        }
    }
    Console.WriteLine("Sondagem do formulário. Nenhum chamado criado.");
    return 0;
}

var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();
var hidePublic = false;
var wrongLatestResolution = false;
var loginMode = false;
var incompleteForm = false;
var portalVisits = 0;
app.MapGet("/{**path}", (HttpContext ctx) =>
{
    if (ctx.Request.Path.Value!.EndsWith("/create/1", StringComparison.Ordinal)) portalVisits++;
    if (loginMode && !ctx.Request.Path.Value!.EndsWith("/user/login", StringComparison.Ordinal))
        return Results.Redirect("/servicedesk/customer/portal/1/user/login");
    if (loginMode || incompleteForm)
        return Results.Content("<html><body><label>Abrir esta requisição em nome de*<span>(required)</span></label><p>Carregando</p></body></html>", "text/html; charset=utf-8");
    return Results.Content(Fixture.Html(hidePublic, wrongLatestResolution), "text/html; charset=utf-8");
});
await app.StartAsync();
var root = Path.Combine(Path.GetTempPath(), "VistoraBrowserTests", Guid.NewGuid().ToString("N"));
var store = new LocalStore(root);
var profile = new VisitProfile
{
    Name = "Perfil de teste", ReporterName = "Pessóa Teste", ReporterEmail = "pessoa@example.com", FullName = "Pessoa Teste",
    Extension = "123456", Unit = "Unidade A", Floors = [new() { Name = "Subsolo", Sector = "DML", Room = "Sala A", Resolution = "Visita Subsolo\n\nComputador: OK\nImpressora: OK" },
    new() { Name = "Térreo", Sector = "Consultórios", Room = "Sala B", Resolution = "Visita Térreo\n\nAvayas: OK" }]
};
var run = ProfileValidation.CreateRun(profile, new AppSettings());
run.Settings.JiraUrl = $"http://127.0.0.1:{port}";
run.Settings.PortalUrl = run.Settings.JiraUrl + "/servicedesk/customer/portal/1/group/1/create/1";
run.Settings.QueueUrl = run.Settings.JiraUrl + "/queue";
run.Settings.TimeoutSeconds = 10;
var engine = new ExecutionEngine(store, () => new EdgeJiraAutomation(store));
engine.Progress += r => Console.WriteLine(r.Message);
var failures = 0;
try
{
    using (var interruption = new CancellationTokenSource())
    {
        void StopAfterCreation(VisitRun progress)
        {
            if (progress.Floors.All(f => f.Stage == FloorStage.Created)) interruption.Cancel();
        }
        engine.Progress += StopAfterCreation;
        await engine.ExecuteAsync(run, interruption.Token);
        engine.Progress -= StopAfterCreation;
        Check(run.State == RunState.Interrupted && run.Floors.All(f => f.Stage == FloorStage.Created), "Não interrompeu antes da atribuição.");
    }
    var createdKeys = run.Floors.Select(f => f.IssueKey).ToArray();
    var portalVisitsBeforeResume = portalVisits;
    run = (await store.LoadRunsAsync()).Single();
    await engine.ExecuteAsync(run, default);
    Check(run.State == RunState.Completed, run.Message);
    Check(run.Floors.Select(f => f.IssueKey).SequenceEqual(createdKeys), "A retomada substituiu os chamados existentes.");
    Check(portalVisits == portalVisitsBeforeResume, "A retomada abriu o formulário de criação.");
    Console.WriteLine("PASS: retomada após criação atribui, inicia e fecha os mesmos chamados.");
    Check(run.Floors.Select(f => f.IssueKey).Distinct().Count() == 2, "Chamados não são distintos.");
    Console.WriteLine("PASS: fluxo completo com rótulos (required) ocultos e comboboxes cobertos pelo valor selecionado.");
    await using (var automation = new EdgeJiraAutomation(store))
    {
        await automation.ConnectAsync(run.Settings, _ => { }, default);
        foreach (var floor in run.Floors)
        {
            var snapshot = await automation.ReadIssueAsync(run, floor, default);
            Check(snapshot.Closed && snapshot.ResolutionMatches && snapshot.PublicCommentMatches && snapshot.TeamMatches && snapshot.AssignedToCurrentUser, "Fechamento divergente.");
        }
        Console.WriteLine("PASS: persistência de textos, equipe, responsável e status após reabrir o Edge.");
        Console.WriteLine("PASS: resolução ausente da tela principal conferida no histórico e comentário público conferido no portal.");
        var swapped = Serialization.Copy(run.Floors[0]); swapped.IssueKey = run.Floors[1].IssueKey;
        try { await automation.ReadIssueAsync(run, swapped, default); throw new Exception("Pavimento errado aceito."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("não corresponde")) { }
        Console.WriteLine("PASS: chamado de outro pavimento é rejeitado antes de qualquer ação.");
        hidePublic = true;
        var incomplete = await automation.ReadIssueAsync(run, run.Floors[0], default);
        Check(!incomplete.PublicCommentMatches, "A resolução foi confundida com o comentário público.");
        Console.WriteLine("PASS: resolução e comentário interno não substituem a verificação do comentário público.");
        hidePublic = false; wrongLatestResolution = true;
        var changed = await automation.ReadIssueAsync(run, run.Floors[0], default);
        Check(!changed.ResolutionMatches && changed.PublicCommentMatches, "Um valor anterior da resolução foi aceito como o valor atual.");
        Console.WriteLine("PASS: a atualização mais recente da resolução prevalece sobre o texto correto de uma atualização antiga.");
    }

    loginMode = true;
    using (var loginCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    await using (var loginBrowser = new EdgeJiraAutomation(store))
    {
        var loginReported = false;
        try
        {
            await loginBrowser.ConnectAsync(run.Settings, message =>
            {
                if (message.StartsWith("Faça login", StringComparison.Ordinal)) { loginReported = true; loginCancellation.Cancel(); }
            }, loginCancellation.Token);
            throw new Exception("Uma página de login foi reconhecida como formulário pronto.");
        }
        catch (OperationCanceledException) { Check(loginReported, "Não houve identificação da espera pelo login."); }
    }
    Console.WriteLine("PASS: a página de login continua aguardando autenticação.");

    loginMode = false; incompleteForm = true;
    await using (var formBrowser = new EdgeJiraAutomation(store))
    {
        var formSettings = Serialization.Copy(run.Settings); formSettings.TimeoutSeconds = 1;
        var messages = new List<string>();
        try { await formBrowser.ConnectAsync(formSettings, messages.Add, default); throw new Exception("Formulário incompleto aceito."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("campos ainda não foram reconhecidos", StringComparison.Ordinal)) { }
        Check(messages.All(m => !m.StartsWith("Faça login", StringComparison.Ordinal)), "Uma falha no formulário foi reportada como falta de login.");
    }
    Console.WriteLine("PASS: formulário incompleto respeita o tempo da etapa e informa o problema correto.");
}
catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + ex); Console.WriteLine("Diagnóstico: " + store.DiagnosticsDirectory(run.Id)); }
finally { await app.StopAsync(); await app.DisposeAsync(); }
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

static class Fixture
{
    public static string Html(bool hidePublic, bool wrongLatestResolution) => """
    <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>Jira — ambiente local de teste</title>
    <style>body{font:16px Segoe UI;padding:28px;background:#f6f8fb;color:#123743}main{max-width:850px;margin:auto;background:white;padding:24px}label{display:block;margin:14px 0 4px}input,textarea,select{font:inherit;padding:8px;width:90%}button{padding:10px;margin:8px}dl>div{display:flex;gap:24px;padding:8px;border-bottom:1px solid #ddd}dl>div>span:first-child{width:250px}dialog{width:650px}pre{white-space:pre-wrap}.options{background:#eef4f4}.options>div{padding:10px;cursor:pointer}.sr-only{position:absolute;width:1px;height:1px;padding:0;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap}.combo-control{position:relative}.selected-overlay{position:absolute;inset:0;background:#eef4f4;z-index:2;padding:8px;width:90%}</style></head><body><main id="main"></main>
    <script>
    const hidePublic = __HIDE_PUBLIC__;
    const wrongLatestResolution = __WRONG_LATEST_RESOLUTION__;
    const main=document.getElementById('main'); const issues=JSON.parse(localStorage.getItem('issues')||'{}');
    const issueKey=location.pathname.match(/SD-\d+/)?.[0];
    const escape=s=>String(s||'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;');
    function persist(){localStorage.setItem('issues',JSON.stringify(issues))}
    // Simula um formulário que só processa os valores depois de sair do campo.
    function delayedValidation(id){
      const input=document.getElementById(id);let pending;
      input.dataset.committed='';
      input.addEventListener('input',()=>{clearTimeout(pending);input.dataset.committed=''});
      input.addEventListener('blur',()=>{const value=input.value||input.innerText;pending=setTimeout(()=>input.dataset.committed=value,300)});
    }
    function row(label,value){return `<div><span>${escape(label)}</span><span>${escape(value)}</span></div>`}
    function historyItem(field,oldValue,newValue){return `<div data-testid="issue-history.ui.history-items.generic-history-item.history-item"><div></div><div><div>Updated the <span>${escape(field)}</span></div><div><div>${escape(oldValue)}</div><div></div><div><span>${escape(newValue)}</span></div></div></div></div>`}
    function combo(id,label,option){return `<label for="${id}">${label}</label><div class="combo-control"><span id="react-select-${id}-single-value" class="selected-overlay">Selecione</span><input id="${id}" role="combobox" aria-describedby="react-select-${id}-single-value" autocomplete="off"></div><div id="${id}options" class="options"></div>`}
    if(issueKey&&location.pathname.includes('/servicedesk/customer/portal/')){
      const issue=issues[issueKey];
      main.innerHTML=`<p>${issueKey}</p><h1>${escape(issue.title)}</h1><section><h2>Descrição</h2><pre>${escape(issue.resolution)}</pre></section><div class="ak-renderer-wrapper is-comment"><div class="ak-renderer-document">${escape(hidePublic?'Outro comentário público':issue.comment)}</div></div>`;
    }else if(!issueKey){
      main.innerHTML=`<h1>Visita Preventiva</h1><form id="form">${combo('reporter','Abrir esta requisição em nome de*','')}
      <label for="title">Título*</label><input id="title" required>
      <label for="extension">Ramal do solicitante*</label><input id="extension" required>
      <label for="fullName">Nome completo do usuário*</label><input id="fullName" required>
      ${combo('unit','Unidade - Usuário*','')}<label for="floor">Andar*</label><input id="floor" required>
      <label for="sector">Setor*</label><input id="sector" required><label for="room">Sala*</label><input id="room" required>
      <label for="phone">Telefone</label><input id="phone"><label for="description-label-target">Detalhes / justificativas*</label><div id="description" role="textbox" aria-label="Detalhes / justificativas" contenteditable="true" style="min-height:80px;border:1px solid #ccc"></div>
      <button type="submit">Enviar</button></form>`;
      for(const label of main.querySelectorAll('label')){
        if(label.textContent.endsWith('*')){const suffix=document.createElement('span');suffix.className='sr-only';suffix.textContent='(required)';label.appendChild(suffix)}
      }
      const hidden=document.createElement('div');hidden.style.display='none';hidden.innerHTML='<label for="hidden-reporter">Abrir esta requisição em nome de*(required)</label><input id="hidden-reporter" role="combobox">';main.appendChild(hidden);
      for(const [id,value] of [['reporter','Pessoa Teste pessoa@example.com'],['unit','Unidade A']]){
        const input=document.getElementById(id),options=document.getElementById(id+'options');
        const selected=document.getElementById('react-select-'+id+'-single-value');
        input.addEventListener('input',()=>{
          selected.style.display='none';
          const values=id==='reporter'?['Pessoa Teste pessoa@example.com.evil',value,'Pessoa Teste pessoa2@example.com']:[value];
          options.innerHTML=values.map(v=>`<div role="option">${escape(v)}</div>`).join('');
          for(const [index,option] of Array.from(options.children).entries())option.onclick=()=>{input.value='';input.dataset.selected=values[index];selected.textContent=values[index];selected.style.display='';options.innerHTML=''};
        });
      }
      for(const id of ['title','description'])delayedValidation(id);
      document.getElementById('form').onsubmit=e=>{e.preventDefault();for(const id of ['title','description']){const input=document.getElementById(id);if(input.dataset.committed!==(input.value||input.innerText))throw Error('Jira ainda não processou '+id)}const data={};for(const id of ['reporter','title','extension','fullName','unit','floor','sector','room','phone','description']){const input=document.getElementById(id);data[id]=input.dataset.selected||input.value||input.innerText}
      const key='SD-'+(1001+Object.keys(issues).length);issues[key]={...data,status:'Aguardando N2',assignee:'Unassigned',resolution:'',comment:'',team:'',commentCount:0};persist();location.href='/jira/servicedesk/projects/SD/queues/issue/'+key};
    }else{
      const issue=issues[issueKey];if(!issue){main.innerHTML='<h1>Chamado não encontrado</h1>'}else{
      main.innerHTML=`<p>${issueKey}</p><h1>${escape(issue.title)}</h1><p>${escape(issue.reporter)}</p><button id="status" aria-label="${issue.status} - Change status" ${issueKey.endsWith('2')?'':'data-testid="issue-field-status.ui.status-view.status-button.status-button"'} hidden>${issue.status}</button><button aria-label="${issue.status} - Change status" data-testid="issue-field-status.ui.status-view.status-button.status-button" hidden>${issue.status}</button><div id="menu"></div><dl>
      ${row('Ramal do solicitante',issue.extension)}${row('Nome completo do usuário',issue.fullName)}${row('Unidade - Usuário',issue.unit)}${row('Andar',issue.floor)}${row('Setor/Local',issue.sector)}${row('Sala',issue.room)}${row('Request Type','Visita Preventiva')}${row('Reporter','Pessoa Teste')}${row('Assignee',issue.assignee)}${row('Equipe',issue.team)}${issueKey.endsWith('1')?'':row('Resolução do chamado',issue.resolution)}</dl>
      ${issue.assignee==='Unassigned'?'<a id="assign" href="#">Assign to me</a>':''}
      <section id="comments">${issue.comment?`<article><b>${hidePublic?'Internal note':'Comentário público'}</b><pre>${escape(issue.comment)}</pre></article>`:''}</section>`;
      if(issueKey.endsWith('1')){
        const activity=document.createElement('section');activity.innerHTML=`<a href="/servicedesk/customer/portal/1/${issueKey}" target="_blank">View request in portal</a><button role="tab" id="comments-tab">Comments</button><button role="tab" id="history-tab">History</button><div role="tabpanel" id="activity-panel"></div>`;main.appendChild(activity);
        document.getElementById('history-tab').onclick=()=>{const panel=document.getElementById('activity-panel');panel.innerHTML='<p>Carregando</p>';setTimeout(()=>{panel.innerHTML=`<div data-testid="issue-history.ui.feed-container">${historyItem('Resolution','None','Concluída')}${historyItem('Resolução do chamado',wrongLatestResolution?issue.resolution:'None',wrongLatestResolution?'Resolução modificada':issue.resolution)}${wrongLatestResolution?historyItem('Resolução do chamado','None',issue.resolution):''}</div>`},400)};
        document.getElementById('comments-tab').onclick=()=>document.getElementById('activity-panel').innerHTML='';
      }
      document.getElementById('assign')?.addEventListener('click',e=>{e.preventDefault();issue.assignee='Técnico Teste';persist();location.reload()});
      setTimeout(()=>document.getElementById('status').hidden=false,700);
      document.getElementById('status').onclick=()=>{
        const menu=document.getElementById('menu');
        if(issue.status==='Aguardando N2'){menu.innerHTML='<button id="start">Iniciar Atendimento</button>';document.getElementById('start').onclick=()=>{issue.status='Em andamento N2';persist();location.reload()}}
        else if(issue.status==='Em andamento N2'){menu.innerHTML='<button id="close">Transitar para Fechado</button>';document.getElementById('close').onclick=()=>{
          const dialog=document.createElement('dialog');dialog.setAttribute('role','dialog');dialog.innerHTML=`<h2>Transitar para Fechado</h2><label for="resolution">Resolução do chamado</label><textarea id="resolution"></textarea><button id="reply" type="button">Reply to customer</button><label for="public">Comentário público</label><textarea id="public"></textarea><label for="team">Equipe</label><select id="team"><option>Selecione</option><option>Field Services</option></select><button id="confirm">Transitar para Fechado</button>`;document.body.appendChild(dialog);dialog.showModal();
          document.getElementById('reply').onclick=()=>dialog.dataset.public='true';
          for(const id of ['resolution','public'])delayedValidation(id);
          document.getElementById('confirm').onclick=()=>{for(const id of ['resolution','public']){const input=document.getElementById(id);if(input.dataset.committed!==input.value)throw Error('Jira ainda não processou '+id)}if(dialog.dataset.public!=='true')throw Error('Comentário não é público');issue.resolution=document.getElementById('resolution').value;issue.comment=document.getElementById('public').value;issue.team=document.getElementById('team').value;issue.commentCount++;issue.status='Fechado';persist();location.reload()}
        }};
      }
      }
    }
    </script></body></html>
    """.Replace("__HIDE_PUBLIC__", hidePublic ? "true" : "false").Replace("__WRONG_LATEST_RESOLUTION__", wrongLatestResolution ? "true" : "false");
}
