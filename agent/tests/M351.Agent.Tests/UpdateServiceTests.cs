using System.Net;
using System.Security.Cryptography;
using System.Text;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Core.Update;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

public class UpdateServiceTests
{
    private const string ServerUrl = "http://localhost:5080";
    private const string MsiUrl = ServerUrl + "/api/v1/agent/releases/MonitorAgent-1.1.0.msi";

    /// <summary>Handler HTTP fake roteando por path (manifesto vs binario do MSI).</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (AgentStateStore State, UpdateClient Client, FakeHandler Handler) BuildClient(
        TempQueue temp, Func<HttpRequestMessage, HttpResponseMessage> respond, bool enrolled = true)
    {
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        if (enrolled)
        {
            state.DeviceId = "01976f00-aaaa-7bbb-8ccc-dddddddddddd";
            state.DeviceToken = "dt_teste";
            state.ServerUrl = ServerUrl;
        }
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        return (state, new UpdateClient(http, state, new NullLogSink()), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string ManifestBody(string version, string sha256, string min = "1.0.0") =>
        $$"""{"version":"{{version}}","url":"{{MsiUrl}}","sha256":"{{sha256}}","min_version":"{{min}}"}""";

    [Fact]
    public async Task Fetch_manifesto_200_usa_device_token_e_query_current()
    {
        using var temp = new TempQueue();
        var (_, client, handler) = BuildClient(temp,
            _ => Json(HttpStatusCode.OK, ManifestBody("1.1.0", new string('a', 64))));

        var manifest = await client.FetchManifestAsync("1.0.3", CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("1.1.0", manifest!.Version);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("update-manifest?current=1.0.3", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("dt_teste", req.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Fetch_manifesto_204_retorna_null_sem_crash()
    {
        using var temp = new TempQueue();
        var (_, client, _) = BuildClient(temp, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var manifest = await client.FetchManifestAsync("1.0.0", CancellationToken.None);

        Assert.Null(manifest);
    }

    [Fact]
    public async Task Fetch_manifesto_erro_de_rede_retorna_null_sem_crash()
    {
        using var temp = new TempQueue();
        var (_, client, _) = BuildClient(temp, _ => throw new HttpRequestException("offline"));

        var manifest = await client.FetchManifestAsync("1.0.0", CancellationToken.None);

        Assert.Null(manifest);
    }

    [Fact]
    public async Task Apply_sha256_correto_grava_sentinela_e_dispara_installer()
    {
        using var temp = new TempQueue();
        var msiBytes = Encoding.UTF8.GetBytes("conteudo do msi 1.1.0");
        var sha = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();

        var (_, client, _) = BuildClient(temp, req =>
            req.RequestUri!.AbsolutePath.Contains("releases")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(msiBytes) }
                : Json(HttpStatusCode.OK, ManifestBody("1.1.0", sha)));

        var updatesDir = Path.Combine(Path.GetTempPath(), $"m351-upd-{Guid.NewGuid():N}");
        var sentinelWritten = false;
        var installerRan = false;
        try
        {
            var installer = new UpdateInstaller(client, new NullLogSink(), updatesDir,
                writeUpdateSentinel: () => sentinelWritten = true,
                runInstaller: (_, _) => { installerRan = true; return Task.FromResult(true); });

            var manifest = new UpdateManifest { Version = "1.1.0", Url = MsiUrl, Sha256 = sha, MinVersion = "1.0.0" };
            var ok = await installer.ApplyAsync(manifest, CancellationToken.None);

            Assert.True(ok);
            Assert.True(sentinelWritten); // sentinela gravada ANTES do installer
            Assert.True(installerRan);
            Assert.True(File.Exists(Path.Combine(updatesDir, "MonitorAgent-1.1.0.msi")));
        }
        finally
        {
            try { Directory.Delete(updatesDir, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Apply_quando_msiexec_nao_inicia_remove_a_sentinela_orfa()
    {
        using var temp = new TempQueue();
        var msiBytes = Encoding.UTF8.GetBytes("conteudo do msi 1.1.0");
        var sha = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();

        var (_, client, _) = BuildClient(temp, req =>
            req.RequestUri!.AbsolutePath.Contains("releases")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(msiBytes) }
                : Json(HttpStatusCode.OK, ManifestBody("1.1.0", sha)));

        var updatesDir = Path.Combine(Path.GetTempPath(), $"m351-upd-{Guid.NewGuid():N}");
        var sentinelWritten = false;
        var sentinelCleared = false;
        try
        {
            // runInstaller retorna false (msiexec nao subiu): a sentinela ja gravada deve ser removida
            // para nao rotular um stop/start NORMAL como update ate o proximo ciclo (~6h).
            var installer = new UpdateInstaller(client, new NullLogSink(), updatesDir,
                writeUpdateSentinel: () => sentinelWritten = true,
                runInstaller: (_, _) => Task.FromResult(false),
                clearUpdateSentinel: () => sentinelCleared = true);

            var manifest = new UpdateManifest { Version = "1.1.0", Url = MsiUrl, Sha256 = sha, MinVersion = "1.0.0" };
            var ok = await installer.ApplyAsync(manifest, CancellationToken.None);

            Assert.False(ok);               // nada instalado: tenta no proximo ciclo
            Assert.True(sentinelWritten);   // gravada antes de tentar (o MSI pode parar o servico cedo)
            Assert.True(sentinelCleared);   // removida porque o msiexec NAO subiu — sem sentinela orfa
        }
        finally
        {
            try { Directory.Delete(updatesDir, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Apply_sha256_errado_descarta_e_NAO_instala()
    {
        using var temp = new TempQueue();
        var msiBytes = Encoding.UTF8.GetBytes("conteudo adulterado");

        var (_, client, _) = BuildClient(temp, req =>
            req.RequestUri!.AbsolutePath.Contains("releases")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(msiBytes) }
                : Json(HttpStatusCode.OK, ManifestBody("1.1.0", new string('f', 64))));

        var updatesDir = Path.Combine(Path.GetTempPath(), $"m351-upd-{Guid.NewGuid():N}");
        var sentinelWritten = false;
        var installerRan = false;
        try
        {
            var installer = new UpdateInstaller(client, new NullLogSink(), updatesDir,
                writeUpdateSentinel: () => sentinelWritten = true,
                runInstaller: (_, _) => { installerRan = true; return Task.FromResult(true); });

            var manifest = new UpdateManifest { Version = "1.1.0", Url = MsiUrl, Sha256 = new string('f', 64), MinVersion = "1.0.0" };
            var ok = await installer.ApplyAsync(manifest, CancellationToken.None);

            Assert.False(ok);
            Assert.False(sentinelWritten); // nunca grava sentinela
            Assert.False(installerRan);    // nunca dispara msiexec
            Assert.False(File.Exists(Path.Combine(updatesDir, "MonitorAgent-1.1.0.msi"))); // arquivo descartado
        }
        finally
        {
            try { Directory.Delete(updatesDir, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task VerifyAuthenticode_e_gancho_e_retorna_true()
    {
        using var temp = new TempQueue();
        var (_, client, _) = BuildClient(temp, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var installer = new UpdateInstaller(client, new NullLogSink(), Path.GetTempPath(),
            writeUpdateSentinel: () => { });

        await Task.CompletedTask;
        Assert.True(installer.VerifyAuthenticode("qualquer.msi")); // F5: placeholder
    }

    [Fact]
    public async Task CheckOnce_204_nao_dispara_installer()
    {
        using var temp = new TempQueue();
        var (state, client, _) = BuildClient(temp, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var installerRan = false;
        var installer = new UpdateInstaller(client, new NullLogSink(), Path.GetTempPath(),
            writeUpdateSentinel: () => { },
            runInstaller: (_, _) => { installerRan = true; return Task.FromResult(true); });
        var service = new UpdateService(client, installer, state, new NullLogSink());

        var fired = await service.CheckOnceAsync(CancellationToken.None);

        Assert.False(fired);
        Assert.False(installerRan);
    }

    [Fact]
    public async Task CheckOnce_nao_enrolado_pula_sem_chamar_http()
    {
        using var temp = new TempQueue();
        var (state, client, handler) = BuildClient(temp, _ => new HttpResponseMessage(HttpStatusCode.NoContent), enrolled: false);
        var installer = new UpdateInstaller(client, new NullLogSink(), Path.GetTempPath(),
            writeUpdateSentinel: () => { },
            runInstaller: (_, _) => Task.FromResult(true));
        var service = new UpdateService(client, installer, state, new NullLogSink());

        var fired = await service.CheckOnceAsync(CancellationToken.None);

        Assert.False(fired);
        Assert.Empty(handler.Requests); // nem toca no servidor
    }

    [Fact]
    public void SafeFileName_evita_path_traversal_e_usa_fallback()
    {
        Assert.Equal("MonitorAgent-1.1.0.msi",
            UpdateInstaller.SafeFileName("https://srv/api/v1/agent/releases/MonitorAgent-1.1.0.msi", "1.1.0"));
        // url sem .msi cai no fallback determinístico
        Assert.Equal("MonitorAgent-1.2.0.msi",
            UpdateInstaller.SafeFileName("https://srv/evil/", "1.2.0"));
    }
}
