using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Trilha de LEITURA POR TITULAR (F5) — pré-requisito do extrato de acessos do pacote DSR.
///
/// A regra: quando a leitura é recortada por UM titular, o detail da linha view_report leva
/// device_user_id. É por esse campo (não pelo target) que o relatório "Dados sobre mim" seleciona
/// os acessos do titular. Cobre as duas leituras individuais existentes:
///  - GET /dashboard/summary?device_user_id= (gravação manual no controller);
///  - GET /device-users/{id} (via AuditReadFilter, com actor_ip resolvido no filter).
///
/// E o contra-exemplo que justifica a nota do relatório: a MESMA tela sem recorte individual
/// (por dispositivo) grava view_report SEM device_user_id — numa máquina compartilhada aquele
/// acesso não identifica titular algum e por isso não pode entrar no extrato de ninguém.
/// </summary>
[Collection(ApiCollection.Name)]
public class SubjectReadAuditTests(ApiTestFixture fixture)
{
    private string Conn => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, Guid UserId, string Token)> SetupAsync()
    {
        var org = await fixture.CreateOrganizationAsync($"AudTit {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        return (client, org.Id, viewer.Id, await AuthClient.LoginAsync(client, viewer));
    }

    private async Task<Guid> SeedDeviceUserAsync(Guid tenantId, Guid deviceId, string windowsUsername)
    {
        var id = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, NULL, now() - interval '5 days', now())
            """,
            ("id", id), ("t", tenantId), ("d", deviceId),
            ("sid", $"S-1-5-21-AUD-{Guid.NewGuid():N}"[..40]), ("wu", windowsUsername));
        return id;
    }

    /// <summary>Linhas view_report do tenant com o detail cru (mais recentes primeiro).</summary>
    private async Task<List<(Guid? TargetId, string? TargetType, string Detail)>> ViewReportRowsAsync(Guid tenantId)
    {
        var rows = new List<(Guid?, string?, string)>();
        await using var connection = new Npgsql.NpgsqlConnection(Conn);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT target_id, target_type, detail::text
            FROM audit_log
            WHERE tenant_id = @t AND action = 'view_report'
            ORDER BY occurred_at DESC
            """, connection);
        command.Parameters.AddWithValue("t", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? "{}" : reader.GetString(2)));
        }

        return rows;
    }

    private static string? DeviceUserIdOf(string detailJson)
    {
        using var doc = JsonDocument.Parse(detailJson);
        return doc.RootElement.TryGetProperty("device_user_id", out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    [Fact]
    public async Task LeituraPorTitular_GravaViewReportComDeviceUserIdNoDetail()
    {
        var (client, tenantId, userId, token) = await SetupAsync();
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-AUD-TITULAR");
        var titular = await SeedDeviceUserAsync(tenantId, device.Id, "acme\\marta.reis");

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // ---- 1. dashboard/summary FILTRADO pelo titular
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from={today}&to={today}&device_user_id={titular}", token))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        // ---- 2. visão individual da pessoa
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/device-users/{titular}", token))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        var rows = await ViewReportRowsAsync(tenantId);
        Assert.Equal(2, rows.Count);

        // as DUAS levam o titular no detail: é o que o extrato de acessos consulta
        Assert.All(rows, r => Assert.Equal(titular.ToString(), DeviceUserIdOf(r.Detail)));
        // e o alvo segue a convenção existente, sem tipo novo
        Assert.All(rows, r =>
        {
            Assert.Equal("device_user", r.TargetType);
            Assert.Equal(titular, r.TargetId);
        });

        // o ator é o usuário do portal que consultou (o extrato entregue ao titular mostra o
        // NOME dele, resolvido por join em users — jamais o IP)
        Assert.Equal(2L, await TestDb.ScalarAsync<long>(Conn,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND actor_user_id = @u",
            ("t", tenantId), ("u", userId)));
    }

    [Fact]
    public async Task LeituraPorDispositivo_GravaViewReportSemDeviceUserId()
    {
        var (client, tenantId, _, token) = await SetupAsync();
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-AUD-DEVICE");

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // summary filtrado por DISPOSITIVO: dado pessoal, mas de quem usou a máquina — não
        // identifica titular individual e por isso não entra no extrato de ninguém
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from={today}&to={today}&device_id={device.Id}", token))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        var row = Assert.Single(await ViewReportRowsAsync(tenantId));
        Assert.Equal("device", row.TargetType);
        Assert.Null(DeviceUserIdOf(row.Detail));
    }

    [Fact]
    public async Task LeituraCrossTenant_Naoregistra_NemNaTrilhaDeA_NemNaDeB()
    {
        var (clientA, tenantA, _, tokenA) = await SetupAsync();
        var (_, tenantB, _, _) = await SetupAsync();
        var deviceB = await fixture.CreateDeviceAsync(tenantB, "NB-AUD-ISO-B");
        var titularB = await SeedDeviceUserAsync(tenantB, deviceB.Id, "acme\\alvo.b");

        // 404 não registra acesso (garantia do AuditReadFilter: só grava em 2xx)
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/device-users/{titularB}", tokenA))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await clientA.SendAsync(request)).StatusCode);
        }

        Assert.Empty(await ViewReportRowsAsync(tenantA));
        Assert.Empty(await ViewReportRowsAsync(tenantB));
    }
}
