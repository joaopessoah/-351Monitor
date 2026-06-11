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
/// GET /api/v1/reports/usage (F3.3): somas corretas por group_by (app, category, device,
/// device_user), paginação com totais do período inteiro, exclusão de devices archived,
/// auditoria view_report SÓ quando individual (device/device_user ou device_ids) e
/// validações 400/404. Dados semeados pelo pipeline REAL (ingestão → intervalização →
/// agregação), como nos testes do dashboard F3.2.
/// </summary>
[Collection(ApiCollection.Name)]
public class UsageReportEndpointTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o");

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, Guid TenantId, string Token, string FullKey)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, token, fullKey);
    }

    private async Task RunIntervalizationAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
    }

    private async Task RunAggregationAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
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
        return JsonDocument.Parse(string.IsNullOrEmpty(body) ? "null" : body);
    }

    /// <summary>Categoria + mapeamento direto por SQL (o CRUD tem testes próprios).</summary>
    private async Task<Guid> MapAppAsync(
        Guid tenantId, string processName, string categoryName, int classification, string? customName = null)
    {
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO categories (id, tenant_id, name, classification)
            VALUES (@c, @t, @n, @cl)
            """, ("c", categoryId), ("t", tenantId), ("n", categoryName), ("cl", (short)classification));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id, custom_display_name)
            SELECT @t, a.id, @c, @cn FROM app_catalog a WHERE a.process_name = @p
            """, ("t", tenantId), ("c", categoryId), ("cn", customName), ("p", processName));
        return categoryId;
    }

    private async Task<long> ViewReportCountAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'", ("t", tenantId));

    // ------------------------------------------------------------ group_by=app + paginação
    [Fact]
    public async Task GroupByApp_SomasCategoriasPaginacaoETotaisDoPeriodoInteiro()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("UsoApp");
        var device1 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-APP-1");
        var device2 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-APP-2");

        // device1: chrome 900 s, excel 360 s, word 180 s; device2: chrome 300 s
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device1.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "uso-chrome.exe" }),
            f.Event("HEARTBEAT", T(9, 8), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 15), new Dictionary<string, object?> { ["process_name"] = "uso-excel.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 21), new Dictionary<string, object?> { ["process_name"] = "uso-word.exe" }),
            f.Event("LOCK", T(9, 24)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await SeedActiveAsync(client, device2, "uso-chrome.exe", T(10, 0), 5);
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        var categoriaId = await MapAppAsync(tenantId, "uso-chrome.exe", "Navegacao", 1, "Chrome Corporativo");

        var date = LocalDate(T(9, 0));

        // página 1 (page_size=2): chrome e excel; totais do PERÍODO INTEIRO
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=app&page_size=2"))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);

            var chrome = items[0];
            Assert.Equal("uso-chrome.exe", chrome.GetProperty("process_name").GetString());
            Assert.Equal(1200, chrome.GetProperty("seconds_active").GetInt64());
            Assert.Equal(2, chrome.GetProperty("device_count").GetInt32());
            Assert.Equal("Chrome Corporativo", chrome.GetProperty("custom_display_name").GetString());
            Assert.Equal(categoriaId, chrome.GetProperty("category").GetProperty("id").GetGuid());
            Assert.Equal(1, chrome.GetProperty("category").GetProperty("classification").GetInt32());

            var excel = items[1];
            Assert.Equal("uso-excel.exe", excel.GetProperty("process_name").GetString());
            Assert.Equal(360, excel.GetProperty("seconds_active").GetInt64());
            Assert.Equal(JsonValueKind.Null, excel.GetProperty("category").ValueKind);

            Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("page_size").GetInt32());
            Assert.Equal(1740, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
        }

        // página 2: só o word, com os MESMOS totais
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=app&page_size=2&page=2"))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("uso-word.exe", item.GetProperty("process_name").GetString());
            Assert.Equal(180, item.GetProperty("seconds_active").GetInt64());
            Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(1740, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
        }

        // agregado de equipe sem filtro: NÃO audita
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ group_by=category (balde null)
    [Fact]
    public async Task GroupByCategory_BaldeNullEhNaoCategorizado()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("UsoCat");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-CAT");

        await SeedActiveAsync(client, device, "uso-cat-mapeado.exe", T(9, 0), 9);  // 540 s
        await SeedActiveAsync(client, device, "uso-cat-livre.exe", T(10, 0), 5);   // 300 s
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        var categoriaId = await MapAppAsync(tenantId, "uso-cat-mapeado.exe", "Desenvolvimento", 1);

        var date = LocalDate(T(9, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=category");

        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var mapeado = items[0];
        Assert.Equal(categoriaId, mapeado.GetProperty("category_id").GetGuid());
        Assert.Equal("Desenvolvimento", mapeado.GetProperty("name").GetString());
        Assert.Equal(1, mapeado.GetProperty("classification").GetInt32());
        Assert.Equal(540, mapeado.GetProperty("seconds_active").GetInt64());
        Assert.Equal(1, mapeado.GetProperty("app_count").GetInt32());

        var naoCategorizado = items[1];
        Assert.Equal(JsonValueKind.Null, naoCategorizado.GetProperty("category_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, naoCategorizado.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, naoCategorizado.GetProperty("classification").ValueKind);
        Assert.Equal(300, naoCategorizado.GetProperty("seconds_active").GetInt64());
        Assert.Equal(1, naoCategorizado.GetProperty("app_count").GetInt32());

        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(840, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ group_by=device (baldes + ordem)
    [Fact]
    public async Task GroupByDevice_BaldesDeClassificacaoOrdenadosPorAtivoDesc()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("UsoDev");
        var device1 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-DEV-1");
        var device2 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-DEV-2");

        // device1: active 180 s + idle 360 s (o caminho de "quem ficou mais ocioso")
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device1.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(10, 0), new Dictionary<string, object?> { ["process_name"] = "uso-dev-work.exe" }),
            f.Event("IDLE_START", T(10, 5), new Dictionary<string, object?> { ["last_input_at"] = Iso(T(10, 3)) }),
            f.Event("LOCK", T(10, 9)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        // device2: active 540 s (app não mapeado: neutro)
        await SeedActiveAsync(client, device2, "uso-dev-livre.exe", T(9, 0), 9);

        await RunIntervalizationAsync();
        // mapeamento ANTES da agregação: a classificação resolve nos baldes do summary
        await MapAppAsync(tenantId, "uso-dev-work.exe", "Desenvolvimento", 1);
        await RunAggregationAsync();

        var date = LocalDate(T(9, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=device");

        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        // ordem default: seconds_active desc (540 > 180)
        var d2 = items[0];
        Assert.Equal(device2.DeviceId, d2.GetProperty("device_id").GetGuid());
        Assert.Equal("NB-USO-DEV-2", d2.GetProperty("device_name").GetString());
        Assert.Equal(540, d2.GetProperty("seconds_active").GetInt64());
        Assert.Equal(0, d2.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(540, d2.GetProperty("seconds_on").GetInt64());
        Assert.Equal(0, d2.GetProperty("seconds_work_related").GetInt64());
        Assert.Equal(540, d2.GetProperty("seconds_neutral").GetInt64());

        var d1 = items[1];
        Assert.Equal(device1.DeviceId, d1.GetProperty("device_id").GetGuid());
        Assert.Equal(180, d1.GetProperty("seconds_active").GetInt64());
        Assert.Equal(360, d1.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(0, d1.GetProperty("seconds_locked").GetInt64());
        Assert.Equal(540, d1.GetProperty("seconds_on").GetInt64());
        Assert.Equal(180, d1.GetProperty("seconds_work_related").GetInt64());
        Assert.Equal(0, d1.GetProperty("seconds_neutral").GetInt64());
        Assert.Equal(0, d1.GetProperty("seconds_not_work_related").GetInt64());

        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(720, doc.RootElement.GetProperty("total_seconds_active").GetInt64());

        // group_by=device é dado pessoal: audita view_report (sem device_ids → alvo team)
        Assert.Equal(1L, await ViewReportCountAsync(tenantId));
        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT target_type, target_id, detail->>'group_by' AS group_by
            FROM audit_log WHERE tenant_id = @t AND action = 'view_report'
            """, ("t", tenantId));
        Assert.Equal("team", (string)audit!["target_type"]!);
        Assert.Null(audit["target_id"]);
        Assert.Equal("device", (string)audit["group_by"]!);
    }

    // ------------------------------------------------------------ group_by=device_user (lane máquina)
    [Fact]
    public async Task GroupByDeviceUser_ResolveUsuarioELaneMaquina()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("UsoUser");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-USER");

        // dois blocos do MESMO usuário com gap >= 600 s entre eles: o buraco vira no_data
        // na lane-máquina (UUID zero), que aparece zerada no relatório
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "uso-user.exe" }),
            f.Event("LOCK", T(9, 5)),
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 20), new Dictionary<string, object?> { ["process_name"] = "uso-user.exe" }),
            f.Event("LOCK", T(9, 25)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        var date = LocalDate(T(9, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=device_user");

        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var usuario = items[0];
        Assert.NotEqual(Guid.Empty, usuario.GetProperty("device_user_id").GetGuid());
        Assert.Equal(device.DeviceId, usuario.GetProperty("device_id").GetGuid());
        Assert.Equal("NB-USO-USER", usuario.GetProperty("device_name").GetString());
        Assert.Equal(EventFactory.DefaultUser, usuario.GetProperty("windows_user").GetString());
        Assert.Equal(600, usuario.GetProperty("seconds_active").GetInt64()); // 300 + 300

        var maquina = items[1];
        Assert.Equal(Guid.Empty, maquina.GetProperty("device_user_id").GetGuid());
        Assert.Equal("Máquina (sem usuário)", maquina.GetProperty("display_name").GetString());
        Assert.Equal(JsonValueKind.Null, maquina.GetProperty("windows_user").ValueKind);
        Assert.Equal(0, maquina.GetProperty("seconds_active").GetInt64());
        Assert.Equal(0, maquina.GetProperty("seconds_on").GetInt64());

        // dado pessoal: audita
        Assert.Equal(1L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ archived fora + filtro device_ids
    [Fact]
    public async Task DeviceArchived_ExcluidoEDeviceIdsFiltraEAudita()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("UsoArc");
        var ativo = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-ARC-1");
        var arquivado = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-USO-ARC-2");

        await SeedActiveAsync(client, ativo, "uso-arc-a.exe", T(9, 0), 9);      // 540 s
        await SeedActiveAsync(client, arquivado, "uso-arc-b.exe", T(10, 0), 5); // 300 s
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET status = 'archived' WHERE id = @d", ("d", arquivado.DeviceId));

        var date = LocalDate(T(9, 0));

        // archived some de app e de device
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=app"))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("uso-arc-a.exe", item.GetProperty("process_name").GetString());
            Assert.Equal(540, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
        }
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=device"))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(ativo.DeviceId, item.GetProperty("device_id").GetGuid());
        }
        Assert.Equal(1L, await ViewReportCountAsync(tenantId)); // só o group_by=device auditou

        // device_ids: mesmo group_by=app vira consulta individual → filtra E audita com alvo device
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from={date}&to={date}&group_by=app&device_ids={ativo.DeviceId}"))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("uso-arc-a.exe", item.GetProperty("process_name").GetString());
            Assert.Equal(540, item.GetProperty("seconds_active").GetInt64());
        }
        Assert.Equal(2L, await ViewReportCountAsync(tenantId));
        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT target_type, target_id, detail->>'group_by' AS group_by, detail->'device_ids'->>0 AS device_0
            FROM audit_log
            WHERE tenant_id = @t AND action = 'view_report' AND target_id IS NOT NULL
            """, ("t", tenantId));
        Assert.Equal("device", (string)audit!["target_type"]!);
        Assert.Equal(ativo.DeviceId, (Guid)audit["target_id"]!);
        Assert.Equal("app", (string)audit["group_by"]!);
        Assert.Equal(ativo.DeviceId.ToString(), (string)audit["device_0"]!);
    }

    // ------------------------------------------------------------ validações 400/404
    [Fact]
    public async Task Validacao_GroupByDatasEDeviceIds()
    {
        var (client, tenantId, token, _) = await SetupAsync("UsoVal");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-USO-VAL");

        // group_by ausente ou inválido
        (await GetJsonAsync(client, token,
            "/api/v1/reports/usage?from=2026-06-01&to=2026-06-07", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/usage?from=2026-06-01&to=2026-06-07&group_by=pessoa", HttpStatusCode.BadRequest)).Dispose();

        // mesma régua de datas do dashboard: from > to, teto de 92 dias, formato
        (await GetJsonAsync(client, token,
            "/api/v1/reports/usage?from=2026-06-10&to=2026-06-01&group_by=app", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/usage?from=2026-01-01&to=2026-04-03&group_by=app", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/usage?from=01-06-2026&to=2026-06-07&group_by=app", HttpStatusCode.BadRequest)).Dispose();

        // device_ids malformado → 400; uuid de device inexistente no tenant → 404
        (await GetJsonAsync(client, token,
            "/api/v1/reports/usage?from=2026-06-01&to=2026-06-07&group_by=app&device_ids=nao-e-uuid",
            HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from=2026-06-01&to=2026-06-07&group_by=app&device_ids={Uuid7.NewUuid7()}",
            HttpStatusCode.NotFound)).Dispose();
        // lista mista (um válido, um inexistente) também é 404
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/usage?from=2026-06-01&to=2026-06-07&group_by=app&device_ids={device.Id},{Uuid7.NewUuid7()}",
            HttpStatusCode.NotFound)).Dispose();

        // range válido sem dados: 200 vazio com totais zerados
        using var doc = await GetJsonAsync(client, token,
            "/api/v1/reports/usage?from=2020-01-01&to=2020-01-31&group_by=app");
        Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("total_seconds_active").GetInt64());

        // os probes que levaram 400/404 não deixam rastro de view_report
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));
    }
}
