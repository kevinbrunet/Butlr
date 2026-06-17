namespace Alveus.Web.Conversations;

/// <summary>
/// Sous-ensemble du format OpenAI Conversations utilisé par <see cref="ConversationEndpoints"/> (cf.
/// ADR 0027) — API self-hosted, aucun appel à api.openai.com.
/// </summary>
public sealed record ConversationContentPart(string Type, string Text);
