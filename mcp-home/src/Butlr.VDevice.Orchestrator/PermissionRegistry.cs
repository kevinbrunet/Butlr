using System.Collections.Concurrent;
using Butlr.VDevice.Config;
using Butlr.VDevice.Config.Models;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Orchestrator;

public sealed class PermissionRegistry
{
    private readonly ConfigRepository _repo;
    private readonly VDeviceYamlSerializer _yaml;
    private readonly ILogger<PermissionRegistry> _logger;
    private readonly ConcurrentDictionary<string, PermissionConfig> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PermissionRegistry(ConfigRepository repo, VDeviceYamlSerializer yaml, ILogger<PermissionRegistry> logger)
    {
        _repo = repo;
        _yaml = yaml;
        _logger = logger;
    }

    public void Load()
    {
        var dir = Path.Combine(_repo.RepoPath, "permissions");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.yaml"))
        {
            try
            {
                var perm = _yaml.DeserializeFile<PermissionConfig>(file);
                _cache[Key(perm.AppId, perm.DeviceId)] = perm;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Permission invalide : {File}", file);
            }
        }
        _logger.LogInformation("{Count} permissions chargées", _cache.Count);
    }

    public PermissionStatus Check(string appId, string deviceId)
    {
        var key = Key(appId, deviceId);
        if (!_cache.TryGetValue(key, out var perm)) return PermissionStatus.Missing;
        return perm.Status switch
        {
            "granted" => PermissionStatus.Granted,
            "revoked" => PermissionStatus.Revoked,
            _ => PermissionStatus.Pending,
        };
    }

    public PermissionConfig? Get(string appId, string deviceId)
    {
        _cache.TryGetValue(Key(appId, deviceId), out var perm);
        return perm;
    }

    public void RequestPermission(string appId, string deviceId, string tierMax, int priorityMax, IEnumerable<string> clusters)
    {
        var perm = new PermissionConfig
        {
            AppId = appId, DeviceId = deviceId,
            TierMax = tierMax, PriorityMax = priorityMax,
            ClustersAllowed = clusters.ToList(),
            Status = "pending"
        };
        Save(perm, $"permission: request {appId} sur {deviceId}");
    }

    public void Grant(string appId, string deviceId, string grantedBy)
    {
        if (!_cache.TryGetValue(Key(appId, deviceId), out var perm))
            throw new KeyNotFoundException($"Permission introuvable : {appId} / {deviceId}");

        perm.Status = "granted";
        perm.GrantedAt = DateTimeOffset.UtcNow;
        perm.GrantedBy = grantedBy;
        Save(perm, $"permission: grant {appId} sur {deviceId} par {grantedBy}");
    }

    public void Revoke(string appId, string deviceId)
    {
        if (!_cache.TryGetValue(Key(appId, deviceId), out var perm))
            throw new KeyNotFoundException($"Permission introuvable : {appId} / {deviceId}");

        perm.Status = "revoked";
        Save(perm, $"permission: revoke {appId} sur {deviceId}");
    }

    public IReadOnlyList<PermissionConfig> GetPending()
        => _cache.Values.Where(p => p.Status == "pending").ToList();

    private void Save(PermissionConfig perm, string commitMessage)
    {
        var dir = Path.Combine(_repo.RepoPath, "permissions");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{perm.AppId}__{perm.DeviceId}.yaml");
        File.WriteAllText(file, _yaml.Serialize(perm));
        _cache[Key(perm.AppId, perm.DeviceId)] = perm;
        _repo.Commit(commitMessage, file);
        _logger.LogInformation("{CommitMessage}", commitMessage);
    }

    private static string Key(string appId, string deviceId) => $"{appId}::{deviceId}";
}

public enum PermissionStatus { Missing, Pending, Granted, Revoked }
