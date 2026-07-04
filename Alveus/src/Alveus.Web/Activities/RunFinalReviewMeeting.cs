using Alveus.Web.Agents;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace Alveus.Web.Activities;

/// <summary>
/// Réunion finale (cf. ADR 0024/0026/0030) : après Worker/EnvironmentManager/Evaluator/UserDoc, les
/// spécialistes configurés (<see cref="MeetingActivityBase.SpecialistRoleKeys"/>), Alveus-Qa et
/// Alveus-Technical votent sur le topic implicite "task-fulfilled" (<see cref="MeetingActivityBase.Topic"/>
/// contient les résumés du travail effectué, cf. ADR 0026 — la réunion travaille sur ces résumés
/// plus la documentation propre de chaque agent, pas sur un accès filesystem étendu). Sortie "OK"
/// si "task-fulfilled" est résolu en accord (unanime ou majoritaire après correction), "KO" sinon
/// (chaque agent a alors écrit un compte-rendu récupéré dans
/// <see cref="SpecialistReports"/>/<see cref="QaReport"/>/<see cref="TechReport"/>), "NeedsHelp" en
/// cas d'absence de consensus après <see cref="MeetingActivityBase.MaxRounds"/> ou de désaccord
/// persistant sur un topic annexe.
/// </summary>
[Activity("Alveus", "AI", "Réunion finale : les spécialistes configurés, Alveus-Qa et Alveus-Technical votent sur le fait que la tâche est correctement remplie (OK/KO/NeedsHelp).")]
public sealed class RunFinalReviewMeeting : MeetingActivityBase
{
    /// <summary>Topic implicite seedé au round 1 (cf. ADR 0024).</summary>
    public const string TaskFulfilledTopic = "task-fulfilled";

    public RunFinalReviewMeeting(IAgentSessionCompactionService compactionService)
        : base(compactionService)
    {
    }

    [Output(Description = "Verdict de la réunion finale : 'ok' ou 'ko'.")]
    public Output<string?> FinalVerdict { get; set; } = new();

    [Output(Description = "Comptes-rendus des spécialistes configurés en cas de verdict 'ko', par clé de rôle (cf. SpecialistRoleCatalog).")]
    public Output<IReadOnlyDictionary<string, string>?> SpecialistReports { get; set; } = new();

    [Output(Description = "Compte-rendu d'Alveus-Qa en cas de verdict 'ko'.")]
    public Output<string?> QaReport { get; set; } = new();

    [Output(Description = "Compte-rendu d'Alveus-Technical en cas de verdict 'ko'.")]
    public Output<string?> TechReport { get; set; } = new();

    protected override IReadOnlyCollection<string> SeedOpenTopics() => [TaskFulfilledTopic];

    protected override string GetRoleTask(string agentRole) => agentRole switch
    {
        "Qa" => "Tu es Alveus-Qa. Voici un résumé du travail effectué par Alveus-Worker, Alveus-EnvironmentManager, "
            + "Alveus-Evaluator et Alveus-UserDoc pour ce ticket. Relis ton plan de test ('test-plan/') et vérifie "
            + $"qu'il couvre bien ce qui a été fait. Vote sur '{TaskFulfilledTopic}' (agree = le travail répond au "
            + "plan de test). Si tu votes 'disagree', écris un compte-rendu markdown dans ton workspace expliquant "
            + "précisément ce qui ne correspond pas, et reprends ce compte-rendu dans le résumé de ton Finish final.",

        "Technical" => "Tu es Alveus-Technical. Voici un résumé du travail effectué par Alveus-Worker, "
            + "Alveus-EnvironmentManager, Alveus-Evaluator et Alveus-UserDoc pour ce ticket. Relis ta documentation "
            + $"d'architecture ('tech-docs/') et vérifie qu'elle correspond bien au travail décrit. Vote sur "
            + $"'{TaskFulfilledTopic}' (agree = le travail est correct d'un point de vue technique). Si tu votes "
            + "'disagree', écris un compte-rendu markdown dans ton workspace expliquant précisément ce qui ne "
            + "correspond pas, et reprends ce compte-rendu dans le résumé de ton Finish final.",

        _ => SpecialistRoleCatalog.Roles.TryGetValue(agentRole, out var def)
            ? def.FinalReviewRoleTask
            : throw new ArgumentOutOfRangeException(nameof(agentRole), agentRole, "Rôle de réunion inconnu."),
    };

    protected override ValueTask OnAgentFinishAsync(ActivityExecutionContext context, string agentRole, FinishCall finish)
        => ValueTask.CompletedTask;

    protected override ValueTask FinalizeAsync(
        ActivityExecutionContext context,
        MeetingOutcome outcome,
        IReadOnlyDictionary<string, MeetingVoteTally> topicTallies,
        IReadOnlyDictionary<string, string> finishSummaries)
    {
        if (outcome == MeetingOutcome.NeedsHelp)
        {
            return context.CompleteActivityWithOutcomesAsync(["NeedsHelp"]);
        }

        var tally = topicTallies.GetValueOrDefault(TaskFulfilledTopic);
        var isOk = tally is not null && tally.Agree > tally.Disagree;

        context.Set(FinalVerdict, isOk ? "ok" : "ko");

        if (isOk)
        {
            return context.CompleteActivityWithOutcomesAsync(["OK"]);
        }

        context.Set(SpecialistReports, finishSummaries
            .Where(kv => kv.Key is not ("Qa" or "Technical"))
            .ToDictionary(kv => kv.Key, kv => kv.Value));
        context.Set(QaReport, finishSummaries.GetValueOrDefault("Qa"));
        context.Set(TechReport, finishSummaries.GetValueOrDefault("Technical"));

        return context.CompleteActivityWithOutcomesAsync(["KO"]);
    }
}
