namespace Vistora.Core;

public interface IRunStore
{
    Task SaveRunAsync(VisitRun run);
}

public interface IJiraAutomation : IAsyncDisposable
{
    void SetDiagnostics(DiagnosticAttempt diagnostics) { }
    Task ConnectAsync(AppSettings settings, Action<string> report, CancellationToken cancellationToken, string? initialIssueKey = null);
    Task PrepareCreateAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken);
    Task<string> SubmitCreateAsync(VisitRun run, FloorRun floor);
    Task<IssueSnapshot> ReadIssueAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken);
    Task AssignAsync(VisitRun run, FloorRun floor);
    Task StartAsync(VisitRun run, FloorRun floor);
    Task PrepareCloseAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken);
    Task SubmitCloseAsync(VisitRun run, FloorRun floor);
    Task CaptureFailureAsync(VisitRun run);
}

public sealed class ExecutionEngine(IRunStore store, Func<IJiraAutomation> automationFactory, IDiagnosticSink? diagnostics = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DiagnosticAttempt? current;
    private bool statePersisted;
    public event Action<VisitRun>? Progress;

    public async Task<bool> ResumeAsync(VisitRun run, Func<FloorRun, Task<string?>> requestIssueKey, CancellationToken cancellationToken,
        Func<FloorRun, IssueSnapshot, Task<bool>>? confirmCloseRetry = null)
    {
        if (run.State == RunState.Completed) throw new InvalidOperationException("Esta execução já foi concluída.");
        foreach (var floor in run.Floors.Where(f => f.IssueKey is null && f.PendingAction == PendingAction.Create))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await requestIssueKey(floor);
            cancellationToken.ThrowIfCancellationRequested();
            if (key is null) return false;
            await AttachIssueAsync(run, floor, key.Trim().ToUpperInvariant(), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await ExecuteAsync(run, cancellationToken, confirmCloseRetry);
        return true;
    }

    private async Task SaveAsync(VisitRun run, string? message = null)
    {
        run.UpdatedAt = DateTimeOffset.Now;
        if (message is not null) run.Message = message;
        statePersisted = false;
        try
        {
            if (current is null) await store.SaveRunAsync(run);
            else await current.StepAsync("store.save", null, () => store.SaveRunAsync(run));
            statePersisted = true;
        }
        finally { current?.RecordState(statePersisted); }
        Progress?.Invoke(run);
    }

    public async Task ExecuteAsync(VisitRun run, CancellationToken cancellationToken,
        Func<FloorRun, IssueSnapshot, Task<bool>>? confirmCloseRetry = null)
    {
        if (run.State == RunState.Completed) throw new InvalidOperationException("Esta execução já foi concluída.");
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            try { diagnostics?.Write(new DiagnosticEvent { EventName = "attempt.rejected", Component = "execution", RunId = run.Id,
                Level = DiagnosticLevel.Warning, Message = "Já há uma execução em andamento.", ErrorCode = "CONCURRENT_RUN_REJECTED" }); } catch { }
            throw new InvalidOperationException("Já há uma execução em andamento.");
        }
        IJiraAutomation? browser = null;
        statePersisted = false;
        try
        {
            current = new DiagnosticAttempt(diagnostics, run);
            run.State = RunState.Running;
            await SaveAsync(run, "Abrindo o navegador");
            var uncertain = run.Floors.FirstOrDefault(f => f.IssueKey is null && (f.PendingAction == PendingAction.Create || f.Stage != FloorStage.Pending));
            if (uncertain is not null)
                throw new ReconciliationException($"A criação de {uncertain.Name} precisa de conferência. Retome pela aba Executar visita para conferir o número do chamado; nenhuma nova abertura foi enviada.");
            var initialIssueKey = run.Floors.All(f => f.IssueKey is not null)
                ? (run.Floors.FirstOrDefault(f => f.Stage != FloorStage.Closed) ?? run.Floors.First()).IssueKey : null;
            browser = await current.StepAsync("browser.create", null, () => Task.FromResult(automationFactory()));
            browser.SetDiagnostics(current);
            await current.StepAsync("browser.connect", null, () => browser.ConnectAsync(run.Settings, message =>
            {
                run.Message = message;
                Progress?.Invoke(run);
            }, cancellationToken, initialIssueKey));

            foreach (var floor in run.Floors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (floor.IssueKey is not null) { current.Skip(floor, "issue_already_registered"); continue; }
                if (floor.PendingAction == PendingAction.Create)
                    throw new ReconciliationException($"A criação de {floor.Name} pode ter sido enviada. Retome pela aba Executar visita para conferir o número do chamado.");
                await SaveAsync(run, $"Preenchendo o chamado de {floor.Name}");
                await current.StepAsync("create.prepare", floor, () => browser.PrepareCreateAsync(run, floor, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                floor.PendingAction = PendingAction.Create;
                floor.Message = "Enviando a abertura";
                await SaveAsync(run);
                current.Confirm(floor, "action.intent_saved");
                await current.FlushAsync();
                // Depois do envio, o cancelamento só é observado após registrar o resultado.
                floor.IssueKey = await current.StepAsync("create.submit", floor, () => browser.SubmitCreateAsync(run, floor));
                floor.Stage = FloorStage.Created;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Chamado aberto";
                await SaveAsync(run);
                current.Confirm(floor, "create.confirmed");
            }

            foreach (var floor in run.Floors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await ReadAsync(browser, run, floor, cancellationToken);
                if (snapshot.Closed)
                {
                    await current.StepAsync("close.verify", floor, () => { AssertClosed(snapshot, floor); return Task.CompletedTask; });
                    floor.Stage = FloorStage.Closed;
                    floor.PendingAction = PendingAction.None;
                    floor.Message = "Fechamento conferido";
                    await SaveAsync(run);
                    current.Confirm(floor, "close.confirmed", snapshot);
                    current.Skip(floor, "closed_and_verified");
                    continue;
                }
                if (floor.Stage == FloorStage.Closed || floor.PendingAction == PendingAction.Close)
                {
                    // Uma pendência antiga só pode ser liberada após conferência explícita no Jira.
                    if (floor.Stage == FloorStage.Closed || !snapshot.Started || !snapshot.AssignedToCurrentUser ||
                        confirmCloseRetry is null || !await confirmCloseRetry(floor, snapshot))
                        throw new ReconciliationException($"Confira {floor.IssueKey}: houve uma tentativa de fechamento com resultado incerto. O chamado ainda não está fechado; nenhuma nova mensagem foi enviada.");
                    cancellationToken.ThrowIfCancellationRequested();
                    floor.PendingAction = PendingAction.None;
                    await SaveAsync(run, $"Novo fechamento de {floor.IssueKey} autorizado após conferência no Jira.");
                    current.Confirm(floor, "close.retry_authorized");
                }
                if (!snapshot.AssignedToCurrentUser)
                    await PerformAsync(run, floor, PendingAction.Assign, $"Atribuindo {floor.Name}", () => browser.AssignAsync(run, floor));
                else current.Skip(floor, "assignment_verified");
                snapshot = await ReadAsync(browser, run, floor, cancellationToken);
                if (!snapshot.AssignedToCurrentUser) throw new InvalidOperationException($"Não foi possível confirmar a atribuição de {floor.IssueKey} ao técnico conectado.");
                floor.Stage = snapshot.Started ? FloorStage.Started : FloorStage.Assigned;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Atribuição conferida";
                await SaveAsync(run);
                current.Confirm(floor, "assign.confirmed", snapshot);
            }

            foreach (var floor in run.Floors.Where(f => f.Stage != FloorStage.Closed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await ReadAsync(browser, run, floor, cancellationToken);
                if (!snapshot.Started)
                    await PerformAsync(run, floor, PendingAction.Start, $"Iniciando atendimento de {floor.Name}", () => browser.StartAsync(run, floor));
                else current.Skip(floor, "start_verified");
                snapshot = await ReadAsync(browser, run, floor, cancellationToken);
                if (!snapshot.Started) throw new InvalidOperationException($"O início de atendimento de {floor.IssueKey} não foi confirmado.");
                floor.Stage = FloorStage.Started;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Em atendimento";
                await SaveAsync(run);
                current.Confirm(floor, "start.confirmed", snapshot);
            }

            foreach (var floor in run.Floors.Where(f => f.Stage != FloorStage.Closed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var beforeClosing = await ReadAsync(browser, run, floor, cancellationToken);
                if (!beforeClosing.AssignedToCurrentUser || !beforeClosing.Started)
                    throw new ReconciliationException($"Confira o responsável e o status de {floor.IssueKey} antes de fechar.");
                await current.StepAsync("close.prepare", floor, () => browser.PrepareCloseAsync(run, floor, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                await PerformAsync(run, floor, PendingAction.Close, $"Fechando o chamado de {floor.Name}", () => browser.SubmitCloseAsync(run, floor));
                var snapshot = await ReadAsync(browser, run, floor, CancellationToken.None);
                await current.StepAsync("close.verify", floor, () => { AssertClosed(snapshot, floor); return Task.CompletedTask; });
                floor.Stage = FloorStage.Closed;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Fechado e conferido";
                await SaveAsync(run);
                current.Confirm(floor, "close.confirmed", snapshot);
            }
            run.State = RunState.Completed;
            await SaveAsync(run, "Todos os chamados foram fechados e conferidos.");
        }
        catch (OperationCanceledException)
        {
            run.State = RunState.Interrupted;
            await SaveAfterErrorAsync(run, "Execução interrompida. O progresso foi salvo.");
        }
        catch (Exception exception)
        {
            run.State = exception is ReconciliationException || run.Floors.Any(f => f.PendingAction == PendingAction.Create && f.IssueKey is null)
                ? RunState.NeedsReconciliation : RunState.Failed;
            current?.Error(exception);
            await SaveAfterErrorAsync(run, exception.Message);
            if (browser is not null)
                try { await browser.CaptureFailureAsync(run); }
                catch (Exception captureError) { current?.Error(captureError, "diagnostic.capture", secondary: true); }
        }
        finally
        {
            try
            {
                if (browser is not null)
                    try { await browser.DisposeAsync(); current?.Event("browser.closed", "Navegador encerrado."); }
                    catch (Exception cleanupError) { current?.Error(cleanupError, "browser.dispose", secondary: true); }
                if (current is not null) await current.FinishAsync(statePersisted);
            }
            finally { current = null; gate.Release(); }
        }
    }

    private async Task SaveAfterErrorAsync(VisitRun run, string message)
    {
        try { await SaveAsync(run, message); }
        catch (Exception saveError)
        {
            statePersisted = false;
            current?.Error(saveError, "store.save", secondary: true);
            run.Message = message + " Não foi possível salvar o progresso local; confira o diagnóstico antes de retomar.";
        }
    }

    private async Task<IssueSnapshot> ReadAsync(IJiraAutomation browser, VisitRun run, FloorRun floor, CancellationToken token)
    {
        var snapshot = await current!.StepAsync("issue.read", floor, () => browser.ReadIssueAsync(run, floor, token));
        current.SnapshotEvent(floor, snapshot);
        return snapshot;
    }

    private async Task PerformAsync(VisitRun run, FloorRun floor, PendingAction action, string message, Func<Task> operation)
    {
        floor.PendingAction = action;
        floor.Message = message;
        await SaveAsync(run, message);
        current?.Confirm(floor, "action.intent_saved");
        if (current is not null)
        {
            await current.FlushAsync();
            await current.StepAsync(action == PendingAction.Close ? "close.submit" : action == PendingAction.Assign ? "assign.submit" : "start.submit", floor, operation);
        }
        else await operation();
        // A intenção fica pendente até a verificação posterior da página.
    }

    private static void AssertClosed(IssueSnapshot snapshot, FloorRun floor)
    {
        if (!snapshot.Closed || !snapshot.ResolutionMatches || !snapshot.PublicCommentMatches || !snapshot.TeamMatches)
            throw new ReconciliationException($"O fechamento de {floor.IssueKey} precisa de conferência: status, resolução, comentário público ou equipe não correspondem ao previsto. Nenhum texto será reenviado automaticamente.");
    }

    public async Task AttachIssueAsync(VisitRun run, FloorRun floor, string key, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Z][A-Z0-9]*-\d+$")) throw new InvalidOperationException("Informe o número no formato SD-123456.");
        if (floor.IssueKey is not null || run.Floors.Any(f => f.IssueKey == key)) throw new InvalidOperationException("Este chamado ou pavimento já está vinculado.");
        if (floor.PendingAction != PendingAction.Create) throw new InvalidOperationException("Este pavimento não tem uma criação pendente de conferência.");
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Pare a execução antes de vincular um chamado.");
        IJiraAutomation? browser = null;
        statePersisted = false;
        var linked = false;
        try
        {
            current = new DiagnosticAttempt(diagnostics, run, "link");
            browser = await current.StepAsync("browser.create", null, () => Task.FromResult(automationFactory()));
            browser.SetDiagnostics(current);
            await current.StepAsync("browser.connect", null, () => browser.ConnectAsync(run.Settings, _ => { }, cancellationToken));
            var candidate = Serialization.Copy(floor);
            candidate.IssueKey = key;
            await ReadAsync(browser, run, candidate, cancellationToken); // Confere todos os campos antes de vincular.
            floor.IssueKey = key;
            floor.Stage = FloorStage.Created;
            floor.PendingAction = PendingAction.None;
            floor.Message = "Chamado vinculado após conferência";
            run.State = RunState.Interrupted;
            await SaveAsync(run, "Chamado vinculado. A execução pode ser retomada.");
            current.Confirm(floor, "issue.linked"); linked = true;
        }
        catch (Exception ex) { if (ex is not OperationCanceledException) current?.Error(ex); throw; }
        finally
        {
            try
            {
                if (browser is not null)
                    try { await browser.DisposeAsync(); } catch (Exception ex) { current?.Error(ex, "browser.dispose", secondary: true); }
                if (current is not null) await current.FinishAsync(statePersisted, linked ? "Linked" : cancellationToken.IsCancellationRequested ? "Interrupted" : "Failed");
            }
            finally { current = null; gate.Release(); }
        }
    }
}
