using Alveus.Web.Conversations;

namespace Alveus.Web.Tests.Conversations;

/// <summary>
/// Test de <see cref="ConversationContextAccessor"/> (cf. ADR 0027) : vérifie la propagation via
/// <see cref="AsyncLocal{T}"/> à travers des <c>await</c>, et son absence de fuite entre des flux
/// <see cref="Task.Run"/> indépendants — d'où la limitation documentée "une conversation à la fois
/// par chaîne d'exécution".
/// </summary>
public sealed class ConversationContextAccessorTests
{
    [Fact]
    public async Task ConversationId_PersistsAcrossAwaits()
    {
        var accessor = new ConversationContextAccessor();
        accessor.ConversationId = "conv-1";

        await Task.Delay(1);

        Assert.Equal("conv-1", accessor.ConversationId);
    }

    [Fact]
    public async Task ConversationId_DoesNotLeakBetweenIndependentTaskRuns()
    {
        var accessor = new ConversationContextAccessor();
        accessor.ConversationId = "outer";

        string? innerValue = null;
        await Task.Run(() =>
        {
            innerValue = accessor.ConversationId;
            accessor.ConversationId = "inner";
        });

        Assert.Equal("outer", innerValue);
        Assert.Equal("outer", accessor.ConversationId);
    }
}
