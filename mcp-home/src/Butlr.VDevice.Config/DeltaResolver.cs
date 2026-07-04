using Microsoft.Extensions.Logging;
using Butlr.VDevice.Config.Models;

namespace Butlr.VDevice.Config;

public sealed class DeltaResolver
{
    private readonly ConfigRepository _repo;
    private readonly VDeviceYamlSerializer _yaml;
    private readonly ILogger<DeltaResolver> _logger;

    public DeltaResolver(ConfigRepository repo, VDeviceYamlSerializer yaml, ILogger<DeltaResolver> logger)
    {
        _repo = repo;
        _yaml = yaml;
        _logger = logger;
    }

    public HomeConfig LoadHomeConfig()
    {
        var path = Path.Combine(_repo.RepoPath, "home.yaml");
        if (!File.Exists(path))
            throw new FileNotFoundException("home.yaml introuvable", path);
        return _yaml.DeserializeFile<HomeConfig>(path);
    }

    // Charge la config effective d'un device en empilant home → étage → pièce → device
    public DeviceConfig ResolveDevice(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var parts = relativePath.Split('/', '\\');
        var effectiveConfig = new DeviceConfig();

        // Accumulation des deltas de haut en bas
        for (int depth = 1; depth <= parts.Length; depth++)
        {
            var dirPath = Path.Combine([_repo.RepoPath, .. parts[..Math.Max(0, depth - 1)]]);
            string fileName = depth < parts.Length
                ? (depth == 1 ? "etage.yaml" : "piece.yaml")
                : parts[^1] + ".yaml";

            var filePath = Path.Combine(dirPath, fileName);
            if (!File.Exists(filePath)) continue;

            var delta = _yaml.DeserializeFile<DeviceConfig>(filePath);
            ApplyDelta(effectiveConfig, delta);
        }

        return effectiveConfig;
    }

    private void ApplyDelta(DeviceConfig target, DeviceConfig delta)
    {
        if (delta.FriendlyName is not null) target.FriendlyName = delta.FriendlyName;
        if (delta.ExternalId is not null) target.ExternalId = delta.ExternalId;
        if (delta.ClustersSupported.Count > 0) target.ClustersSupported = delta.ClustersSupported;
        if (delta.Fallback is not null) target.Fallback = delta.Fallback;
        if (delta.TierOverrides.Count > 0) target.TierOverrides = delta.TierOverrides;
    }
}
