using System.Net;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Exports;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Exports CSV assíncronos (F3.5): POST /exports → queued (com trilha export_csv) →
/// ExportService.RunOnceAsync → done (row_count, expires_at) → download. Gates do DoD 11.3
/// sobre o ARQUIVO gerado: primeiros 3 bytes = BOM UTF-8, separador ';', cabeçalho exato,
/// horas decimais com vírgula, acentuação correta lida como UTF-8 e disclaimer VERBATIM na
/// última linha do CSV de jornada (e AUSENTE no de uso). Mais: validações 400/404, job
/// não-done → 409, expirado → 410 + sweep, claim que nunca processa o mesmo job duas vezes,
/// download SEM Authorization → 401 (por isso o portal baixa via fetch autenticado), params
/// corrompidos → 'failed' sem travar a fila, job órfão em 'running' devolvido à fila após o
/// timeout e teto de linhas com truncated=true exposto na listagem.
/// </summary>
[Collection(ApiCollection.Name)]
public class ExportTests(ApiTestFixture fixture)
{
    /// <summary>Texto VERBATIM do DoD 11.3 — literal no teste de propósito (não referencia a constante do produto).</summary>
    private const string Disclaimer =
        "Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de "
        + "ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT.";

    private const string JornadaHeader =
        "Data;Dia da semana;Dispositivo;Usuários;Primeiro evento;Último evento;"
        + "Tempo ligada;Tempo ativo;Tempo ocioso;Tempo bloqueado;Horas decimais (ativo);Observação";

