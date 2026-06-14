using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

public sealed class FinishToolTests
{
    private readonly FinishTool _tool = new();

    [Theory]
    [InlineData("done")]
    [InlineData("needsmoreinfo")]
    [InlineData("blocked")]
    [InlineData("Done")]
    [InlineData("BLOCKED")]
    public void Finish_ValidOutcome_ReturnsConfirmation(string outcome)
    {
        var result = _tool.Finish("résumé de la tâche", outcome);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Finish_UnknownOutcome_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Finish("résumé", "frobnicate"));
    }

    [Fact]
    public void Finish_EmptySummary_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Finish("", "done"));
    }

    [Fact]
    public void Finish_NeedsMoreInfoWithReasonAndQuestions_Succeeds()
    {
        var result = _tool.Finish(
            "Impossible de continuer sans précision.",
            "needsmoreinfo",
            reason: "Le nom du fichier cible n'est pas précisé.",
            questions: ["Quel est le nom du fichier ?", "Dans quel répertoire ?"]);

        Assert.NotEmpty(result);
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("needmoreinfo")]
    [InlineData("Pass")]
    [InlineData("NEEDMOREINFO")]
    public void Finish_ValidVerdict_ReturnsConfirmation(string verdict)
    {
        var result = _tool.Finish("résumé", "done", verdict: verdict);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Finish_UnknownVerdict_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Finish("résumé", "done", verdict: "frobnicate"));
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("evaluator")]
    [InlineData("userdoc")]
    [InlineData("Worker")]
    public void Finish_ValidDownstreamInstructionTarget_ReturnsConfirmation(string target)
    {
        var result = _tool.Finish(
            "résumé",
            "done",
            downstreamInstructions: [new DownstreamInstruction(target, "Précision complémentaire.")]);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Finish_UnknownDownstreamInstructionTarget_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Finish(
            "résumé",
            "done",
            downstreamInstructions: [new DownstreamInstruction("frobnicate", "Précision complémentaire.")]));
    }
}
