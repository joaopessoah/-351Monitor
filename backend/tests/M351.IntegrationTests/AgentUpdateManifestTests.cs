using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using M351.Api.Backoffice;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// F4.2 — auto-update de canal único (Seção 6.7 + tabela 7.4 l.815). Exercita o caminho REAL:
/// a CLI backoffice publish-agent-release/rollback-agent-release escreve em agent_releases e
/// copia o MSI para Releases:Directory; os dois GET (DEVICE TOKEN) leem dali.
///  - manifesto reflete o release current; 204 sem release publicado;
///  - auth: device token é a ÚNICA auth válida (sem token e JWT de portal -> 401);
///  - rollback: publicar 1.1.0 e depois marcar 1.0.0 como current -> manifesto reflete 1.0.0;
///  - download serve os bytes do arquivo e 404 para inexistente;
///  - sha256 do manifesto bate com o hash do arquivo publicado.
/// O "MSI" é um arquivo pequeno de fixture (não baixamos/instalamos MSI real).
/// </summary>
[Collection(ApiCollection.Name)]
public class AgentUpdateManifestTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    // ------------------------------------------------------------ helpers
    private async Task<string> NewDeviceTokenAsync(HttpClient client)
    {
        var org = await fixture.CreateOrganizationAsync($"Org Update {Guid.NewGuid():N}"[..28]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);
        return device.DeviceToken;
    }

    private static HttpRequestMessage DeviceGet(string url, string deviceToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        return request;
    }

    /// <summary>Cria um "MSI" de fixture e publica via CLI real; devolve o caminho e o sha256 esperado.</summary>
    private async Task<(string FileName, string Sha256)> PublishAsync(
        string version, string minVersion, byte[]? content = null)
    {
        content ??= System.Text.Encoding.UTF8.GetBytes($"FAKE-MSI-{version}-{Guid.NewGuid():N}");
        var fileName = $"MonitorAgent-{version}.msi";
        // subdiretório único para o NOME do arquivo ficar limpo (o file_name = Path.GetFileName)
        var tempDir = Path.Combine(Path.GetTempPath(), $"m351-pub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        await File.WriteAllBytesAsync(tempPath, content);

        var expectedSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var exit = await PublishAgentReleaseCommand.RunAsync(fixture.Services,
            ["--version", version, "--file", tempPath, "--min-version", minVersion]);
        Assert.Equal(0, exit);

        Directory.Delete(tempDir, recursive: true);
        return (fileName, expectedSha);
    }

    // ------------------------------------------------------------ testes
    [Fact]
    public async Task SemReleasePublicado_Retorna204()
    {
        // canal sem release current: a CLI nunca rodou para um canal isolado deste teste —
        // garantimos o estado limpando o canal antes (a suíte compartilha o banco)
        await TestDb.ExecuteAsync(Cs, "DELETE FROM agent_releases WHERE channel = 'stable'");

        var client = fixture.CreateApiClient();
        var token = await NewDeviceTokenAsync(client);

        using var request = DeviceGet("/api/v1/agent/update-manifest?current=1.0.0", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ManifestoRetornaReleaseCurrent_ComSha256DoArquivo()
    {
        var (fileName, expectedSha) = await PublishAsync("2.0.0", minVersion: "1.5.0");

        var client = fixture.CreateApiClient();
        var token = await NewDeviceTokenAsync(client);

        using var request = DeviceGet("/api/v1/agent/update-manifest?current=1.0.3", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("2.0.0", root.GetProperty("version").GetString());
        Assert.Equal("1.5.0", root.GetProperty("min_version").GetString());
        Assert.Equal(expectedSha, root.GetProperty("sha256").GetString());
        Assert.EndsWith($"/api/v1/agent/releases/{fileName}", root.GetProperty("url").GetString());
        // hex64 minúsculo
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("sha256").GetString()!);
    }

    [Fact]
    public async Task SemDeviceToken_Retorna401()
    {
        // publica algo para garantir que o 401 vem da AUTH, não do estado vazio
        await PublishAsync("3.0.0", minVersion: "1.0.0");
        var client = fixture.CreateApiClient();

        var response = await client.GetAsync("/api/v1/agent/update-manifest?current=1.0.0");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ComJwtDoPortal_Retorna401()
    {
        await PublishAsync("3.1.0", minVersion: "1.0.0");
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Portal vs Manifest");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var accessToken = await AuthClient.LoginAsync(client, owner);

        // JWT de portal NÃO passa na PolicyDevice (device token é a única auth válida)
        using var request = DeviceGet("/api/v1/agent/update-manifest?current=1.0.0", accessToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rollback_ManifestoVoltaParaVersaoAnterior()
    {
        // publica 1.0.0 (current) e depois 1.1.0 (vira current)
        await PublishAsync("1.0.0", minVersion: "1.0.0");
        await PublishAsync("1.1.0", minVersion: "1.0.0");

        var client = fixture.CreateApiClient();
        var token = await NewDeviceTokenAsync(client);

        using (var beforeReq = DeviceGet("/api/v1/agent/update-manifest", token))
        {
            var before = await client.SendAsync(beforeReq);
            using var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
            Assert.Equal("1.1.0", beforeDoc.RootElement.GetProperty("version").GetString());
        }

        // rollback para 1.0.0 (move is_current — sem redeploy)
        var exit = await RollbackAgentReleaseCommand.RunAsync(fixture.Services, ["--version", "1.0.0"]);
        Assert.Equal(0, exit);

        using var afterReq = DeviceGet("/api/v1/agent/update-manifest", token);
        var after = await client.SendAsync(afterReq);
        using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal("1.0.0", afterDoc.RootElement.GetProperty("version").GetString());

        // no máximo um current por canal (índice parcial único)
        var currents = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM agent_releases WHERE channel = 'stable' AND is_current");
        Assert.Equal(1, currents);
    }

    [Fact]
    public async Task Download_ServeOsBytesDoArquivo()
    {
        var content = System.Text.Encoding.UTF8.GetBytes($"FAKE-MSI-DL-{Guid.NewGuid():N}");
        var (fileName, _) = await PublishAsync("4.0.0", minVersion: "1.0.0", content: content);

        var client = fixture.CreateApiClient();
        var token = await NewDeviceTokenAsync(client);

        using var request = DeviceGet($"/api/v1/agent/releases/{fileName}", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, bytes);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Download_Inexistente_Retorna404()
    {
        var client = fixture.CreateApiClient();
        var token = await NewDeviceTokenAsync(client);

        using var request = DeviceGet("/api/v1/agent/releases/MonitorAgent-9.9.9.msi", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_SemDeviceToken_Retorna401()
    {
        var (fileName, _) = await PublishAsync("4.1.0", minVersion: "1.0.0");
        var client = fixture.CreateApiClient();

        var response = await client.GetAsync($"/api/v1/agent/releases/{fileName}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Publish_AuditaComTenantSentinela()
    {
        await PublishAsync("5.0.0", minVersion: "1.0.0");

        // a trilha de publicação vai sob o tenant-sentinela Guid.Empty (operação global)
        var audited = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM audit_log WHERE action = 'publish_agent_release' AND tenant_id = @t",
            ("t", Guid.Empty));
        Assert.True(audited >= 1);
    }
}
