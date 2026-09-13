using System.Text.Json;

namespace Prism.Services;

/// <summary>Lecture/ecriture JSON tolerante : un fichier corrompu ne doit pas tuer l'appli.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Ecriture atomique : on ne veut pas d'index de backups a moitie ecrit.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Echec d'ecriture de {path} : {ex.Message}");
        }
    }
}

// Le journal vit dans Diagnostics.cs.
