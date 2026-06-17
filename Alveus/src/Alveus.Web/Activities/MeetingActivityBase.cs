using System.Text;
using System.Text.Json;
using Alveus.Web.Agents;
using Alveus.Web.Conversations;
using Alveus.Web.Tools;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Alveus.Web.Activities;

/// <summary>
/// Base commune aux réunions multi-agents hand-rolled (<c>RunPreTaskMeeting</c>,
/// <c>RunFinalReviewMeeting</c>) — cf. ADR 0024. Orchestre un débat round-robin entre N
/// participants : les spécialistes configurés (<see cref="SpecialistRoleKeys"/>, cf. ADR 0030),
/// Alveus-Qa et Alveus-Technical, chacun avec sa propre <see cref="AgentSession"/> persistée
/// (même mécanisme que <see cref="AgentPromptActivityBase"/> — ADR 0018), outillés avec
/// <see cref="FinishTool"/> et <see cref="MeetingTool"/> (<c>Raise</c>/<c>Vote</c>).
///
/// Déroulement : à chaque round, les participants s'expriment dans l'ordre des spécialistes
/// configurés, puis Qa, puis Technical. Un <c>Raise(topic, ...)</c> ouvre un topic ; un
/// <c>Vote(topic, decision, ...)</c> y répond. Un topic ayant reçu un vote de tous les
/// participants est tranché : unanime → résolu et retiré des topics ouverts ; partagé → les votes
/// sont effacés et le topic repasse en "round de correction" (les participants sont invités à
/// reconsidérer) ; encore partagé après correction → <see cref="MeetingOutcome.NeedsHelp"/>
/// immédiat. La réunion se termine par <see cref="MeetingOutcome.Done"/> dès qu'un round voit tous
/// les participants confirmer "Finish(done)" sans topic ouvert restant, ou par
/// <see cref="MeetingOutcome.NeedsHelp"/> si <see cref="MaxRounds"/> est atteint.
/// </summary>
public abstract class MeetingActivityBase : CodeActivity
{
    private const string SessionStatePropertyPrefix = "AgentSession::";

    /// <summary>Nombre maximal de rounds de débat avant de sortir en "NeedsHelp".</summary>
    public const int MaxRounds = 4;

    private readonly IAgentSessionCompactionService _compactionService;

    protected MeetingActivityBase(IAgentSessionCompactionService compactionService)
    {
        ArgumentNullException.ThrowIfNull(compactionService);
        _compactionService = compactionService;

        SpecialistRoleKeys = new Input<IReadOnlyList<string>>(["BusinessAnalyst"]);
        TeamName = new Input<string>(string.Empty);
    }

    [Input(Description = "Clés des rôles spécialistes participant à la réunion (catalogue SpecialistRoleCatalog, cf. Teams[*].SpecialistRoles et ADR 0030/0031).")]
    public Input<IReadOnlyList<string>> SpecialistRoleKeys { get; set; }

    /// <summary>
    /// Nom de l'équipe (cf. <c>TeamConfig.Name</c>, ADR 0031) utilisé pour dériver les clés DI des
    /// agents (<c>"{TeamName}:{role}"</c>). Vide = nommage legacy <c>"Alveus{role}"</c> (tests isolés).
    /// </summary>
    [Input(Description = "Nom de l'équipe (TeamConfig:Name). Vide = fallback sur les noms d'agents legacy Alveus*.")]
    public Input<string> TeamName { get; set; }

    [Input(Description = "Sujet de la réunion (ticket / consigne de tâche).")]
    public Input<string> Topic { get; set; } = default!;

    [Input(Description = "Contexte additionnel (comptes-rendus de la réunion finale précédente en cas de boucle KO, vide normalement).")]
    public Input<string?> ExtraContext { get; set; } = new(string.Empty);

    [Output(Description = "Point de blocage signalé par un participant (issues NeedsMoreInfo et Blocked).")]
    public Output<string?> Reason { get; set; } = new();

    [Output(Description = "Questions posées par un participant pour pouvoir continuer (issue NeedsMoreInfo).")]
    public Output<IReadOnlyList<string>?> Questions { get; set; } = new();

    /// <summary>Consigne spécifique à cette réunion pour le rôle <paramref name="agentRole"/> ("BusinessAnalyst", "Qa" ou "Technical").</summary>
    protected abstract string GetRoleTask(string agentRole);

