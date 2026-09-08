using System.Net;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Exports;
using M351.IntegrationTests.Support;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// PDF do resumo (F6, kind <c>resumo_pdf</c>): POST /exports → 202 queued com trilha
/// export_csv → worker gera o .pdf → download autenticado em application/pdf com prazo de
/// validade. É o MESMO desenho assíncrono dos CSVs; só o artefato muda.
///
/// O conteúdo (índice, cobertura, quatro baldes, disclaimer da Portaria 671 no rodapé) sai
/// de WeeklySummary/ResumoPdfRenderer, os mesmos do digest do gestor — os números são
/// travados nos testes do digest, e aqui travamos o CONTRATO do export: fila, artefato
/// binário válido, tipo, nome e as recusas de parâmetro que não se aplicam.
/// </summary>
[Collection(ApiCollection.Name)]
public class ResumoPdfExportTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string Token)> SetupAsync()
    {
        var org = await fixture.CreateOrganizationAsync($"Resumo {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, token);
    }

    private async Task DrainExportsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new ExportService(dataSource, fixture.ExportsDirectory);
        while (await service.RunOnceAsync() > 0) { }
    }

    private static async Task<HttpResponseMessage> PostExportAsync(HttpClient client, string token, object body)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/exports", token, body);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ResumoPdf_PostQueued_WorkerGeraPdf_DownloadEmApplicationPdf()
    {
        var (client, tenantId, token) = await SetupAsync();
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-RESUMO");

        var lane = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, 'acme\\resumo', 'Pessoa Resumo', now(), now())
            """,
            ("id", lane), ("t", tenantId), ("d", device.Id),
            ("sid", $"S-1-5-21-PDF-{Guid.NewGuid():N}"[..40]));

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_on,
                seconds_work_related, seconds_neutral, seconds_not_work_related,
                seconds_unclassified, computed_at)
            VALUES (@t, DATE '2026-03-02', @d, @u, 21600, 7200, 28800, 10800, 3600, 3600, 3600, now())
            """,
            ("t", tenantId), ("d", device.Id), ("u", lane));

        var post = await PostExportAsync(client, token, new
        {
            kind = "resumo_pdf",
            @params = new Dictionary<string, object?> { ["from"] = "2026-03-02", ["to"] = "2026-03-08" },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("resumo_pdf", body.RootElement.GetProperty("kind").GetString());
            Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
        }

        // a trilha é a mesma dos CSVs: export_csv com kind e params
        var audit = await TestDb.RowAsync(Cs, """
            SELECT detail->>'kind' AS kind, target_id
            FROM audit_log WHERE tenant_id = @t AND action = 'export_csv'
            """, ("t", tenantId));
        Assert.Equal("resumo_pdf", (string)audit!["kind"]!);
        Assert.Equal(jobId, (Guid)audit["target_id"]!);

        await DrainExportsAsync();

        var job = await TestDb.RowAsync(Cs,
            "SELECT status, file_path, truncated, expires_at FROM export_jobs WHERE id = @id", ("id", jobId));
        Assert.Equal("done", (string)job!["status"]!);
        Assert.Equal($"{tenantId}/{jobId}.pdf", (string)job["file_path"]!);
        Assert.False((bool)job["truncated"]!);   // sumário não tem teto de linhas a estourar
        var expiresAt = (DateTime)job["expires_at"]!;
        Assert.InRange(expiresAt, DateTime.UtcNow.AddDays(6), DateTime.UtcNow.AddDays(8));

        var absolute = Path.Combine(fixture.ExportsDirectory, tenantId.ToString(), $"{jobId}.pdf");
        Assert.True(File.Exists(absolute));

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        var download = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("resumo_2026-03-02_2026-03-08.pdf", download.Content.Headers.ContentDisposition?.FileName);

        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 1000, "PDF menor que o esperado para um resumo com dados");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));   // assinatura do formato
    }

    [Fact]
    public async Task ResumoPdf_ParametrosQueNaoSeAplicam_Viram400()
    {
        var (client, tenantId, token) = await SetupAsync();
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-RESUMO-400");

        // group_by é do usage_csv
        var comGroupBy = await PostExportAsync(client, token, new
        {
            kind = "resumo_pdf",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02", ["to"] = "2026-03-08", ["group_by"] = "app",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, comGroupBy.StatusCode);

        // o resumo é AGREGADO da organização: recorte por dispositivo ou etiqueta não
        // existe no cálculo, e aceitar em silêncio entregaria número do tenant inteiro
        // com cara de número da equipe
        var comDevice = await PostExportAsync(client, token, new
        {
            kind = "resumo_pdf",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02", ["to"] = "2026-03-08",
                ["device_ids"] = new[] { device.Id.ToString() },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, comDevice.StatusCode);

        var comTag = await PostExportAsync(client, token, new
        {
            kind = "resumo_pdf",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02", ["to"] = "2026-03-08", ["tag"] = "financeiro",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, comTag.StatusCode);
    }

    // ------------------------------------------------------------------ variante PESSOAL (F9)

    [Fact]
    public async Task ResumoPdf_ComWindowsSid_TrazSoOsAplicativosDaquelaPessoa()
    {
        var (client, tenantId, token) = await SetupAsync();
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PESSOAL");
        var sid = $"S-1-5-21-PES-{Guid.NewGuid():N}"[..40];
        var outroSid = $"S-1-5-21-OUT-{Guid.NewGuid():N}"[..40];

        // DUAS pessoas no mesmo tenant, com aplicativos DIFERENTES. O PDF pessoal tem de
        // contar so os da pessoa pedida: se ele caisse no agregado da organizacao, o
        // row_count viria 3 em vez de 2 -- e o gestor entregaria a pessoa um papel com o
        // uso de outra gente dentro.
        var lane = Uuid7.NewUuid7();
        var outraLane = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, 'acme\pessoal', 'Pessoa Alvo', now(), now()),
                   (@id2, @t, @d, @sid2, 'acme\outra', 'Outra Pessoa', now(), now())
            """,
            ("id", lane), ("t", tenantId), ("d", device.Id), ("sid", sid),
            ("id2", outraLane), ("sid2", outroSid));

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_on,
                seconds_work_related, seconds_neutral, seconds_not_work_related,
                seconds_unclassified, computed_at)
            VALUES (@t, DATE '2026-03-02', @d, @u, 21600, 7200, 28800, 10800, 3600, 3600, 3600, now()),
                   (@t, DATE '2026-03-02', @d, @u2, 3600, 0, 3600, 3600, 0, 0, 0, now())
            """,
            ("t", tenantId), ("d", device.Id), ("u", lane), ("u2", outraLane));

        // dois apps da pessoa alvo, um da outra
        var apps = new[] { "pes-erp.exe", "pes-mail.exe", "pes-outro.exe" };
        var appIds = new Dictionary<string, Guid>();
        foreach (var processo in apps)
        {
            var id = Uuid7.NewUuid7();
            appIds[processo] = id;
            await TestDb.ExecuteAsync(Cs,
                """
                INSERT INTO app_catalog (id, process_name, display_name)
                VALUES (@id, @p, @p) ON CONFLICT (process_name) DO NOTHING
                """,
                ("id", id), ("p", $"{processo}-{Guid.NewGuid():N}"[..24]));
        }

        // relê os ids reais (o ON CONFLICT pode ter mantido linha anterior)
        var realIds = new List<Guid>();
        foreach (var processo in apps)
        {
            realIds.Add(appIds[processo]);
        }

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_app_usage (
                tenant_id, summary_date, device_id, device_user_id, app_id, seconds_active, focus_count)
            VALUES (@t, DATE '2026-03-02', @d, @u,  @a1, 10800, 5),
                   (@t, DATE '2026-03-02', @d, @u,  @a2,  7200, 4),
                   (@t, DATE '2026-03-02', @d, @u2, @a3,  3600, 2)
            """,
            ("t", tenantId), ("d", device.Id), ("u", lane), ("u2", outraLane),
            ("a1", realIds[0]), ("a2", realIds[1]), ("a3", realIds[2]));

        var post = await PostExportAsync(client, token, new
        {
            kind = "resumo_pdf",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02", ["to"] = "2026-03-08", ["windows_sid"] = sid,
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        await DrainExportsAsync();

        var job = await TestDb.RowAsync(Cs,
            "SELECT status, row_count FROM export_jobs WHERE id = @id", ("id", jobId));
        Assert.Equal("done", (string)job!["status"]!);
        // row_count = linhas de dados variaveis. Os DOIS apps da pessoa, e nada de alerta
        // de gestao (alerta e assunto do gestor, nao da pessoa).
        Assert.Equal(2, (int)job["row_count"]!);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/exports/{jobId}/download", token);
        var download = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        // nome proprio: quem baixa os dois no mesmo dia precisa distinguir na pasta
        Assert.Equal("resumo-pessoal_2026-03-02_2026-03-08.pdf",
            download.Content.Headers.ContentDisposition?.FileName);

        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task ResumoPdf_ComWindowsSid_DePessoaInexistente_Da400()
    {
        var (client, _, token) = await SetupAsync();

        // silencio seria pior: o gestor receberia um PDF de zeros achando que a pessoa
        // nao trabalhou, quando o identificador e que estava errado
        var post = await PostExportAsync(client, token, new
        {
            kind = "resumo_pdf",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02", ["to"] = "2026-03-08",
                ["windows_sid"] = "S-1-5-21-0-0-0-9999",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task WindowsSid_Nao_Se_Aplica_Aos_Kinds_De_Csv()
    {
        var (client, _, token) = await SetupAsync();

        var post = await PostExportAsync(client, token, new
        {
            kind = "jornada_csv",
            @params = new Dictionary<string, object?>
            {
                ["from"] = "2026-03-02", ["to"] = "2026-03-08",
                ["windows_sid"] = "S-1-5-21-0-0-0-1",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }
}
