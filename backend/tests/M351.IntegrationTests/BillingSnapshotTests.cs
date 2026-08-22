using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Billing;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// Congelamento mensal do sinal de cobrança (F5): fecha o caveat documentado no
/// BillingController (arquivar device reescrevia meses passados, risco real de
/// subfaturamento e disputa no billing manual). Mês fechado passa a vir de
/// device_billing_months; o mês corrente segue calculado ao vivo.
/// </summary>
[Collection(ApiCollection.Name)]
public class BillingSnapshotTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private static async Task<HttpResponseMessage> GetBillableAsync(HttpClient client, string token, string month)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/billing/billable-devices?month={month}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Congelamento_MesFechado_NaoMudaAoArquivarDepois()
    {
        var org = await fixture.CreateOrganizationAsync("Org Congelamento");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var ownerToken = await AuthClient.LoginAsync(client, owner);

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-COBRAVEL");

        // sinal de uso no mês PASSADO: keep-alive (last_seen_at no mês anterior)
        var agora = DateTimeOffset.UtcNow;
        var mesPassado = new DateOnly(agora.Year, agora.Month, 1).AddMonths(-1);
        var meioDoMesPassado = new DateTimeOffset(
            mesPassado.Year, mesPassado.Month, 15, 12, 0, 0, TimeSpan.Zero);
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET last_seen_at = @seen WHERE id = @id",
            ("id", device.Id), ("seen", meioDoMesPassado));

        // congela os meses fechados
        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new BillingSnapshotService(dataSource, NullLogger<BillingSnapshotService>.Instance);
        await service.RunOnceAsync(agora);

        var congelado = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM device_billing_months WHERE tenant_id = @t AND month = @m",
            ("t", org.Id), ("m", mesPassado));
        Assert.Equal(1, congelado);

        // o relatório do mês fechado vem do snapshot e se declara congelado
        var mesTexto = mesPassado.ToString("yyyy-MM");
        var antes = await GetBillableAsync(client, ownerToken, mesTexto);
        Assert.Equal(HttpStatusCode.OK, antes.StatusCode);
        using (var body = JsonDocument.Parse(await antes.Content.ReadAsStringAsync()))
        {
            Assert.True(body.RootElement.GetProperty("frozen").GetBoolean());
            Assert.Equal(1, body.RootElement.GetProperty("device_count").GetInt32());
            Assert.Contains("FECHADO", body.RootElement.GetProperty("criteria").GetString());
        }

        // ARQUIVAR o device agora NÃO pode remover o mês passado (era o bug)
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET status = 'archived' WHERE id = @id", ("id", device.Id));

        var depois = await GetBillableAsync(client, ownerToken, mesTexto);
        using (var body = JsonDocument.Parse(await depois.Content.ReadAsStringAsync()))
        {
            Assert.True(body.RootElement.GetProperty("frozen").GetBoolean());
            Assert.Equal(1, body.RootElement.GetProperty("device_count").GetInt32());
        }
    }

    [Fact]
    public async Task Congelamento_Idempotente_NaoDuplicaLinhas()
    {
        var org = await fixture.CreateOrganizationAsync("Org Congelamento Idem");
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-IDEM");

        var agora = DateTimeOffset.UtcNow;
        var mesPassado = new DateOnly(agora.Year, agora.Month, 1).AddMonths(-1);
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET last_seen_at = @seen WHERE id = @id",
            ("id", device.Id),
            ("seen", new DateTimeOffset(mesPassado.Year, mesPassado.Month, 10, 9, 0, 0, TimeSpan.Zero)));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new BillingSnapshotService(dataSource, NullLogger<BillingSnapshotService>.Instance);

        await service.RunOnceAsync(agora);
        await service.RunOnceAsync(agora.AddHours(24));
        await service.RunOnceAsync(agora.AddHours(48));

        var linhas = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM device_billing_months WHERE tenant_id = @t", ("t", org.Id));
        Assert.Equal(1, linhas);
    }

    [Fact]
    public async Task MesCorrente_SegueAoVivo_NaoCongelado()
    {
        var org = await fixture.CreateOrganizationAsync("Org Mês Corrente");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var ownerToken = await AuthClient.LoginAsync(client, owner);

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-CORRENTE");
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET last_seen_at = now() WHERE id = @id", ("id", device.Id));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new BillingSnapshotService(dataSource, NullLogger<BillingSnapshotService>.Instance);
        await service.RunOnceAsync(DateTimeOffset.UtcNow);

        var agora = DateTimeOffset.UtcNow;
        var response = await GetBillableAsync(client, ownerToken, $"{agora.Year:0000}-{agora.Month:00}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("frozen").GetBoolean());
        Assert.Contains("Atenção", body.RootElement.GetProperty("criteria").GetString());
    }
}
