using System.Runtime.InteropServices;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Profils du pilote NVIDIA (DRS), sans dependance : appels directs a nvapi64.dll par
/// <c>nvapi_QueryInterface</c>. Identifiants de fonctions, de reglages et dispositions memoire
/// tires des en-tetes officiels du depot NVIDIA/nvapi (nvapi.h, nvapi_interface.h,
/// NvApiDriverSettings.h). Rien n'est ecrit dans le jeu : c'est le pilote qui applique le reglage
/// a l'executable, comme le fait l'application NVIDIA.
/// </summary>
public static class NvDriverSettings
{
    private const string Src = "nvdrs";

    // nvapi_interface.h
    private const uint Initialize = 0x0150E828;
    private const uint CreateSession = 0x0694D52E, DestroySession = 0xDAD9CFF8;
    private const uint LoadSettings = 0x375DBD6B, SaveSettings = 0xFCBC7E14;
    private const uint FindApplicationByName = 0xEEE566B2, CreateProfile = 0xCC176068, CreateApplication = 0x4347A9DE;
    private const uint SetSetting = 0x577DD202, GetSetting = 0x73BF8338, DeleteProfileSetting = 0xE4A26362;
    private const uint DeleteProfile = 0x17093206;

    // nvapi_lite_common.h
    public const int Ok = 0, IncompatibleStructVersion = -9, SettingNotFound = -160, ExecutableNotFound = -166;

    // NvApiDriverSettings.h
    public const uint DlssSrOverride = 0x10E41E01;            // NGX_DLSS_SR_OVERRIDE_ID : 0 off, 1 on
    public const uint DlssSrPresetSelection = 0x10E41DF3;     // NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION_ID

