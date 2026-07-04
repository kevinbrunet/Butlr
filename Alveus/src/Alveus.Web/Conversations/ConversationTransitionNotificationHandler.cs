using Elsa.Mediator.Contracts;
using Elsa.Workflows;
using Elsa.Workflows.Notifications;

namespace Alveus.Web.Conversations;

/// <summary>
/// Poste un item <see cref="ConversationItemKind.ActivityTransition"/> à chaque démarrage
/// (<see cref="ActivityExecuting"/>) et fin (<see cref="ActivityExecuted"/>) d'une activité du
/// graphe <c>AlveusTaskWorkflow</c> (cf. ADR 0027) — filtré sur <see cref="TrackedActivityIds"/>
/// pour ignorer le bruit des activités internes (variables, expressions, etc.).
/// L'identifiant de conversation est résolu via
/// <c>ActivityExecutionContext.WorkflowExecutionContext.CorrelationId</c> (même mécanisme que
/// <see cref="Activities.AwaitConversationReply"/>).
/// ⚠ <see cref="ActivityExecuted"/> ne porte pas d'information sur les "outcomes" de l'activité
/// (vérifié par inspection des symboles Elsa 3.7.0) — seul <c>ActivityExecutionContext.Status</c>
/// (Completed/Faulted/Canceled) est rapporté.
/// </summary>
public sealed class ConversationTransitionNotificationHandler :
    INotificationHandler<ActivityExecuting>,
    INotificationHandler<ActivityExecuted>
{
    /// <summary>Activités du graphe <c>AlveusTaskWorkflow</c> dont les transitions sont notifiées.</summary>
    private static readonly HashSet<string> TrackedActivityIds = new(StringComparer.Ordinal)
    {
        "RunPreTaskMeeting",
        "AwaitPreTaskReply",
        "PreTaskHelpLoopGuard",
        "RunWorker",
        "RunEnvironmentManager",
        "RunEvaluator",
        "LoopGuard",
        "RunUserDoc",
        "RunFinalReviewMeeting",
        "AwaitFinalReviewReply",
        "FinalReviewHelpLoopGuard",
        "OuterLoopGuard",
        "RecordWorkerEscalation",
        "RecordEnvironmentManagerEscalation",
        "RecordEvaluatorEscalation",
        "RecordUserDocEscalation",
        "AgentEscalationLoopGuard",
    };

    private readonly IConversationStore _store;

    public ConversationTransitionNotificationHandler(IConversationStore store)
    {
        _store = store;
    }

    public Task HandleAsync(ActivityExecuting notification, CancellationToken cancellationToken)
        => PostTransitionAsync(notification.ActivityExecutionContext, "starting");

    public Task HandleAsync(ActivityExecuted notification, CancellationToken cancellationToken)
        => PostTransitionAsync(notification.ActivityExecutionContext, notification.ActivityExecutionContext.Status.ToString());

    private Task PostTransitionAsync(ActivityExecutionContext context, string phase)
    {
        var activityId = context.Activity.Id;
        if (!TrackedActivityIds.Contains(activityId))
        {
            return Task.CompletedTask;
        }

        var conversationId = context.WorkflowExecutionContext.CorrelationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return Task.CompletedTask;
        }

        _store.AddItem(
            conversationId,
            "assistant",
            $"[{activityId}] {phase}",
            ConversationItemKind.ActivityTransition,
            new Dictionary<string, string> { ["activityId"] = activityId, ["phase"] = phase });

        return Task.CompletedTask;
    }
}
