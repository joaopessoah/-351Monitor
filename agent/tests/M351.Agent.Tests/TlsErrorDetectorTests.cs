using System.ComponentModel;
using System.Security.Authentication;
using System.Security.Cryptography;
using M351.Agent.Core.Net;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Mapeamento de excecao de transporte -> estado de conexao (Secao 6.4 l.445). Uma falha de
/// handshake TLS / cadeia invalida (inclusive inspecao MITM corporativa) vira ErroCertificado;
/// qualquer outra falha de rede vira SemRede. NUNCA contornamos a validacao — apenas classificamos.
/// </summary>
public class TlsErrorDetectorTests
{
    [Fact]
    public void AuthenticationException_e_erro_de_certificado()
    {
        var ex = new AuthenticationException("The remote certificate is invalid.");
        Assert.True(TlsErrorDetector.IsCertificateError(ex));
        Assert.Equal(AgentConnectionState.ErroCertificado, TlsErrorDetector.ClassifyTransportFailure(ex));
    }

    [Fact]
    public void HttpRequestException_com_inner_de_autenticacao_e_erro_de_certificado()
    {
        // Forma tipica no Windows: HttpRequestException -> AuthenticationException -> Win32Exception.
        var inner = new AuthenticationException(
            "The remote certificate is invalid according to the validation procedure.",
            new Win32Exception(unchecked((int)0x80090325))); // SEC_E_UNTRUSTED_ROOT
        var ex = new HttpRequestException("An error occurred while sending the request.", inner);

        Assert.True(TlsErrorDetector.IsCertificateError(ex));
        Assert.Equal(AgentConnectionState.ErroCertificado, TlsErrorDetector.ClassifyTransportFailure(ex));
    }

    [Fact]
    public void CryptographicException_e_erro_de_certificado()
    {
        var ex = new HttpRequestException("send failed", new CryptographicException("cadeia de certificado nao confiavel"));
        Assert.True(TlsErrorDetector.IsCertificateError(ex));
    }

    [Fact]
    public void Win32Exception_de_certificado_e_erro_de_certificado()
    {
        var ex = new HttpRequestException("falha", new Win32Exception("The certificate chain was issued by an authority that is not trusted"));
        Assert.True(TlsErrorDetector.IsCertificateError(ex));
    }

    [Fact]
    public void Falha_de_rede_comum_e_sem_rede()
    {
        var ex = new HttpRequestException("Connection refused", new Win32Exception("No connection could be made"));
        Assert.False(TlsErrorDetector.IsCertificateError(ex));
        Assert.Equal(AgentConnectionState.SemRede, TlsErrorDetector.ClassifyTransportFailure(ex));
    }

    [Fact]
    public void Timeout_e_sem_rede()
    {
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        Assert.False(TlsErrorDetector.IsCertificateError(ex));
        Assert.Equal(AgentConnectionState.SemRede, TlsErrorDetector.ClassifyTransportFailure(ex));
    }

    [Theory]
    [InlineData(AgentConnectionState.Ok, "ok")]
    [InlineData(AgentConnectionState.SemRede, "sem_rede")]
    [InlineData(AgentConnectionState.ErroCertificado, "erro_certificado")]
    [InlineData(AgentConnectionState.NaoEnrolado, "nao_enrolado")]
    public void ToWire_mapeia_estado(AgentConnectionState state, string expected)
    {
        Assert.Equal(expected, ConnectionStateNames.ToWire(state));
    }

    [Fact]
    public void Reconcile_propaga_erro_de_certificado_do_enroll_antes_do_registro()
    {
        // Enroll de primeiro boot falhou por TLS: o BatchSender segue NaoEnrolado (sem token), mas
        // o EnrollmentClient classificou ErroCertificado — o tray deve mostrar o erro de certificado.
        Assert.Equal(AgentConnectionState.ErroCertificado,
            ConnectionStateNames.Reconcile(AgentConnectionState.NaoEnrolado, AgentConnectionState.ErroCertificado));
    }

    [Fact]
    public void Reconcile_sem_erro_de_enroll_mantem_nao_enrolado()
    {
        Assert.Equal(AgentConnectionState.NaoEnrolado,
            ConnectionStateNames.Reconcile(AgentConnectionState.NaoEnrolado, null));
        // SemRede no enroll nao deve virar erro de certificado.
        Assert.Equal(AgentConnectionState.NaoEnrolado,
            ConnectionStateNames.Reconcile(AgentConnectionState.NaoEnrolado, AgentConnectionState.SemRede));
    }

    [Fact]
    public void Reconcile_em_regime_o_sender_vence()
    {
        // Ja enrolado: o estado do enroll (mesmo um erro antigo) nunca sobrescreve o do BatchSender.
        Assert.Equal(AgentConnectionState.Ok,
            ConnectionStateNames.Reconcile(AgentConnectionState.Ok, AgentConnectionState.ErroCertificado));
        Assert.Equal(AgentConnectionState.SemRede,
            ConnectionStateNames.Reconcile(AgentConnectionState.SemRede, AgentConnectionState.ErroCertificado));
    }
}
