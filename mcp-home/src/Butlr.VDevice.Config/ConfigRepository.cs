using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Config;

public sealed class ConfigRepository : IDisposable
{
    private readonly string _repoPath;
    private readonly VDeviceYamlSerializer _yaml;
    private readonly ILogger<ConfigRepository> _logger;
    private Repository? _repo;

    public ConfigRepository(string repoPath, VDeviceYamlSerializer yaml, ILogger<ConfigRepository> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        _repoPath = repoPath;
        _yaml = yaml;
        _logger = logger;
    }

    public void Initialize()
    {
        if (!Directory.Exists(_repoPath))
            Directory.CreateDirectory(_repoPath);

        if (!Repository.IsValid(_repoPath))
        {
            Repository.Init(_repoPath);
            _logger.LogInformation("Repo config initialisé dans {RepoPath}", _repoPath);
            _repo = new Repository(_repoPath);
            WriteDefaultPreset();
            Commit("init: preset par défaut");
        }
        else
        {
            _repo = new Repository(_repoPath);
            _logger.LogInformation("Repo config chargé depuis {RepoPath}", _repoPath);
        }
    }

    private void WriteDefaultPreset()
    {
        var homeConfig = new Models.HomeConfig
        {
            Name = "Maison",
            Tiers =
            [
                new Models.TierConfig
                {
                    Id = "safety", Rank = 1, ArbiterRef = "WinnerTakesAll",
                    Admission = new Models.AdmissionConfig { TagsRequired = [], TagsForbidden = [] },
                    DurationPolicy = new Models.DurationPolicyConfig { PersistentAllowed = true },
                    BypassInertia = true
                },
                new Models.TierConfig
                {
                    Id = "user-override", Rank = 2, ArbiterRef = "UserPriorityThenTimestamp",
                    Admission = new Models.AdmissionConfig { TagsRequired = ["user_agent"] },
                    DurationPolicy = new Models.DurationPolicyConfig { PersistentAllowed = false, TtlRequired = true }
                },
                new Models.TierConfig
                {
                    Id = "apps", Rank = 3, ArbiterRef = "StrictPriority",
                    Admission = new Models.AdmissionConfig { TagsRequired = ["app"] },
                    DurationPolicy = new Models.DurationPolicyConfig { PersistentAllowed = true }
                }
            ]
        };

        var homeYaml = _yaml.Serialize(homeConfig);
        File.WriteAllText(Path.Combine(_repoPath, "home.yaml"), homeYaml);

        foreach (var dir in new[] { "permissions", "apps", "arbiters" })
            Directory.CreateDirectory(Path.Combine(_repoPath, dir));
    }

    public void Commit(string message, params string[] filePaths)
    {
        ArgumentNullException.ThrowIfNull(_repo);

        if (filePaths.Length > 0)
        {
            foreach (var file in filePaths)
                Commands.Stage(_repo, file);
        }
        else
        {
            Commands.Stage(_repo, "*");
        }

        if (!_repo.RetrieveStatus().IsDirty) return;

        var sig = new Signature("butlr-mcp-home", "butlr@localhost", DateTimeOffset.UtcNow);
        _repo.Commit(message, sig, sig);
        _logger.LogInformation("Config commit : {Message}", message);
    }

    public string RepoPath => _repoPath;

    public void Dispose() => _repo?.Dispose();
}
