using Alveus.Web.Conversations;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace Alveus.Web.Activities;

/// <summary>
/// Suspend réellement <c>AlveusTaskWorkflow</c> via un bookmark Elsa natif (cf. ADR 0027) : posté
/// après une issue "NeedsHelp" d'une réunion (<see cref="MeetingActivityBase"/>), en attente d'une
/// réponse humaine via la conversation associée (<c>POST /v1/conversations/{id}/items</c>).
/// <see cref="ExecuteAsync"/> poste un item <see cref="ConversationItemKind.NeedsHelpQuestion"/>,
/// crée le bookmark (<see cref="OnResumeAsync"/> en callback, <c>AutoComplete = false</c>) et
/// l'enregistre auprès d'<see cref="IConversationStore"/> — l'activité ne se complète pas : le
/// workflow est suspendu sans consommer de ressources jusqu'à
/// <c>IWorkflowClient.RunInstanceAsync</c> (reprise via <c>BookmarkId</c>).
/// L'identifiant de conversation est lu via <c>WorkflowExecutionContext.CorrelationId</c> (stable à
/// travers la suspension/reprise, à la différence des entrées d'<c>Input</c> du workflow qui ne sont
/// pas conservées lors de la reprise — ⚠ vérifié empiriquement, cf. ADR 0027).
/// <see cref="OnResumeAsync"/> lit l'input de reprise ("Reply"), poste un item
/// <see cref="ConversationItemKind.HumanReply"/>, expose <see cref="HumanReply"/> et complète par
/// "Done".
/// </summary>
[Activity("Alveus", "AI", "Suspend le workflow (bookmark) en attendant une réponse humaine via la conversation associée, puis reprend avec HumanReply.")]
public sealed class AwaitConversationReply : CodeActivity
{
    [Input(Description = "Libellé de la source de la demande d'aide (ex. 'RunPreTaskMeeting', 'RunFinalReviewMeeting').")]
    public Input<string> SourceLabel { get; set; } = default!;

    [Input(Description = "Point de blocage signalé par la réunion (issue NeedsHelp), si disponible.")]
    public Input<string?> Reason { get; set; } = new(default(string?));

    [Input(Description = "Questions posées par la réunion (issue NeedsHelp), si disponibles.")]
    public Input<IReadOnlyList<string>?> Questions { get; set; } = new(default(IReadOnlyList<string>?));

    [Output(Description = "Réponse de l'utilisateur reçue via la conversation.")]
    public Output<string> HumanReply { get; set; } = new();

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var conversationId = context.WorkflowExecutionContext.CorrelationId;
        if (!string.IsNullOrEmpty(conversationId))
        {
            var sourceLabel = context.Get(SourceLabel) ?? string.Empty;
            var store = context.GetRequiredService<IConversationStore>();
            store.AddItem(
                conversationId,
                "assistant",
                BuildQuestionText(sourceLabel, context.Get(Reason), context.Get(Questions)),
                ConversationItemKind.NeedsHelpQuestion,
                new Dictionary<string, string> { ["source"] = sourceLabel });
        }

        var bookmark = context.CreateBookmark(new CreateBookmarkArgs
        {
            Callback = OnResumeAsync,
            AutoComplete = false,
        });

        if (!string.IsNullOrEmpty(conversationId))
        {
            var store = context.GetRequiredService<IConversationStore>();
            store.SetPendingBookmark(conversationId, bookmark.Id);
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        context.TryGetWorkflowInput<string?>("Reply", out var reply);
        reply ??= string.Empty;

        var conversationId = context.WorkflowExecutionContext.CorrelationId;
        if (!string.IsNullOrEmpty(conversationId))
        {
            var store = context.GetRequiredService<IConversationStore>();
            store.AddItem(conversationId, "user", reply, ConversationItemKind.HumanReply);
        }

        context.Set(HumanReply, reply);
        await context.CompleteActivityWithOutcomesAsync(["Done"]);
    }

    private static string BuildQuestionText(string sourceLabel, string? reason, IReadOnlyList<string>? questions)
    {
        var text = $"[{sourceLabel}] La réunion a besoin d'aide pour continuer.";

        if (!string.IsNullOrWhiteSpace(reason))
        {
            text += $"\nPoint de blocage : {reason}";
        }

        if (questions is { Count: > 0 })
        {
            text += "\nQuestions :\n" + string.Join("\n", questions.Select(q => $"- {q}"));
        }

        return text;
    }
}
