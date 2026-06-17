using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

public sealed class StrReplaceEditorToolTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly StrReplaceEditorTool _tool;

    public StrReplaceEditorToolTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("alveus-editor-").FullName;
        _tool = new StrReplaceEditorTool(_workspaceRoot);
    }

    public void Dispose() => Directory.Delete(_workspaceRoot, recursive: true);

    private string FullPath(string relativePath) => Path.Combine(_workspaceRoot, relativePath);

    [Fact]
    public void Execute_Create_WritesFileContent()
    {
        _tool.Execute("create", "greeting.txt", file_text: "bonjour");

        Assert.Equal("bonjour", File.ReadAllText(FullPath("greeting.txt")));
    }

    [Fact]
    public void Execute_Create_OnExistingFile_ReturnsError()
    {
        _tool.Execute("create", "greeting.txt", file_text: "bonjour");

        var result = _tool.Execute("create", "greeting.txt", file_text: "autre");

        Assert.StartsWith("Erreur", result);
        Assert.Contains("existe déjà", result);
    }

    [Fact]
    public void Execute_ViewFile_ReturnsCatNFormat()
    {
        _tool.Execute("create", "file.txt", file_text: "ligne1\nligne2");

        var result = _tool.Execute("view", "file.txt");

        Assert.Contains("     1\tligne1", result);
        Assert.Contains("     2\tligne2", result);
    }

    [Fact]
    public void Execute_ViewFile_OnMissingFile_ReturnsError()
    {
        var result = _tool.Execute("view", "absent.txt");

        Assert.StartsWith("Erreur", result);
        Assert.Contains("introuvable", result);
    }

    [Fact]
    public void Execute_ViewDirectory_ReturnsErrorWithRedirectToCmdRunTool()
    {
        Directory.CreateDirectory(FullPath("sub"));

        var result = _tool.Execute("view", "sub");

        Assert.StartsWith("Erreur", result);
        Assert.Contains("répertoire", result);
        Assert.Contains("ls", result);
    }

    [Fact]
    public void Execute_StrReplace_ReplacesUniqueOccurrence()
    {
        _tool.Execute("create", "file.txt", file_text: "hello world");

        _tool.Execute("str_replace", "file.txt", old_str: "world", new_str: "Butlr");

        Assert.Equal("hello Butlr", File.ReadAllText(FullPath("file.txt")));
    }

    [Fact]
    public void Execute_StrReplace_OldStrNotFound_ReturnsError()
    {
        _tool.Execute("create", "file.txt", file_text: "hello world");

        var result = _tool.Execute("str_replace", "file.txt", old_str: "missing", new_str: "x");

        Assert.StartsWith("Erreur", result);
        Assert.Contains("introuvable", result);
    }

    [Fact]
    public void Execute_StrReplace_OldStrNotUnique_ReturnsError()
    {
        _tool.Execute("create", "file.txt", file_text: "a a");

        var result = _tool.Execute("str_replace", "file.txt", old_str: "a", new_str: "b");

        Assert.StartsWith("Erreur", result);
    }

    [Fact]
    public void Execute_Insert_InsertsAfterGivenLine()
    {
        _tool.Execute("create", "file.txt", file_text: "ligne1\nligne3");

        _tool.Execute("insert", "file.txt", new_str: "ligne2", insert_line: 1);

        Assert.Equal(["ligne1", "ligne2", "ligne3"], File.ReadAllLines(FullPath("file.txt")));
    }

    [Fact]
    public void Execute_Insert_AtBeginning_InsertsAtLineZero()
    {
        _tool.Execute("create", "file.txt", file_text: "ligne2");

        _tool.Execute("insert", "file.txt", new_str: "ligne1", insert_line: 0);

        Assert.Equal(["ligne1", "ligne2"], File.ReadAllLines(FullPath("file.txt")));
    }

    [Fact]
    public void Execute_Insert_LineOutOfRange_ReturnsError()
    {
        _tool.Execute("create", "file.txt", file_text: "ligne1");

        var result = _tool.Execute("insert", "file.txt", new_str: "x", insert_line: 99);

        Assert.StartsWith("Erreur", result);
    }

    [Fact]
    public void Execute_UndoEdit_RevertsStrReplace()
    {
        _tool.Execute("create", "file.txt", file_text: "hello world");
        _tool.Execute("str_replace", "file.txt", old_str: "world", new_str: "Butlr");

        _tool.Execute("undo_edit", "file.txt");

        Assert.Equal("hello world", File.ReadAllText(FullPath("file.txt")));
    }

    [Fact]
    public void Execute_UndoEdit_RevertsInsert()
    {
        _tool.Execute("create", "file.txt", file_text: "ligne1");
        _tool.Execute("insert", "file.txt", new_str: "ligne2", insert_line: 1);

        _tool.Execute("undo_edit", "file.txt");

        Assert.Equal("ligne1", File.ReadAllText(FullPath("file.txt")));
    }

    [Fact]
    public void Execute_UndoEdit_RevertsCreate_DeletesFile()
    {
        _tool.Execute("create", "file.txt", file_text: "hello");

        _tool.Execute("undo_edit", "file.txt");

        Assert.False(File.Exists(FullPath("file.txt")));
    }

    [Fact]
    public void Execute_UndoEdit_WithoutHistory_ReturnsError()
    {
        _tool.Execute("create", "file.txt", file_text: "hello");

        var result = _tool.Execute("undo_edit", "other.txt");

        Assert.StartsWith("Erreur", result);
        Assert.Contains("annuler", result);
    }

    [Fact]
    public void Execute_PathOutsideWorkspace_ReturnsError()
    {
        var result = _tool.Execute("view", "../outside.txt");

        Assert.StartsWith("Erreur", result);
        Assert.Contains("workspace", result);
    }

    [Fact]
    public void Execute_UnknownCommand_ReturnsErrorWithExpectedCommands()
    {
        var result = _tool.Execute("frobnicate", "file.txt");

        Assert.StartsWith("Erreur", result);
        Assert.Contains("view", result);
    }
}
