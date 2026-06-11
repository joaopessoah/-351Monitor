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
/// GET /api/v1/reports/jornada (F3.5): linha por device × dia do range INTEIRO (dias sem
/// dados também viram linha, com a observação correta), users/somas/first-last corretos,
/// device_totals do range inteiro, paginação, archived fora por default (e dentro com
/// device_ids explícito), auditoria view_report SEMPRE e validações 400/404.
/// Inclui o teste de CONSISTÊNCIA 11.3 (gate do DoD): rodapé da timeline == linha da
/// jornada para o mesmo device/dia, com dados semeados pelo pipeline REAL.
/// </summary>
[Collection(ApiCollection.Name)]
public class JornadaReportEndpointTests(ApiTestFixture fixture)
{
    /// <summary>UUID zero = lane-máquina (spec linha 652).</summary>
    private static readonly Guid MachineLane = Guid.Empty;

    private async Task<(HttpClient Client, Guid TenantId, string Token)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, token);
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

    // ------------------------------------------------------------ seeds diretos
    // A jornada lê daily_device_summaries + activity_intervals; o pipeline que os produz
    // tem testes próprios (F2/F3.1) — aqui semeamos direto para controle fino dos números.
    private async Task SeedSummaryAsync(
        Guid tenantId, Guid deviceId, string date, Guid laneId,
        int active, int idle, int locked,
        DateTimeOffset? first = null, DateTimeOffset? last = null, bool incomplete = false)
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_locked, seconds_on,
                first_event_at, last_event_at, data_incomplete, computed_at)
            VALUES (@t, @day::date, @d, @u, @a, @i, @l, @a + @i + @l, @first, @last, @inc, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", laneId),
            ("a", active), ("i", idle), ("l", locked),
            ("first", first), ("last", last), ("inc", incomplete));
    }

    private async Task<Guid> SeedDeviceUserAsync(Guid tenantId, Guid deviceId, string displayName)
    {
        var id = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, @dn, now(), now())
            """,
            ("id", id), ("t", tenantId), ("d", deviceId),
            ("sid", $"S-1-5-21-JORN-{Guid.NewGuid():N}"[..40]), ("wu", $"acme\\{displayName.ToLowerInvariant()}"),
            ("dn", displayName));
        return id;
    }

    /// <summary>
    /// Partição mensal de activity_intervals para datas FIXAS dos testes (a InitialCreate
    /// só cria mês corrente e próximo; o pipeline real cria as demais sob demanda).
    /// </summary>
    private async Task SeedNoDataIntervalAsync(Guid tenantId, Guid deviceId, string date)
    {
        var day = DateOnly.ParseExact(date, "yyyy-MM-dd");
        var monthStart = new DateOnly(day.Year, day.Month, 1);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, $"""
            CREATE TABLE IF NOT EXISTS activity_intervals_{monthStart:yyyyMM} PARTITION OF activity_intervals
            FOR VALUES FROM ('{monthStart:yyyy-MM-dd}') TO ('{monthStart.AddMonths(1):yyyy-MM-dd}')
            """);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO activity_intervals (
                id, tenant_id, device_id, device_user_id, started_at, ended_at, state, source_day)
            VALUES (@id, @t, @d, NULL, @s, @e, 'no_data', @day::date)
            """,
            ("id", Uuid7.NewUuid7()), ("t", tenantId), ("d", deviceId),
            ("s", day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)),
            ("e", day.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Utc)),
            ("day", day));
    }

    private async Task<long> ViewReportCountAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'", ("t", tenantId));

    // ------------------------------------------------------------ linhas, notas, users e totais
    [Fact]
    public async Task LinhaPorDeviceXDia_NotasUsersETotaisDoRangeInteiro()
    {
        var (client, tenantId, token) = await SetupAsync("JornRows");
        var deviceA = await fixture.CreateDeviceAsync(tenantId, "NB-JORN-A");
        var deviceB = await fixture.CreateDeviceAsync(tenantId, "NB-JORN-B");

        var ana = await SeedDeviceUserAsync(tenantId, deviceA.Id, "Ana");
        var bruno = await SeedDeviceUserAsync(tenantId, deviceA.Id, "Bruno");
        var carla = await SeedDeviceUserAsync(tenantId, deviceB.Id, "Carla");

        // device A, 02/03: duas lanes de usuário + lane-máquina zerada (fora de users)
        await SeedSummaryAsync(tenantId, deviceA.Id, "2026-03-02", ana, 3600, 600, 0,
            DateTimeOffset.Parse("2026-03-02T12:00:00Z"), DateTimeOffset.Parse("2026-03-02T16:00:00Z"));
        await SeedSummaryAsync(tenantId, deviceA.Id, "2026-03-02", bruno, 1800, 0, 600,
            DateTimeOffset.Parse("2026-03-02T11:00:00Z"), DateTimeOffset.Parse("2026-03-02T13:00:00Z"));
        await SeedSummaryAsync(tenantId, deviceA.Id, "2026-03-02", MachineLane, 0, 0, 0);
        // device A, 04/03: só intervalo no_data (sem summary) → "sem_comunicacao"
        await SeedNoDataIntervalAsync(tenantId, deviceA.Id, "2026-03-04");
        // device B, 02/03: dia com data_incomplete → "dados_incompletos" vence as demais notas
        await SeedSummaryAsync(tenantId, deviceB.Id, "2026-03-02", carla, 1200, 0, 0,
            DateTimeOffset.Parse("2026-03-02T12:00:00Z"), DateTimeOffset.Parse("2026-03-02T12:20:00Z"),
            incomplete: true);

        using var doc = await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-03-02&to=2026-03-04");

        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(6, items.Count); // 2 devices × 3 dias — dias vazios TAMBÉM viram linha
        Assert.Equal(6, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, doc.RootElement.GetProperty("page_size").GetInt32());

        // ordenação: device_name, date
        Assert.All(items.Take(3), i => Assert.Equal("NB-JORN-A", i.GetProperty("device_name").GetString()));
        Assert.All(items.Skip(3), i => Assert.Equal("NB-JORN-B", i.GetProperty("device_name").GetString()));
        Assert.Equal(
            new[] { "2026-03-02", "2026-03-03", "2026-03-04", "2026-03-02", "2026-03-03", "2026-03-04" },
            items.Select(i => i.GetProperty("date").GetString()).ToArray());

        // A/02-03: somas das lanes, MIN/MAX de first/last, users ordenados, note null
        var a1 = items[0];
        Assert.Equal(deviceA.Id, a1.GetProperty("device_id").GetGuid());
        Assert.Equal("Ana, Bruno", a1.GetProperty("users").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T11:00:00Z"), a1.GetProperty("first_event_at").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T16:00:00Z"), a1.GetProperty("last_event_at").GetDateTimeOffset());
        Assert.Equal(6600, a1.GetProperty("seconds_on").GetInt64());
        Assert.Equal(5400, a1.GetProperty("seconds_active").GetInt64());
        Assert.Equal(600, a1.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(600, a1.GetProperty("seconds_locked").GetInt64());
        Assert.Equal(JsonValueKind.Null, a1.GetProperty("note").ValueKind);

        // A/03-03: dia vazio → "sem_dados" com tudo zerado
        var a2 = items[1];
        Assert.Equal("sem_dados", a2.GetProperty("note").GetString());
        Assert.Equal(0, a2.GetProperty("seconds_on").GetInt64());
        Assert.Equal(JsonValueKind.Null, a2.GetProperty("users").ValueKind);
        Assert.Equal(JsonValueKind.Null, a2.GetProperty("first_event_at").ValueKind);

        // A/04-03: zero tempo MAS houve no_data → "sem_comunicacao" (≠ sem_dados)
        Assert.Equal("sem_comunicacao", items[2].GetProperty("note").GetString());

        // B/02-03: data_incomplete do dia vence → "dados_incompletos"
        var b1 = items[3];
        Assert.Equal("dados_incompletos", b1.GetProperty("note").GetString());
        Assert.Equal("Carla", b1.GetProperty("users").GetString());
        Assert.Equal(1200, b1.GetProperty("seconds_on").GetInt64());

        // device_totals do RANGE INTEIRO, ordenados por nome
        var totals = doc.RootElement.GetProperty("device_totals").EnumerateArray().ToList();
        Assert.Equal(2, totals.Count);
        Assert.Equal(deviceA.Id, totals[0].GetProperty("device_id").GetGuid());
        Assert.Equal(6600, totals[0].GetProperty("seconds_on").GetInt64());
        Assert.Equal(5400, totals[0].GetProperty("seconds_active").GetInt64());
        Assert.Equal(600, totals[0].GetProperty("seconds_idle").GetInt64());
        Assert.Equal(600, totals[0].GetProperty("seconds_locked").GetInt64());
        Assert.Equal(1, totals[0].GetProperty("days_with_data").GetInt32());
        Assert.Equal(deviceB.Id, totals[1].GetProperty("device_id").GetGuid());
        Assert.Equal(1200, totals[1].GetProperty("seconds_on").GetInt64());
        Assert.Equal(1, totals[1].GetProperty("days_with_data").GetInt32());

        // jornada é SEMPRE dado pessoal: a consulta auditou
        Assert.Equal(1L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ paginação (device_totals estáveis)
    [Fact]
    public async Task Paginacao_TotalDoRangeInteiroEDeviceTotaisIndependemDaPagina()
    {
        var (client, tenantId, token) = await SetupAsync("JornPag");
        var deviceA = await fixture.CreateDeviceAsync(tenantId, "NB-JORN-PAG-A");
        var deviceB = await fixture.CreateDeviceAsync(tenantId, "NB-JORN-PAG-B");
        var ana = await SeedDeviceUserAsync(tenantId, deviceA.Id, "Ana");
        await SeedSummaryAsync(tenantId, deviceA.Id, "2026-03-02", ana, 600, 0, 0,
            DateTimeOffset.Parse("2026-03-02T12:00:00Z"), DateTimeOffset.Parse("2026-03-02T12:10:00Z"));

        // página 1: 4 primeiras linhas (A 02..04 + B 02); totais SEMPRE do range inteiro
        using (var doc = await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-03-02&to=2026-03-04&page_size=4"))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(4, items.Count);
            Assert.Equal(6, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(deviceA.Id, items[0].GetProperty("device_id").GetGuid());
            Assert.Equal(deviceB.Id, items[3].GetProperty("device_id").GetGuid());
            Assert.Equal(2, doc.RootElement.GetProperty("device_totals").GetArrayLength());
        }

        // página 2: as 2 restantes (B 03..04), com os MESMOS device_totals
        using (var doc = await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-03-02&to=2026-03-04&page_size=4&page=2"))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal(deviceB.Id, i.GetProperty("device_id").GetGuid()));
            Assert.Equal(6, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("device_totals").GetArrayLength());
        }

        // duas consultas, duas linhas de auditoria (view_report SEMPRE)
        Assert.Equal(2L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ CONSISTÊNCIA 11.3 (gate do DoD)
    [Fact]
    public async Task Consistencia113_RodapeDaTimelineIgualLinhaDaJornada()
    {
        // pipeline REAL: ingestão → intervalização → agregação (mesmo harness da F3.2/F3.3)
        var baseT = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
        var org = await fixture.CreateOrganizationAsync($"Jorn113 {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-JORN-113");

        // espaçamento SEMPRE < 10 min entre eventos: o gap N7 dispara em ≥ 600 s e viraria no_data
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", baseT.AddHours(12),
                new Dictionary<string, object?> { ["process_name"] = "jorn-113.exe" }),
            f.Event("IDLE_START", baseT.AddHours(12).AddMinutes(8),
                new Dictionary<string, object?> { ["last_input_at"] = baseT.AddHours(12).AddMinutes(6).UtcDateTime.ToString("o") }),
            f.Event("LOCK", baseT.AddHours(12).AddMinutes(11)),
            f.Event("SESSION_END", baseT.AddHours(12).AddMinutes(15)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using (var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new IntervalizationService(dataSource).RunOnceAsync();
            await new DailyAggregationService(dataSource).RunOnceAsync();
        }

        var date = baseT.AddHours(12).AddHours(-3).ToString("yyyy-MM-dd"); // dia local GMT-3

        using var timeline = await GetJsonAsync(client, token,
            $"/api/v1/timeline/device?device_id={device.DeviceId}&date={date}");
        var summary = timeline.RootElement.GetProperty("summary");

        using var jornada = await GetJsonAsync(client, token,
            $"/api/v1/reports/jornada?from={date}&to={date}&device_ids={device.DeviceId}");
        var row = Assert.Single(jornada.RootElement.GetProperty("items").EnumerateArray());

        // sanidade do cenário: ativo 12:00→12:06 (fechado RETROATIVAMENTE no last_input_at),
        // ocioso 12:06→12:11, bloqueado 12:11→12:15
        Assert.Equal(360, row.GetProperty("seconds_active").GetInt64());
        Assert.Equal(300, row.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(240, row.GetProperty("seconds_locked").GetInt64());
        Assert.Equal(900, row.GetProperty("seconds_on").GetInt64());

        // GATE 11.3: rodapé da timeline == linha da jornada, campo a campo
        Assert.Equal(summary.GetProperty("seconds_on").GetInt64(), row.GetProperty("seconds_on").GetInt64());
        Assert.Equal(summary.GetProperty("seconds_active").GetInt64(), row.GetProperty("seconds_active").GetInt64());
        Assert.Equal(summary.GetProperty("seconds_idle").GetInt64(), row.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(summary.GetProperty("seconds_locked").GetInt64(), row.GetProperty("seconds_locked").GetInt64());
        Assert.Equal(
            summary.GetProperty("first_event_at").GetDateTimeOffset(),
            row.GetProperty("first_event_at").GetDateTimeOffset());
        Assert.Equal(
            summary.GetProperty("last_event_at").GetDateTimeOffset(),
            row.GetProperty("last_event_at").GetDateTimeOffset());
    }

    // ------------------------------------------------------------ archived
    [Fact]
    public async Task Archived_ForaPorDefault_DentroComDeviceIdsExplicito()
    {
        var (client, tenantId, token) = await SetupAsync("JornArc");
        var ativo = await fixture.CreateDeviceAsync(tenantId, "NB-JORN-ARC-1");
        var arquivado = await fixture.CreateDeviceAsync(tenantId, "NB-JORN-ARC-2");
        var ana = await SeedDeviceUserAsync(tenantId, arquivado.Id, "Ana");
        await SeedSummaryAsync(tenantId, arquivado.Id, "2026-03-02", ana, 600, 0, 0,
            DateTimeOffset.Parse("2026-03-02T12:00:00Z"), DateTimeOffset.Parse("2026-03-02T12:10:00Z"));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET status = 'archived' WHERE id = @d", ("d", arquivado.Id));

        // default: o arquivado some (linhas E totais)
        using (var doc = await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-03-02&to=2026-03-02"))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(ativo.Id, item.GetProperty("device_id").GetGuid());
            var total = Assert.Single(doc.RootElement.GetProperty("device_totals").EnumerateArray());
            Assert.Equal(ativo.Id, total.GetProperty("device_id").GetGuid());
        }

        // device_ids EXPLÍCITO inclui o arquivado (decisão documentada: histórico sob demanda)
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/jornada?from=2026-03-02&to=2026-03-02&device_ids={arquivado.Id}"))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(arquivado.Id, item.GetProperty("device_id").GetGuid());
            Assert.Equal(600, item.GetProperty("seconds_active").GetInt64());
        }
    }

    // ------------------------------------------------------------ validação e auditoria
    [Fact]
    public async Task Validacao400_404_EAuditoriaSempreNoSucesso()
    {
        var (client, tenantId, token) = await SetupAsync("JornVal");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-JORN-VAL");

        // mesma régua de datas do dashboard: formato, from > to, teto de 92 dias
        (await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?to=2026-03-02", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=02-03-2026&to=2026-03-02", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-03-10&to=2026-03-02", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-01-01&to=2026-04-03", HttpStatusCode.BadRequest)).Dispose();

        // device_ids malformado → 400; device de outro tenant/inexistente → 404
        (await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-03-02&to=2026-03-02&device_ids=nao-e-uuid",
            HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/jornada?from=2026-03-02&to=2026-03-02&device_ids={Uuid7.NewUuid7()}",
            HttpStatusCode.NotFound)).Dispose();

        // os probes 400/404 não deixam rastro
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));

        // sucesso audita SEMPRE (mesmo sem filtro: jornada lista pessoas por dia)
        (await GetJsonAsync(client, token,
            "/api/v1/reports/jornada?from=2026-03-02&to=2026-03-02")).Dispose();
        Assert.Equal(1L, await ViewReportCountAsync(tenantId));
        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString, """
            SELECT target_type, target_id, detail->>'from' AS detail_from, detail->>'to' AS detail_to
            FROM audit_log WHERE tenant_id = @t AND action = 'view_report'
            """, ("t", tenantId));
        Assert.Equal("team", (string)audit!["target_type"]!);
        Assert.Null(audit["target_id"]);
        Assert.Equal("2026-03-02", (string)audit["detail_from"]!);
        Assert.Equal("2026-03-02", (string)audit["detail_to"]!);

        // com device_ids de UM device, o alvo da trilha é o device
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/jornada?from=2026-03-02&to=2026-03-02&device_ids={device.Id}")).Dispose();
        var auditDevice = await TestDb.RowAsync(fixture.Database.ConnectionString, """
            SELECT target_type, target_id FROM audit_log
            WHERE tenant_id = @t AND action = 'view_report' AND target_id IS NOT NULL
            """, ("t", tenantId));
        Assert.Equal("device", (string)auditDevice!["target_type"]!);
        Assert.Equal(device.Id, (Guid)auditDevice["target_id"]!);
    }
}
