using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Compte Nexus Mods, par la cle API personnelle (www.nexusmods.com/settings/api-keys).
/// La cle est chiffree par Windows (DPAPI, liee a la session) et ne quitte le PC que vers
/// api.nexusmods.com.
///
/// API v1, telle que l'utilise le client officiel de Nexus (node-nexus-api) :
///  - GET /v1/users/validate : nom, is_premium ;
///  - GET /v1/games/{jeu}/mods/{mod}/files/{fichier}/download_link : liens directs, reserves aux
///    comptes Premium (un compte gratuit doit passer par le bouton du site) ;
///  - en-tetes apikey, Application-Name, Application-Version.
/// </summary>
public sealed class NexusAccount
{
    private const string Src = "nexus";
    private const string Api = "https://api.nexusmods.com/v1";
    public const string KeysPage = "https://www.nexusmods.com/settings/api-keys";

    private string? _key;

    public NexusAccount()
    {
        try
        {
            if (File.Exists(KeyFile)) _key = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(KeyFile)));
        }
        catch (Exception ex) { Log.Warn(Src, $"Stored Nexus key unreadable: {ex.Message}"); }
    }

    private static string KeyFile => Path.Combine(AppPaths.Root, "nexus-key.bin");

    public bool HasKey => !string.IsNullOrEmpty(_key);
    public bool Validated { get; private set; }
    public bool IsPremium { get; private set; }
    public string? UserName { get; private set; }

    /// <summary>Quota restant annonce par Nexus (quotidien, horaire).</summary>
    public (int Daily, int Hourly)? Quota { get; private set; }

    public event Action? Changed;

    /// <summary>Verifie une cle et la garde si elle est valide. Renvoie null, ou le message d'erreur.</summary>
    public async Task<string?> SetKeyAsync(string key, CancellationToken ct = default)
    {
        key = key.Trim();
        if (key.Length == 0) return Loc.T("nexus.acct.err.empty");
        var error = await ValidateAsync(key, ct);
        if (error is not null) return error;
        _key = key;
        File.WriteAllBytes(KeyFile, Dpapi.Protect(Encoding.UTF8.GetBytes(key)));
        Changed?.Invoke();
        return null;
    }

    public void Forget()
    {
        _key = null;
        Validated = IsPremium = false;
        UserName = null;
        try { File.Delete(KeyFile); } catch { /* deja absent */ }
        Changed?.Invoke();
    }

    /// <summary>Revalide la cle gardee (statut Premium a jour). Sans cle, ne fait rien.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_key is null || Validated) return;
        var error = await ValidateAsync(_key, ct);
        if (error is not null) Log.Warn(Src, $"Stored Nexus key: {error}");
        Changed?.Invoke();
    }

    private async Task<string?> ValidateAsync(string key, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"{Api}/users/validate", key, ct);
            if ((int)resp.StatusCode == 401) return Loc.T("nexus.acct.err.invalid");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            UserName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            IsPremium = doc.RootElement.TryGetProperty("is_premium", out var p) && p.ValueKind == JsonValueKind.True;
            Validated = true;
            Log.Info(Src, $"Nexus account {UserName}: {(IsPremium ? "Premium" : "free")}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Loc.T("nexus.acct.err.network", ex.Message);
        }
    }

    /// <summary>Lien de telechargement direct d'un fichier : comptes Premium seulement.</summary>
    public async Task<string> DownloadLinkAsync(string domain, long modId, long fileId, CancellationToken ct = default)
    {
        if (_key is null || !IsPremium) throw new InvalidOperationException(Loc.T("nexus.acct.err.not_premium"));
        using var resp = await SendAsync(HttpMethod.Get, $"{Api}/games/{domain}/mods/{modId}/files/{fileId}/download_link", _key, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var link in doc.RootElement.EnumerateArray())
            if (link.TryGetProperty("URI", out var uri) && uri.GetString() is { Length: > 0 } s) return s;
        throw new HttpRequestException(Loc.T("nexus.acct.err.no_link"));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("apikey", key);
        req.Headers.Add("Application-Name", "Prism");
        req.Headers.Add("Application-Version", AppVersion);
        var resp = await DownloadService.Client.SendAsync(req, ct);
        if (resp.Headers.TryGetValues("x-rl-daily-remaining", out var d) && resp.Headers.TryGetValues("x-rl-hourly-remaining", out var h) &&
            int.TryParse(d.FirstOrDefault(), out var daily) && int.TryParse(h.FirstOrDefault(), out var hourly))
            Quota = (daily, hourly);
        return resp;
    }

    /// <summary>Version semantique de Prism (« 1.2.0 »), comme l'exige l'en-tete Application-Version.</summary>
    private static string AppVersion
    {
        get
        {
            var v = typeof(NexusAccount).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }
}

/// <summary>Chiffrement Windows lie a la session (CryptProtectData), sans paquet supplementaire.</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] data) => Run(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
        var output = new Blob();
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "Prism", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            handle.Free();
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
