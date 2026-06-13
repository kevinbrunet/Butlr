using System.Text.Json;
using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

public sealed class FinishCallTests
{
    [Fact]
    public void FromArguments_Done_ParsesSummaryAndOutcome()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = "Fichier créé.",
            ["outcome"] = "done",
        };

        var finish = FinishCall.FromArguments(arguments);

        Assert.NotNull(finish);
        Assert.Equal(AgentTaskOutcome.Done, finish!.Outcome);
        Assert.Equal("Fichier créé.", finish.Summary);
        Assert.Null(finish.Reason);
        Assert.Null(finish.Questions);
    }

    [Fact]
    public void FromArguments_NeedsMoreInfo_ParsesReasonAndQuestions()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = "Bloqué avant de continuer.",
            ["outcome"] = "needsmoreinfo",
            ["reason"] = "Le nom du fichier cible n'est pas précisé.",
            ["questions"] = new[] { "Quel est le nom du fichier ?", "Quel répertoire ?" },
        };

        var finish = FinishCall.FromArguments(arguments);

        Assert.NotNull(finish);
        Assert.Equal(AgentTaskOutcome.NeedsMoreInfo, finish!.Outcome);
        Assert.Equal("Le nom du fichier cible n'est pas précisé.", finish.Reason);
        Assert.Equal(["Quel est le nom du fichier ?", "Quel répertoire ?"], finish.Questions);
    }

    [Fact]
    public void FromArguments_Blocked_ParsesReason()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = "Impossible de continuer.",
            ["outcome"] = "blocked",
            ["reason"] = "Le service externe requis n'est pas accessible.",
        };

        var finish = FinishCall.FromArguments(arguments);

        Assert.NotNull(finish);
        Assert.Equal(AgentTaskOutcome.Blocked, finish!.Outcome);
        Assert.Equal("Le service externe requis n'est pas accessible.", finish.Reason);
    }

    [Fact]
    public void FromArguments_ArgumentsAsJsonElements_AreParsed()
    {
        using var document = JsonDocument.Parse(
            """{"summary":"s","outcome":"needsmoreinfo","reason":"r","questions":["a","b"]}""");

        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = document.RootElement.GetProperty("summary"),
            ["outcome"] = document.RootElement.GetProperty("outcome"),
            ["reason"] = document.RootElement.GetProperty("reason"),
            ["questions"] = document.RootElement.GetProperty("questions"),
        };

        var finish = FinishCall.FromArguments(arguments);

        Assert.NotNull(finish);
        Assert.Equal(AgentTaskOutcome.NeedsMoreInfo, finish!.Outcome);
        Assert.Equal("s", finish.Summary);
        Assert.Equal("r", finish.Reason);
        Assert.Equal(["a", "b"], finish.Questions);
    }

    [Fact]
    public void FromArguments_UnknownOutcome_ReturnsNull()
    {
        var arguments = new Dictionary<string, object?> { ["summary"] = "s", ["outcome"] = "frobnicate" };

        Assert.Null(FinishCall.FromArguments(arguments));
    }

    [Fact]
    public void FromArguments_MissingOutcome_ReturnsNull()
    {
        var arguments = new Dictionary<string, object?> { ["summary"] = "s" };

        Assert.Null(FinishCall.FromArguments(arguments));
    }

    [Fact]
    public void FromArguments_NullArguments_ReturnsNull()
    {
        Assert.Null(FinishCall.FromArguments(null));
    }

    [Theory]
    [InlineData("pass", AgentVerdict.Pass)]
    [InlineData("fail", AgentVerdict.Fail)]
    [InlineData("needmoreinfo", AgentVerdict.NeedMoreInfo)]
    [InlineData("NeedMoreInfo", AgentVerdict.NeedMoreInfo)]
    public void FromArguments_KnownVerdict_ParsesVerdict(string verdictRaw, AgentVerdict expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = "Environnement démarré.",
            ["outcome"] = "done",
            ["verdict"] = verdictRaw,
        };

        var finish = FinishCall.FromArguments(arguments);

        Assert.NotNull(finish);
        Assert.Equal(expected, finish!.Verdict);
    }

    [Fact]
    public void FromArguments_MissingVerdict_VerdictIsNull()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = "Fichier créé.",
            ["outcome"] = "done",
        };

        var finish = FinishCall.FromArguments(arguments);

        Assert.NotNull(finish);
        Assert.Null(finish!.Verdict);
    }

    [Fact]
    public void FromArguments_UnknownVerdict_VerdictIsNull()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["summary"] = "Fichier créé.",
            ["outcome"] = "done",
            ["verdict"] = "frobnicate",
        };

        var finish = FinishCall.FromArguments(arguments);

        Assert.NotNull(finish);
        Assert.Null(finish!.Verdict);
    }
}
