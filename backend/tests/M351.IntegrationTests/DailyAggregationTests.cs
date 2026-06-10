using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// DailyAggregationService fim-a-fim (F3.1): ingestão pela API real → IntervalizationService
/// → DailyAggregationService → daily_device_summaries / daily_app_usage. Cobre o dia básico,
/// a consistência 11.3 com o rodapé da timeline, o cenário timezone da 11.2 (corte na
/// meia-noite do TENANT), a lane-máquina (off_clean/no_data fora de seconds_on), a
/// classificação via tenant_app_categories, idempotência, consumo de dirty_days e
/// propagação de data_incomplete.
/// </summary>
[Collection(ApiCollection.Name)]
public class DailyAggregationTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    private static DateTimeOffset T(int h, int m, int s = 0) => Base.AddHours(h).AddMinutes(m).AddSeconds(s);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o");

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");
    private static DateOnly LocalDay(DateTimeOffset utc) => DateOnly.FromDateTime(utc.AddHours(-3).UtcDateTime);

    private async Task<(HttpClient Client, EnrolledDevice Device, Guid TenantId)> SetupAsync(string hostname)
    {
        var org = await fixture.CreateOrganizationAsync($"Agg {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var client = fixture.CreateApiClient();
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: hostname);
        return (client, device, org.Id);
    }

    /// <summary>
    /// zeroClockOffsets: a ingestão real calcula um skew de poucos ms (EMA) que deslocaria
    /// as asserções de timestamp — zera antes de rodar (mesmo helper do pipeline da F2).
    /// </summary>
    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
    }

    private async Task<int> RunAggregationAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        return await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    private async Task<List<Dictionary<string, object?>>> RowsAsync(string sql, params (string Name, object? Value)[] args)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private Task<List<Dictionary<string, object?>>> SummariesAsync(Guid deviceId) =>
        RowsAsync("""
            SELECT summary_date, device_user_id, seconds_active, seconds_idle, seconds_locked,
                   seconds_on, seconds_work_related, seconds_neutral, seconds_not_work_related,
                   first_event_at, last_event_at, data_incomplete
            FROM daily_device_summaries WHERE device_id = @d
            ORDER BY summary_date, device_user_id
            """, ("d", deviceId));

    private Task<List<Dictionary<string, object?>>> AppUsageAsync(Guid deviceId) =>
        RowsAsync("""
            SELECT u.summary_date, u.device_user_id, u.seconds_active, u.focus_count, a.process_name
            FROM daily_app_usage u JOIN app_catalog a ON a.id = u.app_id
            WHERE u.device_id = @d ORDER BY u.summary_date, a.process_name
            """, ("d", deviceId));

    private async Task<long> DirtyCountAsync(Guid deviceId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM dirty_days WHERE device_id = @d", ("d", deviceId));

    private static DateTimeOffset Ts(object? v) => v switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
        _ => throw new InvalidOperationException($"timestamp inesperado: {v?.GetType().Name}"),
    };

    private static DateOnly Day(object? v) => v switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => throw new InvalidOperationException($"date inesperado: {v?.GetType().Name}"),
    };

    private static int I(object? v) => Convert.ToInt32(v);

    // ------------------------------------------------------------ (a) dia básico
    [Fact]
    public async Task DiaBasico_SummaryEAppUsage_SomasExatas()
    {
        var (client, device, _) = await SetupAsync("NB-AGG-BASICO");
        var f = new EventFactory();
        // espaçamento sempre < 600 s: 600 exatos dispara o gap N7 (vira no_data de máquina)
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "agg-excel.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 5), new Dictionary<string, object?> { ["process_name"] = "agg-chrome.exe" }),
            f.Event("IDLE_START", T(9, 12), new Dictionary<string, object?> { ["last_input_at"] = Iso(T(9, 10)) }),
            f.Event("IDLE_END", T(9, 15)),
            f.Event("LOCK", T(9, 20)),
            f.Event("UNLOCK", T(9, 28)),
            f.Event("SESSION_END", T(9, 33)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();
        var processed = await RunAggregationAsync();
        Assert.True(processed >= 1, $"esperava >= 1 par processado, veio {processed}");

        // uma única lane (a do usuário): excel 300s + chrome 300+300+300; idle 300; locked 480
        var lane = Assert.Single(await SummariesAsync(device.DeviceId));
        Assert.NotEqual(Guid.Empty, (Guid)lane["device_user_id"]!);
        Assert.Equal(LocalDay(T(9, 0)), Day(lane["summary_date"]));
        Assert.Equal(1200, I(lane["seconds_active"]));
        Assert.Equal(300, I(lane["seconds_idle"]));
        Assert.Equal(480, I(lane["seconds_locked"]));
        Assert.Equal(1980, I(lane["seconds_on"]));
        Assert.Equal(T(9, 0), Ts(lane["first_event_at"]));
        Assert.Equal(T(9, 33), Ts(lane["last_event_at"]));
        Assert.False((bool)lane["data_incomplete"]!);

        // sem categoria mapeada: tudo cai em neutral ("Não categorizado")
        Assert.Equal(0, I(lane["seconds_work_related"]));
        Assert.Equal(1200, I(lane["seconds_neutral"]));
        Assert.Equal(0, I(lane["seconds_not_work_related"]));

        var usage = await AppUsageAsync(device.DeviceId);
        Assert.Equal(2, usage.Count);
        var chrome = usage.Single(u => (string)u["process_name"]! == "agg-chrome.exe");
        Assert.Equal(900, I(chrome["seconds_active"]));
        Assert.Equal(3, I(chrome["focus_count"])); // 3 intervalos active distintos do chrome
        var excel = usage.Single(u => (string)u["process_name"]! == "agg-excel.exe");
        Assert.Equal(300, I(excel["seconds_active"]));
        Assert.Equal(1, I(excel["focus_count"]));
    }

    // ------------------------------------------------------------ (b) consistência 11.3 com a timeline
    [Fact]
    public async Task Consistencia_RodapeDaTimeline_BateComAgregadoDiario()
    {
        var org = await fixture.CreateOrganizationAsync($"AggTl {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-AGG-CONSIST");
        var token = await AuthClient.LoginAsync(client, viewer);

        // Dia rico em DUAS lanes com durações FRACIONÁRIAS (ms): idle retroativo real, gap
        // no_data e suspend (off_clean) na lane A; lock/unlock + SESSION_END na lane B.
        // Espaçamento SEMPRE < 600 s onde não se quer gap: 600 exatos dispara o N7 ('>='),
        // mata idle/locked do dia e degenera o gate em 0==0 (já aconteceu uma vez).
        // As frações somam >= 1 s entre as lanes DE PROPÓSITO: floor da soma global (2901)
        // difere da soma dos floors por lane (2900) — pina a regra canônica por lane.
        const string SidB = "S-1-5-21-3623811015-3361044348-30300820-2044";
        const string UserB = "ACME\\joao.bose";
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "agg-tl.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0).AddMilliseconds(400), new Dictionary<string, object?> { ["process_name"] = "agg-tl-b.exe" }, windowsSid: SidB, windowsUser: UserB, sessionId: 2),
            f.Event("LOCK", T(9, 5).AddMilliseconds(900), windowsSid: SidB, windowsUser: UserB, sessionId: 2),
            f.Event("HEARTBEAT", T(9, 8), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("UNLOCK", T(9, 9), windowsSid: SidB, windowsUser: UserB, sessionId: 2),
            f.Event("SESSION_END", T(9, 12, 30).AddMilliseconds(300), windowsSid: SidB, windowsUser: UserB, sessionId: 2),
            // sustenta 9:08→9:20 (< 600 s por trecho); sem ele o gap N7 anularia o idle retroativo
            f.Event("HEARTBEAT", T(9, 14), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("IDLE_START", T(9, 20), new Dictionary<string, object?> { ["last_input_at"] = Iso(T(9, 15).AddMilliseconds(600)) }),
            f.Event("IDLE_END", T(9, 29)),
            f.Event("LOCK", T(9, 35)),
            f.Event("UNLOCK", T(9, 44)),
            // buraco de 21 min sem desligamento limpo → no_data (máquina)
            f.Event("ACTIVE_WINDOW_CHANGED", T(10, 5), new Dictionary<string, object?> { ["process_name"] = "agg-tl2.exe" }),
            f.Event("SYSTEM_SUSPEND", T(10, 14), windowsSid: null, windowsUser: null, sessionId: null),
            f.Event("SYSTEM_RESUME", T(11, 0), windowsSid: null, windowsUser: null, sessionId: null),
            f.Event("ACTIVE_WINDOW_CHANGED", T(11, 0, 10), new Dictionary<string, object?> { ["process_name"] = "agg-tl2.exe" }),
            f.Event("LOCK", T(11, 10)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();
        await RunAggregationAsync();

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/timeline/device?device_id={device.DeviceId}&date={LocalDate(T(9, 0))}", token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"timeline falhou: {response.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        var summary = doc.RootElement.GetProperty("summary");

        var agg = await TestDb.RowAsync(fixture.Database.ConnectionString, """
            SELECT sum(seconds_on)::bigint AS s_on, sum(seconds_active)::bigint AS s_active,
                   sum(seconds_idle)::bigint AS s_idle, sum(seconds_locked)::bigint AS s_locked,
                   min(first_event_at) AS first_event_at, max(last_event_at) AS last_event_at
            FROM daily_device_summaries WHERE device_id = @d AND summary_date = @day
            """, ("d", device.DeviceId), ("day", LocalDay(T(9, 0))));
        Assert.NotNull(agg);

        // GATE 11.3: rodapé do dia == soma das linhas do agregado diário
        Assert.Equal(summary.GetProperty("seconds_on").GetInt64(), (long)agg!["s_on"]!);
        Assert.Equal(summary.GetProperty("seconds_active").GetInt64(), (long)agg["s_active"]!);
        Assert.Equal(summary.GetProperty("seconds_idle").GetInt64(), (long)agg["s_idle"]!);
        Assert.Equal(summary.GetProperty("seconds_locked").GetInt64(), (long)agg["s_locked"]!);
        Assert.Equal(summary.GetProperty("first_event_at").GetDateTimeOffset(), Ts(agg["first_event_at"]));
        Assert.Equal(summary.GetProperty("last_event_at").GetDateTimeOffset(), Ts(agg["last_event_at"]));

        // guardas anti-degeneração + pino da regra de arredondamento POR LANE:
        // lane A: active 900.6 + 360 + 540 + 590 = 2390.6→2390; idle 839.4→839; locked 540
        // lane B: active 300.5 + 210.3 = 510.8→510; locked 239.1→239
        // floors por lane (2390+510=2900) ≠ floor da soma global (2901.4→2901)
        Assert.Equal(2900L, (long)agg["s_active"]!);
        Assert.Equal(839L, (long)agg["s_idle"]!);
        Assert.Equal(779L, (long)agg["s_locked"]!);
        Assert.Equal(4518L, (long)agg["s_on"]!);
    }

    // ------------------------------------------------------------ (c) timezone 11.2
    [Fact]
    public async Task Timezone_DeviceGmt4_CorteNaMeiaNoiteDoTenantGmt3()
    {
        var (client, device, tenantId) = await SetupAsync("NB-AGG-TZ");
        var f = new EventFactory();

        // meia-noite de America/Sao_Paulo (GMT-3) = 03:00 UTC; atividade 02:30→03:30 UTC cruza.
        // No fuso do DEVICE (GMT-4) o trecho inteiro cai num dia só — o corte é do TENANT.
        var events = new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(2, 30), new Dictionary<string, object?> { ["process_name"] = "agg-tz.exe" }),
            f.Event("HEARTBEAT", T(2, 38), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("HEARTBEAT", T(2, 46), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("HEARTBEAT", T(2, 54), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("HEARTBEAT", T(3, 2), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("HEARTBEAT", T(3, 10), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("HEARTBEAT", T(3, 18), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("HEARTBEAT", T(3, 26), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("LOCK", T(3, 30)),
        };
        foreach (var e in events) e["tz_offset_min"] = -240; // device em GMT-4

        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, events);
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();
        await RunAggregationAsync();

        var summaries = await SummariesAsync(device.DeviceId);
        Assert.Equal(2, summaries.Count); // um summary_date por dia LOCAL DO TENANT

        var dia1 = summaries[0];
        Assert.Equal(LocalDay(T(2, 30)), Day(dia1["summary_date"]));
        Assert.Equal(1800, I(dia1["seconds_active"])); // 02:30→03:00 UTC
        Assert.Equal(1800, I(dia1["seconds_on"]));
        Assert.Equal(T(2, 30), Ts(dia1["first_event_at"]));
        Assert.Equal(T(3, 0), Ts(dia1["last_event_at"])); // borda exata da meia-noite do tenant

        var dia2 = summaries[1];
        Assert.Equal(LocalDay(T(2, 30)).AddDays(1), Day(dia2["summary_date"]));
        Assert.Equal(1800, I(dia2["seconds_active"])); // 03:00→03:30 UTC
        Assert.Equal(T(3, 0), Ts(dia2["first_event_at"]));
        Assert.Equal(T(3, 30), Ts(dia2["last_event_at"]));

        // cross-check 11.3 NO CORTE: o rodapé da timeline de cada dia bate com o agregado
        // do dia. A janela da leitura (TimelineController.LocalMidnightUtc) e o source_day
        // da escrita (SplitAtLocalMidnights) são implementações independentes da mesma
        // meia-noite do tenant — sem este teste, uma regressão em qualquer uma quebraria a
        // consistência exatamente no cenário timezone sem nenhum teste vermelho.
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);
        var token = await AuthClient.LoginAsync(client, viewer);

        async Task<JsonElement> FooterAsync(string date)
        {
            using var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
                $"/api/v1/timeline/device?device_id={device.DeviceId}&date={date}", token);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"timeline falhou: {response.StatusCode} {body}");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("summary").Clone();
        }

        var footer1 = await FooterAsync(LocalDate(T(2, 30)));
        Assert.Equal(I(dia1["seconds_active"]), (int)footer1.GetProperty("seconds_active").GetInt64());
        Assert.Equal(I(dia1["seconds_on"]), (int)footer1.GetProperty("seconds_on").GetInt64());
        Assert.Equal(T(2, 30), footer1.GetProperty("first_event_at").GetDateTimeOffset());
        Assert.Equal(T(3, 0), footer1.GetProperty("last_event_at").GetDateTimeOffset()); // dia 1 TERMINA na meia-noite do tenant

        var footer2 = await FooterAsync(LocalDate(T(3, 30)));
        Assert.Equal(I(dia2["seconds_active"]), (int)footer2.GetProperty("seconds_active").GetInt64());
        Assert.Equal(I(dia2["seconds_on"]), (int)footer2.GetProperty("seconds_on").GetInt64());
        Assert.Equal(T(3, 0), footer2.GetProperty("first_event_at").GetDateTimeOffset()); // dia 2 COMEÇA na meia-noite do tenant
        Assert.Equal(T(3, 30), footer2.GetProperty("last_event_at").GetDateTimeOffset());
    }

    // ------------------------------------------------------------ (d) lane-máquina
    [Fact]
    public async Task LaneMaquina_OffCleanENoData_ForaDeSecondsOn_FirstLastNull()
    {
        var (client, device, _) = await SetupAsync("NB-AGG-MAQUINA");
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(11, 55), new Dictionary<string, object?> { ["process_name"] = "agg-maq.exe" }),
            f.Event("SYSTEM_SUSPEND", T(12, 0), windowsSid: null, windowsUser: null, sessionId: null),
            f.Event("SYSTEM_RESUME", T(13, 0), windowsSid: null, windowsUser: null, sessionId: null),
            f.Event("ACTIVE_WINDOW_CHANGED", T(13, 0, 10), new Dictionary<string, object?> { ["process_name"] = "agg-maq.exe" }),
            f.Event("HEARTBEAT", T(13, 5), new Dictionary<string, object?> { ["state"] = "active" }),
            // buraco de 20 min sem desligamento limpo → no_data (máquina)
            f.Event("ACTIVE_WINDOW_CHANGED", T(13, 25), new Dictionary<string, object?> { ["process_name"] = "agg-maq.exe" }),
            f.Event("LOCK", T(13, 30)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();
        await RunAggregationAsync();

        var summaries = await SummariesAsync(device.DeviceId);
        Assert.Equal(2, summaries.Count);

        // lane do usuário: 300 (11:55→12:00) + 290 (13:00:10→13:05) + 300 (13:25→13:30)
        var usuario = summaries.Single(s => (Guid)s["device_user_id"]! != Guid.Empty);
        Assert.Equal(890, I(usuario["seconds_active"]));
        Assert.Equal(890, I(usuario["seconds_on"])); // off_clean (3600s) e no_data (1200s) NÃO contam
        Assert.Equal(T(11, 55), Ts(usuario["first_event_at"]));
        Assert.Equal(T(13, 30), Ts(usuario["last_event_at"]));

        // lane-máquina (UUID zero): existe porque houve intervalos de máquina, mas zerada
        var maquina = summaries.Single(s => (Guid)s["device_user_id"]! == Guid.Empty);
        Assert.Equal(0, I(maquina["seconds_active"]));
        Assert.Equal(0, I(maquina["seconds_idle"]));
        Assert.Equal(0, I(maquina["seconds_locked"]));
        Assert.Equal(0, I(maquina["seconds_on"]));
        Assert.Null(maquina["first_event_at"]); // só off_clean/no_data → sem bordas de usuário
        Assert.Null(maquina["last_event_at"]);
    }

    // ------------------------------------------------------------ (e) classificação
    [Fact]
    public async Task Classificacao_TresBaldes_SomamSecondsActive()
    {
        var (client, device, tenantId) = await SetupAsync("NB-AGG-CLASS");
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(10, 0), new Dictionary<string, object?> { ["process_name"] = "agg-work.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(10, 9), new Dictionary<string, object?> { ["process_name"] = "agg-fun.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(10, 14), new Dictionary<string, object?> { ["process_name"] = "agg-unknown.exe" }),
            f.Event("LOCK", T(10, 19)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync(); // cria os apps no app_catalog e suja o dia

        // semeadura por SQL: CreateOrganizationAsync NÃO semeia categorias (seed é do backoffice)
        var catWork = Uuid7.NewUuid7();
        var catFun = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO categories (id, tenant_id, name, classification)
            VALUES (@cw, @t, 'Trabalho', 1), (@cf, @t, 'Lazer', -1)
            """, ("cw", catWork), ("cf", catFun), ("t", tenantId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = 'agg-work.exe'
            """, ("t", tenantId), ("c", catWork));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = 'agg-fun.exe'
            """, ("t", tenantId), ("c", catFun));

        await RunAggregationAsync();

        var lane = Assert.Single(await SummariesAsync(device.DeviceId));
        Assert.Equal(1140, I(lane["seconds_active"]));
        Assert.Equal(540, I(lane["seconds_work_related"]));      // agg-work.exe (+1)
        Assert.Equal(300, I(lane["seconds_not_work_related"]));  // agg-fun.exe (−1)
        Assert.Equal(300, I(lane["seconds_neutral"]));           // agg-unknown.exe sem mapeamento
        Assert.Equal(I(lane["seconds_active"]),
            I(lane["seconds_work_related"]) + I(lane["seconds_neutral"]) + I(lane["seconds_not_work_related"]));
    }

    // ------------------------------------------------------------ (f) idempotência
    [Fact]
    public async Task Idempotencia_DuasRodadas_MesmasLinhasEValores()
    {
        var (client, device, tenantId) = await SetupAsync("NB-AGG-IDEM");
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(14, 0), new Dictionary<string, object?> { ["process_name"] = "agg-idem.exe" }),
            f.Event("IDLE_START", T(14, 8), new Dictionary<string, object?> { ["last_input_at"] = Iso(T(14, 5)) }),
            f.Event("LOCK", T(14, 12)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        var processadosAntes = await RunAggregationAsync();
        Assert.True(processadosAntes >= 1, $"primeira rodada não processou nada ({processadosAntes})");
        var antes = Snapshot(await SummariesAsync(device.DeviceId), await AppUsageAsync(device.DeviceId));
        var computedAntes = await TestDb.ScalarAsync<DateTime>(fixture.Database.ConnectionString,
            "SELECT max(computed_at) FROM daily_device_summaries WHERE device_id = @d", ("d", device.DeviceId));

        // re-suja o dia (mesmo upsert no-op da intervalização) e roda de novo
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO dirty_days (tenant_id, device_id, day) VALUES (@t, @d, @day)
            ON CONFLICT (tenant_id, device_id, day) DO UPDATE SET day = EXCLUDED.day
            """, ("t", tenantId), ("d", device.DeviceId), ("day", LocalDay(T(14, 0))));
        var processadosDepois = await RunAggregationAsync();
        Assert.True(processadosDepois >= 1,
            $"o re-dirty não foi consumido — a segunda rodada não recomputou nada ({processadosDepois})");
        var depois = Snapshot(await SummariesAsync(device.DeviceId), await AppUsageAsync(device.DeviceId));
        var computedDepois = await TestDb.ScalarAsync<DateTime>(fixture.Database.ConnectionString,
            "SELECT max(computed_at) FROM daily_device_summaries WHERE device_id = @d", ("d", device.DeviceId));

        Assert.Equal(antes, depois);
        // prova que o delete+insert de fato RODOU (e não que o dia ficou intacto por engano)
        Assert.True(computedDepois > computedAntes,
            $"computed_at não avançou ({computedAntes:O} → {computedDepois:O}) — o rebuild da segunda rodada não aconteceu");

        static List<string> Snapshot(
            List<Dictionary<string, object?>> summaries, List<Dictionary<string, object?>> usage) =>
            summaries.Select(s =>
                    $"S|{Day(s["summary_date"])}|{s["device_user_id"]}|{s["seconds_active"]}|{s["seconds_idle"]}|" +
                    $"{s["seconds_locked"]}|{s["seconds_on"]}|{s["seconds_work_related"]}|{s["seconds_neutral"]}|" +
                    $"{s["seconds_not_work_related"]}|{s["first_event_at"]:O}|{s["last_event_at"]:O}|{s["data_incomplete"]}")
                .Concat(usage.Select(u =>
                    $"U|{Day(u["summary_date"])}|{u["device_user_id"]}|{u["process_name"]}|{u["seconds_active"]}|{u["focus_count"]}"))
                .ToList();
    }

    // ------------------------------------------------------------ (g) consumo de dirty_days
    [Fact]
    public async Task DirtyDays_ConsumidoNaRodada_ResujadoPorIngestaoRetroativa()
    {
        var (client, device, _) = await SetupAsync("NB-AGG-DIRTY");
        var f = new EventFactory();
        var ack1 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "agg-dirty.exe" }),
            f.Event("LOCK", T(9, 9)),
        });
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        await RunPipelineAsync();
        Assert.True(await DirtyCountAsync(device.DeviceId) >= 1, "pipeline não sujou o dia");

        await RunAggregationAsync();
        Assert.Equal(0, await DirtyCountAsync(device.DeviceId)); // linhas processadas sumiram
        var lane1 = Assert.Single(await SummariesAsync(device.DeviceId));
        Assert.Equal(540, I(lane1["seconds_active"]));

        // ingestão retroativa do MESMO dia: re-suja e a próxima rodada reflete o rebuild
        var ack2 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("UNLOCK", T(9, 15)),
            f.Event("LOCK", T(9, 24)),
        });
        (await AgentClient.ReadAckAsync(ack2)).Dispose();
        await RunPipelineAsync();
        Assert.True(await DirtyCountAsync(device.DeviceId) >= 1, "intervalização não re-sujou o dia");

        await RunAggregationAsync();
        Assert.Equal(0, await DirtyCountAsync(device.DeviceId));
        var lane2 = Assert.Single(await SummariesAsync(device.DeviceId));
        Assert.Equal(1080, I(lane2["seconds_active"])); // + active 09:15→09:24
        Assert.Equal(360, I(lane2["seconds_locked"]));  // locked 09:09→09:15
        Assert.Equal(T(9, 24), Ts(lane2["last_event_at"]));
    }

    // ------------------------------------------------------------ (h) data_incomplete
    [Fact]
    public async Task LacunaDeSeq_DataIncomplete_PropagadoParaODia()
    {
        var (client, device, _) = await SetupAsync("NB-AGG-LACUNA");
        var f1 = new EventFactory(startSeq: 100);
        var ack1 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f1.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "agg-seq.exe" }),
            f1.Event("HEARTBEAT", T(9, 1), new Dictionary<string, object?> { ["state"] = "active" }),
        });
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        var f2 = new EventFactory(startSeq: 105); // seq 102-104 nunca chegam
        var ack2 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f2.Event("ACTIVE_WINDOW_CHANGED", T(9, 5), new Dictionary<string, object?> { ["process_name"] = "agg-seq2.exe" }),
            f2.Event("LOCK", T(9, 9)),
        });
        (await AgentClient.ReadAckAsync(ack2)).Dispose();

        await RunPipelineAsync();
        await RunAggregationAsync();

        var lane = Assert.Single(await SummariesAsync(device.DeviceId));
        Assert.True((bool)lane["data_incomplete"]!); // bool_or dos intervalos da lane no dia
    }
}
