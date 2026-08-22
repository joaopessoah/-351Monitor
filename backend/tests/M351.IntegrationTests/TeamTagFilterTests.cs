using System.Net;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Exports;
using M351.IntegrationTests.Support;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// Filtro por etiqueta de equipe (F5, ?tag): "me mostra só o comercial" é a primeira pergunta
/// do gestor com mais de 30 máquinas, e até aqui a resposta era exportar CSV. É filtro de
/// VISUALIZAÇÃO, não escopo de permissão (o papel Manager-por-equipe segue adiado para a v1.1):
/// qualquer papel continua vendo tudo, só escolhe o recorte exibido.
///
/// A MESMA etiqueta recorta dashboards, timeline de equipe, RELATÓRIOS (uso, jornada e
/// atividade fora do horário) e os CSVs assíncronos — tela e arquivo saem do mesmo recorte
/// (DoD 11.3). O recorte é sempre agregado: a etiqueta escolhe QUEM entra na conta, jamais
/// coloca duas equipes lado a lado em ranking.
/// </summary>
[Collection(ApiCollection.Name)]
public class TeamTagFilterTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string Token)> SetupAsync(string orgName)
    {
        var org = await fixture.CreateOrganizationAsync(orgName);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        return (client, org.Id, await AuthClient.LoginAsync(client, viewer));
    }

    private async Task SetTagsAsync(Guid deviceId, params string[] tags) =>
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET tags = @tags WHERE id = @id", ("id", deviceId), ("tags", tags));

    private async Task SeedSummaryAsync(Guid tenantId, Guid deviceId, DateOnly date, int activeSeconds) =>
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_on, computed_at)
            VALUES (@t, @day, @d, @u, @a, @a, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", Uuid7.NewUuid7()), ("a", activeSeconds));

    /// <summary>Um ciclo do worker de exports com o MESMO diretório que a API serve.</summary>
    private async Task DrainExportsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new ExportService(dataSource, fixture.ExportsDirectory);
        while (await service.RunOnceAsync() > 0) { }
    }

    /// <summary>POST /exports + drenagem da fila; devolve as LINHAS do CSV baixado (sem BOM).</summary>
    private async Task<string[]> ExportCsvLinesAsync(HttpClient client, string token, object body)
    {
        Guid jobId;
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/exports", token, body))
        {
            var post = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
            using var posted = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            jobId = posted.RootElement.GetProperty("id").GetGuid();
        }

        await DrainExportsAsync();

        using var downloadRequest = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        var download = await client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        // o arquivo nasce com BOM UTF-8 (Excel pt-BR): pula os 3 primeiros bytes
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");
    }

    [Fact]
    public async Task Summary_ComTag_SomaSoOsDevicesDaEquipe()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Summary");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var comercial = await fixture.CreateDeviceAsync(tenantId, "NB-COMERCIAL");
        await SetTagsAsync(comercial.Id, "comercial");
        await SeedSummaryAsync(tenantId, comercial.Id, hoje, 3600);

        var financeiro = await fixture.CreateDeviceAsync(tenantId, "NB-FINANCEIRO");
        await SetTagsAsync(financeiro.Id, "financeiro");
        await SeedSummaryAsync(tenantId, financeiro.Id, hoje, 7200);

        var range = $"from={hoje:yyyy-MM-dd}&to={hoje:yyyy-MM-dd}";

        // sem tag: soma a organização inteira
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(10800, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
            Assert.Equal(2, body.RootElement.GetProperty("totals").GetProperty("device_count").GetInt32());
        }

        // com tag: só a equipe pedida
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}&tag=comercial", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(3600, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
            Assert.Equal(1, body.RootElement.GetProperty("totals").GetProperty("device_count").GetInt32());
        }

        // etiqueta inexistente: recorte vazio, não 404 (tag não é recurso com dono)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}&tag=inexistente", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(0, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
        }

        // tag vazia equivale a sem filtro (o portal pode mandar o parâmetro sempre)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}&tag=", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(10800, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
        }
    }

    [Fact]
    public async Task Presence_ETimelineTeam_RespeitamATag()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Presença");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var suporte = await fixture.CreateDeviceAsync(tenantId, "NB-SUPORTE");
        await SetTagsAsync(suporte.Id, "suporte", "noturno");
        var outro = await fixture.CreateDeviceAsync(tenantId, "NB-OUTRO");
        await SetTagsAsync(outro.Id, "financeiro");

        // presença precisa de linha em device_current_state
        foreach (var id in new[] { suporte.Id, outro.Id })
        {
            await TestDb.ExecuteAsync(Cs, """
                INSERT INTO device_current_state (
                    tenant_id, device_id, state, last_contact_at, updated_at)
                VALUES (@t, @d, 'active', now(), now())
                """, ("t", tenantId), ("d", id));
        }

        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/dashboard/presence?tag=suporte", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items);
            Assert.Equal("NB-SUPORTE", items[0].GetProperty("hostname").GetString());
        }

        // timeline de equipe: uma lane por device do recorte (lane vazia também conta)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/timeline/team?date={hoje:yyyy-MM-dd}&tag=suporte", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var lanes = body.RootElement.GetProperty("lanes").EnumerateArray().ToList();
            Assert.Single(lanes);
            Assert.Equal("NB-SUPORTE", lanes[0].GetProperty("device_name").GetString());
        }
    }

    [Fact]
    public async Task TopApps_ComTag_RecortaRankingEDenominador()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Apps");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var comercial = await fixture.CreateDeviceAsync(tenantId, "NB-APPS-COM");
        await SetTagsAsync(comercial.Id, "comercial");
        var financeiro = await fixture.CreateDeviceAsync(tenantId, "NB-APPS-FIN");
        await SetTagsAsync(financeiro.Id, "financeiro");

        var appExcel = Uuid7.NewUuid7();
        var appChrome = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO app_catalog (id, process_name, display_name, curated)
            VALUES (@e, @en, 'Excel', false), (@c, @cn, 'Chrome', false)
            """,
            ("e", appExcel), ("en", $"excel-{Guid.NewGuid():N}.exe"),
            ("c", appChrome), ("cn", $"chrome-{Guid.NewGuid():N}.exe"));

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_app_usage (
                tenant_id, summary_date, device_id, device_user_id, app_id, seconds_active, focus_count)
            VALUES (@t, @day, @dcom, @u1, @excel, 3600, 10),
                   (@t, @day, @dfin, @u2, @chrome, 7200, 20)
            """,
            ("t", tenantId), ("day", hoje), ("dcom", comercial.Id), ("dfin", financeiro.Id),
            ("u1", Uuid7.NewUuid7()), ("u2", Uuid7.NewUuid7()), ("excel", appExcel), ("chrome", appChrome));

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/top-apps?from={hoje:yyyy-MM-dd}&to={hoje:yyyy-MM-dd}&tag=comercial", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Excel", items[0].GetProperty("display_name").GetString());

        // o denominador da porcentagem TAMBÉM é recortado (senão a soma passaria de 100%)
        Assert.Equal(3600, body.RootElement.GetProperty("total_seconds_active").GetInt64());
    }

    [Fact]
    public async Task Usage_ComTag_RecortaLinhasEDenominador()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Uso");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var comercial = await fixture.CreateDeviceAsync(tenantId, "NB-USO-COM");
        await SetTagsAsync(comercial.Id, "comercial");
        await SeedSummaryAsync(tenantId, comercial.Id, hoje, 3600);

        var financeiro = await fixture.CreateDeviceAsync(tenantId, "NB-USO-FIN");
        await SetTagsAsync(financeiro.Id, "financeiro");
        await SeedSummaryAsync(tenantId, financeiro.Id, hoje, 7200);

        var range = $"from={hoje:yyyy-MM-dd}&to={hoje:yyyy-MM-dd}";

        // sem tag: a organização inteira, com o denominador somando os dois dispositivos
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/reports/usage?{range}&group_by=device", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(10800, body.RootElement.GetProperty("total_seconds_active").GetInt64());
        }

        // com tag: só a equipe pedida, e o denominador acompanha, senão a soma das linhas
        // exibidas passaria de 100%
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/reports/usage?{range}&group_by=device&tag=comercial", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("NB-USO-COM", item.GetProperty("device_name").GetString());
            Assert.Equal(3600, item.GetProperty("seconds_active").GetInt64());
            Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(3600, body.RootElement.GetProperty("total_seconds_active").GetInt64());
        }

        // a etiqueta usada fica na trilha view_report (group_by=device é dado pessoal)
        var detail = await TestDb.ScalarAsync<string>(Cs, """
            SELECT detail->>'tag' FROM audit_log
            WHERE tenant_id = @t AND action = 'view_report' AND detail->>'tag' IS NOT NULL
            ORDER BY occurred_at DESC LIMIT 1
            """, ("t", tenantId));
        Assert.Equal("comercial", detail);

        // tag vazia equivale a sem filtro (o portal pode mandar o parâmetro sempre)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/reports/usage?{range}&group_by=device&tag=", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(10800, body.RootElement.GetProperty("total_seconds_active").GetInt64());
        }
    }

    [Fact]
    public async Task Jornada_ComTagInexistente_RecorteVazioSem404()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Jornada");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var suporte = await fixture.CreateDeviceAsync(tenantId, "NB-JOR-SUP");
        await SetTagsAsync(suporte.Id, "suporte");
        await SeedSummaryAsync(tenantId, suporte.Id, hoje, 3600);

        var outro = await fixture.CreateDeviceAsync(tenantId, "NB-JOR-OUT");
        await SetTagsAsync(outro.Id, "financeiro");
        await SeedSummaryAsync(tenantId, outro.Id, hoje, 7200);

        var range = $"from={hoje:yyyy-MM-dd}&to={hoje:yyyy-MM-dd}";

        // com tag: uma linha por device do recorte × dia do range
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/reports/jornada?{range}&tag=suporte", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("NB-JOR-SUP", item.GetProperty("device_name").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
            var totais = Assert.Single(body.RootElement.GetProperty("device_totals").EnumerateArray());
            Assert.Equal(3600, totais.GetProperty("seconds_active").GetInt64());
        }

        // etiqueta inexistente: recorte VAZIO com 200, nunca 404, etiqueta não é recurso com
        // dono, então não há existência a confirmar ou negar (mesma régua do dashboard)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/reports/jornada?{range}&tag=inexistente", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(0, body.RootElement.GetProperty("total").GetInt32());
            Assert.Empty(body.RootElement.GetProperty("device_totals").EnumerateArray());
        }
    }

    [Fact]
    public async Task ExportCsv_ComTag_GeraArquivoDoMesmoRecorteDaTela()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Export");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        var dia = hoje.ToString("yyyy-MM-dd");

        var comercial = await fixture.CreateDeviceAsync(tenantId, "NB-CSV-COM");
        await SetTagsAsync(comercial.Id, "comercial");
        await SeedSummaryAsync(tenantId, comercial.Id, hoje, 3600);

        var financeiro = await fixture.CreateDeviceAsync(tenantId, "NB-CSV-FIN");
        await SetTagsAsync(financeiro.Id, "financeiro");
        await SeedSummaryAsync(tenantId, financeiro.Id, hoje, 7200);

        // usage_csv: só as linhas da equipe pedida
        var uso = await ExportCsvLinesAsync(client, token, new
        {
            kind = "usage_csv",
            @params = new Dictionary<string, object?>
            {
                ["from"] = dia, ["to"] = dia, ["group_by"] = "device", ["tag"] = "comercial",
            },
        });
        Assert.Contains(uso, l => l.StartsWith("NB-CSV-COM;", StringComparison.Ordinal));
        Assert.DoesNotContain(uso, l => l.StartsWith("NB-CSV-FIN;", StringComparison.Ordinal));

        // jornada_csv: mesmo recorte, com o disclaimer da Portaria 671 preservado
        var jornada = await ExportCsvLinesAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = dia, ["to"] = dia, ["tag"] = "comercial" },
        });
        Assert.Contains(jornada, l => l.Contains("NB-CSV-COM", StringComparison.Ordinal));
        Assert.DoesNotContain(jornada, l => l.Contains("NB-CSV-FIN", StringComparison.Ordinal));
        Assert.Contains(jornada, l => l.Contains("Portaria 671/MTE", StringComparison.Ordinal));

        // a etiqueta entra nos params normalizados e, com eles, na trilha export_csv
        var tagNaTrilha = await TestDb.ScalarAsync<string>(Cs, """
            SELECT detail->'params'->>'tag' FROM audit_log
            WHERE tenant_id = @t AND action = 'export_csv'
            ORDER BY occurred_at DESC LIMIT 1
            """, ("t", tenantId));
        Assert.Equal("comercial", tagNaTrilha);
    }

    [Fact]
    public async Task Relatorios_ComTagIgual_NaoAtravessamTenants()
    {
        var (clientA, tenantA, tokenA) = await SetupAsync("Org Tag Cross A");
        var (_, tenantB, _) = await SetupAsync("Org Tag Cross B");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        var dia = hoje.ToString("yyyy-MM-dd");

        // a MESMA etiqueta existe nos dois tenants: nomes de equipe são livres e se repetem
        var deviceA = await fixture.CreateDeviceAsync(tenantA, "NB-CROSS-A");
        await SetTagsAsync(deviceA.Id, "comercial");
        await SeedSummaryAsync(tenantA, deviceA.Id, hoje, 3600);

        var deviceB = await fixture.CreateDeviceAsync(tenantB, "NB-CROSS-B");
        await SetTagsAsync(deviceB.Id, "comercial");
        await SeedSummaryAsync(tenantB, deviceB.Id, hoje, 7200);

        var range = $"from={dia}&to={dia}&tag=comercial";

        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/reports/usage?{range}&group_by=device", tokenA))
        {
            var response = await clientA.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("NB-CROSS-A", item.GetProperty("device_name").GetString());
            Assert.Equal(3600, body.RootElement.GetProperty("total_seconds_active").GetInt64());
        }

        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/reports/jornada?{range}", tokenA))
        {
            var response = await clientA.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("NB-CROSS-A", item.GetProperty("device_name").GetString());
        }

        // e o CSV do tenant A também para na fronteira do tenant
        var linhas = await ExportCsvLinesAsync(clientA, tokenA, new
        {
            kind = "usage_csv",
            @params = new Dictionary<string, object?>
            {
                ["from"] = dia, ["to"] = dia, ["group_by"] = "device", ["tag"] = "comercial",
            },
        });
        Assert.Contains(linhas, l => l.StartsWith("NB-CROSS-A;", StringComparison.Ordinal));
        Assert.DoesNotContain(linhas, l => l.Contains("NB-CROSS-B", StringComparison.Ordinal));
    }
}
