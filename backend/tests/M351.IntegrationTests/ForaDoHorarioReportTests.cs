using System.Net;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Exports;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET /api/v1/reports/fora-do-horario e o CSV fora_horario_csv: painel de ATIVIDADE FORA DO
/// HORÁRIO DE TRABALHO. Cobre as somas nos três baldes (antes do início, depois do fim e dias
/// fora da escala), os DOIS estados vazios explicativos que a verificação adversarial exigiu
/// (horário de trabalho não configurado e coleta restrita ao horário de trabalho, em que zero
/// seria um número falso), a auditoria view_report condicional ao recorte pessoal, as
/// validações 400/404 e o disclaimer da Portaria 671/MTE no arquivo exportado.
///
/// Dados semeados pelo pipeline REAL (ingestão → intervalização), como nos demais testes de
/// relatório: a fonte AQUI é activity_intervals, nunca os agregados diários — que não têm
/// granularidade horária e por isso não sabem dizer se o tempo caiu dentro ou fora da janela.
/// </summary>
[Collection(ApiCollection.Name)]
public class ForaDoHorarioReportTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    /// <summary>Texto VERBATIM do DoD 11.3 — literal de propósito (não referencia a constante do produto).</summary>
    private const string Disclaimer =
        "Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de "
        + "ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT.";

    private const string ForaHeader =
        "Dispositivo;Tempo ativo no período;Atividade fora do horário;Antes do horário;"
        + "Depois do horário;Em dias fora da escala;Dias com atividade fora;Horas decimais (fora do horário)";

    /// <summary>
    /// Instante UTC do dia-base. O fuso da org de teste é America/Sao_Paulo (GMT-3), então
    /// T(9,0) é 06:00 local, T(12,0) é 09:00 local e T(22,0) é 19:00 local — todos no MESMO
    /// dia local, o que mantém as somas por balde legíveis.
    /// </summary>
    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    /// <summary>Dia ISO (1 = segunda ... 7 = domingo) de um dia local yyyy-MM-dd.</summary>
    private static int IsoDay(string localDate)
    {
        var day = DateOnly.ParseExact(localDate, "yyyy-MM-dd");
        return day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
    }

    private async Task<(HttpClient Client, Guid TenantId, string Token, string FullKey)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, token, fullKey);
    }

    /// <summary>Horário de trabalho da org: dias ISO + limites locais (o jsonb cru de business_hours).</summary>
    private async Task SetBusinessHoursAsync(Guid tenantId, int[] isoDays, string start, string end)
    {
        var json = JsonSerializer.Serialize(new { days = isoDays, start, end });
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE organizations SET business_hours = @bh::jsonb WHERE id = @t",
            ("bh", json), ("t", tenantId));
    }

    /// <summary>Janela de coleta do agente (a linha nasce no enroll).</summary>
    private async Task SetCollectionWindowAsync(Guid tenantId, string json)
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE tenant_agent_configs SET collection_window = @cw::jsonb WHERE tenant_id = @t",
            ("cw", json), ("t", tenantId));
    }

    private async Task RunIntervalizationAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
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

    private async Task<long> ViewReportCountAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'", ("t", tenantId));

    // ------------------------------------------------------------ somas nos três baldes
    [Fact]
    public async Task SomaTempoAtivoForaDaJanelaEmTresBaldes()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("ForaSoma");
        var device1 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FORA-1");
        var device2 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FORA-2");

        // janela 08:00-18:00 local em TODOS os dias ISO: o dia da semana do teste não interfere
        await SetBusinessHoursAsync(tenantId, [1, 2, 3, 4, 5, 6, 7], "08:00", "18:00");

        await SeedActiveAsync(client, device1, "fora-a.exe", T(9, 0), 9);    // 06:00 local → ANTES (540 s)
        await SeedActiveAsync(client, device1, "fora-b.exe", T(12, 0), 9);   // 09:00 local → DENTRO (540 s)
        await SeedActiveAsync(client, device1, "fora-c.exe", T(22, 0), 5);   // 19:00 local → DEPOIS (300 s)
        await SeedActiveAsync(client, device2, "fora-d.exe", T(23, 0), 5);   // 20:00 local → DEPOIS (300 s)
        await RunIntervalizationAsync();

        var date = LocalDate(T(9, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}&include_devices=true");
        var root = doc.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("America/Sao_Paulo", root.GetProperty("timezone").GetString());
        Assert.Equal("ALWAYS", root.GetProperty("collection_window_mode").GetString());
        Assert.Equal("08:00", root.GetProperty("business_hours").GetProperty("start").GetString());
        Assert.Equal("18:00", root.GetProperty("business_hours").GetProperty("end").GetString());

        var totals = root.GetProperty("totals");
        Assert.Equal(1680, totals.GetProperty("seconds_active").GetInt64());          // 540+540+300+300
        Assert.Equal(1140, totals.GetProperty("seconds_outside").GetInt64());         // tudo menos o bloco de dentro
        Assert.Equal(540, totals.GetProperty("seconds_before").GetInt64());
        Assert.Equal(600, totals.GetProperty("seconds_after").GetInt64());
        Assert.Equal(0, totals.GetProperty("seconds_non_business_day").GetInt64());
        Assert.Equal(2, totals.GetProperty("devices_with_activity_outside").GetInt32());

        // ordem: maior tempo fora primeiro
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(device1.DeviceId, items[0].GetProperty("device_id").GetGuid());
        Assert.Equal("NB-FORA-1", items[0].GetProperty("device_name").GetString());
        Assert.Equal(1380, items[0].GetProperty("seconds_active").GetInt64());
        Assert.Equal(840, items[0].GetProperty("seconds_outside").GetInt64());
        Assert.Equal(540, items[0].GetProperty("seconds_before").GetInt64());
        Assert.Equal(300, items[0].GetProperty("seconds_after").GetInt64());
        Assert.Equal(1, items[0].GetProperty("days_with_activity_outside").GetInt32());

        Assert.Equal(device2.DeviceId, items[1].GetProperty("device_id").GetGuid());
        Assert.Equal(300, items[1].GetProperty("seconds_outside").GetInt64());
        Assert.Equal(0, items[1].GetProperty("seconds_before").GetInt64());

        Assert.Equal(2, root.GetProperty("total").GetInt32());
    }

    // ------------------------------------------------------------ dia fora da escala
    [Fact]
    public async Task DiaForaDaEscala_TodoOTempoAtivoContaComoFora()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("ForaEscala");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FORA-ESC");

        var date = LocalDate(T(12, 0));
        // escala declarada em um dia ISO que NÃO é o do teste: o dia inteiro fica fora dela
        var outroDia = IsoDay(date) == 7 ? 1 : IsoDay(date) + 1;
        await SetBusinessHoursAsync(tenantId, [outroDia], "08:00", "18:00");

        // 09:00 local: dentro dos limites de hora, mas em dia fora da escala
        await SeedActiveAsync(client, device, "fora-esc.exe", T(12, 0), 9);
        await RunIntervalizationAsync();

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}&include_devices=true");
        var totals = doc.RootElement.GetProperty("totals");

        Assert.Equal(540, totals.GetProperty("seconds_active").GetInt64());
        Assert.Equal(540, totals.GetProperty("seconds_outside").GetInt64());
        Assert.Equal(540, totals.GetProperty("seconds_non_business_day").GetInt64());
        Assert.Equal(0, totals.GetProperty("seconds_before").GetInt64());
        Assert.Equal(0, totals.GetProperty("seconds_after").GetInt64());

        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(device.DeviceId, item.GetProperty("device_id").GetGuid());
        Assert.Equal(540, item.GetProperty("seconds_non_business_day").GetInt64());
    }

    // ------------------------------------------------------------ estado vazio 1: sem janela declarada
    [Fact]
    public async Task SemHorarioDeTrabalho_EstadoVazioExplicativoSemNumero()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("ForaSemBh");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FORA-SEMBH");

        // atividade EXISTE; o que falta é a janela declarada — mesmo assim, nada de número
        await SeedActiveAsync(client, device, "fora-sembh.exe", T(9, 0), 9);
        await RunIntervalizationAsync();

        var date = LocalDate(T(9, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}&include_devices=true");
        var root = doc.RootElement;

        Assert.Equal("horario_nao_configurado", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("totals").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("business_hours").ValueKind);
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.Equal(0, root.GetProperty("total").GetInt32());

        // business_hours malformada tem o MESMO tratamento de ausente (fim antes do início)
        await SetBusinessHoursAsync(tenantId, [1, 2, 3, 4, 5], "18:00", "08:00");
        using var malformada = await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}");
        Assert.Equal("horario_nao_configurado", malformada.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, malformada.RootElement.GetProperty("totals").ValueKind);

        // sem número entregue não há dado pessoal lido: nada de view_report
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ estado vazio 2: coleta restrita
    [Fact]
    public async Task ColetaRestritaAoHorario_EstadoVazioExplicativoSemNumero()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("ForaColeta");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FORA-COL");

        await SetBusinessHoursAsync(tenantId, [1, 2, 3, 4, 5, 6, 7], "08:00", "18:00");
        await SetCollectionWindowAsync(tenantId,
            """{"mode":"BUSINESS_HOURS","days":[1,2,3,4,5],"start":"08:00","end":"18:00"}""");

        await SeedActiveAsync(client, device, "fora-col.exe", T(9, 0), 9);
        await RunIntervalizationAsync();

        var date = LocalDate(T(9, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}&include_devices=true");
        var root = doc.RootElement;

        // fora da janela NÃO há coleta por design: zero seria enganoso, então não vem número
        Assert.Equal("coleta_restrita_ao_horario", root.GetProperty("status").GetString());
        Assert.Equal("BUSINESS_HOURS", root.GetProperty("collection_window_mode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("totals").ValueKind);
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        // a janela declarada continua no contrato: a tela explica qual é o horário
        Assert.Equal("08:00", root.GetProperty("business_hours").GetProperty("start").GetString());

        Assert.Equal(0L, await ViewReportCountAsync(tenantId));
    }

    // ------------------------------------------------------------ auditoria condicional
    [Fact]
    public async Task Auditoria_AgregadoDeEquipeNaoAuditaRecortePessoalAudita()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("ForaAudit");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FORA-AUD");

        await SetBusinessHoursAsync(tenantId, [1, 2, 3, 4, 5, 6, 7], "08:00", "18:00");
        await SeedActiveAsync(client, device, "fora-aud.exe", T(9, 0), 9);
        await RunIntervalizationAsync();

        var date = LocalDate(T(9, 0));

        // card da Visão Geral: só os totais, agregado de EQUIPE → não audita
        using (var doc = await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}"))
        {
            Assert.Equal(540, doc.RootElement.GetProperty("totals").GetProperty("seconds_outside").GetInt64());
            Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());
        }
        Assert.Equal(0L, await ViewReportCountAsync(tenantId));

        // lista por dispositivo: recorte pessoal → view_report com alvo team
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}&include_devices=true")).Dispose();
        Assert.Equal(1L, await ViewReportCountAsync(tenantId));

        // filtro por device: view_report com alvo device
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from={date}&to={date}&device_ids={device.DeviceId}")).Dispose();
        Assert.Equal(2L, await ViewReportCountAsync(tenantId));

        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT target_type, target_id, detail->>'report' AS report
            FROM audit_log
            WHERE tenant_id = @t AND action = 'view_report' AND target_id IS NOT NULL
            """, ("t", tenantId));
        Assert.Equal("device", (string)audit!["target_type"]!);
        Assert.Equal(device.DeviceId, (Guid)audit["target_id"]!);
        Assert.Equal("fora_do_horario", (string)audit["report"]!);
    }

    // ------------------------------------------------------------ validações 400/404
    [Fact]
    public async Task Validacao_DatasEDeviceIds()
    {
        var (client, tenantId, token, _) = await SetupAsync("ForaVal");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-FORA-VAL");
        await SetBusinessHoursAsync(tenantId, [1, 2, 3, 4, 5], "08:00", "18:00");

        // mesma régua de datas do dashboard: formato, from > to e teto de 92 dias
        (await GetJsonAsync(client, token,
            "/api/v1/reports/fora-do-horario?to=2026-06-07", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/fora-do-horario?from=2026-06-10&to=2026-06-01", HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            "/api/v1/reports/fora-do-horario?from=2026-01-01&to=2026-04-03", HttpStatusCode.BadRequest)).Dispose();

        // device_ids malformado → 400; uuid fora do tenant → 404 (nunca 403 — Princípio 4)
        (await GetJsonAsync(client, token,
            "/api/v1/reports/fora-do-horario?from=2026-06-01&to=2026-06-07&device_ids=nao-e-uuid",
            HttpStatusCode.BadRequest)).Dispose();
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from=2026-06-01&to=2026-06-07&device_ids={Uuid7.NewUuid7()}",
            HttpStatusCode.NotFound)).Dispose();
        (await GetJsonAsync(client, token,
            $"/api/v1/reports/fora-do-horario?from=2026-06-01&to=2026-06-07&device_ids={device.Id},{Uuid7.NewUuid7()}",
            HttpStatusCode.NotFound)).Dispose();

        // período sem dado nenhum: 200 com zeros HONESTOS (a janela existe, só não houve atividade)
        using var doc = await GetJsonAsync(client, token,
            "/api/v1/reports/fora-do-horario?from=2020-01-01&to=2020-01-31&include_devices=true");
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("totals").GetProperty("seconds_outside").GetInt64());
        Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());

        // os probes que levaram 400/404 não deixam rastro de view_report
        Assert.Equal(1L, await ViewReportCountAsync(tenantId)); // só a leitura 200 com include_devices
    }

    // ------------------------------------------------------------ CSV: gate de configuração + disclaimer
    [Fact]
    public async Task ExportCsv_GateDeConfiguracaoEArquivoComDisclaimer()
    {
        var (client, tenantId, token, fullKey) = await SetupAsync("ForaCsv");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FORA-CSV");

        await SeedActiveAsync(client, device, "fora-csv.exe", T(9, 0), 9);   // 06:00 local → antes
        await SeedActiveAsync(client, device, "fora-csv2.exe", T(22, 0), 5); // 19:00 local → depois
        await RunIntervalizationAsync();

        var date = LocalDate(T(9, 0));
        object Body() => new
        {
            kind = "fora_horario_csv",
            @params = new Dictionary<string, object?> { ["from"] = date, ["to"] = date },
        };

        // sem horário de trabalho declarado: 409 explicativo, jamais um CSV de zeros
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/exports", token, Body()))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await SetBusinessHoursAsync(tenantId, [1, 2, 3, 4, 5, 6, 7], "08:00", "18:00");

        // com a coleta restrita ao próprio horário de trabalho: 409 pelo mesmo motivo
        await SetCollectionWindowAsync(tenantId,
            """{"mode":"BUSINESS_HOURS","days":[1,2,3,4,5],"start":"08:00","end":"18:00"}""");
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/exports", token, Body()))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await SetCollectionWindowAsync(tenantId, """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");

        Guid jobId;
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/exports", token, Body()))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        await using (var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            var service = new ExportService(dataSource, fixture.ExportsDirectory);
            while (await service.RunOnceAsync() > 0) { }
        }

        using var download = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        var file = await client.SendAsync(download);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Contains($"fora-do-horario_{date}_{date}.csv",
            file.Content.Headers.ContentDisposition!.ToString());

        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal(0xEF, bytes[0]); // BOM UTF-8 (Excel pt-BR)
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");

        Assert.Equal(ForaHeader, lines[0]);
        Assert.Equal("NB-FORA-CSV;14min;14min;9min;5min;0s;1;0,23", lines[1]);
        Assert.Equal(Disclaimer, lines[2]); // disclaimer da Portaria 671 herdado da jornada
        Assert.Equal("", lines[3]);
        Assert.Equal(4, lines.Length);
    }
}
