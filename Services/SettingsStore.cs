using Prism.Models;

namespace Prism.Services;

/// <summary>Reglages globaux, charges au demarrage et reecrits a chaque modification.</summary>
public sealed class SettingsStore
{
    public AppSettings Current { get; }

    public SettingsStore() => Current = JsonStore.Load(AppPaths.SettingsFile, () => new AppSettings());

    public void Save() => JsonStore.Save(AppPaths.SettingsFile, Current);
}

/// <summary>Profils par jeu : ce que l'utilisateur a choisi, pour le reproposer ensuite.</summary>
public sealed class ProfileStore
{
    private readonly Dictionary<string, GameProfile> _profiles;

    public ProfileStore()
    {
        var list = JsonStore.Load(AppPaths.ProfilesFile, () => new List<GameProfile>());
        _profiles = list
            .Where(p => !string.IsNullOrEmpty(p.GameId))
            .GroupBy(p => p.GameId)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public GameProfile Get(string gameId)
    {
        if (_profiles.TryGetValue(gameId, out var p)) return p;
        var created = new GameProfile { GameId = gameId };
        _profiles[gameId] = created;
        return created;
    }

    public void Update(GameProfile profile)
    {
        profile.LastModified = DateTimeOffset.Now;
        _profiles[profile.GameId] = profile;
        Save();
    }

    public void Save() => JsonStore.Save(AppPaths.ProfilesFile, _profiles.Values.ToList());

    /// <summary>Jeux ayant recu au moins une modification, du plus recent au plus ancien.</summary>
    public IEnumerable<GameProfile> Modified =>
        _profiles.Values.Where(p => p.LastModified is not null).OrderByDescending(p => p.LastModified);
}