    /// <summary>
    /// Topics ouverts dès le round 1, avant tout <c>Raise</c> — utilisé par la réunion finale pour
    /// injecter le topic implicite "task-fulfilled".
    /// </summary>
    protected virtual IReadOnlyCollection<string> SeedOpenTopics() => [];

    /// <summary>
    /// Appelé pour chaque <see cref="FinishCall"/> "done" d'un participant — permet à la
    /// sous-classe de capter des champs spécifiques (ex. <see cref="FinishCall.DownstreamInstructions"/>
    /// côté pré-tâche, comptes-rendus côté réunion finale).
    /// </summary>
    protected abstract ValueTask OnAgentFinishAsync(ActivityExecutionContext context, string agentRole, FinishCall finish);

    /// <summary>
    /// Termine l'activité avec les issues propres à la sous-classe ("Done"/"NeedsHelp" pour
    /// <c>RunPreTaskMeeting</c> ; "OK"/"KO"/"NeedsHelp" pour <c>RunFinalReviewMeeting</c>, à partir
    /// du tally de "task-fulfilled" dans <paramref name="topicTallies"/>).
    /// </summary>
    protected abstract ValueTask FinalizeAsync(
        ActivityExecutionContext context,
        MeetingOutcome outcome,
        IReadOnlyDictionary<string, MeetingVoteTally> topicTallies,
        IReadOnlyDictionary<string, string> finishSummaries);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var conversationId = context.WorkflowExecutionContext.CorrelationId;
        var contextAccessor = context.GetRequiredService<IConversationContextAccessor>();
        contextAccessor.ConversationId = conversationId;
        var conversationStore = string.IsNullOrEmpty(conversationId) ? null : context.GetRequiredService<IConversationStore>();

        var topicText = context.Get(Topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicText);

        var extraContext = context.Get(ExtraContext) ?? string.Empty;

        var specialistRoleKeys = context.Get(SpecialistRoleKeys) ?? [];
        var roles = specialistRoleKeys.Concat(["Qa", "Technical"]).ToList();

        var teamName = context.Get(TeamName) ?? string.Empty;
        var agentNames = new Dictionary<string, string>();
        foreach (var roleKey in specialistRoleKeys)
        {
            agentNames[roleKey] = string.IsNullOrEmpty(teamName) ? "Alveus" + roleKey : $"{teamName}:{roleKey}";
        }

        agentNames["Qa"] = string.IsNullOrEmpty(teamName) ? "AlveusQa" : $"{teamName}:Qa";
        agentNames["Technical"] = string.IsNullOrEmpty(teamName) ? "AlveusTechnical" : $"{teamName}:Technical";

        var serviceProvider = context.GetRequiredService<IServiceProvider>();
        var agents = new Dictionary<string, AIAgent>();
        var sessions = new Dictionary<string, AgentSession>();
        foreach (var role in roles)
        {
            var agent = serviceProvider.GetRequiredKeyedService<AIAgent>(agentNames[role]);
            agents[role] = agent;
            sessions[role] = await RestoreSessionAsync(context, agent, SessionStatePropertyPrefix + agentNames[role]);
        }

        var transcript = new List<string>();
        var lastSeenIndex = roles.ToDictionary(role => role, _ => 0);
        var topics = new Dictionary<string, MeetingTopicState>();
        foreach (var seeded in SeedOpenTopics())
        {
            topics[seeded] = new MeetingTopicState();
        }

        var finishSummaries = new Dictionary<string, string>();

