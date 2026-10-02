namespace Vistora.Core;

// Uma tentativa vazia não deve esconder a visita que já tem chamados no Jira.
public static class VisitRunSelection
{
    public static VisitRun? PendingForProfile(IEnumerable<VisitRun> runs, string profileId)
    {
        var pending = runs.Where(r => r.Profile.Id == profileId && r.State != RunState.Completed).ToList();
        var withIssues = pending.Where(r => r.Floors.Any(f => f.IssueKey is not null)).ToList();
        if (withIssues.Count > 1)
            throw new InvalidOperationException("Este perfil tem mais de uma visita pendente com chamados. Selecione a visita no histórico e clique em Retomar execução.");
        if (withIssues.Count == 1) return withIssues[0];
        var withProgress = pending.Where(HasProgress).ToList();
        if (withProgress.Count > 1)
            throw new InvalidOperationException("Este perfil tem mais de uma abertura que precisa de conferência. Selecione a visita no histórico; nenhum novo chamado foi aberto.");
        return withProgress.SingleOrDefault() ?? pending.OrderBy(r => r.CreatedAt).FirstOrDefault();
    }

    public static VisitRun ForResume(IEnumerable<VisitRun> runs, VisitRun selected)
    {
        if (selected.State == RunState.Completed) throw new InvalidOperationException("Esta execução já foi concluída.");
        return HasProgress(selected) ? selected : PendingForProfile(runs, selected.Profile.Id) ?? selected;
    }

    private static bool HasProgress(VisitRun run) => run.Floors.Any(f =>
        f.IssueKey is not null || f.PendingAction != PendingAction.None || f.Stage != FloorStage.Pending);
}
