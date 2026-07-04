using System.Text.Json;

namespace Alveus.Web.Tools;

/// <summary>Helpers de parsing partagés par <see cref="RaiseCall"/> et <see cref="VoteCall"/>.</summary>
internal static class MeetingCallArguments
{
    public static string? ReadString(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => value.ToString(),
        };
    }
}
