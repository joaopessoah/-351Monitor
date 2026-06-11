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
/// GET /api/v1/dashboard/summary e /top-apps (F3.2) + business_hours no /me: dados semeados
/// pelo pipeline REAL (ingestão pela API → IntervalizationService → DailyAggregationService),
/// leitura pelos endpoints. Cobre somas por dia, dias sem dados ausentes, totals (device_count
/// distinct do período), ordenação/limit/total do top-apps, exclusão de device archived,
/// auditoria view_report só com filtro individual e validação 400 do range.
/// </summary>
[Collection(ApiCollection.Name)]
public class DashboardEndpointTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    private static DateTimeOffset T(int dayOffset, int h, int m, int s = 0) =>
        Base.AddDays(dayOffset).AddHours(h).AddMinutes(m).AddSeconds(s);

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o");

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, Guid TenantId, string Token, string FullKey, TestUser Viewer)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, token, fullKey, viewer);
    }

    /// <summary>
    /// Pipeline completo: zera clock_offset (a EMA da ingestão deslocaria as asserções de
    /// timestamp — mesmo helper da F2/F3.1), intervaliza e agrega os dirty_days.
    /// </summary>
    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
        await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    /// <summary>Bloco active de N minutos (sempre &lt; 10 min — gap N7 dispara em ≥ 600 s).</summary>
    private static async Task SeedActiveAsync(
        HttpClient client, EnrolledDevice device, string process, DateTimeOffset start, int minutes)
    {
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", start, new Dictionary<string, object?> { ["process_name"] = process }),
            f.Event("LOCK", start.AddMinutes(minutes)),
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

    private async Task<long> ViewReportCountAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'", ("t", tenantId));

    // ------------------------------------------------------------ somas, dias ausentes, totals
    [Fact]
    public async Task Summary_SomaPorDia_DiasSemDadosAusentes_TotalsComDistinct()
    {
        var (client, _, token, fullKey, _) = await SetupAsync("DashSum");
        var device1 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-SUM-1");
        var device2 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-SUM-2");

        // device1 só no dia D (ontem): active 540 s
        await SeedActiveAsync(client, device1, "dash-sum1.exe", T(0, 9, 0), 9);

        // device2 nos dias D-1 e D: D-1 = active 180 (10:00→10:03 retroativo) + idle 360
        // (10:03→10:09); D = active 300. O buraco entre os dias vira no_data de máquina
        // (zerado em todos os seconds_*) — não interfere nas somas nem no distinct.
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device2.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(-1, 10, 0), new Dictionary<string, object?> { ["process_name"] = "dash-sum2.exe" }),
            f.Event("IDLE_START", T(-1, 10, 5), new Dictionary<string, object?> { ["last_input_at"] = Iso(T(-1, 10, 3)) }),
            f.Event("LOCK", T(-1, 10, 9)),
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 11, 0), new Dictionary<string, object?> { ["process_name"] = "dash-sum2.exe" }),
            f.Event("LOCK", T(0, 11, 5)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        // janela maior que os dados: dias sem linhas NÃO aparecem em days
        var from = LocalDate(T(-3, 12, 0));
        var to = LocalDate(T(0, 12, 0));
        using var doc = await GetJsonAsync(client, token, $"/api/v1/dashboard/summary?from={from}&to={to}");

        var days = doc.RootElement.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(2, days.Count);

        var dia1 = days[0];
        Assert.Equal(LocalDate(T(-1, 10, 0)), dia1.GetProperty("date").GetString());
        Assert.Equal(180, dia1.GetProperty("seconds_active").GetInt64());
        Assert.Equal(360, dia1.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(0, dia1.GetProperty("seconds_locked").GetInt64());
        Assert.Equal(540, dia1.GetProperty("seconds_on").GetInt64());
        Assert.Equal(180, dia1.GetProperty("seconds_neutral").GetInt64()); // sem categorias: tudo neutro
        Assert.Equal(1, dia1.GetProperty("device_count").GetInt32());
        Assert.False(dia1.GetProperty("data_incomplete").GetBoolean());

        var dia2 = days[1];
        Assert.Equal(LocalDate(T(0, 9, 0)), dia2.GetProperty("date").GetString());
        Assert.Equal(840, dia2.GetProperty("seconds_active").GetInt64()); // 540 + 300
        Assert.Equal(840, dia2.GetProperty("seconds_on").GetInt64());
        Assert.Equal(2, dia2.GetProperty("device_count").GetInt32());

        var totals = doc.RootElement.GetProperty("totals");
        Assert.Equal(1020, totals.GetProperty("seconds_active").GetInt64());
        Assert.Equal(360, totals.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(1380, totals.GetProperty("seconds_on").GetInt64());
        Assert.Equal(0, totals.GetProperty("seconds_work_related").GetInt64());
        Assert.Equal(1020, totals.GetProperty("seconds_neutral").GetInt64());
        Assert.Equal(0, totals.GetProperty("seconds_not_work_related").GetInt64());
        Assert.False(totals.GetProperty("data_incomplete").GetBoolean());
        Assert.Equal(2, totals.GetProperty("device_count").GetInt32()); // DISTINCT do período, não soma dos dias
    }

    // ------------------------------------------------------------ filtro individual + auditoria
    [Fact]
    public async Task Summary_AuditoriaViewReport_SoComFiltroIndividual()
    {
        var (client, tenantId, token, fullKey, viewer) = await SetupAsync("DashAud");
        var device1 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-AUD-1");
        var device2 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-AUD-2");
        await SeedActiveAsync(client, device1, "dash-aud1.exe", T(0, 9, 0), 9);  // 540 s
        await SeedActiveAsync(client, device2, "dash-aud2.exe", T(0, 10, 0), 5); // 300 s
        await RunPipelineAsync();

        var date = LocalDate(T(0, 9, 0));

        // SEM filtro: agregado de equipe — NÃO audita
        using (var doc = await GetJsonAsync(client, token, $"/api/v1/dashboard/summary?from={date}&to={date}"))
        {
            Assert.Equal(840, doc.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
        }
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));

        // COM device_id: filtra E grava view_report com alvo device
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/summary?from={date}&to={date}&device_id={device1.DeviceId}"))
        {
            var dia = Assert.Single(doc.RootElement.GetProperty("days").EnumerateArray());
            Assert.Equal(540, dia.GetProperty("seconds_active").GetInt64());
            Assert.Equal(1, dia.GetProperty("device_count").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("totals").GetProperty("device_count").GetInt32());
        }
        Assert.Equal(1L, await ViewReportCountAsync(tenantId));
        var auditDevice = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT actor_user_id, target_type, target_id,
                   detail->>'from' AS detail_from, detail->>'to' AS detail_to, detail->>'device_id' AS detail_device
            FROM audit_log WHERE tenant_id = @t AND action = 'view_report'
            """,
            ("t", tenantId));
        Assert.NotNull(auditDevice);
        Assert.Equal(viewer.Id, (Guid)auditDevice!["actor_user_id"]!);
        Assert.Equal("device", (string)auditDevice["target_type"]!);
        Assert.Equal(device1.DeviceId, (Guid)auditDevice["target_id"]!);
        Assert.Equal(date, (string)auditDevice["detail_from"]!);
        Assert.Equal(date, (string)auditDevice["detail_to"]!);
        Assert.Equal(device1.DeviceId.ToString(), (string)auditDevice["detail_device"]!);

        // COM device_user_id: filtra a lane do usuário E grava view_report com alvo device_user
        var deviceUserId = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM device_users WHERE tenant_id = @t AND device_id = @d",
            ("t", tenantId), ("d", device1.DeviceId));
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/summary?from={date}&to={date}&device_user_id={deviceUserId}"))
        {
            Assert.Equal(540, doc.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
        }
        Assert.Equal(2L, await ViewReportCountAsync(tenantId));
        var targetType = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT target_type FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND target_id = @id",
            ("t", tenantId), ("id", deviceUserId));
        Assert.Equal("device_user", targetType);
    }

    // ------------------------------------------------------------ archived fora dos DOIS endpoints
    [Fact]
    public async Task DeviceArchived_ExcluidoDeSummaryEDeTopApps()
    {
        var (client, _, token, fullKey, _) = await SetupAsync("DashArc");
        var ativo = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-ARC-1");
        var arquivado = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-ARC-2");
        await SeedActiveAsync(client, ativo, "dash-arc1.exe", T(0, 9, 0), 9);      // 540 s
        await SeedActiveAsync(client, arquivado, "dash-arc2.exe", T(0, 10, 0), 5); // 300 s
        await RunPipelineAsync();

        // arquiva DEPOIS da agregação: a exclusão é da LEITURA (spec linha 954, "sai dos dashboards")
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET status = 'archived' WHERE id = @d", ("d", arquivado.DeviceId));

        var date = LocalDate(T(0, 9, 0));
        using (var doc = await GetJsonAsync(client, token, $"/api/v1/dashboard/summary?from={date}&to={date}"))
        {
            var dia = Assert.Single(doc.RootElement.GetProperty("days").EnumerateArray());
            Assert.Equal(540, dia.GetProperty("seconds_active").GetInt64());
            Assert.Equal(1, dia.GetProperty("device_count").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("totals").GetProperty("device_count").GetInt32());
        }

        using (var doc = await GetJsonAsync(client, token, $"/api/v1/dashboard/top-apps?from={date}&to={date}"))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("dash-arc1.exe", item.GetProperty("process_name").GetString());
            Assert.Equal(540, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
        }
    }

    // ------------------------------------------------------------ top-apps: ordenação, limit, total, categoria
    [Fact]
    public async Task TopApps_OrdenacaoLimitCategoriaETotalDoPeriodoInteiro()
    {
        var (client, tenantId, token, fullKey, _) = await SetupAsync("DashTop");
        var device1 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-TOP-1");
        var device2 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DASH-TOP-2");

        // device1: chrome 900 s, excel 360 s, word 180 s (espaçamento sempre < 600 s)
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device1.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 9, 0), new Dictionary<string, object?> { ["process_name"] = "dash-top-chrome.exe" }),
            f.Event("HEARTBEAT", T(0, 9, 8), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 9, 15), new Dictionary<string, object?> { ["process_name"] = "dash-top-excel.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 9, 21), new Dictionary<string, object?> { ["process_name"] = "dash-top-word.exe" }),
            f.Event("LOCK", T(0, 9, 24)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        // device2: chrome 300 s — soma 1200 e device_count 2 no chrome
        await SeedActiveAsync(client, device2, "dash-top-chrome.exe", T(0, 10, 0), 5);
        await RunPipelineAsync();

        // mapeamento de categoria do TENANT (semeado por SQL: o seed real é do backoffice)
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO categories (id, tenant_id, name, classification, color)
            VALUES (@c, @t, 'Navegação', 1, '#3b82f6')
            """, ("c", categoryId), ("t", tenantId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id, custom_display_name)
            SELECT @t, a.id, @c, 'Chrome Corporativo' FROM app_catalog a WHERE a.process_name = 'dash-top-chrome.exe'
            """, ("t", tenantId), ("c", categoryId));

        var date = LocalDate(T(0, 9, 0));

        // limit=2: só os 2 maiores, mas total_seconds_active soma TODOS os apps do período
        using (var doc = await GetJsonAsync(client, token, $"/api/v1/dashboard/top-apps?from={date}&to={date}&limit=2"))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);

            var chrome = items[0];
            Assert.Equal("dash-top-chrome.exe", chrome.GetProperty("process_name").GetString());
            Assert.Equal(1200, chrome.GetProperty("seconds_active").GetInt64());
            Assert.Equal(2, chrome.GetProperty("device_count").GetInt32());
            Assert.Equal("Chrome Corporativo", chrome.GetProperty("custom_display_name").GetString());
            var category = chrome.GetProperty("category");
            Assert.Equal(categoryId, category.GetProperty("id").GetGuid());
            Assert.Equal("Navegação", category.GetProperty("name").GetString());
            Assert.Equal(1, category.GetProperty("classification").GetInt32());
            Assert.Equal("#3b82f6", category.GetProperty("color").GetString());

            var excel = items[1];
            Assert.Equal("dash-top-excel.exe", excel.GetProperty("process_name").GetString());
            Assert.Equal(360, excel.GetProperty("seconds_active").GetInt64());
            Assert.Equal(JsonValueKind.Null, excel.GetProperty("category").ValueKind);
            Assert.Equal(JsonValueKind.Null, excel.GetProperty("custom_display_name").ValueKind);

            Assert.Equal(1740, doc.RootElement.GetProperty("total_seconds_active").GetInt64()); // inclui o word
        }

        // sem limit: default 10 — os 3 apps, ordenados por seconds_active desc
        using (var doc = await GetJsonAsync(client, token, $"/api/v1/dashboard/top-apps?from={date}&to={date}"))
        {
            var nomes = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("process_name").GetString()).ToList();
            Assert.Equal(new[] { "dash-top-chrome.exe", "dash-top-excel.exe", "dash-top-word.exe" }, nomes);
        }

        // top-apps é agregado de equipe: NUNCA audita
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ validação do range (400)
    [Fact]
    public async Task Validacao_RangeInvalido_Retorna400_EVazioRetorna200()
    {
        var (client, _, token, _, _) = await SetupAsync("DashVal");

        // from > to
        (await GetJsonAsync(client, token,
            "/api/v1/dashboard/summary?from=2026-06-10&to=2026-06-01", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/dashboard/top-apps?from=2026-06-10&to=2026-06-01", HttpStatusCode.BadRequest)).Dispose();

        // borda do teto: 92 dias inclusivos passam, 93 não
        (await GetJsonAsync(client, token, "/api/v1/dashboard/summary?from=2026-01-01&to=2026-04-02")).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/dashboard/summary?from=2026-01-01&to=2026-04-03", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/dashboard/top-apps?from=2026-01-01&to=2026-04-03", HttpStatusCode.BadRequest)).Dispose();

        // datas malformadas ou ausentes
        (await GetJsonAsync(client, token,
            "/api/v1/dashboard/summary?from=01-06-2026&to=2026-06-10", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/dashboard/summary?from=2026-06-01", HttpStatusCode.BadRequest)).Dispose();

        // range válido sem dados: 200 com days vazio e totals zerados
        using var doc = await GetJsonAsync(client, token, "/api/v1/dashboard/summary?from=2020-01-01&to=2020-01-31");
        Assert.Empty(doc.RootElement.GetProperty("days").EnumerateArray());
        var totals = doc.RootElement.GetProperty("totals");
        Assert.Equal(0, totals.GetProperty("seconds_active").GetInt64());
        Assert.Equal(0, totals.GetProperty("device_count").GetInt32());
        Assert.False(totals.GetProperty("data_incomplete").GetBoolean());
    }

    // ------------------------------------------------------------ business_hours no /me
    [Fact]
    public async Task Me_BusinessHours_ObjetoQuandoDefinido_NullQuandoAusente()
    {
        // org COM business_hours definido (jsonb cru da org)
        var (client, tenantId, token, _, _) = await SetupAsync("DashMe");
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE organizations SET business_hours = @v::jsonb WHERE id = @t",
            ("v", """{"days":[1,2,3,4,5],"start":"08:00","end":"18:00"}"""), ("t", tenantId));

        using (var doc = await GetJsonAsync(client, token, "/api/v1/me"))
        {
            var businessHours = doc.RootElement.GetProperty("organization").GetProperty("business_hours");
            Assert.Equal(JsonValueKind.Object, businessHours.ValueKind);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 },
                businessHours.GetProperty("days").EnumerateArray().Select(d => d.GetInt32()).ToArray());
            Assert.Equal("08:00", businessHours.GetProperty("start").GetString());
            Assert.Equal("18:00", businessHours.GetProperty("end").GetString());
        }

        // org SEM business_hours: a chave existe e é null
        var (client2, _, token2, _, _) = await SetupAsync("DashMe2");
        using (var doc = await GetJsonAsync(client2, token2, "/api/v1/me"))
        {
            var organization = doc.RootElement.GetProperty("organization");
            Assert.True(organization.TryGetProperty("business_hours", out var businessHours));
            Assert.Equal(JsonValueKind.Null, businessHours.ValueKind);
        }
    }
}
