namespace Alveus.Web.Conversations;

/// <inheritdoc cref="IConversationContextAccessor"/>
public sealed class ConversationContextAccessor : IConversationContextAccessor
{
    private static readonly AsyncLocal<string?> CurrentConversationId = new();
    private static readonly AsyncLocal<string?> CurrentAgentName = new();

    public string? ConversationId
    {
        get => CurrentConversationId.Value;
        set => CurrentConversationId.Value = value;
    }

    public string? AgentName
    {
        get => CurrentAgentName.Value;
        set => CurrentAgentName.Value = value;
    }
}
