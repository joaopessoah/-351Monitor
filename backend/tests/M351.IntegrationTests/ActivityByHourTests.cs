using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// hourly_activity + GET /api/v1/dashboard/activity-by-hour (F6): o agregado por hora sai da
/// MESMA transação da agregação diária, em hora LOCAL do tenant, e o endpoint devolve as 24
/// horas com tempo ativo, ocioso e a média de pessoas ativas simultâneas no período.
/// </summary>
[Collection(ApiCollection.Name)]
public class ActivityByHourTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
        await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string token, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    // ------------------------------------------------------------ recorte por hora local
    [Fact]
    public async Task ActivityByHour_DistribuiIntervaloEntreHorasLocais_ECalculaMediaDePessoas()
    {
        var org = await fixture.CreateOrganizationAsync($"Hora {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-HORA-1");

        // 13:55Z → 14:03Z = 10:55 → 11:03 local (GMT-3): 5 min na hora 10, 3 min na hora 11.
        // O bloco tem 8 min de propósito: espaçamento de 600 s ou mais dispara o gap N7 e o
        // trecho viraria no_data de máquina, sem tempo ativo nenhum para distribuir.
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(13, 55), new Dictionary<string, object?> { ["process_name"] = "hora-excel.exe" }),
            f.Event("LOCK", T(14, 3)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        var day = LocalDate(T(13, 55));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/activity-by-hour?from={day}&to={day}");

        var hours = doc.RootElement.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal(24, hours.Count);
        Assert.Equal(1, doc.RootElement.GetProperty("days_with_data").GetInt32());

        var h10 = hours.Single(h => h.GetProperty("hour").GetInt32() == 10);
        var h11 = hours.Single(h => h.GetProperty("hour").GetInt32() == 11);
        Assert.Equal(5 * 60, h10.GetProperty("seconds_active").GetInt64());
        Assert.Equal(3 * 60, h11.GetProperty("seconds_active").GetInt64());

        // 300 s ÷ 3600 s ÷ 1 dia = 0,0833 pessoa ativa em média naquela hora
        Assert.Equal(0.0833, h10.GetProperty("avg_people_active").GetDouble(), 4);

        // hora vazia existe na série, com zeros (o gráfico desenha o dia inteiro)
        var h3 = hours.Single(h => h.GetProperty("hour").GetInt32() == 3);
        Assert.Equal(0, h3.GetProperty("seconds_active").GetInt64());
        Assert.Equal(0, h3.GetProperty("seconds_idle").GetInt64());
    }

    // ------------------------------------------------------------ ocioso entra no balde certo
    [Fact]
    public async Task ActivityByHour_Ociosidade_SeparadaDoTempoAtivo()
    {
        var org = await fixture.CreateOrganizationAsync($"HoraIdle {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-HORA-IDLE");

        // 15:00Z ativo, ocioso de 15:05 a 15:12 (retroativo ao último input), lock às 15:12.
        // Local (GMT-3): tudo na hora 12.
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(15, 0), new Dictionary<string, object?> { ["process_name"] = "hora-erp.exe" }),
            f.Event("IDLE_START", T(15, 7), new Dictionary<string, object?>
            {
                ["last_input_at"] = T(15, 5).UtcDateTime.ToString("o"),
            }),
            f.Event("LOCK", T(15, 12)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        var day = LocalDate(T(15, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/activity-by-hour?from={day}&to={day}");

        var h12 = doc.RootElement.GetProperty("hours").EnumerateArray()
            .Single(h => h.GetProperty("hour").GetInt32() == 12);
        Assert.Equal(5 * 60, h12.GetProperty("seconds_active").GetInt64());
        Assert.Equal(7 * 60, h12.GetProperty("seconds_idle").GetInt64());
    }

    // ------------------------------------------------------------ validação do período
    [Fact]
    public async Task ActivityByHour_RangeAcimaDe92Dias_400()
    {
        var org = await fixture.CreateOrganizationAsync($"HoraErr {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/dashboard/activity-by-hour?from=2026-01-01&to=2026-12-31", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------ período vazio: 24 horas zeradas
    [Fact]
    public async Task ActivityByHour_PeriodoSemDados_VinteQuatroHorasZeradasESemMedia()
    {
        var org = await fixture.CreateOrganizationAsync($"HoraVazio {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        var day = LocalDate(T(13, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/activity-by-hour?from={day}&to={day}");

        Assert.Equal(0, doc.RootElement.GetProperty("days_with_data").GetInt32());
        var hours = doc.RootElement.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal(24, hours.Count);
        Assert.All(hours, h =>
        {
            Assert.Equal(0, h.GetProperty("seconds_active").GetInt64());
            // sem dia com dado não há denominador: média é null, nunca zero
            Assert.Equal(JsonValueKind.Null, h.GetProperty("avg_people_active").ValueKind);
        });
    }
}
