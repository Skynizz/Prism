using System.Collections.ObjectModel;

namespace Prism.Services;

public enum LogLevel { Trace, Info, Warn, Error }

/// <summary>Une ligne de journal structuree, telle qu'affichee par la console interne.</summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss");
    public string LevelTag => Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };

    public string ToLine() => $"{Timestamp:HH:mm:ss}  {LevelTag,-5}  {Source,-12}  {Message}";
}

/// <summary>
/// Journal de l'application. Ecrit sur disque et conserve un tampon circulaire en
/// memoire pour la console interne. Aucune methode ne doit jamais lever : une panne
/// de journalisation ne doit pas interrompre une operation sur les fichiers d'un jeu.
/// </summary>
public static class Log
{
    private const int Capacity = 4000;

    private static readonly object Gate = new();
    private static readonly string FilePath =
        Path.Combine(AppPaths.Logs, $"prism-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>
    /// Historique complet, alimente des le premier appel. L'interface s'abonne plus
    /// tard : sans ce tampon, les lignes du demarrage seraient perdues.
    /// </summary>
    private static readonly List<LogEntry> All = new();

    /// <summary>Tampon consultable par l'interface. Alimente sur le thread appelant.</summary>
    public static ObservableCollection<LogEntry> Buffer { get; } = new();

    /// <summary>Leve a chaque nouvelle ligne, pour que la vue puisse marshaller vers son thread.</summary>
    public static event Action<LogEntry>? Appended;

    public static void Trace(string source, string message) => Append(LogLevel.Trace, source, message);
    public static void Info(string source, string message) => Append(LogLevel.Info, source, message);
    public static void Warn(string source, string message) => Append(LogLevel.Warn, source, message);
    public static void Error(string source, string message) => Append(LogLevel.Error, source, message);

    /// <summary>Forme historique conservee pour les appels existants.</summary>
    public static void Write(string message) => Append(LogLevel.Info, "prism", message);

    private static void Append(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);

        lock (Gate)
        {
            All.Add(entry);
            while (All.Count > Capacity) All.RemoveAt(0);

            try { File.AppendAllText(FilePath, entry.ToLine() + Environment.NewLine); }
            catch { /* disque plein ou fichier verrouille : on continue en memoire */ }
        }

        try { Appended?.Invoke(entry); }
        catch { /* un abonne defaillant ne doit pas remonter jusqu'a l'appelant */ }
    }

    /// <summary>Copie de l'historique, pour amorcer la console au demarrage.</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate) return All.ToList();
    }

    /// <summary>Ajoute au tampon memoire. Appele par l'interface sur son propre thread.</summary>
    public static void Push(LogEntry entry)
    {
        Buffer.Add(entry);
        while (Buffer.Count > Capacity) Buffer.RemoveAt(0);
    }

    public static string CurrentFile => FilePath;
}
