using Alveus.Web.Conversations;
using Alveus.Web.Tools;

namespace Alveus.Web.Tests.Tools;

/// <summary>
/// Test du wrapper <see cref="ConversationAwareStrReplaceEditorTool"/> (cf. ADR 0027) : sans LLM.
/// </summary>
public sealed class ConversationAwareStrReplaceEditorToolTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly IConversationStore _store;
    private readonly IConversationContextAccessor _contextAccessor;
    private readonly ConversationAwareStrReplaceEditorTool _tool;

    public ConversationAwareStrReplaceEditorToolTests()
    {
        _workspaceRoot = Directory.CreateTempSubdirectory("alveus-conv-editor-").FullName;
        _store = new ConversationStore();
        _contextAccessor = new ConversationContextAccessor();
        _tool = new ConversationAwareStrReplaceEditorTool(
            new StrReplaceEditorTool(_workspaceRoot),
            _contextAccessor,
            _store,
            "Alveus-Worker");
    }

    public void Dispose() => Directory.Delete(_workspaceRoot, recursive: true);

    [Fact]
    public void Execute_Create_WithActiveConversation_PostsFileEditItem()
    {
        var conversation = _store.Create();
        _contextAccessor.ConversationId = conversation.Id;

        _tool.texteditor("create", "greeting.txt", file_text: "bonjour");

        var items = _store.GetItems(conversation.Id);
        var item = Assert.Single(items);
        Assert.Equal(ConversationItemKind.FileEdit, item.Kind);
        Assert.Equal("Alveus-Worker", item.Metadata["agent"]);
        Assert.Equal("create", item.Metadata["command"]);
        Assert.Equal("greeting.txt", item.Metadata["path"]);
    }

    [Fact]
    public void Execute_StrReplace_WithActiveConversation_PostsFileEditItem()
    {
        var conversation = _store.Create();
        _contextAccessor.ConversationId = conversation.Id;

        _tool.texteditor("create", "greeting.txt", file_text: "bonjour");
        _tool.texteditor("str_replace", "greeting.txt", old_str: "bonjour", new_str: "salut");

        var items = _store.GetItems(conversation.Id);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(ConversationItemKind.FileEdit, i.Kind));
        Assert.Equal("str_replace", items[1].Metadata["command"]);
    }

    [Fact]
    public void Execute_View_DoesNotPostItem()
    {
        var conversation = _store.Create();
        _contextAccessor.ConversationId = conversation.Id;

        _tool.texteditor("create", "greeting.txt", file_text: "bonjour");
        _tool.texteditor("view", "greeting.txt");

        var items = _store.GetItems(conversation.Id);
        var item = Assert.Single(items);
        Assert.Equal("create", item.Metadata["command"]);
    }

    [Fact]
    public void Execute_WithoutActiveConversation_BehavesLikeInnerTool()
    {
        var result = _tool.texteditor("create", "greeting.txt", file_text: "bonjour");

        Assert.Equal("Fichier créé : '" + Path.Combine(_workspaceRoot, "greeting.txt") + "'.", result);
    }
}
