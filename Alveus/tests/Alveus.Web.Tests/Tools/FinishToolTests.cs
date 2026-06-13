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
}
