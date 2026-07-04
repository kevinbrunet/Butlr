using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

public sealed class MeetingToolTests
{
    private readonly MeetingTool _tool = new();

    [Fact]
    public void Raise_ValidTopicAndComment_ReturnsConfirmation()
    {
        var result = _tool.Raise("format-date-facture", "Le ticket ne précise pas le format attendu.");

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Raise_EmptyTopic_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Raise("", "commentaire"));
    }

    [Fact]
    public void Raise_EmptyComment_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Raise("topic", ""));
    }

    [Theory]
    [InlineData("agree")]
    [InlineData("Agree")]
    public void Vote_Agree_ReturnsConfirmation(string decision)
    {
        var result = _tool.Vote("task-fulfilled", decision);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Vote_DisagreeWithComment_ReturnsConfirmation()
    {
        var result = _tool.Vote("task-fulfilled", "disagree", "Le plan de test ne couvre pas le cas limite X.");

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Vote_DisagreeWithoutComment_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Vote("task-fulfilled", "disagree"));
    }

    [Fact]
    public void Vote_UnknownDecision_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Vote("task-fulfilled", "frobnicate"));
    }

    [Fact]
    public void Vote_EmptyTopic_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Vote("", "agree"));
    }
}
