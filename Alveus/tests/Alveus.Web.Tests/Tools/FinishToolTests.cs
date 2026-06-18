using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

public sealed class FinishToolTests
{
    private readonly FinishTool _tool = new();

    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("needmoreinfo")]
    [InlineData("blocked")]
    [InlineData("Pass")]
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
        Assert.Throws<ArgumentException>(() => _tool.Finish("", "pass"));
    }

    [Fact]
    public void Finish_NeedMoreInfoWithReasonAndQuestions_Succeeds()
    {
        var result = _tool.Finish(
            "Impossible de continuer sans précision.",
            "needmoreinfo",
            reason: "Le nom du fichier cible n'est pas précisé.",
            questions: ["Quel est le nom du fichier ?", "Dans quel répertoire ?"]);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Finish_FailWithReason_Succeeds()
    {
        var result = _tool.Finish(
            "L'environnement ne démarre pas.",
            "fail",
            reason: "Le port 8080 est déjà utilisé.");

        Assert.NotEmpty(result);
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
            "pass",
            downstreamInstructions: [new DownstreamInstruction(target, "Précision complémentaire.")]);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Finish_UnknownDownstreamInstructionTarget_Throws()
    {
        Assert.Throws<ArgumentException>(() => _tool.Finish(
            "résumé",
            "pass",
            downstreamInstructions: [new DownstreamInstruction("frobnicate", "Précision complémentaire.")]));
    }
}
