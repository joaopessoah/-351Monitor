using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace M351.Agent.Core.Update;

/// <summary>Resultado da verificação de assinatura de um arquivo (nunca lança).</summary>
/// <param name="Trusted">true só quando o WinVerifyTrust aprovou a cadeia inteira.</param>
/// <param name="SignerSubject">Subject do certificado do signatário (null se não houver assinatura).</param>
/// <param name="Detail">Motivo legível para o log (código do WinVerifyTrust ou exceção).</param>
public sealed record AuthenticodeResult(bool Trusted, string? SignerSubject, string Detail);

/// <summary>
/// Verificação REAL de assinatura Authenticode via WinVerifyTrust (wintrust.dll) — supply-chain do
/// auto-update (Seção 6.7): um MSI com SHA-256 correto ainda pode vir de um manifesto adulterado,
/// então a assinatura é a segunda barreira independente.
///
/// Fica DESLIGADA por padrão (InstallConfig.verify_authenticode = false) porque o certificado de
/// code signing ainda não foi comprado (docs/runbooks/comprar-certificado-codesigning.md); a
/// versão empacotada com o certificado liga a flag no install.json.
///
/// WINTRUST_ACTION_GENERIC_VERIFY_V2 é a ação canônica para "este arquivo tem assinatura confiável
/// segundo as políticas da máquina". Usamos WTD_UI_NONE (serviço, Session 0, sem UI possível) e
/// WTD_REVOKE_WHOLECHAIN (revogação da cadeia inteira — não aceitamos certificado revogado).
/// </summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;

    /// <summary>
    /// Verifica a assinatura do arquivo e, quando <paramref name="expectedSignerCn"/> é informado,
    /// exige que o Subject do certificado do signatário contenha esse CN (defesa contra um MSI
    /// legitimamente assinado por OUTRA empresa). Nunca lança: falha vira Trusted=false.
    /// </summary>
    public static AuthenticodeResult Verify(string filePath, string? expectedSignerCn)
    {
        if (!File.Exists(filePath))
        {
            return new AuthenticodeResult(false, null, "arquivo inexistente");
        }

        uint status;
        try
        {
            status = VerifyTrust(filePath);
        }
        catch (Exception ex)
        {
            // DllNotFoundException/EntryPointNotFoundException (SO sem wintrust) também caem aqui:
            // sem poder verificar, NÃO confiamos.
            return new AuthenticodeResult(false, null, $"WinVerifyTrust indisponível ({ex.GetType().Name})");
        }

        if (status != 0)
        {
            return new AuthenticodeResult(false, null, DescribeStatus(status));
        }

        // Cadeia aprovada: agora conferimos QUEM assinou.
        string? subject;
        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            subject = certificate.Subject;
        }
        catch (Exception ex)
        {
            return new AuthenticodeResult(false, null, $"assinatura aprovada, mas certificado ilegível ({ex.GetType().Name})");
        }

        if (!string.IsNullOrWhiteSpace(expectedSignerCn)
            && subject?.Contains(expectedSignerCn, StringComparison.OrdinalIgnoreCase) != true)
        {
            return new AuthenticodeResult(false, subject,
                $"assinado por outro titular (esperado CN contendo '{expectedSignerCn}')");
        }

        return new AuthenticodeResult(true, subject, "assinatura confiável");
    }

    private static uint VerifyTrust(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(filePath),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };
        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());

        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            pPolicyCallbackData = IntPtr.Zero,
            pSIPClientData = IntPtr.Zero,
            dwUIChoice = WtdUiNone,
            fdwRevocationChecks = WtdRevokeWholeChain,
            dwUnionChoice = WtdChoiceFile,
            pFile = fileInfoPtr,
            dwStateAction = WtdStateActionVerify,
            hWVTStateData = IntPtr.Zero,
            pwszURLReference = IntPtr.Zero,
            dwProvFlags = WtdSaferFlag,
            dwUIContext = 0,
            pSignatureSettings = IntPtr.Zero
        };
        var dataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            Marshal.StructureToPtr(data, dataPtr, false);

            var action = WinTrustActionGenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

            // WTD_STATEACTION_CLOSE é OBRIGATÓRIO após um VERIFY: sem ele o provider vaza o
            // estado interno a cada verificação (o serviço checa update a cada 6 h, para sempre).
            var toClose = Marshal.PtrToStructure<WinTrustData>(dataPtr);
            toClose.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(toClose, dataPtr, false);
            WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

            return unchecked((uint)status);
        }
        finally
        {
            if (fileInfo.pcwszFilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(dataPtr);
        }
    }

    /// <summary>Códigos do WinVerifyTrust que importam no diagnóstico (Seção 6.7).</summary>
    private static string DescribeStatus(uint status) => status switch
    {
        0x800B0100 => "arquivo SEM assinatura (TRUST_E_NOSIGNATURE)",
        0x800B0111 => "certificado do signatário não é confiável (CERT_E_UNTRUSTEDROOT)",
        0x800B010C => "certificado do signatário foi REVOGADO (CERT_E_REVOKED)",
        0x800B0101 => "certificado expirado (CERT_E_EXPIRED)",
        0x80096010 => "assinatura NÃO confere com o conteúdo (TRUST_E_BAD_DIGEST)",
        0x800B010A => "cadeia de certificação incompleta (CERT_E_CHAINING)",
        _ => $"WinVerifyTrust recusou (0x{status:X8})"
    };

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);

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
}
