using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// F4.4 — saúde de agentes no backend: materialização do AGENT_TAMPER em devices (monotônica,
/// igual ao notice_acked_at) e o DeviceResponse estendido (notice_acked_at, last_tamper_*,
/// agent_outdated). agent_outdated é comparado por SEMVER no backend contra o min_version do
/// release current do canal 'stable' (F4.2). Isolamento por tenant preservado.
/// </summary>
[Collection(ApiCollection.Name)]
public class DeviceHealthTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, EnrolledDevice Device, Guid TenantId)> EnrolledSetupAsync(string orgName)
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync(orgName);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);
        return (client, device, org.Id);
    }

    /// <summary>
    /// agent_releases é GLOBAL (sem tenant) e o banco de teste é compartilhado pela coleção, então
    /// limpamos o current explicitamente para o cenário "sem release publicado".
    /// </summary>
    private Task ClearCurrentStableReleaseAsync() => TestDb.ExecuteAsync(Cs,
        "UPDATE agent_releases SET is_current = false WHERE channel = 'stable' AND is_current");

    /// <summary>Publica um release 'stable' current com o min_version dado (a tabela é global, sem tenant).</summary>
    private async Task PublishStableReleaseAsync(string version, string minVersion)
    {
        await ClearCurrentStableReleaseAsync();
        // idempotente por (channel, version): o banco é compartilhado pela coleção e várias provas
        // podem publicar a mesma versão; o que importa é qual fica is_current com qual min_version
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO agent_releases (id, channel, version, url, sha256, min_version, file_name, is_current)
            VALUES (@id, 'stable', @version, @url, @sha, @min, @file, true)
            ON CONFLICT (channel, version) DO UPDATE SET
              min_version = EXCLUDED.min_version, is_current = true
            """,
            ("id", Guid.NewGuid()),
            ("version", version),
            ("url", $"/api/v1/agent/releases/MonitorAgent-{version}.msi"),
            ("sha", new string('a', 64)),
            ("min", minVersion),
            ("file", $"MonitorAgent-{version}.msi"));
    }

    private Dictionary<string, object?> TamperEvent(EventFactory factory, DateTimeOffset at, string reason) =>
        factory.Event("AGENT_TAMPER", at, new Dictionary<string, object?> { ["reason"] = reason });

    // ------------------------------------------------------------ materialização do tamper
    [Fact]
    public async Task AgentTamper_NoLote_MaterializaLastTamperAtEReason()
    {
        var (client, device, _) = await EnrolledSetupAsync("Saude Tamper");
        var factory = new EventFactory();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [TamperEvent(factory, at, "helper_killed")]);
        (await AgentClient.ReadAckAsync(response)).Dispose();

        var row = await TestDb.RowAsync(Cs,
            "SELECT last_tamper_at, last_tamper_reason FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.NotNull(row!["last_tamper_at"]);
        Assert.Equal("helper_killed", row["last_tamper_reason"]);
    }

    [Fact]
    public async Task AgentTamper_Monotonico_TamperAntigoNaoSobrescreveRecente()
    {
        var (client, device, _) = await EnrolledSetupAsync("Saude Tamper Mono");
        var factory = new EventFactory();
        var recente = DateTimeOffset.UtcNow.AddMinutes(-2);
        var antigo = DateTimeOffset.UtcNow.AddDays(-3);

        // primeiro chega o tamper RECENTE (repetido)
        var r1 = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [TamperEvent(factory, recente, "helper_killed_repeatedly")]);
        (await AgentClient.ReadAckAsync(r1)).Dispose();

        // depois chega um tamper mais ANTIGO (lote fora de ordem): não pode regredir nem trocar o reason
        var r2 = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [TamperEvent(factory, antigo, "pipe_denied")]);
        (await AgentClient.ReadAckAsync(r2)).Dispose();

        var row = await TestDb.RowAsync(Cs,
            "SELECT last_tamper_at, last_tamper_reason FROM devices WHERE id = @d", ("d", device.DeviceId));
        // Npgsql devolve timestamptz como DateTime (UTC) no GetValue cru do helper
        var stored = (DateTime)row!["last_tamper_at"]!;
        Assert.Equal("helper_killed_repeatedly", row["last_tamper_reason"]);
        Assert.True(stored > antigo.AddDays(1).UtcDateTime, "last_tamper_at regrediu para o tamper antigo");
    }

    [Fact]
    public async Task AgentTamper_MaisRecenteNoMesmoLote_VenceEDefineOReason()
    {
        var (client, device, _) = await EnrolledSetupAsync("Saude Tamper Lote");
        var factory = new EventFactory();
        var antigo = DateTimeOffset.UtcNow.AddMinutes(-10);
        var recente = DateTimeOffset.UtcNow.AddMinutes(-1);

        // dois tampers no MESMO lote: o reason materializado é o do mais recente (por occurred_at)
        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            TamperEvent(factory, antigo, "pipe_denied"),
            TamperEvent(factory, recente, "helper_killed"),
        ]);
        (await AgentClient.ReadAckAsync(response)).Dispose();

        var row = await TestDb.RowAsync(Cs,
            "SELECT last_tamper_reason FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.Equal("helper_killed", row!["last_tamper_reason"]);
    }

    // ------------------------------------------------------------ DeviceResponse estendido
    [Fact]
    public async Task DeviceResponse_TrazNoticeAck_TamperEAgentOutdated()
    {
        var (client, device, tenantId) = await EnrolledSetupAsync("Saude Response");
        var admin = await fixture.CreateUserAsync(tenantId, UserRole.Admin, mfaEnabled: true);
        var adminToken = await AuthClient.LoginAsync(client, admin);
        var factory = new EventFactory();

        // NOTICE_ACK + AGENT_TAMPER no pipeline; release current com min_version > agent_version (1.0.0)
        var ackAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var tamperAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("NOTICE_ACK", ackAt),
            TamperEvent(factory, tamperAt, "pipe_denied"),
        ]);
        (await AgentClient.ReadAckAsync(response)).Dispose();
        await PublishStableReleaseAsync("1.2.0", "1.1.0");

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/devices/{device.DeviceId}", adminToken);
        var get = await client.SendAsync(request);
        var body = await get.Content.ReadAsStringAsync();
        Assert.True(get.StatusCode == HttpStatusCode.OK, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("notice_acked_at").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("last_tamper_at").ValueKind);
        Assert.Equal("pipe_denied", root.GetProperty("last_tamper_reason").GetString());
        Assert.True(root.GetProperty("agent_outdated").GetBoolean(), "agent 1.0.0 < min 1.1.0 deveria ser outdated");
    }

    [Fact]
    public async Task AgentOutdated_FalseSemRelease_FalseQuandoIgualOuMaior()
    {
        var (client, device, tenantId) = await EnrolledSetupAsync("Saude Outdated");
        var admin = await fixture.CreateUserAsync(tenantId, UserRole.Admin, mfaEnabled: true);
        var adminToken = await AuthClient.LoginAsync(client, admin);

        async Task<bool> OutdatedAsync()
        {
            using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/devices/{device.DeviceId}", adminToken);
            var get = await client.SendAsync(request);
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("agent_outdated").GetBoolean();
        }

        // sem release publicado: nunca outdated (o device foi enrollado com agent_version 1.0.0)
        await ClearCurrentStableReleaseAsync();
        Assert.False(await OutdatedAsync());

        // min_version IGUAL ao agent_version: não é outdated (outdated = estritamente menor)
        await PublishStableReleaseAsync("1.0.0", "1.0.0");
        Assert.False(await OutdatedAsync());

        // min_version MENOR que o agent_version: não é outdated
        await PublishStableReleaseAsync("0.9.0", "0.9.0");
        Assert.False(await OutdatedAsync());

        // semver numérico (não-lexicográfico): 1.0.0 < 1.10.0 -> outdated
        await PublishStableReleaseAsync("1.11.0", "1.10.0");
        Assert.True(await OutdatedAsync());
    }

    [Fact]
    public async Task ListaDevices_TrazCamposDeSaude()
    {
        var (client, device, tenantId) = await EnrolledSetupAsync("Saude Lista");
        var admin = await fixture.CreateUserAsync(tenantId, UserRole.Admin, mfaEnabled: true);
        var adminToken = await AuthClient.LoginAsync(client, admin);
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [TamperEvent(factory, DateTimeOffset.UtcNow.AddMinutes(-1), "helper_killed")]);
        (await AgentClient.ReadAckAsync(response)).Dispose();

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/devices?page_size=100", adminToken);
        var get = await client.SendAsync(request);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(d => d.GetProperty("id").GetGuid() == device.DeviceId);

        Assert.True(item.TryGetProperty("notice_acked_at", out _));
        Assert.Equal("helper_killed", item.GetProperty("last_tamper_reason").GetString());
        Assert.True(item.TryGetProperty("agent_outdated", out _));
    }

    // ------------------------------------------------------------ isolamento por tenant
    [Fact]
    public async Task Isolamento_DeviceDeOutroTenantNaoApareceNemEhVisivel()
    {
        var (client, deviceA, tenantA) = await EnrolledSetupAsync("Saude TenantA");
        var (_, deviceB, _) = await EnrolledSetupAsync("Saude TenantB");

        var adminA = await fixture.CreateUserAsync(tenantA, UserRole.Admin, mfaEnabled: true);
        var adminToken = await AuthClient.LoginAsync(client, adminA);

        // tamper no device do tenant B (não deve vazar para o tenant A)
        var factoryB = new EventFactory();
        var rB = await AgentClient.SendBatchAsync(client, deviceB.DeviceToken,
            [TamperEvent(factoryB, DateTimeOffset.UtcNow.AddMinutes(-1), "pipe_denied")]);
        (await AgentClient.ReadAckAsync(rB)).Dispose();

        // lista do tenant A não contém o device de B
        using var listReq = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/devices?page_size=100", adminToken);
        var list = await client.SendAsync(listReq);
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var ids = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(d => d.GetProperty("id").GetGuid()).ToList();
            Assert.Contains(deviceA.DeviceId, ids);
            Assert.DoesNotContain(deviceB.DeviceId, ids);
        }

        // GET direto do device de B pelo admin de A -> 404 (filtro de tenant, nunca 403)
        using var getReq = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/devices/{deviceB.DeviceId}", adminToken);
        var getB = await client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.NotFound, getB.StatusCode);
    }
}
