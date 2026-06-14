namespace Alveus.Web.Conversations;

/// <inheritdoc cref="IConversationContextAccessor"/>
public sealed class ConversationContextAccessor : IConversationContextAccessor
{
    private static readonly AsyncLocal<string?> CurrentConversationId = new();

    public string? ConversationId
    {
        get => CurrentConversationId.Value;
        set => CurrentConversationId.Value = value;
    }
}
