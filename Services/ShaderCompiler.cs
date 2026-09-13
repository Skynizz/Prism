using System.Diagnostics;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Compilateur de shaders embarque par le jeu.
///
/// Certains titres livrent un <c>d3dcompiler_47.dll</c> de Windows 8.1 (6.3.9600). Windows
/// charge la copie du dossier du jeu avant celle de System32, et cette version ne connait
/// pas le Shader Model 5.1 : RenoDX DLSS 5 echoue alors en boucle sur
/// « X3506: unrecognized compiler target 'cs_5_1' ». La copie de Windows 10+ est
/// retrocompatible ; Prism la pose a la place, original sauvegarde.
/// </summary>
public static class ShaderCompiler
{
    public const string FileName = "d3dcompiler_47.dll";

    /// <summary>Premiere generation qui compile le Shader Model 5.1.</summary>
    private const int MinimumMajor = 10;

    private static string SystemCopy => Path.Combine(Environment.SystemDirectory, FileName);

    /// <summary>Version du compilateur du jeu, ou null s'il n'en embarque pas.</summary>
    public static string? LocalVersion(string dir)
    {
        var path = Path.Combine(dir, FileName);
        return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
    }

    /// <summary>Vrai si le jeu embarque un compilateur trop ancien pour DLSS 5.</summary>
    public static bool IsOutdated(string dir)
    {
        var path = Path.Combine(dir, FileName);
        return File.Exists(path) && MajorOf(path) < MinimumMajor;
    }

    /// <summary>Remplace le compilateur du jeu par celui de Windows, original sauvegarde.</summary>
    public static bool Upgrade(GameInfo game, string dir, BackupService backups, string? origin = null)
    {
        var dest = Path.Combine(dir, FileName);
        var src = SystemCopy;
        if (!File.Exists(dest) || !File.Exists(src) || MajorOf(src) < MinimumMajor) return false;

        var before = FileVersionInfo.GetVersionInfo(dest).FileVersion;
        backups.Capture(game, dest, origin);
        DllInstaller.ClearReadOnly(dest);
        File.Copy(src, dest, overwrite: true);

        Log.Info("dlss5", $"{FileName} du jeu remplace : {before} -> {FileVersionInfo.GetVersionInfo(dest).FileVersion}");
        return true;
    }

    private static int MajorOf(string path) => FileVersionInfo.GetVersionInfo(path).FileMajorPart;
}