    private async Task<(HttpClient Client, Guid TenantId, string Token)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, token);
    }

    private static async Task<HttpResponseMessage> PostExportAsync(
        HttpClient client, string token, object body)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/exports", token, body);
        return await client.SendAsync(request);
    }

    /// <summary>Um ciclo do worker com o MESMO diretório que a API serve (volume compartilhado).</summary>
    private async Task<int> RunExportWorkerOnceAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        return await new ExportService(dataSource, fixture.ExportsDirectory).RunOnceAsync();
    }

    /// <summary>
    /// Drena a fila até esvaziar (como o ExportJob do worker faz por ciclo): a fila de
    /// export_jobs é GLOBAL no banco compartilhado da suíte e o claim pega o job mais
    /// antigo — sobras de outros testes não podem desviar as asserções.
    /// </summary>
    private async Task DrainExportsAsync()
    {
        while (await RunExportWorkerOnceAsync() > 0) { }
    }

    private async Task SeedSummaryAsync(
        Guid tenantId, Guid deviceId, string date, Guid laneId,
        int active, int idle, int locked, DateTimeOffset? first = null, DateTimeOffset? last = null)
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_locked, seconds_on,
                first_event_at, last_event_at, data_incomplete, computed_at)
            VALUES (@t, @day::date, @d, @u, @a, @i, @l, @a + @i + @l, @first, @last, false, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", laneId),
            ("a", active), ("i", idle), ("l", locked), ("first", first), ("last", last));
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
            ("sid", $"S-1-5-21-EXP-{Guid.NewGuid():N}"[..40]), ("wu", $"acme\\{displayName.ToLowerInvariant()}"),
            ("dn", displayName));
        return id;
    }

    /// <summary>Lê o corpo do download validando o BOM e devolve as LINHAS (CRLF) decodificadas.</summary>
    private static async Task<string[]> ReadCsvLinesAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        // DoD 11.3: os PRIMEIROS 3 BYTES do arquivo são o BOM UTF-8 (Excel pt-BR)
        Assert.True(bytes.Length >= 3, "arquivo menor que o BOM");
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return text.Split("\r\n");
    }

    // ------------------------------------------------------------ fluxo completo: jornada
    [Fact]
    public async Task Jornada_PostQueued_WorkerDone_DownloadComBomSeparadorEDisclaimer()
    {
        var (client, tenantId, token) = await SetupAsync("ExpJorn");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-EXP-JORN");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");
        // 5h50 ativo + 30min ocioso + 20min bloqueado = 6h40 ligada; 12:00Z→09:00 local (GMT-3)
        await SeedSummaryAsync(tenantId, device.Id, "2026-03-02", ana, 21000, 1800, 1200,
            DateTimeOffset.Parse("2026-03-02T12:00:00Z"), DateTimeOffset.Parse("2026-03-02T18:40:00Z"));

        // POST → 202 queued + trilha export_csv na mesma transação
        var post = await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-03" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("jornada_csv", body.RootElement.GetProperty("kind").GetString());
            Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
            Assert.NotEqual(default, body.RootElement.GetProperty("created_at").GetDateTimeOffset());
        }

        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString, """
            SELECT detail->>'kind' AS kind, detail->'params'->>'from' AS p_from, target_id
            FROM audit_log WHERE tenant_id = @t AND action = 'export_csv'
            """, ("t", tenantId));
        Assert.Equal("jornada_csv", (string)audit!["kind"]!);
        Assert.Equal("2026-03-02", (string)audit["p_from"]!);
        Assert.Equal(jobId, (Guid)audit["target_id"]!);

        Assert.Equal("queued", await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM export_jobs WHERE id = @id", ("id", jobId)));

        // worker processa o job
        await DrainExportsAsync();

        var job = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT status, row_count, file_path, expires_at FROM export_jobs WHERE id = @id", ("id", jobId));
        Assert.Equal("done", (string)job!["status"]!);
        Assert.Equal(2, (int)job["row_count"]!); // 1 device × 2 dias (o dia vazio TAMBÉM é linha)
        Assert.Equal($"{tenantId}/{jobId}.csv", (string)job["file_path"]!);
        var expiresAt = (DateTime)job["expires_at"]!;
        Assert.InRange(expiresAt, DateTime.UtcNow.AddDays(6), DateTime.UtcNow.AddDays(8));
        Assert.True(File.Exists(Path.Combine(fixture.ExportsDirectory, tenantId.ToString(), $"{jobId}.csv")));

        // listagem: quem gerou, quando, filtros (trilha de 30 dias do tenant)
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/exports", token))
        {
            var list = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(jobId, item.GetProperty("id").GetGuid());
            Assert.Equal("done", item.GetProperty("status").GetString());
            Assert.Equal("Usuário viewer", item.GetProperty("requested_by_name").GetString());
            Assert.Equal(2, item.GetProperty("row_count").GetInt32());
            Assert.False(item.GetProperty("truncated").GetBoolean());
            Assert.False(item.GetProperty("expired").GetBoolean());
            Assert.Equal("2026-03-02", item.GetProperty("params").GetProperty("from").GetString());
        }

        // download: BOM + separador ';' + cabeçalho EXATO + vírgula decimal + acentuação +
        // disclaimer VERBATIM na última linha
        using var downloadRequest = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        var download = await client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", download.Content.Headers.ContentType!.ToString());
        Assert.Contains("jornada_2026-03-02_2026-03-03.csv",
            download.Content.Headers.ContentDisposition!.ToString());

        var lines = await ReadCsvLinesAsync(download);
        Assert.Equal(JornadaHeader, lines[0]);
        Assert.Equal("02/03/2026;segunda-feira;NB-EXP-JORN;Ana;09:00;15:40;6h 40min;5h 50min;30min;20min;5,83;", lines[1]);
        Assert.Equal("03/03/2026;terça-feira;NB-EXP-JORN;;;;0s;0s;0s;0s;0,00;Sem dados", lines[2]);
        Assert.Equal(Disclaimer, lines[3]); // ÚLTIMA linha de TODO CSV de jornada
        Assert.Equal("", lines[4]);         // CRLF final
        Assert.Equal(5, lines.Length);
    }

    // ------------------------------------------------------------ fluxo completo: uso (SEM disclaimer)
    [Fact]
    public async Task Usage_CsvComColunasDoGroupBy_SemDisclaimer()
    {
        var (client, tenantId, token) = await SetupAsync("ExpUso");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-EXP-USO");

        // app com acentuação + categoria mapeada (vocabulário fixo da classificação)
        var appId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO app_catalog (id, process_name, display_name)
            VALUES (@a, 'relatorio-export.exe', 'Relatório de Exportação')
            ON CONFLICT (process_name) DO NOTHING
            """, ("a", appId));
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, 'Comunicação', 1)
            """, ("c", categoryId), ("t", tenantId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = 'relatorio-export.exe'
            """, ("t", tenantId), ("c", categoryId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO daily_app_usage (
                tenant_id, summary_date, device_id, device_user_id, app_id, seconds_active, focus_count)
            SELECT @t, '2026-03-02', @d, '00000000-0000-0000-0000-000000000000', a.id, 5400, 3
            FROM app_catalog a WHERE a.process_name = 'relatorio-export.exe'
            """, ("t", tenantId), ("d", device.Id));

        var post = await PostExportAsync(client, token, new
        {
            kind = "usage_csv",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02",
                ["to"] = "2026-03-02",
                ["group_by"] = "app",
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        await DrainExportsAsync();
        Assert.Equal(1, await TestDb.ScalarAsync<int>(fixture.Database.ConnectionString,
            "SELECT row_count FROM export_jobs WHERE id = @id", ("id", jobId)));

        using var downloadRequest = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        var download = await client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Contains("uso_2026-03-02_2026-03-02.csv",
            download.Content.Headers.ContentDisposition!.ToString());

        var lines = await ReadCsvLinesAsync(download);
        Assert.Equal("Aplicativo;Nome de exibição;Categoria;Classificação;Tempo ativo;Horas decimais (ativo);Dispositivos", lines[0]);
        Assert.Equal("relatorio-export.exe;Relatório de Exportação;Comunicação;Relacionado ao trabalho;1h 30min;1,50;1", lines[1]);
        Assert.Equal("", lines[2]);
        Assert.Equal(3, lines.Length); // header + 1 linha de dados — SEM disclaimer no CSV de uso
        Assert.DoesNotContain("Portaria 671", string.Join("\n", lines));
    }

    // ------------------------------------------------------------ validações do POST
    [Fact]
    public async Task Post_Validacoes400EGate404()
    {
        var (client, tenantId, token) = await SetupAsync("ExpVal");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-EXP-VAL");
        var okParams = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-03" };

        // kinds dsr_* são F4 → 400 (assim como kind desconhecido/ausente)
        foreach (var kind in new[] { "dsr_subject", "dsr_device", "tenant_full", "pdf", "", null })
        {
            var response = await PostExportAsync(client, token, new { kind, @params = okParams });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // params com os MESMOS validadores dos endpoints de leitura
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token,
            new { kind = "jornada_csv" })).StatusCode); // sem params
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "02/03/2026", ["to"] = "2026-03-03" },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-10", ["to"] = "2026-03-02" },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-01-01", ["to"] = "2026-04-03" },
        })).StatusCode); // 93 dias

        // group_by: obrigatório/válido no usage_csv; não se aplica a jornada_csv
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token, new
        {
            kind = "usage_csv",
            @params = okParams,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token, new
        {
            kind = "usage_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-03", ["group_by"] = "pessoa" },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-03", ["group_by"] = "app" },
        })).StatusCode);

        // device_ids: malformado → 400; inexistente/cross-tenant → 404 (nunca 403)
        Assert.Equal(HttpStatusCode.BadRequest, (await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02",
                ["to"] = "2026-03-03",
                ["device_ids"] = new[] { "nao-e-uuid" },
            },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02",
                ["to"] = "2026-03-03",
                ["device_ids"] = new[] { device.Id.ToString(), Uuid7.NewUuid7().ToString() },
            },
        })).StatusCode);

        // nenhum job enfileirado, nenhuma trilha: os probes inválidos não deixam rastro
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM export_jobs WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'export_csv'", ("t", tenantId)));
    }

    // ------------------------------------------------------------ 409 / 410 / sweep
    [Fact]
    public async Task Download_NaoDone409_Expirado410_SweepRemoveArquivo()
    {
        var (client, tenantId, token) = await SetupAsync("ExpExp");

        var post = await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-02" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        // ainda na fila → 409 (ProblemDetails)
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token))
        {
            var conflict = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType!.MediaType);
        }

        await DrainExportsAsync();
        var filePath = Path.Combine(fixture.ExportsDirectory, tenantId.ToString(), $"{jobId}.csv");
        Assert.True(File.Exists(filePath));

        // done e dentro do prazo → 200
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        // vence o prazo via SQL → 410 e expired=true na consulta
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE export_jobs SET expires_at = now() - interval '1 hour' WHERE id = @id", ("id", jobId));
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token))
        {
            Assert.Equal(HttpStatusCode.Gone, (await client.SendAsync(request)).StatusCode);
        }
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/exports/{jobId}", token))
        {
            var get = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("expired").GetBoolean());
        }

        // sweep do próximo ciclo: arquivo some do disco e file_path é zerado
        await DrainExportsAsync();
        Assert.False(File.Exists(filePath));
        Assert.Null(await TestDb.ScalarAsync<string?>(fixture.Database.ConnectionString,
            "SELECT file_path FROM export_jobs WHERE id = @id", ("id", jobId)));
    }

    // ------------------------------------------------------------ isolamento entre tenants
    [Fact]
    public async Task JobDeOutroTenant_404NoGetENoDownload()
    {
        var (clientB, _, tokenB) = await SetupAsync("ExpIsoB");
        var post = await PostExportAsync(clientB, tokenB, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-02" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobB;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobB = body.RootElement.GetProperty("id").GetGuid();
        }

        var (clientA, _, tokenA) = await SetupAsync("ExpIsoA");
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/exports/{jobB}", tokenA))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await clientA.SendAsync(request)).StatusCode);
        }
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/exports/{jobB}/download", tokenA))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await clientA.SendAsync(request)).StatusCode);
        }
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/exports", tokenA))
        {
            var list = await clientA.SendAsync(request);
            using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.DoesNotContain(jobB, body.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid()));
        }
    }

    // ------------------------------------------------------------ download exige Bearer
    /// <summary>
    /// Navegação de browser (location.href) NÃO envia Authorization → challenge 401 mesmo
    /// para um job done dentro do prazo. Documenta o contrato: o portal baixa via fetch
    /// autenticado (apiDownload) e jamais por navegação direta.
    /// </summary>
    [Fact]
    public async Task Download_SemHeaderAuthorization_Responde401()
    {
        var (client, _, token) = await SetupAsync("ExpAnon");
        var post = await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-02" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        await DrainExportsAsync();

        // job done e dentro do prazo, mas SEM header → 401 (a [Authorize] vem antes de tudo)
        var anonymous = await client.GetAsync($"/api/v1/exports/{jobId}/download");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // o MESMO job baixa normalmente com o Bearer — a diferença é só o header
        using var authorized = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authorized)).StatusCode);
    }

    // ------------------------------------------------------------ caminho de falha
    /// <summary>
    /// Job com params corrompidos (semeado direto no banco — o POST valida e nunca enfileira
    /// assim) vira 'failed' SEM travar a fila: o job seguinte processa normalmente.
    /// </summary>
    [Fact]
    public async Task ParamsCorrompidos_ViraFailed_SemTravarAFila()
    {
        var (client, tenantId, token) = await SetupAsync("ExpFail");
        var userId = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM users WHERE tenant_id = @t LIMIT 1", ("t", tenantId));

        await DrainExportsAsync(); // fila limpa: o venenoso será o primeiro claim

        var poisonId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO export_jobs (id, tenant_id, requested_by, kind, params, status)
            VALUES (@id, @t, @u, 'jornada_csv', '{}'::jsonb, 'queued')
            """, ("id", poisonId), ("t", tenantId), ("u", userId));

        var post = await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-02" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid goodId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            goodId = body.RootElement.GetProperty("id").GetGuid();
        }

        await DrainExportsAsync();

        var poison = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT status, file_path FROM export_jobs WHERE id = @id", ("id", poisonId));
        Assert.Equal("failed", (string)poison!["status"]!);
        Assert.Null(poison["file_path"]); // arquivo parcial removido

        Assert.Equal("done", await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM export_jobs WHERE id = @id", ("id", goodId)));
    }

    // ------------------------------------------------------------ órfão em running
    /// <summary>
    /// Worker morto sem shutdown gracioso (kill -9/OOM) deixa o job em 'running': o sweep
    /// devolve à fila após o StaleRunningTimeout e o job é reprocessado. Um 'running'
    /// RECENTE (outro worker no meio da geração) não é tocado.
    /// </summary>
    [Fact]
    public async Task JobOrfaoEmRunning_VoltaParaAFilaAposTimeout()
    {
        var (_, tenantId, _) = await SetupAsync("ExpOrfao");
        var userId = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM users WHERE tenant_id = @t LIMIT 1", ("t", tenantId));

        var staleId = Uuid7.NewUuid7();
        var freshId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO export_jobs (id, tenant_id, requested_by, kind, params, status, started_at)
            VALUES
              (@stale, @t, @u, 'jornada_csv', '{"from":"2026-03-02","to":"2026-03-02"}'::jsonb,
               'running', now() - interval '20 minutes'),
              (@fresh, @t, @u, 'jornada_csv', '{"from":"2026-03-02","to":"2026-03-02"}'::jsonb,
               'running', now())
            """, ("stale", staleId), ("fresh", freshId), ("t", tenantId), ("u", userId));

        await DrainExportsAsync();

        Assert.Equal("done", await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM export_jobs WHERE id = @id", ("id", staleId)));
        Assert.Equal("running", await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM export_jobs WHERE id = @id", ("id", freshId)));
    }

    // ------------------------------------------------------------ teto de linhas
    /// <summary>
    /// Teto atingido (exercitado com maxDataRows=1): o CSV para no teto, truncated=true é
    /// exposto na listagem (jamais truncamento silencioso) e o disclaimer segue como ÚLTIMA
    /// linha do arquivo de jornada mesmo truncado. No CSV de uso, mesmo corte sem disclaimer.
    /// </summary>
    [Fact]
    public async Task TetoDeLinhas_MarcaTruncatedEDisclaimerPermaneceUltimaLinha()
    {
        var (client, tenantId, token) = await SetupAsync("ExpTeto");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-EXP-TETO");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Bia");
        await SeedSummaryAsync(tenantId, device.Id, "2026-03-02", ana, 3600, 0, 0);
        await SeedSummaryAsync(tenantId, device.Id, "2026-03-03", ana, 7200, 0, 0);

        await DrainExportsAsync(); // fila limpa: o worker de teto 1 só pega o job deste teste

        // jornada: 1 device × 3 dias = 3 linhas no range; teto 1 → trunca
        var post = await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-04" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        await using (var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            var tinyWorker = new ExportService(dataSource, fixture.ExportsDirectory, maxDataRows: 1);
            Assert.Equal(1, await tinyWorker.RunOnceAsync());
        }

        var job = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT status, row_count, truncated FROM export_jobs WHERE id = @id", ("id", jobId));
        Assert.Equal("done", (string)job!["status"]!);
        Assert.Equal(1, (int)job["row_count"]!);
        Assert.True((bool)job["truncated"]!);

        // listagem expõe truncated — é o que a tela usa para avisar "CSV parcial"
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, $"/api/v1/exports/{jobId}", token))
        {
            var get = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, body.RootElement.GetProperty("row_count").GetInt32());
        }

        // arquivo: header + 1 linha de dados + disclaimer (ÚLTIMA linha mesmo truncado)
        using var downloadRequest = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        var download = await client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var lines = await ReadCsvLinesAsync(download);
        Assert.Equal(JornadaHeader, lines[0]);
        Assert.StartsWith("02/03/2026;segunda-feira;NB-EXP-TETO;Bia;", lines[1]);
        Assert.Equal(Disclaimer, lines[2]);
        Assert.Equal("", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    // ------------------------------------------------------------ claim transacional
    [Fact]
    public async Task Claim_NaoProcessaOMesmoJobDuasVezes()
    {
        var (client, tenantId, token) = await SetupAsync("ExpClaim");

        // a fila de export_jobs é GLOBAL no banco compartilhado da suíte: drena sobras de
        // outros testes para o cenário "1 job na fila" ser determinístico
        while (await RunExportWorkerOnceAsync() > 0) { }

        var post = await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-02" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        // primeira chamada claima e processa o ÚNICO job; a segunda encontra a fila vazia
        Assert.Equal(1, await RunExportWorkerOnceAsync());
        Assert.Equal(0, await RunExportWorkerOnceAsync());

        Assert.Equal("done", await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM export_jobs WHERE tenant_id = @t", ("t", tenantId)));
    }
}
