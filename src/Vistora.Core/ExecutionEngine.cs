namespace Vistora.Core;

public interface IRunStore
{
    Task SaveRunAsync(VisitRun run);
}

public interface IJiraAutomation : IAsyncDisposable
{
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

public sealed class ExecutionEngine(IRunStore store, Func<IJiraAutomation> automationFactory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action<VisitRun>? Progress;

    private async Task SaveAsync(VisitRun run, string? message = null)
    {
        run.UpdatedAt = DateTimeOffset.Now;
        if (message is not null) run.Message = message;
        await store.SaveRunAsync(run);
        Progress?.Invoke(run);
    }

    public async Task ExecuteAsync(VisitRun run, CancellationToken cancellationToken)
    {
        if (run.State == RunState.Completed) throw new InvalidOperationException("Esta execução já foi concluída.");
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Já há uma execução em andamento.");
        await using var browser = automationFactory();
        try
        {
            run.State = RunState.Running;
            await SaveAsync(run, "Abrindo o Microsoft Edge");
            var uncertain = run.Floors.FirstOrDefault(f => f.IssueKey is null && (f.PendingAction == PendingAction.Create || f.Stage != FloorStage.Pending));
            if (uncertain is not null)
                throw new ReconciliationException($"A criação de {uncertain.Name} precisa de conferência. Vincule o número do chamado no histórico antes de retomar; nenhuma nova abertura foi enviada.");
            var initialIssueKey = run.Floors.All(f => f.IssueKey is not null)
                ? (run.Floors.FirstOrDefault(f => f.Stage != FloorStage.Closed) ?? run.Floors.First()).IssueKey : null;
            await browser.ConnectAsync(run.Settings, message =>
            {
                run.Message = message;
                Progress?.Invoke(run);
            }, cancellationToken, initialIssueKey);

            foreach (var floor in run.Floors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (floor.IssueKey is not null) continue;
                if (floor.PendingAction == PendingAction.Create)
                    throw new ReconciliationException($"A criação de {floor.Name} pode ter sido enviada. Vincule o número do chamado no histórico antes de retomar.");
                await SaveAsync(run, $"Preenchendo o chamado de {floor.Name}");
                await browser.PrepareCreateAsync(run, floor, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                floor.PendingAction = PendingAction.Create;
                floor.Message = "Enviando a abertura";
                await SaveAsync(run);
                // Depois do envio, o cancelamento só é observado após registrar o resultado.
                floor.IssueKey = await browser.SubmitCreateAsync(run, floor);
                floor.Stage = FloorStage.Created;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Chamado aberto";
                await SaveAsync(run);
            }

            foreach (var floor in run.Floors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await browser.ReadIssueAsync(run, floor, cancellationToken);
                if (snapshot.Closed)
                {
                    AssertClosed(snapshot, floor);
                    floor.Stage = FloorStage.Closed;
                    floor.PendingAction = PendingAction.None;
                    floor.Message = "Fechamento conferido";
                    await SaveAsync(run);
                    continue;
                }
                if (floor.Stage == FloorStage.Closed || floor.PendingAction == PendingAction.Close)
                    throw new ReconciliationException($"Confira {floor.IssueKey}: houve uma tentativa de fechamento com resultado incerto. O chamado ainda não está fechado; nenhuma nova mensagem foi enviada.");
                if (!snapshot.AssignedToCurrentUser)
                    await PerformAsync(run, floor, PendingAction.Assign, $"Atribuindo {floor.Name}", () => browser.AssignAsync(run, floor));
                snapshot = await browser.ReadIssueAsync(run, floor, cancellationToken);
                if (!snapshot.AssignedToCurrentUser) throw new InvalidOperationException($"Não foi possível confirmar a atribuição de {floor.IssueKey} ao técnico conectado.");
                floor.Stage = snapshot.Started ? FloorStage.Started : FloorStage.Assigned;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Atribuição conferida";
                await SaveAsync(run);
            }

            foreach (var floor in run.Floors.Where(f => f.Stage != FloorStage.Closed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await browser.ReadIssueAsync(run, floor, cancellationToken);
                if (!snapshot.Started)
                    await PerformAsync(run, floor, PendingAction.Start, $"Iniciando atendimento de {floor.Name}", () => browser.StartAsync(run, floor));
                snapshot = await browser.ReadIssueAsync(run, floor, cancellationToken);
                if (!snapshot.Started) throw new InvalidOperationException($"O início de atendimento de {floor.IssueKey} não foi confirmado.");
                floor.Stage = FloorStage.Started;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Em atendimento";
                await SaveAsync(run);
            }

            foreach (var floor in run.Floors.Where(f => f.Stage != FloorStage.Closed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var beforeClosing = await browser.ReadIssueAsync(run, floor, cancellationToken);
                if (!beforeClosing.AssignedToCurrentUser || !beforeClosing.Started)
                    throw new ReconciliationException($"Confira o responsável e o status de {floor.IssueKey} antes de fechar.");
                await browser.PrepareCloseAsync(run, floor, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await PerformAsync(run, floor, PendingAction.Close, $"Fechando o chamado de {floor.Name}", () => browser.SubmitCloseAsync(run, floor));
                var snapshot = await browser.ReadIssueAsync(run, floor, CancellationToken.None);
                AssertClosed(snapshot, floor);
                floor.Stage = FloorStage.Closed;
                floor.PendingAction = PendingAction.None;
                floor.Message = "Fechado e conferido";
                await SaveAsync(run);
            }
            run.State = RunState.Completed;
            await SaveAsync(run, "Todos os chamados foram fechados e conferidos.");
        }
        catch (OperationCanceledException)
        {
            run.State = RunState.Interrupted;
            await SaveAsync(run, "Execução interrompida. O progresso foi salvo.");
        }
        catch (Exception exception)
        {
            run.State = exception is ReconciliationException || run.Floors.Any(f => f.PendingAction == PendingAction.Create && f.IssueKey is null)
                ? RunState.NeedsReconciliation : RunState.Failed;
            await SaveAsync(run, exception.Message);
            try { await browser.CaptureFailureAsync(run); } catch { /* O erro original fica no registro. */ }
        }
        finally { gate.Release(); }
    }

    private async Task PerformAsync(VisitRun run, FloorRun floor, PendingAction action, string message, Func<Task> operation)
    {
        floor.PendingAction = action;
        floor.Message = message;
        await SaveAsync(run, message);
        await operation();
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
        try
        {
            await using var browser = automationFactory();
            await browser.ConnectAsync(run.Settings, _ => { }, cancellationToken);
            var candidate = Serialization.Copy(floor);
            candidate.IssueKey = key;
            await browser.ReadIssueAsync(run, candidate, cancellationToken); // Confere todos os campos antes de vincular.
            floor.IssueKey = key;
            floor.Stage = FloorStage.Created;
            floor.PendingAction = PendingAction.None;
            floor.Message = "Chamado vinculado após conferência";
            run.State = RunState.Interrupted;
            await SaveAsync(run, "Chamado vinculado. A execução pode ser retomada.");
        }
        finally { gate.Release(); }
    }
}
