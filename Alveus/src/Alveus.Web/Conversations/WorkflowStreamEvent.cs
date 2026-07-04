namespace Alveus.Web.Conversations;

/// <summary>
/// Union discriminée des événements diffusés dans le flux d'observabilité d'une conversation.
/// Combine les items haute-niveau (<see cref="ConversationItemStreamEvent"/>), les échanges LLM
/// bruts (<see cref="LlmExchangeStreamEvent"/>), les signaux de suspension
/// (<see cref="WorkflowSuspendedStreamEvent"/>) et de complétion
/// (<see cref="WorkflowCompletedStreamEvent"/>).
/// </summary>
public abstract record WorkflowStreamEvent(string ConversationId);
