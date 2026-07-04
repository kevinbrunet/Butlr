using System.Text.Json;
using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

public sealed class MeetingCallTests
{
    [Fact]
    public void RaiseCall_FromArguments_ParsesTopicAndComment()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["topic"] = "format-date-facture",
            ["comment"] = "Le ticket ne précise pas le format attendu.",
        };

        var raise = RaiseCall.FromArguments(arguments);

        Assert.NotNull(raise);
        Assert.Equal("format-date-facture", raise!.Topic);
        Assert.Equal("Le ticket ne précise pas le format attendu.", raise.Comment);
    }

    [Fact]
    public void RaiseCall_FromArguments_MissingComment_ReturnsNull()
    {
        var arguments = new Dictionary<string, object?> { ["topic"] = "format-date-facture" };

        Assert.Null(RaiseCall.FromArguments(arguments));
    }

    [Fact]
    public void RaiseCall_FromArguments_NullArguments_ReturnsNull()
    {
        Assert.Null(RaiseCall.FromArguments(null));
    }

    [Fact]
    public void RaiseCall_FromArguments_ArgumentsAsJsonElements_AreParsed()
    {
        using var document = JsonDocument.Parse("""{"topic":"t","comment":"c"}""");

        var arguments = new Dictionary<string, object?>
        {
            ["topic"] = document.RootElement.GetProperty("topic"),
            ["comment"] = document.RootElement.GetProperty("comment"),
        };

        var raise = RaiseCall.FromArguments(arguments);

        Assert.NotNull(raise);
        Assert.Equal("t", raise!.Topic);
        Assert.Equal("c", raise.Comment);
    }

    [Theory]
    [InlineData("agree", MeetingVoteDecision.Agree)]
    [InlineData("disagree", MeetingVoteDecision.Disagree)]
    [InlineData("Agree", MeetingVoteDecision.Agree)]
    public void VoteCall_FromArguments_ParsesDecision(string decisionRaw, MeetingVoteDecision expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["topic"] = "task-fulfilled",
            ["decision"] = decisionRaw,
        };

        var vote = VoteCall.FromArguments(arguments);

        Assert.NotNull(vote);
        Assert.Equal("task-fulfilled", vote!.Topic);
        Assert.Equal(expected, vote.Decision);
        Assert.Null(vote.Comment);
    }

    [Fact]
    public void VoteCall_FromArguments_Disagree_ParsesComment()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["topic"] = "task-fulfilled",
            ["decision"] = "disagree",
            ["comment"] = "Le plan de test ne couvre pas le cas limite X.",
        };

        var vote = VoteCall.FromArguments(arguments);

        Assert.NotNull(vote);
        Assert.Equal(MeetingVoteDecision.Disagree, vote!.Decision);
        Assert.Equal("Le plan de test ne couvre pas le cas limite X.", vote.Comment);
    }

    [Fact]
    public void VoteCall_FromArguments_UnknownDecision_ReturnsNull()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["topic"] = "task-fulfilled",
            ["decision"] = "frobnicate",
        };

        Assert.Null(VoteCall.FromArguments(arguments));
    }

    [Fact]
    public void VoteCall_FromArguments_MissingTopic_ReturnsNull()
    {
        var arguments = new Dictionary<string, object?> { ["decision"] = "agree" };

        Assert.Null(VoteCall.FromArguments(arguments));
    }

    [Fact]
    public void VoteCall_FromArguments_NullArguments_ReturnsNull()
    {
        Assert.Null(VoteCall.FromArguments(null));
    }
}
