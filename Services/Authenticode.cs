using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Prism.Services;

/// <summary>
/// Verification Authenticode d'un binaire, par l'API Windows elle-meme.
///
/// C'est le garde-fou des paquets tiers : un paquet complet peut embarquer des
/// runtimes NVIDIA, dont <c>nvngx_dlssnr.dll</c>. Aucune empreinte publique ne
/// permet de les authentifier, mais une signature NVIDIA valide, elle, le permet.
/// Un fichier non signe, altere ou signe par quelqu'un d'autre n'est jamais pose.
/// </summary>
public static class Authenticode
{
    public sealed record Result(bool Valid, string? Signer)
    {
        public bool IsNvidia => Valid && Signer is not null
                                && Signer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    }

    public static Result Verify(string path)
    {
        if (!File.Exists(path)) return new Result(false, null);

        var valid = WinVerifyTrustFile(path);
        string? signer = null;
        try
        {
#pragma warning disable SYSLIB0057 // Seule API du BCL qui lit le certificat d'une signature Authenticode.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch
        {
            // Pas de signature : le verdict est deja negatif.
        }

        return new Result(valid, signer);
    }

    // --------------------------------------------------------------- WinTrust

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

    private static bool WinVerifyTrustFile(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var filePtr = IntPtr.Zero;
        var dataPtr = IntPtr.Zero;

        try
        {
            var file = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = pathPtr
            };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePtr, false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = filePtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone
            };
            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPtr, false);

            var status = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr);

            // Liberation de l'etat alloue par la verification.
            data = Marshal.PtrToStructure<WinTrustData>(dataPtr);
            data.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(data, dataPtr, true);
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr);

            return status == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
