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
/// GET /api/v1/dashboard/overview?grain=month (item 3 do estudo).
///
/// O teto de 92 dias existe porque varrer daily_device_summaries num ano inteiro não é leitura
/// de tela — e por causa dele trimestre e ano eram impossíveis de prometer. Com o grão mensal a
/// leitura sai de monthly_summaries e a janela vai a 24 meses, que é exatamente a retenção dos
/// agregados: a janela máxima do produto passa a ser a do dado que ele guarda.
///
/// O CONTRATO NÃO MUDA: a série continua em days[], com date no primeiro dia do mês. Um segundo
/// contrato para manter em sincronia com o primeiro seria a forma mais barata de deixar os dois
/// divergirem.
/// </summary>
[Collection(ApiCollection.Name)]
public class DashboardMonthGrainTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string token, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>Organização com um dia de uso já agregado E já resumido no mensal.</summary>
    private async Task<(HttpClient Client, string Token, string Day)> SeedAsync(string prefixo)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefixo} {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: $"NB-{prefixo}");

        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 0), new Dictionary<string, object?> { ["process_name"] = "mgr-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 5), new Dictionary<string, object?> { ["process_name"] = "mgr-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 10), new Dictionary<string, object?> { ["process_name"] = "mgr-erp.exe" }),
            f.Event("LOCK", T(12, 15)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new IntervalizationService(ds).RunOnceAsync();
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        // classificado de propósito: sem categoria o índice viria null e a comparação entre os
        // dois grãos não provaria que a fórmula roda igual nos dois
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, 'Trabalho', 1)",
            ("c", categoryId), ("t", org.Id));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = 'mgr-erp.exe'
            ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
            """,
            ("t", org.Id), ("c", categoryId));

        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(org.Id);
            await new DailyAggregationService(ds).RunOnceAsync();
            await new MonthlyRollupService(ds).RunOnceAsync();
        }

        return (client, token, LocalDate(T(12, 0)));
    }

    // ------------------------------------------------------------------ o mensal bate com o diário

    [Fact]
    public async Task Grain_Month_Da_Os_Mesmos_Totais_Que_O_Grain_Day()
    {
        var (client, token, day) = await SeedAsync("MgrIgual");

        // o mês inteiro que contém o dia semeado, nos dois grãos
        var dia = DateOnly.ParseExact(day, "yyyy-MM-dd");
        var inicio = new DateOnly(dia.Year, dia.Month, 1).ToString("yyyy-MM-dd");
        var fim = new DateOnly(dia.Year, dia.Month, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");

        using var diario = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={inicio}&to={fim}");
        using var mensal = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={inicio}&to={fim}&grain=month");

        foreach (var campo in new[]
        {
            "seconds_on", "seconds_active", "seconds_idle", "seconds_locked",
            "seconds_work_related", "seconds_neutral", "seconds_not_work_related",
            "seconds_unclassified",
        })
        {
            Assert.Equal(
                diario.RootElement.GetProperty("totals").GetProperty(campo).GetInt64(),
                mensal.RootElement.GetProperty("totals").GetProperty(campo).GetInt64());
        }

        // índice e cobertura seguem calculados no SERVIDOR pela mesma fórmula
        Assert.Equal(
            diario.RootElement.GetProperty("totals").GetProperty("productivity_index").GetDouble(),
            mensal.RootElement.GetProperty("totals").GetProperty("productivity_index").GetDouble(), 4);

        // pessoas e dispositivos sobrevivem ao rollup porque as chaves sobrevivem ao grão
        Assert.Equal(
            diario.RootElement.GetProperty("totals").GetProperty("person_count").GetInt32(),
            mensal.RootElement.GetProperty("totals").GetProperty("person_count").GetInt32());
        Assert.Equal(
            diario.RootElement.GetProperty("totals").GetProperty("person_days").GetInt32(),
            mensal.RootElement.GetProperty("totals").GetProperty("person_days").GetInt32());
    }

    [Fact]
    public async Task Grain_Month_Devolve_A_Serie_Por_Mes_No_Mesmo_Contrato()
    {
        var (client, token, day) = await SeedAsync("MgrSerie");

        var dia = DateOnly.ParseExact(day, "yyyy-MM-dd");
        var primeiroDoMes = new DateOnly(dia.Year, dia.Month, 1);
        var inicio = primeiroDoMes.ToString("yyyy-MM-dd");
        var fim = primeiroDoMes.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={inicio}&to={fim}&grain=month");

        var serie = doc.RootElement.GetProperty("days").EnumerateArray().ToList();
        var unico = Assert.Single(serie);
        Assert.Equal(inicio, unico.GetProperty("date").GetString());
        Assert.True(unico.GetProperty("seconds_active").GetInt64() > 0);
    }

    // ------------------------------------------------------------------ a régua nova

    [Fact]
    public async Task Grain_Month_Aceita_Janela_De_24_Meses()
    {
        var (client, token, _) = await SeedAsync("Mgr24");

        var fim = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var inicio = fim.AddMonths(-23);

        using var _2 = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={inicio:yyyy-MM-dd}&to={fim:yyyy-MM-dd}&grain=month");
    }

    [Fact]
    public async Task Grain_Month_Recusa_Janela_Maior_Que_24_Meses()
    {
        var (client, token, _) = await SeedAsync("Mgr25");

        var fim = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var inicio = fim.AddMonths(-24);

        using var _2 = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={inicio:yyyy-MM-dd}&to={fim:yyyy-MM-dd}&grain=month",
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grain_Day_Continua_Com_O_Teto_De_92_Dias()
    {
        var (client, token, _) = await SeedAsync("MgrDia");

        // a mesma janela que o grão mensal aceita continua barrada no diário: o teto não afrouxou
        var fim = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var inicio = fim.AddMonths(-23);

        using var _2 = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={inicio:yyyy-MM-dd}&to={fim:yyyy-MM-dd}",
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grain_Desconhecido_Da_400()
    {
        var (client, token, day) = await SeedAsync("MgrRuim");

        using var _2 = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={day}&to={day}&grain=semana",
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grain_Month_Compara_Com_O_Periodo_Anterior_De_Mesma_Duracao()
    {
        var (client, token, day) = await SeedAsync("MgrCmp");

        var dia = DateOnly.ParseExact(day, "yyyy-MM-dd");
        var primeiroDoMes = new DateOnly(dia.Year, dia.Month, 1);
        var inicio = primeiroDoMes.ToString("yyyy-MM-dd");
        var fim = primeiroDoMes.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/overview?from={inicio}&to={fim}&grain=month&compare=true");

        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("previous").ValueKind);
    }
}
