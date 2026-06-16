using System.Net;
using System.Security.Authentication;
using M351.Agent.Core.Fingerprint;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Net;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Enroll de primeiro boot (golden image / NOENROLL, Secao 6.6) e propagacao do erro de certificado
/// (Secao 6.4 l.445). Um MITM que quebra o handshake JA NO ENROLL nao pode ficar invisivel ao tray:
/// o EnrollmentClient registra ErroCertificado em LastConnectionState, que o PipeServer reconcilia.
/// NUNCA desabilitamos a validacao de TLS — apenas classificamos e reportamos.
/// </summary>
public class EnrollmentClientTests
{
    private sealed class StubFingerprint : IFingerprintSource
    {
        public string? GetMachineGuid() => "test-machine-guid";
        public string? GetBiosSerial() => "test-bios-serial";
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static EnrollmentClient Build(TempQueue temp, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var http = new HttpClient(new FakeHandler(respond));
        return new EnrollmentClient(http, state, new StubFingerprint(), new NullLogSink(), () => "Windows 11");
    }

    [Fact]
    public async Task Enroll_com_falha_TLS_marca_LastConnectionState_ErroCertificado()
    {
        // Inspecao MITM apresenta certificado nao confiavel: o handshake falha no enroll.
        var certError = new HttpRequestException("send failed",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));
        using var temp = new TempQueue();
        var enroll = Build(temp, _ => throw certError);

        var ok = await enroll.EnrollAsync("https://api.exemplo.com.br", "ek_teste", CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(AgentConnectionState.ErroCertificado, enroll.LastConnectionState);

        // O cenario que a fatia cobre: reconciliacao do estado reportado ao tray (sender NaoEnrolado).
        Assert.Equal(AgentConnectionState.ErroCertificado,
            ConnectionStateNames.Reconcile(AgentConnectionState.NaoEnrolado, enroll.LastConnectionState));
    }

    [Fact]
    public async Task Enroll_com_falha_de_rede_comum_marca_SemRede()
    {
        using var temp = new TempQueue();
        var enroll = Build(temp, _ => throw new HttpRequestException("connection refused"));

        var ok = await enroll.EnrollAsync("https://api.exemplo.com.br", "ek_teste", CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(AgentConnectionState.SemRede, enroll.LastConnectionState);
        // Sem erro de certificado: o tray segue mostrando "nao enrolado", nao "erro de certificado".
        Assert.Equal(AgentConnectionState.NaoEnrolado,
            ConnectionStateNames.Reconcile(AgentConnectionState.NaoEnrolado, enroll.LastConnectionState));
    }

    [Fact]
    public async Task Enroll_ok_persiste_identidade()
    {
        // config omitido: o EnrollResponse mantem o AgentConfig default (new()).
        const string body =
            """{"device_id":"01976f00-aaaa-7bbb-8ccc-dddddddddddd","device_token":"dt_novo","config_version":0}""";
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));
        var enroll = new EnrollmentClient(http, state, new StubFingerprint(), new NullLogSink(), () => "Windows 11");

        var ok = await enroll.EnrollAsync("https://api.exemplo.com.br", "ek_teste", CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("dt_novo", state.DeviceToken);
        Assert.True(state.IsEnrolled);
    }
}
