namespace Prism.Models;

/// <summary>Reglages persistes pour un jeu donne.</summary>
public sealed class GameProfile
{
    public string GameId { get; set; } = "";
    public string? PinnedDlssVersion { get; set; }
    public string? PinnedDlssGVersion { get; set; }
    public string? PinnedDlssDVersion { get; set; }
    public FgBackend FgBackend { get; set; } = FgBackend.None;
    public int FgMultiplier { get; set; } = 2;
    public string? RenoDxAddon { get; set; }
    public bool ReShadeInstalled { get; set; }

    /// <summary>Addon HDR pose par Prism, et sa famille.</summary>
    public string? HdrAddonPath { get; set; }
    public HdrModKind? HdrKind { get; set; }

    /// <summary>Valeurs de [renodx] avant ecriture ; null quand la cle n'existait pas.</summary>
    public Dictionary<string, string?> HdrPreviousKeys { get; set; } = new();

    /// <summary>Engine.ini modifie par Prism, et s'il l'a cree de toutes pieces.</summary>
    public string? HdrEngineIniPath { get; set; }
    public bool HdrEngineIniCreated { get; set; }
    public DateTimeOffset? LastModified { get; set; }
}

/// <summary>Un fichier original mis de cote avant remplacement.</summary>
public sealed class BackupEntry
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    /// <summary>Chemin d'origine, absolu, ou le fichier doit etre reinjecte.</summary>
    public string OriginalPath { get; set; } = "";
    /// <summary>Copie conservee dans le dossier de sauvegardes de Prism.</summary>
    public string BackupPath { get; set; } = "";
    public string? FileVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Installation qui a provoque le remplacement : "RenoDX DLSS 5", "RenoDX HDR"...</summary>
    public string? Origin { get; set; }

    public string FileName => System.IO.Path.GetFileName(OriginalPath);
    public bool StillExists => File.Exists(BackupPath);
}

/// <summary>Reglages globaux de l'application.</summary>
public sealed class AppSettings
{
    public List<string> ExtraLibraryFolders { get; set; } = new();

    /// <summary>Langue de l'interface ; null suit celle de Windows.</summary>
    public string? Language { get; set; }
    public bool DarkTheme { get; set; } = true;

    /// <summary>Theme de l'interface : "classic" ou "studio".</summary>
    public string Theme { get; set; } = "classic";

    /// <summary>Explications affichees ; par defaut l'interface s'en tient aux mots-cles.</summary>
    public bool ShowDetails { get; set; }

    /// <summary>Animations coupees : recommande sur un ecran OLED avec VRR active.</summary>
    public bool ReduceMotion { get; set; }
    public bool ShowDevBuilds { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTimeOffset? ManifestFetchedAt { get; set; }
}