        for (var round = 1; round <= MaxRounds; round++)
        {
            var confirmedDone = new HashSet<string>();
            var roundStartIndex = transcript.Count;

            foreach (var role in roles)
            {
                var message = BuildMessage(role, round, topicText, extraContext, transcript, lastSeenIndex, topics);
                lastSeenIndex[role] = transcript.Count;

                sessions[role] = await _compactionService.CompactIfNeededAsync(agents[role], sessions[role], context.CancellationToken);
                contextAccessor.AgentName = agentNames[role];
                var response = await agents[role].RunAsync(message, sessions[role], cancellationToken: context.CancellationToken);
                await PersistSessionAsync(context, agents[role], sessions[role], SessionStatePropertyPrefix + agentNames[role]);

                if (!string.IsNullOrWhiteSpace(response.Text))
                {
                    transcript.Add($"[{role}] {response.Text}");
                }

                foreach (var raise in FindRaiseCalls(response))
                {
                    topics.TryAdd(raise.Topic, new MeetingTopicState());
                    transcript.Add($"[{role}] (Raise '{raise.Topic}') {raise.Comment}");
                }

                foreach (var vote in FindVoteCalls(response))
                {
                    if (topics.TryGetValue(vote.Topic, out var state))
                    {
                        state.Votes[role] = (vote.Decision, vote.Comment);
                        transcript.Add($"[{role}] (Vote '{vote.Topic}') {vote.Decision}"
                            + (vote.Comment is null ? string.Empty : $" — {vote.Comment}"));
                    }
                }

                var finish = FindFinishCall(response);
                if (finish is not null)
                {
                    finishSummaries[role] = finish.Summary;
                    transcript.Add($"[{role}] (Finish outcome={finish.Outcome}) {finish.Summary}");

                    switch (finish.Outcome)
                    {
                        case AgentTaskOutcome.Done:
                            confirmedDone.Add(role);
                            await OnAgentFinishAsync(context, role, finish);
                            break;

                        case AgentTaskOutcome.NeedsMoreInfo:
                            context.Set(Reason, finish.Reason);
                            context.Set(Questions, finish.Questions);
                            PostOutcome(conversationStore, conversationId, $"NeedsMoreInfo : {finish.Reason}");
                            await context.CompleteActivityWithOutcomesAsync(["NeedsMoreInfo"]);
                            return;

                        case AgentTaskOutcome.Blocked:
                            context.Set(Reason, finish.Reason);
                            PostOutcome(conversationStore, conversationId, $"Bloqué : {finish.Reason}");
                            await context.CompleteActivityWithOutcomesAsync(["Blocked"]);
                            return;
                    }
                }
            }

            if (conversationStore is not null && transcript.Count > roundStartIndex)
            {
                conversationStore.AddItem(
                    conversationId!,
                    "assistant",
                    string.Join("\n", transcript.Skip(roundStartIndex)),
                    ConversationItemKind.MeetingRound,
                    new Dictionary<string, string> { ["meeting"] = GetType().Name, ["round"] = round.ToString() });
            }

            var unresolvedAfterCorrection = false;
            foreach (var state in topics.Values)
            {
                if (state.Resolved || state.Votes.Count < roles.Count)
                {
                    continue;
                }

                var decisions = state.Votes.Values.Select(v => v.Decision).ToList();
                var agreeCount = decisions.Count(d => d == MeetingVoteDecision.Agree);
                var disagreeCount = decisions.Count(d => d == MeetingVoteDecision.Disagree);

                if (agreeCount == 0 || disagreeCount == 0)
                {
                    state.Resolved = true;
                    state.FinalTally = new MeetingVoteTally(agreeCount, disagreeCount);
                }
                else if (state.InCorrectionRound)
                {
                    unresolvedAfterCorrection = true;
                }
                else
                {
                    state.InCorrectionRound = true;
                    state.Votes.Clear();
                }
            }

            if (unresolvedAfterCorrection)
            {
                await FinalizeAsync(context, MeetingOutcome.NeedsHelp, BuildTallies(topics), finishSummaries);
                PostOutcome(conversationStore, conversationId, "NeedsHelp : désaccord non résolu après correction");
                return;
            }

            var hasOpenTopics = topics.Values.Any(state => !state.Resolved);
            if (confirmedDone.Count == roles.Count && !hasOpenTopics)
            {
                await FinalizeAsync(context, MeetingOutcome.Done, BuildTallies(topics), finishSummaries);
                PostOutcome(conversationStore, conversationId, "Done");
                return;
            }
        }

