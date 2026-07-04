using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Config.State;

public sealed class SnapshotStore
{
    private readonly string _path;
    private readonly ILogger<SnapshotStore> _logger;
    private readonly Lock _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SnapshotStore(string path, ILogger<SnapshotStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Append(SnapshotEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (_lock)
            File.AppendAllText(_path, line + Environment.NewLine);
    }

    public IReadOnlyList<SnapshotEntry> ReadAll()
    {
        if (!File.Exists(_path)) return [];

        var entries = new List<SnapshotEntry>();
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<SnapshotEntry>(line, JsonOptions);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Ligne JSONL ignorée (parse error)");
            }
        }
        return entries;
    }

    // Compaction : calcule l'état net et réécrit le fichier
    public void Compact()
    {
        var entries = ReadAll();
        var net = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.EventType is "released" or "expired")
                net.Remove(entry.VDeviceId);
            else
                net[entry.VDeviceId] = entry;
        }

        var tmpPath = _path + ".new";
        var lines = net.Values.Select(e => JsonSerializer.Serialize(e, JsonOptions));
        File.WriteAllLines(tmpPath, lines);
        File.Move(tmpPath, _path, overwrite: true);
        _logger.LogInformation("Snapshot compacté : {Count} VDevices actifs", net.Count);
    }
}

public sealed record SnapshotEntry(
    string VDeviceId,
    string DeviceId,
    string TierId,
    string ActorKind,
    int Priority,
    string EventType,   // created | renewed | updated | released | expired | preempted
    DateTimeOffset At,
    string? AppId = null,
    string? ActorUserId = null,
    string? ViaAgentId = null);