    private const int UnicodeMax = 2048;                      // NVAPI_UNICODE_STRING_MAX (en NvU16)
    private const int SettingSize = 12328;                    // NVDRS_SETTING_V1, pack 8, avec le champ NvU64
    private const int SettingSizeLegacy = 12320;              // meme structure avant l'ajout du champ NvU64
    private const int ApplicationV4Size = 20492;              // NVDRS_APPLICATION_V4
    private const int ProfileV1Size = 4116;                   // NVDRS_PROFILE_V1

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArg();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionOut(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionIn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindApp(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProf(IntPtr session, IntPtr profileInfo, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateApp(IntPtr session, IntPtr profile, IntPtr application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSett(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSett(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DelSett(IntPtr session, IntPtr profile, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DelProf(IntPtr session, IntPtr profile);

    private static T Fn<T>(uint id) where T : Delegate
    {
        var ptr = QueryInterface(id);
        if (ptr == IntPtr.Zero) throw new EntryPointNotFoundException($"nvapi 0x{id:X8}");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>Pilote NVIDIA present et API initialisee.</summary>
    public static bool Available
    {
        get
        {
            try { return Fn<NoArg>(Initialize)() == Ok; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
        }
    }

    /// <summary>Valeur DWORD d'un reglage pour cet executable ; null s'il n'est pas defini.</summary>
    public static uint? Read(string exeName, uint settingId)
    {
        uint? value = null;
        WithProfile(exeName, create: false, (session, profile) =>
        {
            var (status, v) = GetDword(session, profile, settingId);
            if (status == Ok) value = v;
            return false;
        });
        return value;
    }

    /// <summary>Ecrit des reglages DWORD pour cet executable (profil cree s'il n'existe pas), puis enregistre.</summary>
    public static bool Write(string exeName, params (uint Id, uint Value)[] values)
        => WithProfile(exeName, create: true, (session, profile) =>
        {
            foreach (var (id, value) in values)
            {
                var status = SetDword(session, profile, id, value);
                if (status != Ok) { Log.Warn(Src, $"SetSetting 0x{id:X8} on {exeName}: {status}"); return false; }
            }
            return true;
        });

    /// <summary>Retire des reglages : l'executable reprend la valeur du profil de base.</summary>
    public static bool Clear(string exeName, params uint[] ids)
        => WithProfile(exeName, create: false, (session, profile) =>
        {
            var del = Fn<DelSett>(DeleteProfileSetting);
            foreach (var id in ids)
            {
                var status = del(session, profile, id);
                if (status is not (Ok or SettingNotFound)) { Log.Warn(Src, $"Delete 0x{id:X8} on {exeName}: {status}"); return false; }
            }
            return true;
        });

    /// <summary>Supprime le profil qui contient cet executable. A reserver aux profils crees par Prism.</summary>
    public static bool DeleteProfileOf(string exeName)
        => WithProfile(exeName, create: false, (session, profile) => Fn<DelProf>(DeleteProfile)(session, profile) == Ok);

    // ------------------------------------------------------------ Mecanique

    /// <summary>
    /// Ouvre une session, charge la base, trouve (ou cree) le profil de l'executable, appelle
    /// <paramref name="work"/> ; si elle renvoie vrai, enregistre. La session est toujours fermee.
    /// </summary>
    private static bool WithProfile(string exeName, bool create, Func<IntPtr, IntPtr, bool> work)
    {
        if (!Available) return false;
        var session = IntPtr.Zero;
        try
        {
            if (Fn<SessionOut>(CreateSession)(out session) != Ok) return false;
            if (Fn<SessionIn>(LoadSettings)(session) != Ok) return false;

            var profile = FindProfile(session, exeName);
            if (profile == IntPtr.Zero)
            {
                if (!create) return false;
                profile = CreateProfileFor(session, exeName);
                if (profile == IntPtr.Zero) return false;
            }

            var save = work(session, profile);
            if (!save) return !create;
            var saved = Fn<SessionIn>(SaveSettings)(session);
            if (saved != Ok) Log.Warn(Src, $"SaveSettings: {saved} (droits administrateur ?)");
            return saved == Ok;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Log.Warn(Src, $"NVAPI unavailable: {ex.Message}");
            return false;
        }
        finally
        {
            if (session != IntPtr.Zero) try { Fn<SessionIn>(DestroySession)(session); } catch { /* deja fermee */ }
        }
    }

    private static IntPtr FindProfile(IntPtr session, string exeName)
    {
        var name = Unicode(exeName.ToLowerInvariant());
        var app = Marshal.AllocHGlobal(ApplicationV4Size);
        try
        {
            Zero(app, ApplicationV4Size);
            Marshal.WriteInt32(app, ApplicationV4Size | (4 << 16));
            var status = Fn<FindApp>(FindApplicationByName)(session, name, out var profile, app);
            return status == Ok ? profile : IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(app); Marshal.FreeHGlobal(name); }
    }

    /// <summary>Profil utilisateur « Prism · &lt;exe&gt; » contenant l'executable, comme l'application NVIDIA le fait.</summary>
    private static IntPtr CreateProfileFor(IntPtr session, string exeName)
    {
        var info = Marshal.AllocHGlobal(ProfileV1Size);
        var app = Marshal.AllocHGlobal(ApplicationV4Size);
        try
        {
            Zero(info, ProfileV1Size);
            Marshal.WriteInt32(info, ProfileV1Size | (1 << 16));
            WriteUnicode(info + 4, "Prism · " + exeName);
            if (Fn<CreateProf>(CreateProfile)(session, info, out var profile) != Ok) return IntPtr.Zero;

            Zero(app, ApplicationV4Size);
            Marshal.WriteInt32(app, ApplicationV4Size | (4 << 16));
            WriteUnicode(app + 8, exeName.ToLowerInvariant());          // appName, apres version et isPredefined
            var status = Fn<CreateApp>(CreateApplication)(session, profile, app);
            if (status != Ok) { Log.Warn(Src, $"CreateApplication {exeName}: {status}"); return IntPtr.Zero; }
            return profile;
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(app); }
    }

    /// <summary>
    /// NVDRS_SETTING_V1 : version, nom (NvU16[2048]), id, type, emplacement, deux drapeaux, puis
    /// deux unions alignees sur 8 (valeur predefinie, valeur courante). DWORD = type 0.
    /// </summary>
    private static int SetDword(IntPtr session, IntPtr profile, uint id, uint value)
        => WithSetting((buffer, size) =>
        {
            Marshal.WriteInt32(buffer, 4100, unchecked((int)id));         // settingId
            Marshal.WriteInt32(buffer, 4104, 0);                           // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(buffer, CurrentValueOffset(size), unchecked((int)value));
            return Fn<SetSett>(SetSetting)(session, profile, buffer);
        });

    private static (int Status, uint Value) GetDword(IntPtr session, IntPtr profile, uint id)
    {
        uint value = 0;
        var status = WithSetting((buffer, size) =>
        {
            var s = Fn<GetSett>(GetSetting)(session, profile, id, buffer);
            if (s == Ok) value = unchecked((uint)Marshal.ReadInt32(buffer, CurrentValueOffset(size)));
            return s;
        });
        return (status, value);
    }

    /// <summary>Essaie la taille actuelle de la structure, puis l'ancienne si le pilote refuse la version.</summary>
    private static int WithSetting(Func<IntPtr, int, int> call)
    {
        var buffer = Marshal.AllocHGlobal(SettingSize);
        try
        {
            var status = IncompatibleStructVersion;
            foreach (var size in new[] { SettingSize, SettingSizeLegacy })
            {
                Zero(buffer, SettingSize);
                Marshal.WriteInt32(buffer, size | (1 << 16));
                status = call(buffer, size);
                if (status != IncompatibleStructVersion) return status;
            }
            return status;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Valeur courante : seconde union, a 4120 + taille de la premiere (4104 alignee sur 8, ou 4100).</summary>
    private static int CurrentValueOffset(int size) => size == SettingSize ? 4120 + 4104 : 4120 + 4100;

    private static IntPtr Unicode(string s)
    {
        var p = Marshal.AllocHGlobal(UnicodeMax * 2);
        Zero(p, UnicodeMax * 2);
        WriteUnicode(p, s);
        return p;
    }

    private static void WriteUnicode(IntPtr p, string s)
    {
        var chars = s.Length >= UnicodeMax ? s[..(UnicodeMax - 1)] : s;
        Marshal.Copy(chars.ToCharArray(), 0, p, chars.Length);
    }

    private static void Zero(IntPtr p, int size)
    {
        for (var i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
    }
}
