using System.ComponentModel;
using Alveus.Web.Conversations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Tools;

/// <summary>
/// Outil de consultation d'un expert de l'équipe sans passer par une réunion de pré-tâche (cf. ADR
/// 0032) : invoque l'agent expert en in-process, poste la question et la réponse dans la
/// conversation en cours, retourne la réponse à l'agent appelant. Enregistré par équipe sous la clé
/// "{teamName}" ; ajouté au Worker uniquement si <c>EscalationMode = "tool"</c>.
/// </summary>
public sealed class AskExpertTool(
    string teamName,
    IServiceProvider serviceProvider,
    IConversationStore conversationStore,
    IConversationContextAccessor conversationContextAccessor)
{
    private const int MaxIterations = 4;

    private const string ReminderPrompt =
        "Tu n'as pas appelé l'outil Finish. Réponds à la question posée et appelle Finish avec "
        + "outcome='pass' et summary=ta réponse complète.";

    [Description("Consulte un expert de l'équipe (BusinessAnalyst, Qa, Technical, ou tout autre rôle configuré) "
        + "pour obtenir une réponse à une question précise sur la tâche en cours. La question et la réponse "
        + "sont enregistrées dans la conversation en cours.")]
    public async Task<string> AskExpertAsync(
        [Description("Rôle de l'expert à consulter (ex. 'BusinessAnalyst', 'Qa', 'Technical').")] string expertRole,
        [Description("Question précise à poser à l'expert.")] string question,
        CancellationToken cancellationToken = default)
    {
        var agentKey = $"{teamName}:{expertRole}";
        AIAgent agent;
        try
        {
            agent = serviceProvider.GetRequiredKeyedService<AIAgent>(agentKey);
        }
        catch (InvalidOperationException)
        {
            return $"Expert '{expertRole}' introuvable pour l'équipe '{teamName}'.";
        }

        var conversationId = conversationContextAccessor.ConversationId;
        if (conversationId is not null)
        {
            conversationStore.AddItem(conversationId, "agent", question, ConversationItemKind.ExpertQuestion,
                new Dictionary<string, string> { ["expert"] = expertRole });
        }

        var answer = await InvokeAsync(agent, question, cancellationToken);

        if (conversationId is not null)
        {
            conversationStore.AddItem(conversationId, "agent", answer, ConversationItemKind.ExpertAnswer,
                new Dictionary<string, string> { ["expert"] = expertRole });
        }

        return answer;
    }

    internal async Task<string> InvokeAsync(AIAgent agent, string question, CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync(cancellationToken);
        var prompt = $"Un agent de l'équipe te pose la question suivante :\n\n{question}\n\n"
            + "Réponds de façon précise, en consultant ta documentation si nécessaire, "
            + "puis appelle Finish avec outcome='pass' et summary=ta réponse complète.";

        for (var i = 0; i < MaxIterations; i++)
        {
            var response = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            var finish = FindFinishCall(response);
            if (finish is not null)
            {
                return finish.Summary;
            }
            prompt = ReminderPrompt;
        }

        return $"L'expert n'a pas pu répondre (nombre maximal de relances atteint).";
    }

    private static FinishCall? FindFinishCall(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { Name: FinishTool.FunctionName } call)
                {
                    return FinishCall.FromArguments(call.Arguments);
                }
            }
        }
        return null;
    }
}