        await FinalizeAsync(context, MeetingOutcome.NeedsHelp, BuildTallies(topics), finishSummaries);
        PostOutcome(conversationStore, conversationId, "NeedsHelp : nombre maximal de rounds atteint");
    }

    private string BuildMessage(
        string role,
        int round,
        string topicText,
        string extraContext,
        List<string> transcript,
        Dictionary<string, int> lastSeenIndex,
        Dictionary<string, MeetingTopicState> topics)
    {
        var sb = new StringBuilder();

        if (round == 1)
        {
            sb.Append(GetRoleTask(role));
            sb.Append("\n\n---\nSujet de la réunion :\n").Append(topicText);
            if (!string.IsNullOrWhiteSpace(extraContext))
            {
                sb.Append("\n\n---\nComptes-rendus de la réunion précédente (à prendre en compte) :\n").Append(extraContext);
            }
        }
        else
        {
            var newEvents = transcript.Skip(lastSeenIndex[role]).ToList();
            sb.Append("Voici les échanges depuis ton dernier tour :\n");
            sb.Append(newEvents.Count > 0 ? string.Join("\n", newEvents) : "(aucun nouvel échange)");

            var toRevisit = topics
                .Where(kv => kv.Value.InCorrectionRound && !kv.Value.Resolved && !kv.Value.Votes.ContainsKey(role))
                .Select(kv => kv.Key)
                .ToList();
            if (toRevisit.Count > 0)
            {
                sb.Append("\n\n---\nLes points suivants n'ont pas obtenu de consensus (2 contre 1) — reconsidère ta "
                    + "position et revote avec Vote : ").Append(string.Join(", ", toRevisit));
            }
        }

        sb.Append("\n\n---\nUtilise Raise(topic, comment) pour signaler un point de désaccord ou une question aux "
            + "autres participants, Vote(topic, decision, comment) pour voter sur un topic, et Finish(outcome='done') "
            + "quand tu n'as plus rien à ajouter à ce round.");

        return sb.ToString();
    }

    private void PostOutcome(IConversationStore? store, string? conversationId, string outcome)
    {
        if (store is null || string.IsNullOrEmpty(conversationId)) return;
        store.AddItem(conversationId, "assistant", $"{GetType().Name} → {outcome}",
            ConversationItemKind.ActivityTransition,
            new Dictionary<string, string> { ["activityId"] = GetType().Name, ["phase"] = "outcome" });
    }

    private static IReadOnlyDictionary<string, MeetingVoteTally> BuildTallies(Dictionary<string, MeetingTopicState> topics)
        => topics
            .Where(kv => kv.Value.Resolved && kv.Value.FinalTally is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.FinalTally!);

    private static FinishCall? FindFinishCall(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { Name: FinishTool.FunctionName } call)
                {
                    var finish = FinishCall.FromArguments(call.Arguments);
                    if (finish is not null)
                    {
                        return finish;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<RaiseCall> FindRaiseCalls(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { Name: MeetingTool.RaiseFunctionName } call)
                {
                    var raise = RaiseCall.FromArguments(call.Arguments);
                    if (raise is not null)
                    {
                        yield return raise;
                    }
                }
            }
        }
    }

    private static IEnumerable<VoteCall> FindVoteCalls(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { Name: MeetingTool.VoteFunctionName } call)
                {
                    var vote = VoteCall.FromArguments(call.Arguments);
                    if (vote is not null)
                    {
                        yield return vote;
                    }
                }
            }
        }
    }

    private static async Task<AgentSession> RestoreSessionAsync(ActivityExecutionContext context, AIAgent agent, string sessionStatePropertyName)
    {
        var serializedSession = context.GetProperty<string?>(sessionStatePropertyName);
        if (string.IsNullOrEmpty(serializedSession))
        {
            return await agent.CreateSessionAsync(context.CancellationToken);
        }

        using var document = JsonDocument.Parse(serializedSession);
        return await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: context.CancellationToken);
    }

    private static async Task PersistSessionAsync(ActivityExecutionContext context, AIAgent agent, AgentSession session, string sessionStatePropertyName)
    {
        var serializedSession = await agent.SerializeSessionAsync(session, cancellationToken: context.CancellationToken);
        context.SetProperty(sessionStatePropertyName, serializedSession.GetRawText());
    }

    /// <summary>État de débat d'un topic (ouvert via <c>Raise</c> ou seedé par <see cref="SeedOpenTopics"/>).</summary>
    private sealed class MeetingTopicState
    {
        public Dictionary<string, (MeetingVoteDecision Decision, string? Comment)> Votes { get; } = new();

        public bool InCorrectionRound { get; set; }

        public bool Resolved { get; set; }

        public MeetingVoteTally? FinalTally { get; set; }
    }
}
