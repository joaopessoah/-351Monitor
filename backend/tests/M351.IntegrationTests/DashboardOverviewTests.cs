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
/// GET /api/v1/dashboard/overview (F6): os KPIs da Visão Geral numa chamada, com o ÍNDICE DE
/// PRODUTIVIDADE e a COBERTURA DA CLASSIFICAÇÃO calculados no SERVIDOR — fonte única da
/// fórmula (decisão 4 do spec de 07/09/2026). Cobre os seis baldes, a série por dia, o período
/// anterior de mesma duração e a régua de "sem denominador devolve null, nunca zero".
/// </summary>
[Collection(ApiCollection.Name)]
public class DashboardOverviewTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

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

    /// <summary>Cria a categoria com a classificação pedida e mapeia o app do catálogo nela.</summary>
    private async Task MapAppAsync(Guid tenantId, string processName, int classification)
    {
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, @n, @cls)",
            ("c", categoryId), ("t", tenantId), ("n", $"Cat{classification}-{processName}"), ("cls", classification));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = @p
            ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
            """,
            ("t", tenantId), ("c", categoryId), ("p", processName));
    }

    // ------------------------------------------------------------ índice e cobertura
    [Fact]
    public async Task Overview_IndiceECobertura_CalculadosNoServidor()
    {
        var org = await fixture.CreateOrganizationAsync($"Ovw {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-OVW-1");

        // Eventos a cada 5 min: espaçamento de 600 s ou mais dispararia o gap N7 e o trecho
        // viraria no_data. Resultado: 900 s produtivos (erp), 300 s improdutivos (zap) e
        // 300 s sem classificação (misterio, que ninguém mapeou).
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 0), new Dictionary<string, object?> { ["process_name"] = "ovw-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 5), new Dictionary<string, object?> { ["process_name"] = "ovw-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 10), new Dictionary<string, object?> { ["process_name"] = "ovw-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 15), new Dictionary<string, object?> { ["process_name"] = "ovw-zap.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 20), new Dictionary<string, object?> { ["process_name"] = "ovw-misterio.exe" }),
            f.Event("LOCK", T(12, 25)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        await MapAppAsync(org.Id, "ovw-erp.exe", 1);
        await MapAppAsync(org.Id, "ovw-zap.exe", -1);
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(org.Id);
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={day}&to={day}&compare=true");

        var totals = doc.RootElement.GetProperty("totals");
        Assert.Equal(25 * 60, totals.GetProperty("seconds_active").GetInt64());
        Assert.Equal(15 * 60, totals.GetProperty("seconds_work_related").GetInt64());
        Assert.Equal(5 * 60, totals.GetProperty("seconds_not_work_related").GetInt64());
        Assert.Equal(5 * 60, totals.GetProperty("seconds_unclassified").GetInt64());
        Assert.Equal(0, totals.GetProperty("seconds_neutral").GetInt64());

        // índice = 900 / (900 + 0 + 300) = 0,75 · cobertura = (1500 − 300) / 1500 = 0,8
        Assert.Equal(0.75, totals.GetProperty("productivity_index").GetDouble(), 4);
        Assert.Equal(0.8, totals.GetProperty("classification_coverage").GetDouble(), 4);

        Assert.Equal(1, totals.GetProperty("device_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("person_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("person_days").GetInt32());

        // período resolvido e série por dia
        Assert.Equal(day, doc.RootElement.GetProperty("period").GetProperty("from").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("period").GetProperty("days").GetInt32());
        var days = doc.RootElement.GetProperty("days").EnumerateArray().ToList();
        var único = Assert.Single(days);
        Assert.Equal(day, único.GetProperty("date").GetString());
        Assert.Equal(15 * 60, único.GetProperty("seconds_work_related").GetInt64());

        // período anterior existe no contrato mesmo sem dado, com os indicadores em null
        var previous = doc.RootElement.GetProperty("previous");
        Assert.Equal(0, previous.GetProperty("seconds_active").GetInt64());
        Assert.Equal(JsonValueKind.Null, previous.GetProperty("productivity_index").ValueKind);
        Assert.Equal(JsonValueKind.Null, previous.GetProperty("classification_coverage").ValueKind);
    }

    // ------------------------------------------------------------ comparação de mesma duração
    [Fact]
    public async Task Overview_Compare_UsaPeriodoAnteriorDeMesmaDuracao()
    {
        var org = await fixture.CreateOrganizationAsync($"OvwCmp {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-OVW-CMP");

        // dia -1 (anterior): 5 min ativos · dia 0 (corrente): 10 min ativos
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 0), new Dictionary<string, object?> { ["process_name"] = "ovw-cmp.exe" }),
            f.Event("LOCK", T(-24 + 12, 5)),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 0), new Dictionary<string, object?> { ["process_name"] = "ovw-cmp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 5), new Dictionary<string, object?> { ["process_name"] = "ovw-cmp.exe" }),
            f.Event("LOCK", T(12, 10)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={day}&to={day}&compare=true");

        Assert.Equal(10 * 60, doc.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
        Assert.Equal(5 * 60, doc.RootElement.GetProperty("previous").GetProperty("seconds_active").GetInt64());
    }

    // ------------------------------------------------------------ sem compare, sem dado
    [Fact]
    public async Task Overview_SemCompare_NaoTrazPeriodoAnterior_EIndicadoresNull()
    {
        var org = await fixture.CreateOrganizationAsync($"OvwNC {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={day}&to={day}");

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("previous").ValueKind);
        var totals = doc.RootElement.GetProperty("totals");
        Assert.Equal(0, totals.GetProperty("seconds_on").GetInt64());
        // sem denominador não há índice nem cobertura: null, nunca zero
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("productivity_index").ValueKind);
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("classification_coverage").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("days").EnumerateArray());
    }

    // ------------------------------------------------------------ metas da organização
    [Fact]
    public async Task Overview_TrazMetasDaOrganizacao()
    {
        var org = await fixture.CreateOrganizationAsync($"OvwMeta {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE organizations SET goal_weekly_active_hours = 160, goal_work_related_pct = 70 WHERE id = @t",
            ("t", org.Id));

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={day}&to={day}");

        var goals = doc.RootElement.GetProperty("goals");
        Assert.Equal(160, goals.GetProperty("weekly_active_hours").GetInt32());
        Assert.Equal(70, goals.GetProperty("work_related_pct").GetInt32());
    }

    // ------------------------------------------------------------ validação do período
    [Fact]
    public async Task Overview_RangeAcimaDe92Dias_400()
    {
        var org = await fixture.CreateOrganizationAsync($"OvwErr {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/dashboard/overview?from=2026-01-01&to=2026-12-31", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
