namespace Butlr.VDevice.Core;

public sealed record Admission(
    IReadOnlySet<string> TagsRequired,
    IReadOnlySet<string> TagsForbidden)
{
    public static readonly Admission Any = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool Matches(IReadOnlySet<string> tags)
    {
        foreach (var required in TagsRequired)
            if (!tags.Contains(required)) return false;
        foreach (var forbidden in TagsForbidden)
            if (tags.Contains(forbidden)) return false;
        return true;
    }
}
