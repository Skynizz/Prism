using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prism.Models;

/// <summary>
/// Le manifeste amont laisse "signed_datetime" vide sur certaines entrees. Le
/// convertisseur standard leve alors une exception qui fait echouer la lecture du
/// fichier entier : on prefere rendre null et garder les 200 autres versions.
/// </summary>
public sealed class TolerantDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto : null;
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Une version de DLL publiee, telle que decrite par le manifeste DLSS Swapper.
/// Les noms JSON suivent exactement le manifeste amont.
/// </summary>
public sealed class DllRecord
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("version_number")] public long VersionNumber { get; set; }
    [JsonPropertyName("internal_name")] public string? InternalName { get; set; }
    [JsonPropertyName("additional_label")] public string? AdditionalLabel { get; set; }
    [JsonPropertyName("md5_hash")] public string Md5 { get; set; } = "";
    [JsonPropertyName("zip_md5_hash")] public string ZipMd5 { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("file_description")] public string? FileDescription { get; set; }
    [JsonPropertyName("signed_datetime")]
    [JsonConverter(typeof(TolerantDateTimeOffsetConverter))]
    public DateTimeOffset? SignedAt { get; set; }
    [JsonPropertyName("is_signature_valid")] public bool IsSignatureValid { get; set; }
    [JsonPropertyName("is_dev_file")] public bool IsDevFile { get; set; }
    [JsonPropertyName("file_size")] public long FileSize { get; set; }

    [JsonIgnore] public DllKind Kind { get; set; }

    /// <summary>Libelle de liste : "310.9.1.0  ·  MFGLW  ·  signee".</summary>
    [JsonIgnore]
    public string Display
    {
        get
        {
            var bits = new List<string> { Version };
            if (!string.IsNullOrWhiteSpace(AdditionalLabel)) bits.Add(AdditionalLabel!);
            else if (!string.IsNullOrWhiteSpace(InternalName)) bits.Add(InternalName!);
            if (IsDevFile) bits.Add("dev");
            return string.Join("  ·  ", bits);
        }
    }
}

/// <summary>Racine du manifeste.</summary>
public sealed class DllManifest
{
    [JsonPropertyName("dlss")] public List<DllRecord> Dlss { get; set; } = new();
    [JsonPropertyName("dlss_g")] public List<DllRecord> DlssG { get; set; } = new();
    [JsonPropertyName("dlss_d")] public List<DllRecord> DlssD { get; set; } = new();
    [JsonPropertyName("fsr_31_dx12")] public List<DllRecord> FsrDx12 { get; set; } = new();
    [JsonPropertyName("fsr_31_vk")] public List<DllRecord> FsrVk { get; set; } = new();
    [JsonPropertyName("xess")] public List<DllRecord> XeSS { get; set; } = new();
    [JsonPropertyName("xess_fg")] public List<DllRecord> XeSSFg { get; set; } = new();
    [JsonPropertyName("xell")] public List<DllRecord> XeLL { get; set; } = new();
}
