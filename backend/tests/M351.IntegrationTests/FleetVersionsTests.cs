using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Vigilância de rollout: ingestão do UPDATE_FAILED (19º tipo da tabela 5.3) materializado em
/// devices, no mesmo desenho monotônico do AGENT_TAMPER, e a leitura server-side dela em
/// GET /devices/version-summary (distribuição de versões da frota + falhas recentes).
///
/// O que estes testes protegem: antes disso a única vigilância de rollout era o contador
/// "desatualizados" do health-summary, que diz QUANTOS ficaram para trás mas não em qual versão
/// pararam nem em que etapa a atualização emperrou.
/// </summary>
[Collection(ApiCollection.Name)]
public class FleetVersionsTests(ApiTestFixture fixture)
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

    private static Dictionary<string, object?> UpdateFailedEvent(
        EventFactory factory, DateTimeOffset at, string reason, string toVersion = "1.1.0") =>
        factory.Event("UPDATE_FAILED", at, new Dictionary<string, object?>
        {
            ["from_version"] = "1.0.0", ["to_version"] = toVersion, ["reason"] = reason,
        }, windowsSid: null, windowsUser: null, sessionId: null);

    /// <summary>agent_releases é GLOBAL (sem tenant) e o banco é compartilhado pela coleção.</summary>
    private Task ClearCurrentStableReleaseAsync() => TestDb.ExecuteAsync(Cs,
        "UPDATE agent_releases SET is_current = false WHERE channel = 'stable' AND is_current");

    private async Task PublishStableReleaseAsync(string version, string minVersion)
    {
        await ClearCurrentStableReleaseAsync();
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

    // ------------------------------------------------------------ materialização do UPDATE_FAILED

    [Fact]
    public async Task UpdateFailed_NoLote_MaterializaMotivoEVersaoAlvo()
    {
        var (client, device, _) = await EnrolledSetupAsync("Rollout Materializa");
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [UpdateFailedEvent(factory, DateTimeOffset.UtcNow.AddMinutes(-5), "signature", "1.2.0")]);
        (await AgentClient.ReadAckAsync(response)).Dispose();

        var row = await TestDb.RowAsync(Cs,
            """
            SELECT last_update_failure_at, last_update_failure_reason, last_update_target_version
            FROM devices WHERE id = @d
            """,
            ("d", device.DeviceId));
        Assert.NotNull(row!["last_update_failure_at"]);
        Assert.Equal("signature", row["last_update_failure_reason"]);
        Assert.Equal("1.2.0", row["last_update_target_version"]);
    }

    [Fact]
    public async Task UpdateFailed_Monotonico_LoteAtrasadoNaoRegrideAFalhaConhecida()
    {
        var (client, device, _) = await EnrolledSetupAsync("Rollout Monotonico");
        var factory = new EventFactory();
        var recente = DateTimeOffset.UtcNow.AddMinutes(-2);
        var antigo = DateTimeOffset.UtcNow.AddDays(-3);

        var r1 = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [UpdateFailedEvent(factory, recente, "signature", "1.2.0")]);
        (await AgentClient.ReadAckAsync(r1)).Dispose();

        // fila drenando depois de dias offline: o evento antigo não pode sobrescrever o recente
        var r2 = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [UpdateFailedEvent(factory, antigo, "download", "1.1.0")]);
        (await AgentClient.ReadAckAsync(r2)).Dispose();

        var row = await TestDb.RowAsync(Cs,
            """
            SELECT last_update_failure_at, last_update_failure_reason, last_update_target_version
            FROM devices WHERE id = @d
            """,
            ("d", device.DeviceId));
        var stored = (DateTime)row!["last_update_failure_at"]!;
        Assert.Equal("signature", row["last_update_failure_reason"]);
        Assert.Equal("1.2.0", row["last_update_target_version"]);
        Assert.True(stored > antigo.AddDays(1).UtcDateTime, "last_update_failure_at regrediu para a falha antiga");
    }

    /// <summary>
    /// Motivo fora da lista canônica (agente adulterado ou de versão futura) não é materializado,
    /// mesma régua do AGENT_TAMPER. O evento segue persistido em raw_events.
    /// </summary>
    [Fact]
    public async Task UpdateFailed_ComMotivoDesconhecido_NaoMaterializa_MasPersisteOEvento()
    {
        var (client, device, _) = await EnrolledSetupAsync("Rollout Motivo Invalido");
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [UpdateFailedEvent(factory, DateTimeOffset.UtcNow.AddMinutes(-1), "motivo_inventado")]);
        (await AgentClient.ReadAckAsync(response)).Dispose();

        var row = await TestDb.RowAsync(Cs,
            "SELECT last_update_failure_at, last_update_failure_reason FROM devices WHERE id = @d",
            ("d", device.DeviceId));
        Assert.Null(row!["last_update_failure_at"]);
        Assert.Null(row["last_update_failure_reason"]);

        var persisted = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d AND event_type = 'UPDATE_FAILED'",
            ("d", device.DeviceId));
        Assert.Equal(1, persisted);
    }

    // ------------------------------------------------------------ GET /devices/version-summary

    [Fact]
    public async Task VersionSummary_AgrupaAFrotaPorVersao_ComOutdatedDoBackend()
    {
        var org = await fixture.CreateOrganizationAsync("Rollout Distribuicao");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var ownerToken = await AuthClient.LoginAsync(client, owner);

        // 2 na 1.0.0 (abaixo do min), 1 na 1.2.0 (em dia) e 1 sem versão reportada
        foreach (var (hostname, version) in new[]
                 {
                     ("NB-A", "1.0.0"), ("NB-B", "1.0.0"), ("NB-C", "1.2.0"), ("NB-D", (string?)null),
                 })
        {
            var d = await fixture.CreateDeviceAsync(org.Id, hostname);
            await TestDb.ExecuteAsync(Cs,
                "UPDATE devices SET agent_version = @v::text WHERE id = @id",
                ("id", d.Id), ("v", (object?)version ?? DBNull.Value));
        }

        await PublishStableReleaseAsync("1.2.0", "1.1.0");

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/devices/version-summary", ownerToken);
        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, payload);

        using var body = JsonDocument.Parse(payload);
        var root = body.RootElement;
        Assert.Equal(4, root.GetProperty("active_devices").GetInt32());
        Assert.Equal("1.2.0", root.GetProperty("current_version").GetString());
        Assert.Equal("1.1.0", root.GetProperty("min_version").GetString());

        var versions = root.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(3, versions.Count);
        // ordenado da versão mais nova para a mais velha, com a desconhecida por último
        Assert.Equal("1.2.0", versions[0].GetProperty("version").GetString());
        Assert.Equal(1, versions[0].GetProperty("count").GetInt32());
        Assert.False(versions[0].GetProperty("outdated").GetBoolean());
        Assert.Equal("1.0.0", versions[1].GetProperty("version").GetString());
        Assert.Equal(2, versions[1].GetProperty("count").GetInt32());
        Assert.True(versions[1].GetProperty("outdated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, versions[2].GetProperty("version").ValueKind);

        // a soma das contagens fecha com o total de ativos: nenhuma máquina some da leitura
        Assert.Equal(4, versions.Sum(v => v.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task VersionSummary_DestacaFalhasRecentes_EIgnoraAsAntigas()
    {
        var (client, device, tenantId) = await EnrolledSetupAsync("Rollout Falhas Recentes");
        var owner = await fixture.CreateUserAsync(tenantId, UserRole.Owner, mfaEnabled: true);
        var ownerToken = await AuthClient.LoginAsync(client, owner);
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [UpdateFailedEvent(factory, DateTimeOffset.UtcNow.AddHours(-2), "hash", "1.3.0")]);
        (await AgentClient.ReadAckAsync(response)).Dispose();

        // um segundo device com falha VELHA (fora da janela de 7 dias): não entra no destaque
        var antigo = await fixture.CreateDeviceAsync(tenantId, "NB-FALHA-VELHA");
        await TestDb.ExecuteAsync(Cs, """
            UPDATE devices
            SET last_update_failure_at = now() - interval '30 days',
                last_update_failure_reason = 'download',
                last_update_target_version = '1.3.0'
            WHERE id = @id
            """, ("id", antigo.Id));

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/devices/version-summary", ownerToken);
        var get = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("update_failures").GetInt32());
        Assert.Equal(7, root.GetProperty("update_failure_window_days").GetInt32());

        var falha = Assert.Single(root.GetProperty("recent_failures").EnumerateArray().ToList());
        Assert.Equal(device.DeviceId, falha.GetProperty("device_id").GetGuid());
        Assert.Equal("hash", falha.GetProperty("reason").GetString());
        Assert.Equal("1.3.0", falha.GetProperty("target_version").GetString());
    }

    [Fact]
    public async Task VersionSummary_NaoVazaDeviceDeOutroTenant()
    {
        var orgA = await fixture.CreateOrganizationAsync("Rollout Tenant A");
        var orgB = await fixture.CreateOrganizationAsync("Rollout Tenant B");
        var ownerA = await fixture.CreateUserAsync(orgA.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var tokenA = await AuthClient.LoginAsync(client, ownerA);

        var deviceA = await fixture.CreateDeviceAsync(orgA.Id, "NB-TENANT-A");
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET agent_version = '1.0.0' WHERE id = @id", ("id", deviceA.Id));

        var deviceB = await fixture.CreateDeviceAsync(orgB.Id, "NB-TENANT-B");
        await TestDb.ExecuteAsync(Cs, """
            UPDATE devices
            SET agent_version = '9.9.9', last_update_failure_at = now(),
                last_update_failure_reason = 'install', last_update_target_version = '9.9.9'
            WHERE id = @id
            """, ("id", deviceB.Id));

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/devices/version-summary", tokenA);
        var get = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("active_devices").GetInt32());
        Assert.Equal(0, root.GetProperty("update_failures").GetInt32());
        var versoes = root.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetProperty("version").GetString()).ToList();
        Assert.Equal(["1.0.0"], versoes);
    }

    [Fact]
    public async Task VersionSummary_ExigeAutenticacao()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync("/api/v1/devices/version-summary");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
