using System.Collections.Concurrent;
using System.Text;
using Alveus.Web.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Alveus.Web.Logging;

/// <summary>
/// Implémentation de <see cref="ITaskLogger"/> qui écrit un fichier markdown par activité dans
/// <c>TaskLogger:LogRoot/{conversationId}/</c>. Chaque changement d'activité (item
/// <see cref="ConversationItemKind.ActivityTransition"/> phase "starting") ouvre un nouveau
/// fichier numéroté <c>{n:D2}-{activityId}.md</c>. Thread-safe par conversation (lock par état).
/// </summary>
public sealed class FileTaskLogger : ITaskLogger
{
    private sealed class ConvState
    {
        public required string LogDir { get; init; }
        public int ActivityCounter { get; set; }
        public StreamWriter? Writer { get; set; }
        public DateTimeOffset? ActivityStarted { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConvState> _states = new();
    private readonly string _logRoot;
    private readonly ILogger<FileTaskLogger> _logger;

    public FileTaskLogger(IConfiguration config, ILogger<FileTaskLogger> logger)
    {
        _logRoot = config["TaskLogger:LogRoot"] ?? Path.Combine(Path.GetTempPath(), "alveus-logs");
        _logger = logger;
        Directory.CreateDirectory(_logRoot);
    }

    public void OnItem(ConversationItem item)
    {
        try
        {
            var state = _states.GetOrAdd(item.ConversationId, id =>
            {
                var dir = Path.Combine(_logRoot, id);
                Directory.CreateDirectory(dir);
                return new ConvState { LogDir = dir };
            });

            lock (state)
            {
                if (item.Kind == ConversationItemKind.ActivityTransition
                    && item.Metadata.TryGetValue("phase", out var phase)
                    && item.Metadata.TryGetValue("activityId", out var activityId))
                {
                    if (phase == "starting")
                    {
                        CloseCurrentFile(state, null);
                        OpenNewFile(state, activityId, item.CreatedAt);
                    }
                    else
                    {
                        // Completed / Faulted / Canceled — on écrit le pied de page dans le fichier courant
                        var duration = state.ActivityStarted.HasValue
                            ? item.CreatedAt - state.ActivityStarted.Value
                            : TimeSpan.Zero;
                        WriteFooter(state.Writer, phase, duration);
                    }
                }
                else
                {
                    AppendItem(state.Writer, item);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileTaskLogger: erreur item {Kind} conv {ConvId}", item.Kind, item.ConversationId);
        }
    }

    public void OnLlmExchange(string conversationId, IEnumerable<ChatMessage> messages, ChatResponse response)
    {
        try
        {
            if (!_states.TryGetValue(conversationId, out var state)) return;
            lock (state)
            {
                if (state.Writer is null) return;
                var w = state.Writer;

                // Dernier message entrant (le prompt courant de l'agent)
                var lastUserMsg = messages.LastOrDefault(m => m.Role == ChatRole.User || m.Role == ChatRole.Tool);
                if (lastUserMsg is not null)
                {
                    var text = string.Concat(lastUserMsg.Contents.OfType<TextContent>().Select(c => c.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        w.WriteLine();
                        w.WriteLine($"**→ [{lastUserMsg.Role}]**");
                        w.WriteLine(text);
                    }
                }

                // Réponse LLM (thinking + texte + tool calls)
                // On déduplique les FunctionCallContent identiques consécutifs (ex. Finish en boucle).
                string? lastFcKey = null;
                int lastFcCount = 0;
                foreach (var msg in response.Messages)
                {
                    foreach (var content in msg.Contents)
                    {
                        switch (content)
                        {
                            case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                                FlushLastFc(w, ref lastFcKey, ref lastFcCount);
                                w.WriteLine();
                                w.WriteLine("**← [assistant]**");
                                w.WriteLine(tc.Text);
                                break;
                            case FunctionCallContent fc:
                                var args = fc.Arguments is not null
                                    ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                                    : string.Empty;
                                // Tronqué à 100 chars : les summaries de Finish varient en fin de texte
                                // mais sont identiques au début ; les commandes Run diffèrent dès le début.
                                var argsKey = args.Length > 100 ? args[..100] : args;
                                var fcKey = $"{fc.Name}|{argsKey}";
                                if (fcKey == lastFcKey)
                                {
                                    lastFcCount++;
                                }
                                else
                                {
                                    FlushLastFc(w, ref lastFcKey, ref lastFcCount);
                                    lastFcKey = fcKey;
                                    lastFcCount = 1;
                                    w.WriteLine();
                                    w.WriteLine($"**← [tool_call]** `{fc.Name}`");
                                    if (!string.IsNullOrEmpty(args))
                                        w.WriteLine($"> {args}");
                                }
                                break;
                        }
                    }
                }
                FlushLastFc(w, ref lastFcKey, ref lastFcCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileTaskLogger: erreur OnLlmExchange conv {ConvId}", conversationId);
        }
    }

    public void OnCompleted(string conversationId, string status)
    {
        if (!_states.TryGetValue(conversationId, out var state)) return;
        lock (state)
        {
            CloseCurrentFile(state, status);
        }
    }

    private static void FlushLastFc(StreamWriter w, ref string? lastFcKey, ref int lastFcCount)
    {
        if (lastFcKey is not null && lastFcCount > 1)
        {
            w.WriteLine($"> *(×{lastFcCount} appels identiques dédupliqués)*");
        }
        lastFcKey = null;
        lastFcCount = 0;
    }

    private static void OpenNewFile(ConvState state, string activityId, DateTimeOffset startedAt)
    {
        state.ActivityCounter++;
        var path = Path.Combine(state.LogDir, $"{state.ActivityCounter:D2}-{activityId}.md");
        state.Writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        state.ActivityStarted = startedAt;
        state.Writer.WriteLine($"# {activityId}");
        state.Writer.WriteLine($"*Démarré : {startedAt:yyyy-MM-dd HH:mm:ss} UTC*");
        state.Writer.WriteLine();
    }

    private static void CloseCurrentFile(ConvState state, string? reason)
    {
        if (state.Writer is null) return;
        if (reason is not null)
        {
            state.Writer.WriteLine();
            state.Writer.WriteLine($"---");
            state.Writer.WriteLine($"*{reason}*");
        }

        state.Writer.Flush();
        state.Writer.Dispose();
        state.Writer = null;
        state.ActivityStarted = null;
    }

    private static void WriteFooter(StreamWriter? writer, string phase, TimeSpan duration)
    {
        if (writer is null) return;
        var mins = (int)duration.TotalMinutes;
        var secs = duration.Seconds;
        writer.WriteLine();
        writer.WriteLine($"---");
        writer.WriteLine($"*{phase} — {mins}m {secs:D2}s*");
    }

    private static void AppendItem(StreamWriter? writer, ConversationItem item)
    {
        if (writer is null) return;

        writer.WriteLine();

        switch (item.Kind)
        {
            case ConversationItemKind.UserMessage:
            case ConversationItemKind.HumanReply:
                writer.WriteLine($"**[{item.Role}]**");
                writer.WriteLine(item.Text);
                break;

            case ConversationItemKind.MeetingRound:
                var round = item.Metadata.GetValueOrDefault("round", "?");
                writer.WriteLine($"### Round {round}");
                writer.WriteLine();
                writer.WriteLine(item.Text);
                break;

            case ConversationItemKind.FileEdit:
                var agent = item.Metadata.GetValueOrDefault("agent", item.Role);
                var cmd = item.Metadata.GetValueOrDefault("command", "?");
                var path = item.Metadata.GetValueOrDefault("path", "?");
                writer.WriteLine($"**[{agent}]** `{cmd}` → `{path}`");
                writer.WriteLine();
                writer.Write("> ");
                writer.WriteLine(item.Text.Replace("\n", "\n> "));
                break;

            case ConversationItemKind.NeedsHelpQuestion:
                writer.WriteLine($"**[NeedsHelp]**");
                writer.WriteLine(item.Text);
                break;

            case ConversationItemKind.ExpertQuestion:
            case ConversationItemKind.ExpertAnswer:
                var expert = item.Metadata.GetValueOrDefault("expert", "?");
                writer.WriteLine($"**[{item.Kind} — {expert}]**");
                writer.WriteLine(item.Text);
                break;

            default:
                writer.WriteLine($"**[{item.Kind}]**");
                writer.WriteLine(item.Text);
                break;
        }
    }
}
