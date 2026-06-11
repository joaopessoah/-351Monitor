using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET /api/v1/timeline/team (Seção 7.4/8.5, F3.4): uma lane por device NÃO-archived
/// (lane vazia incluída), ordenação por nome de exibição, intervals com a MESMA shape e os
/// MESMOS merges do modo device, ETag para dias passados e auditoria team (DoD 11.3).
/// </summary>
[Collection(ApiCollection.Name)]
public class TeamTimelineEndpointTests(ApiTestFixture fixture)
{
    // "ontem" no dia LOCAL do tenant (GMT-3) — mesma justificativa do TimelineEndpointTests:
    // entre 21:00 e 00:00 locais a data UTC já virou e o dia viraria "hoje" (sem ETag).
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.AddHours(-3).Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m, int s = 0) => Base.AddHours(h).AddMinutes(m).AddSeconds(s);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o");

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, string Token, Guid OrgId, string FullKey)> SetupOrgAsync()
    {
        var org = await fixture.CreateOrganizationAsync($"TeamTL {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, token, org.Id, fullKey);
    }

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
    }

    private static async Task<JsonDocument> GetTeamAsync(
        HttpClient client, string token, string date, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/timeline/team?date={date}", token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>Dia padrão do device A (heartbeats &lt; 600 s — nada de no_data acidental).</summary>
    private async Task SeedDeviceAAsync(HttpClient client, EnrolledDevice device)
    {
        var factory = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(14, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe", ["window_title"] = "Orcamento_2026.xlsx - Excel",
            }),
            factory.Event("HEARTBEAT", T(14, 8), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(14, 16), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(14, 24), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("IDLE_START", T(14, 31, 40), new Dictionary<string, object?>
            {
                ["last_input_at"] = Iso(T(14, 26, 40)),
            }),
            factory.Event("IDLE_END", T(14, 40)),
            factory.Event("LOCK", T(14, 45)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
    }

    private async Task SeedDeviceBAsync(HttpClient client, EnrolledDevice device)
    {
        var factory = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
            }),
            factory.Event("HEARTBEAT", T(9, 5), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("LOCK", T(9, 8)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
    }

    // ------------------------------------------------ contrato: 2 lanes, merges, shape
    [Fact]
    public async Task DoisDevices_DuasLanes_MergesIdenticosAoModoDevice()
    {
        var (client, token, _, fullKey) = await SetupOrgAsync();
        var deviceA = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-AAA");
        var deviceB = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-BBB");
        await SeedDeviceAAsync(client, deviceA);
        await SeedDeviceBAsync(client, deviceB);
        await RunPipelineAsync();
        var date = LocalDate(T(14, 0));

        using var doc = await GetTeamAsync(client, token, date);
        var root = doc.RootElement;

        Assert.Equal(date, root.GetProperty("date").GetString());
        Assert.Equal(60, root.GetProperty("resolution_sec").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean()); // caso normal: sem cap
        Assert.True(root.TryGetProperty("server_time", out _));

        var lanes = root.GetProperty("lanes").EnumerateArray().ToList();
        Assert.Equal(2, lanes.Count);
        Assert.Equal(deviceA.DeviceId, lanes[0].GetProperty("device_id").GetGuid());
        Assert.Equal("NB-TT-AAA", lanes[0].GetProperty("device_name").GetString());
        Assert.True(lanes[0].TryGetProperty("device_tz_offset_min", out _));
        Assert.False(lanes[0].GetProperty("data_incomplete").GetBoolean());

        // merge correto na lane A (espelho do contrato do modo device: N5 retroativo do idle)
        var intervalsA = lanes[0].GetProperty("intervals").EnumerateArray().ToList();
        var active = intervalsA.First(i => i.GetProperty("state").GetString() == "active");
        Assert.Equal(T(14, 0), active.GetProperty("started_at").GetDateTimeOffset());
        Assert.Equal(T(14, 26, 40), active.GetProperty("ended_at").GetDateTimeOffset());
        Assert.Equal("excel.exe", active.GetProperty("app").GetProperty("process_name").GetString());
        Assert.Equal("Orcamento_2026.xlsx - Excel", active.GetProperty("window_title").GetString());
        var idle = intervalsA.First(i => i.GetProperty("state").GetString() == "idle");
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("app").ValueKind); // app só em active

        // mesma shape e MESMOS merges do modo device: array de intervals byte a byte igual
        foreach (var (lane, device) in new[] { (lanes[0], deviceA), (lanes[1], deviceB) })
        {
            using var req = AuthClient.AuthorizedRequest(
                HttpMethod.Get, $"/api/v1/timeline/device?device_id={device.DeviceId}&date={date}", token);
            var deviceResponse = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, deviceResponse.StatusCode);
            using var deviceDoc = JsonDocument.Parse(await deviceResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                deviceDoc.RootElement.GetProperty("intervals").GetRawText(),
                lane.GetProperty("intervals").GetRawText());
        }
    }

    // ------------------------------------------------ lanes: archived fora, vazia dentro
    [Fact]
    public async Task DeviceArchived_NaoGanhaLane()
    {
        var (client, token, _, fullKey) = await SetupOrgAsync();
        var vivo = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-VIVO");
        var arquivado = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-ARQ");
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET status = 'archived' WHERE id = @d", ("d", arquivado.DeviceId));

        using var doc = await GetTeamAsync(client, token, LocalDate(T(14, 0)));
        var ids = doc.RootElement.GetProperty("lanes").EnumerateArray()
            .Select(l => l.GetProperty("device_id").GetGuid()).ToList();

        Assert.Contains(vivo.DeviceId, ids);
        Assert.DoesNotContain(arquivado.DeviceId, ids);
    }

    [Fact]
    public async Task DeviceSemIntervalos_GanhaLaneVazia()
    {
        var (client, token, _, fullKey) = await SetupOrgAsync();
        var comDados = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-COM");
        var semDados = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-SEM");
        await SeedDeviceAAsync(client, comDados);
        await RunPipelineAsync();

        using var doc = await GetTeamAsync(client, token, LocalDate(T(14, 0)));
        var lanes = doc.RootElement.GetProperty("lanes").EnumerateArray().ToList();
        Assert.Equal(2, lanes.Count);

        // o gestor varre a equipe INTEIRA: quem não tem dado aparece com lane vazia
        var vazia = lanes.Single(l => l.GetProperty("device_id").GetGuid() == semDados.DeviceId);
        Assert.Empty(vazia.GetProperty("intervals").EnumerateArray());
        Assert.False(vazia.GetProperty("data_incomplete").GetBoolean());

        var cheia = lanes.Single(l => l.GetProperty("device_id").GetGuid() == comDados.DeviceId);
        Assert.NotEmpty(cheia.GetProperty("intervals").EnumerateArray());
    }

    // ------------------------------------------------ ordenação por nome de exibição
    [Fact]
    public async Task Lanes_OrdenadasPorNomeDeExibicao_DisplayNameVenceHostname()
    {
        var (client, token, _, fullKey) = await SetupOrgAsync();
        var zulu = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-ZULU");
        var mike = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-MIKE");
        // renomeado no portal: o nome de exibição (não o hostname) manda na ordenação
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET display_name = 'Alfa Estacao' WHERE id = @d", ("d", zulu.DeviceId));

        using var doc = await GetTeamAsync(client, token, LocalDate(T(14, 0)));
        var lanes = doc.RootElement.GetProperty("lanes").EnumerateArray().ToList();

        Assert.Equal(2, lanes.Count);
        Assert.Equal(zulu.DeviceId, lanes[0].GetProperty("device_id").GetGuid());
        Assert.Equal("Alfa Estacao", lanes[0].GetProperty("device_name").GetString());
        Assert.Equal(mike.DeviceId, lanes[1].GetProperty("device_id").GetGuid());
        Assert.Equal("NB-TT-MIKE", lanes[1].GetProperty("device_name").GetString());
    }

    // ------------------------------------------------ ETag (8.5)
    [Fact]
    public async Task DiaPassado_ETag_E_304()
    {
        var (client, token, _, fullKey) = await SetupOrgAsync();
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-ETAG");
        await SeedDeviceAAsync(client, device);
        await RunPipelineAsync();
        var date = LocalDate(T(14, 0));

        using var first = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/timeline/team?date={date}", token);
        var r1 = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etag), "ETag ausente para dia passado");

        using var second = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/timeline/team?date={date}", token);
        second.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await client.SendAsync(second);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    // ------------------------------------------------ auditoria (DoD 11.3)
    [Fact]
    public async Task Visualizacao_GravaAuditLog_TargetTeam()
    {
        var (client, token, orgId, fullKey) = await SetupOrgAsync();
        _ = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-AUD");

        (await GetTeamAsync(client, token, LocalDate(T(14, 0)))).Dispose();

        // dado pessoal de VÁRIAS pessoas: target_type team, sem alvo individual (target_id null)
        var count = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT count(*) FROM audit_log
            WHERE action = 'view_timeline' AND target_type = 'team'
              AND target_id IS NULL AND tenant_id = @t
            """,
            ("t", orgId));
        Assert.True(count >= 1, "view_timeline do modo equipe não foi auditado");
    }

    // ------------------------------------------------ cauda viva de hoje (por lane)
    [Fact]
    public async Task Hoje_CaudaNoData_SoNaLaneDoDeviceSilencioso()
    {
        var (client, token, _, fullKey) = await SetupOrgAsync();
        var silencioso = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-MUDO");
        var ativo = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-RECENTE");
        var now = DateTimeOffset.UtcNow;
        var f1 = new EventFactory();
        var ack1 = await AgentClient.SendBatchAsync(client, silencioso.DeviceToken, new[]
        {
            f1.Event("ACTIVE_WINDOW_CHANGED", now.AddMinutes(-40), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
            }),
            f1.Event("HEARTBEAT", now.AddMinutes(-31), new Dictionary<string, object?> { ["state"] = "active" }),
        });
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        var f2 = new EventFactory();
        var ack2 = await AgentClient.SendBatchAsync(client, ativo.DeviceToken, new[]
        {
            f2.Event("ACTIVE_WINDOW_CHANGED", now.AddMinutes(-10), new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe",
            }),
            f2.Event("HEARTBEAT", now.AddMinutes(-2), new Dictionary<string, object?> { ["state"] = "active" }),
        });
        (await AgentClient.ReadAckAsync(ack2)).Dispose();
        await RunPipelineAsync();

        // silêncio "real" SÓ no primeiro device: último contato há 31 min (> 600 s, N7) —
        // a ingestão tinha marcado agora; o segundo continua com contato recente
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE device_current_state SET last_contact_at = now() - interval '31 minutes' WHERE device_id = @d",
            ("d", silencioso.DeviceId));

        using var doc = await GetTeamAsync(client, token, LocalDate(now));
        var lanes = doc.RootElement.GetProperty("lanes").EnumerateArray().ToList();

        // a cauda sintetizada cai na lane CERTA (currentByDevice keyed por device_id)...
        var laneMuda = lanes.Single(l => l.GetProperty("device_id").GetGuid() == silencioso.DeviceId);
        var intervalsMudos = laneMuda.GetProperty("intervals").EnumerateArray().ToList();
        Assert.NotEmpty(intervalsMudos);
        var tail = intervalsMudos[^1];
        Assert.Equal("no_data", tail.GetProperty("state").GetString());
        Assert.True(tail.GetProperty("ended_at").GetDateTimeOffset() >= now.AddSeconds(-30));

        // ...e NÃO vaza para a lane do device com contato recente
        var laneViva = lanes.Single(l => l.GetProperty("device_id").GetGuid() == ativo.DeviceId);
        Assert.DoesNotContain(laneViva.GetProperty("intervals").EnumerateArray(),
            i => i.GetProperty("state").GetString() == "no_data");
    }

    // ------------------------------------------------ cap N21 (truncated)
    /// <summary>
    /// Semeia direto em activity_intervals (a timeline lê SEMPRE de intervals — F2) uma grade
    /// de <paramref name="sessions"/> sessões simultâneas (device_user_id distintos — terminal
    /// server / fast user switching) × <paramref name="minutes"/> intervalos de 1 min, a partir
    /// de T(9,0). Intervalos ≥ 1 min atravessam o MergeToResolution um a um, então a lane sai
    /// com sessions × minutes intervalos — é assim que UMA lane passa de 3.000 num dia de 1.440 min.
    /// </summary>
    private async Task SeedSimultaneousSessionsAsync(Guid orgId, Guid deviceId, int sessions, int minutes)
    {
        var start = T(9, 0);

        // partição mensal do dia semeado (a migration só cria mês corrente e próximo; se Base
        // cair no mês anterior — roda no dia 1º — ela ainda não existe). Mesma convenção de
        // nome/faixa do worker, então IF NOT EXISTS colide no nome certo.
        foreach (var anchor in new[] { start.AddDays(-2), start, start.AddDays(2) })
        {
            var month = new DateOnly(anchor.Year, anchor.Month, 1);
            await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                $"CREATE TABLE IF NOT EXISTS activity_intervals_{month:yyyyMM} PARTITION OF activity_intervals " +
                $"FOR VALUES FROM ('{month:yyyy-MM-dd}') TO ('{month.AddMonths(1):yyyy-MM-dd}')");
        }

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO activity_intervals
              (id, tenant_id, device_id, device_user_id, started_at, ended_at, state, data_incomplete, source_day)
            SELECT md5('m351-cap-' || @d::text || '-' || s || 'x' || n)::uuid,
                   @t, @d,
                   md5('m351-sess-' || @d::text || '-' || s)::uuid,
                   @start + make_interval(mins => n),
                   @start + make_interval(mins => n + 1),
                   'active', false, @day
            FROM generate_series(0, @sessions - 1) AS s,
                 generate_series(0, @minutes - 1) AS n
            """,
            ("t", orgId), ("d", deviceId), ("start", start),
            ("day", DateOnly.ParseExact(LocalDate(start), "yyyy-MM-dd")), ("sessions", sessions), ("minutes", minutes));
    }

    [Fact]
    public async Task CapN21_LaneQueEstouraFicaInteiraDeFora_TruncatedTrue()
    {
        var (client, token, orgId, fullKey) = await SetupOrgAsync();
        var primeira = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-CAP-A");
        var segunda = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-CAP-B");
        // 1.600 intervalos por device: lane A entra (1600 ≤ 3000); lane B estouraria a soma
        // (3200 > 3000) e fica INTEIRA de fora — prefixo da ordem de exibição + truncated
        await SeedSimultaneousSessionsAsync(orgId, primeira.DeviceId, sessions: 2, minutes: 800);
        await SeedSimultaneousSessionsAsync(orgId, segunda.DeviceId, sessions: 2, minutes: 800);

        using var doc = await GetTeamAsync(client, token, LocalDate(T(9, 0)));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("truncated").GetBoolean());
        var lanes = root.GetProperty("lanes").EnumerateArray().ToList();
        var lane = Assert.Single(lanes);
        Assert.Equal(primeira.DeviceId, lane.GetProperty("device_id").GetGuid());
        Assert.Equal(1600, lane.GetProperty("intervals").GetArrayLength()); // lane incluída fica intacta
    }

    [Fact]
    public async Task CapN21_PrimeiraLaneSozinhaEstoura_TruncaDentroDaLane()
    {
        var (client, token, orgId, fullKey) = await SetupOrgAsync();
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TT-CAP-1");
        // 3.200 intervalos num device só: o único caminho em que o teto estouraria DENTRO de
        // uma lane — corta com o mesmo Take do modo device e truncated = true (nunca false)
        await SeedSimultaneousSessionsAsync(orgId, device.DeviceId, sessions: 4, minutes: 800);

        using var doc = await GetTeamAsync(client, token, LocalDate(T(9, 0)));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("truncated").GetBoolean());
        var lanes = root.GetProperty("lanes").EnumerateArray().ToList();
        var lane = Assert.Single(lanes);
        Assert.Equal(3000, lane.GetProperty("intervals").GetArrayLength()); // resposta jamais excede N21
    }

    // ------------------------------------------------ validação
    [Fact]
    public async Task DataInvalida_Retorna400()
    {
        var (client, token, _, _) = await SetupOrgAsync();
        (await GetTeamAsync(client, token, "10-06-2026", HttpStatusCode.BadRequest)).Dispose();
    }
}
