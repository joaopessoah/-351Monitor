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
/// GET /api/v1/people/daily: a quebra por (pessoa, DIA) que o mapa do mês da Linha do Tempo
/// precisa. A listagem de /people só agrega o período inteiro, então pintar 31 colunas com ela
/// custaria uma requisição por dia; aqui o mês sai em UMA consulta.
///
/// A régua é a MESMA da listagem e é isso que estes testes prendem: identidade por windows_sid,
/// display_name resolvido no servidor, índice null quando não há tempo classificado (nunca 0),
/// isolamento entre tenants e a janela de 92 dias. Eventos sempre com lacuna &lt; 600 s para não
/// cair na regra de no_data.
/// </summary>
[Collection(ApiCollection.Name)]
public class PeopleDailyEndpointTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    /// <summary>Dia LOCAL do tenant (GMT-3) — é a chave de summary_date e do contrato.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
        await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    /// <summary>Bloco ativo de N minutos na lane de um SID (lacuna sempre &lt; 600 s).</summary>
    private static async Task SeedActiveAsync(
        HttpClient client, EnrolledDevice device, string sid, string username,
        string process, DateTimeOffset start, int minutes)
    {
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", start,
                new Dictionary<string, object?> { ["process_name"] = process },
                windowsSid: sid, windowsUser: username),
            f.Event("LOCK", start.AddMinutes(minutes), windowsSid: sid, windowsUser: username),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
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

    // ------------------------------------------------------------ uma pessoa, dois dias
    [Fact]
    public async Task PeopleDaily_UmaPessoaComDoisDias_DuasLinhasComOsValoresDeCadaDia()
    {
        var org = await fixture.CreateOrganizationAsync($"PDia {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-PDIA-1");

        const string sid = "S-1-5-21-5151-5151-5151-8001";
        // Dois dias LOCAIS distintos: T(-24+12) é o dia anterior a T(12) (mesma régua do
        // comparativo do overview), os dois já fechados — nenhuma consulta olha o futuro.
        await SeedActiveAsync(client, device, sid, "ACME\\ana.pdia", "pdia-erp.exe", T(-24 + 12, 0), 5);
        await SeedActiveAsync(client, device, sid, "ACME\\ana.pdia", "pdia-erp.exe", T(12, 0), 8);
        await RunPipelineAsync();

        var dia1 = LocalDate(T(-24 + 12, 0));
        var dia2 = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token, $"/api/v1/people/daily?from={dia1}&to={dia2}");

        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("windows_sid").GetString() == sid).ToList();
        Assert.Equal(2, items.Count);

        // Ordem estável por nome e data: a resposta é matéria-prima de um mapa.
        Assert.Equal(new[] { dia1, dia2 }, items.Select(i => i.GetProperty("date").GetString()));
        Assert.All(items, i => Assert.Equal("ACME\\ana.pdia", i.GetProperty("display_name").GetString()));

        // 5 min num dia, 8 min no outro: cada linha carrega SÓ o seu dia.
        Assert.Equal(5 * 60, items[0].GetProperty("seconds_active").GetInt64());
        Assert.Equal(8 * 60, items[1].GetProperty("seconds_active").GetInt64());
        // Sem categoria mapeada tudo é "sem classificação"; ocioso segue 0 (não houve IDLE).
        Assert.Equal(5 * 60, items[0].GetProperty("seconds_unclassified").GetInt64());
        Assert.Equal(8 * 60, items[1].GetProperty("seconds_unclassified").GetInt64());
        Assert.All(items, i => Assert.Equal(0, i.GetProperty("seconds_idle").GetInt64()));
        // Ligada nunca é menor que ativa (o tempo ativo é subconjunto do tempo ligado).
        Assert.All(items, i => Assert.True(
            i.GetProperty("seconds_on").GetInt64() >= i.GetProperty("seconds_active").GetInt64(),
            "seconds_on tem de conter seconds_active"));

        // Mesmo desenho de auditoria da listagem: view_report com target_type "people".
        var audited = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND target_type = 'people'",
            ("t", org.Id));
        Assert.Equal(1, audited);
    }

    // ------------------------------------------------------------ índice do dia e o null
    [Fact]
    public async Task PeopleDaily_IndiceDoDia_ENullNoDiaSemClassificacao()
    {
        var org = await fixture.CreateOrganizationAsync($"PIdx {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-PIDX-1");

        const string sid = "S-1-5-21-6262-6262-6262-8002";
        // Dia 1: erp (produtivo) 6 min + zap (improdutivo) 2 min -> índice 0,75.
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 0),
                new Dictionary<string, object?> { ["process_name"] = "pidx-erp.exe" },
                windowsSid: sid, windowsUser: "ACME\\bruno.pidx"),
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 6),
                new Dictionary<string, object?> { ["process_name"] = "pidx-zap.exe" },
                windowsSid: sid, windowsUser: "ACME\\bruno.pidx"),
            f.Event("LOCK", T(-24 + 12, 8), windowsSid: sid, windowsUser: "ACME\\bruno.pidx"),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        // Dia 2: só um app SEM categoria -> nada classificado -> índice null, jamais 0%.
        await SeedActiveAsync(client, device, sid, "ACME\\bruno.pidx", "pidx-nada.exe", T(12, 0), 7);
        await RunPipelineAsync();

        await MapAppAsync(org.Id, "pidx-erp.exe", 1);
        await MapAppAsync(org.Id, "pidx-zap.exe", -1);
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(org.Id);
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        var dia1 = LocalDate(T(-24 + 12, 0));
        var dia2 = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token, $"/api/v1/people/daily?from={dia1}&to={dia2}");
        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("windows_sid").GetString() == sid)
            .ToDictionary(i => i.GetProperty("date").GetString()!);

        Assert.Equal(0.75, items[dia1].GetProperty("productivity_index").GetDouble(), 4);
        // Dia sem NENHUM tempo classificado: null é "não sei", nunca 0.
        Assert.Equal(JsonValueKind.Null, items[dia2].GetProperty("productivity_index").ValueKind);
        Assert.Equal(7 * 60, items[dia2].GetProperty("seconds_unclassified").GetInt64());
    }

    // ------------------------------------------------------------ isolamento entre tenants
    [Fact]
    public async Task PeopleDaily_NaoVazaPessoaDeOutroTenant()
    {
        var orgA = await fixture.CreateOrganizationAsync($"PDA {Guid.NewGuid():N}"[..20]);
        var orgB = await fixture.CreateOrganizationAsync($"PDB {Guid.NewGuid():N}"[..20]);
        var (_, keyA) = await fixture.CreateEnrollmentKeyWithSecretAsync(orgA.Id);
        var (_, keyB) = await fixture.CreateEnrollmentKeyWithSecretAsync(orgB.Id);
        var viewerA = await fixture.CreateUserAsync(orgA.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var tokenA = await AuthClient.LoginAsync(client, viewerA);

        var deviceA = await AgentClient.EnrollAsync(client, keyA, hostname: "NB-PDA-1");
        var deviceB = await AgentClient.EnrollAsync(client, keyB, hostname: "NB-PDB-1");

        const string sidA = "S-1-5-21-7373-7373-7373-8003";
        const string sidB = "S-1-5-21-8484-8484-8484-8004";
        await SeedActiveAsync(client, deviceA, sidA, "A\\gil", "pdiso-erp.exe", T(12, 0), 5);
        await SeedActiveAsync(client, deviceB, sidB, "B\\hel", "pdiso-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, tokenA, $"/api/v1/people/daily?from={day}&to={day}");
        var sids = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("windows_sid").GetString()).ToList();

        Assert.Contains(sidA, sids);
        Assert.DoesNotContain(sidB, sids);
    }

    // ------------------------------------------------------------ validação do período
    [Fact]
    public async Task PeopleDaily_RangeAcimaDe92Dias_400()
    {
        var org = await fixture.CreateOrganizationAsync($"PDErr {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/people/daily?from=2026-01-01&to=2026-12-31", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);

        // 400 de janela NÃO deixa rastro de leitura (o AuditReadFilter só grava em 2xx).
        var audited = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'", ("t", org.Id));
        Assert.Equal(0, audited);
    }

    // ------------------------------------------------------------ helper de categoria
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
}
