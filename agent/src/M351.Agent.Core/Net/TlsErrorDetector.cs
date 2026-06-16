using System.Security.Authentication;

namespace M351.Agent.Core.Net;

/// <summary>
/// Estados possiveis da conexao do agente com o servidor, propagados servico -> helper
/// (Secao 6.4 / 6.5: "Status da conexao" no tray). NUNCA desabilitamos a validacao de TLS:
/// uma falha de certificado (ex.: inspecao MITM corporativa) e apenas REPORTADA aqui.
/// </summary>
public enum AgentConnectionState
{
    /// <summary>Ultimo envio/enroll concluiu (HTTP recebido do servidor real).</summary>
    Ok,

    /// <summary>Device ainda nao registrado (sem device_token).</summary>
    NaoEnrolado,

    /// <summary>Servidor inacessivel (timeout, DNS, recusa de conexao) — fila preservada.</summary>
    SemRede,

    /// <summary>Falha de handshake TLS / cadeia de certificado invalida (possivel MITM).</summary>
    ErroCertificado,
}

/// <summary>
/// Nomes de fio (wire) do estado de conexao, compartilhados entre servico (PipeServer) e helper
/// (tray). Mantidos estaveis para o JSON do pipe.
/// </summary>
public static class ConnectionStateNames
{
    public const string Ok = "ok";
    public const string NaoEnrolado = "nao_enrolado";
    public const string SemRede = "sem_rede";
    public const string ErroCertificado = "erro_certificado";

    public static string ToWire(AgentConnectionState state) => state switch
    {
        AgentConnectionState.Ok => Ok,
        AgentConnectionState.SemRede => SemRede,
        AgentConnectionState.ErroCertificado => ErroCertificado,
        _ => NaoEnrolado,
    };

    /// <summary>
    /// Estado de conexao a reportar ao helper (tray), reconciliando o BatchSender (fonte em regime)
    /// com o EnrollmentClient (enroll de primeiro boot). Enquanto o device nao esta enrolado o
    /// BatchSender fica em NaoEnrolado e nunca classifica o transporte; se o enroll de primeiro boot
    /// falhou por erro de certificado (possivel MITM, Secao 6.4 l.445), preferimos esse estado para
    /// que o tray mostre o erro de certificado em vez de "dispositivo ainda nao registrado".
    /// </summary>
    public static AgentConnectionState Reconcile(AgentConnectionState senderState,
        AgentConnectionState? enrollmentState) =>
        senderState == AgentConnectionState.NaoEnrolado
        && enrollmentState == AgentConnectionState.ErroCertificado
            ? AgentConnectionState.ErroCertificado
            : senderState;

    /// <summary>Texto pt-BR para o tray ("Status da conexao").</summary>
    public static string ToHumanPtBr(string? wire) => wire switch
    {
        Ok => "conectado ao servidor",
        SemRede => "servidor inacessivel (sem rede) — eventos preservados na fila local",
        ErroCertificado => "erro de certificado (possivel inspecao de rede/MITM) — verifique com o TI",
        NaoEnrolado => "dispositivo ainda nao registrado",
        _ => "estado desconhecido",
    };
}

/// <summary>
/// Classifica excecoes de rede do HttpClient para distinguir "sem rede" de "erro de certificado".
/// Uma inspecao MITM que reescreve o TLS com um certificado nao confiavel cai aqui sem que o
/// agente jamais relaxe a validacao — apenas geramos diagnostico e o estado ErroCertificado.
/// </summary>
public static class TlsErrorDetector
{
    /// <summary>
    /// true se a excecao (ou alguma inner) for falha de validacao/handshake de certificado TLS.
    /// Cobre AuthenticationException direto e HttpRequestException com inner de TLS/cripto
    /// (no Windows o SslStream costuma encapsular um Win32Exception/CryptographicException).
    /// </summary>
    public static bool IsCertificateError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case AuthenticationException:
                    return true;
                case System.Security.Cryptography.CryptographicException:
                    return true;
            }

            // O canal seguro do Windows reporta a maioria das falhas de cadeia como Win32Exception
            // (SEC_E_*/CERT_E_*). Reconhecemos pelo nome do tipo para nao depender de P/Invoke.
            var typeName = e.GetType().Name;
            if (typeName is "Win32Exception" && LooksLikeCertSpec(e.Message))
                return true;
        }
        return false;
    }

    private static bool LooksLikeCertSpec(string message) =>
        message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("certificado", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("trust", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("TLS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mapeia uma excecao de transporte (apos falha de SendAsync/PostAsync) para o estado de conexao
    /// reportado ao helper. Erro de certificado tem precedencia sobre "sem rede".
    /// </summary>
    public static AgentConnectionState ClassifyTransportFailure(Exception ex) =>
        IsCertificateError(ex) ? AgentConnectionState.ErroCertificado : AgentConnectionState.SemRede;
}
