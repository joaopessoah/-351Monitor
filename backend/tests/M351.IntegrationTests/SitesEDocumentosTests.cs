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
/// SITES E DOCUMENTOS fim a fim: ingestão pela API real (site_domain/document_name no payload do
/// ACTIVE_WINDOW_CHANGED) → raw_events → IntervalizationService (site_catalog + colunas do
/// intervalo) → DailyAggregationService (daily_site_usage + PRECEDÊNCIA site &gt; app) →
/// /site-catalog, /reports/usage?group_by=site e /reports/documents.
///
/// O teste que mais importa aqui é <see cref="Classificacao_RegraDeSite_VenceRegraDeApp"/>: é ele
/// que prova a promessa do produto — marcar "mercadolivre.com.br" como improdutivo muda o número
/// mesmo com o navegador classificado como Navegação.
/// </summary>
[Collection(ApiCollection.Name)]
public class SitesEDocumentosTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, Guid TenantId, string AdminToken, string ViewerToken, string FullKey)>
        SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var adminToken = await AuthClient.LoginAsync(client, admin);
        var viewerToken = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, adminToken, viewerToken, fullKey);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, token, body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(string.IsNullOrEmpty(body) ? "null" : body);
    }

    /// <summary>Bloco active de N minutos com site e/ou arquivo (sempre &lt; 10 min — gap N7).</summary>
    private static async Task SeedActiveAsync(
        HttpClient client, EnrolledDevice device, string process, DateTimeOffset start, int minutes,
        string? site = null, string? document = null, string? title = null)
    {
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", start, new Dictionary<string, object?>
            {
                ["process_name"] = process,
                ["window_title"] = title,
                ["site_domain"] = site,
                ["document_name"] = document,
            }),
            f.Event("LOCK", start.AddMinutes(minutes)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
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

    private static async Task<Guid> PostCategoryAsync(
        HttpClient client, string token, string name, int classification)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", token,
            new { name, classification });
        using var doc = await ReadAsync(response, HttpStatusCode.Created);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> SiteIdAsync(string domain) =>
        await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM site_catalog WHERE domain = @d", ("d", domain));

    // ------------------------------------------------------------ ingestão e pipeline
    [Fact]
    public async Task Ingestao_GravaSiteEArquivo_EPipelineResolveSiteCatalog()
    {
        var (client, tenantId, _, _, fullKey) = await SetupAsync("SitePipe");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SITE-PIPE");
        var dominio = $"pipe-{Guid.NewGuid():N}"[..14] + ".com.br";

        await SeedActiveAsync(client, device, "chrome.exe", T(10, 0), 6,
            site: dominio, title: "Loja - Google Chrome");
        await SeedActiveAsync(client, device, "winword.exe", T(10, 10), 5,
            document: "Contrato 2026.docx", title: "Contrato 2026.docx - Word");

        // colunas dedicadas de raw_events (o payload cru fica ao lado, como informação)
        var siteNoRaw = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT site_domain FROM raw_events WHERE device_id = @d AND site_domain IS NOT NULL LIMIT 1",
            ("d", device.DeviceId));
        Assert.Equal(dominio, siteNoRaw);

        await RunIntervalizationAsync();

        // auto-insert não-curado no catálogo GLOBAL (mesmo comportamento do app_catalog)
        var curado = await TestDb.ScalarAsync<bool>(fixture.Database.ConnectionString,
            "SELECT curated FROM site_catalog WHERE domain = @d", ("d", dominio));
        Assert.False(curado);

        var intervaloComSite = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT count(*) FROM activity_intervals i
            JOIN site_catalog s ON s.id = i.site_id
            WHERE i.tenant_id = @t AND i.device_id = @d AND s.domain = @dom AND i.state = 'active'
            """,
            ("t", tenantId), ("d", device.DeviceId), ("dom", dominio));
        Assert.True(intervaloComSite > 0, "o intervalo active deveria carregar o site_id resolvido");

        var intervaloComArquivo = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT count(*) FROM activity_intervals
            WHERE tenant_id = @t AND device_id = @d AND document_name = 'Contrato 2026.docx'
            """,
            ("t", tenantId), ("d", device.DeviceId));
        Assert.True(intervaloComArquivo > 0, "o intervalo active deveria carregar o nome do arquivo");
    }

    /// <summary>
    /// Revalidação de SERVIDOR (Seção 5.6): o agente é a autoridade de privacidade, mas não é
    /// fonte confiável de formato. Domínio sem ponto, com espaço ou gigante e nome de arquivo com
    /// CAMINHO viram null — o evento continua valendo, só sem o campo.
    /// </summary>
    [Fact]
    public async Task Ingestao_SaneiaCampoTorto_SemRejeitarOLote()
    {
        var (client, _, _, _, fullKey) = await SetupAsync("SiteSanit");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SITE-SANIT");

        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(11, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
                ["site_domain"] = "isto nao e um dominio",
                ["document_name"] = @"C:\Users\fulano\Documentos\rescisao.docx",
            }),
        });
        using var acked = await AgentClient.ReadAckAsync(ack);
        Assert.Equal(1, acked.RootElement.GetProperty("accepted").GetInt32());

        var row = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT count(*) FROM raw_events
            WHERE device_id = @d AND site_domain IS NULL AND document_name IS NULL
            """, ("d", device.DeviceId));
        Assert.True(row > 0, "campo torto deveria virar null, e o evento continuar persistido");
    }

    // ------------------------------------------------------------ agregação e classificação
    [Fact]
    public async Task Agregacao_PreencheDailySiteUsage()
    {
        var (client, tenantId, _, _, fullKey) = await SetupAsync("SiteAgg");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SITE-AGG");
        var dominio = $"agg-{Guid.NewGuid():N}"[..13] + ".com.br";

        await SeedActiveAsync(client, device, "chrome.exe", T(9, 0), 6, site: dominio);
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        var segundos = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT COALESCE(sum(u.seconds_active), 0) FROM daily_site_usage u
            JOIN site_catalog s ON s.id = u.site_id
            WHERE u.tenant_id = @t AND s.domain = @d
            """, ("t", tenantId), ("d", dominio));
        Assert.Equal(360L, segundos); // 6 minutos
    }

    /// <summary>
    /// A PROMESSA DA FEATURE: o navegador está classificado como trabalho, o site como lazer, e
    /// quem manda no balde é o SITE. Sem esta precedência, classificar sites não mudaria número
    /// nenhum — todo o tempo de navegação ficaria no balde do chrome.exe.
    /// </summary>
    [Fact]
    public async Task Classificacao_RegraDeSite_VenceRegraDeApp()
    {
        var (client, tenantId, adminToken, _, fullKey) = await SetupAsync("SiteClass");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SITE-CLASS");
        var dominio = $"lazer-{Guid.NewGuid():N}"[..15] + ".com.br";

        await SeedActiveAsync(client, device, "chrome.exe", T(14, 0), 6, site: dominio);
        await RunIntervalizationAsync();

        var navegacao = await PostCategoryAsync(client, adminToken, "Navegação", 1);
        var lazer = await PostCategoryAsync(client, adminToken, "Lazer", -1);

        // regra de APP: chrome.exe é trabalho
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = 'chrome.exe'
            ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
            """, ("t", tenantId), ("c", navegacao));

        await RunAggregationAsync();
        var soComApp = await SummaryAsync(device.DeviceId);
        Assert.Equal(360, soComApp.Work);
        Assert.Equal(0, soComApp.NotWork);

        // agora a regra de SITE: o mesmo tempo vira "não relacionado ao trabalho"
        var siteId = await SiteIdAsync(dominio);
        var put = await SendAsync(client, HttpMethod.Put, $"/api/v1/site-catalog/{siteId}/category",
            adminToken, new { category_id = lazer });
        (await ReadAsync(put, HttpStatusCode.OK)).Dispose();

        await RunAggregationAsync();
        var comSite = await SummaryAsync(device.DeviceId);
        Assert.Equal(0, comSite.Work);
        Assert.Equal(360, comSite.NotWork);
        Assert.Equal(360, comSite.Active); // o tempo total não mudou, só o balde
    }

    private async Task<(int Active, int Work, int NotWork, int Unclassified)> SummaryAsync(Guid deviceId)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(sum(seconds_active), 0)::int, COALESCE(sum(seconds_work_related), 0)::int,
                   COALESCE(sum(seconds_not_work_related), 0)::int, COALESCE(sum(seconds_unclassified), 0)::int
            FROM daily_device_summaries WHERE device_id = @d
            """, connection);
        command.Parameters.AddWithValue("d", deviceId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    // ------------------------------------------------------------ catálogo de sites (API)
    [Fact]
    public async Task SiteCatalog_Lista_ComCoberturaEFilaDeClassificacao()
    {
        var (client, _, adminToken, viewerToken, fullKey) = await SetupAsync("SiteCat");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SITE-CAT");
        var usado = $"usado-{Guid.NewGuid():N}"[..15] + ".com.br";
        var pouco = $"pouco-{Guid.NewGuid():N}"[..15] + ".com.br";

        await SeedActiveAsync(client, device, "chrome.exe", T(8, 0), 8, site: usado);
        await SeedActiveAsync(client, device, "chrome.exe", T(8, 30), 2, site: pouco);
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        // Viewer LÊ o catálogo (a curadoria é do Admin, a leitura não)
        var lista = await SendAsync(client, HttpMethod.Get, "/api/v1/site-catalog?sort=impacto", viewerToken);
        using var doc = await ReadAsync(lista, HttpStatusCode.OK);

        var itens = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        var dominios = itens.Select(i => i.GetProperty("domain").GetString()).ToList();
        Assert.Contains(usado, dominios);
        Assert.Contains(pouco, dominios);

        // nada classificado ainda: cobertura zero e os dois na fila
        Assert.Equal(
            doc.RootElement.GetProperty("total_seconds_active").GetInt64(),
            doc.RootElement.GetProperty("uncategorized_seconds_active").GetInt64());
        Assert.True(doc.RootElement.GetProperty("uncategorized_count").GetInt32() >= 2);

        // sem categoria, ordem é por tempo desc: o mais usado vem primeiro
        Assert.Equal(usado, dominios[0]);

        // classificar move a cobertura
        var categoria = await PostCategoryAsync(client, adminToken, "Trabalho", 1);
        var siteId = await SiteIdAsync(usado);
        var put = await SendAsync(client, HttpMethod.Put, $"/api/v1/site-catalog/{siteId}/category",
            adminToken, new { category_id = categoria });
        (await ReadAsync(put, HttpStatusCode.OK)).Dispose();

        var depois = await SendAsync(client, HttpMethod.Get, "/api/v1/site-catalog", viewerToken);
        using var doc2 = await ReadAsync(depois, HttpStatusCode.OK);
        Assert.True(doc2.RootElement.GetProperty("uncategorized_seconds_active").GetInt64()
                    < doc2.RootElement.GetProperty("total_seconds_active").GetInt64());
    }

    [Fact]
    public async Task SiteCatalog_Viewer_NaoClassifica_E_SiteInexistente_Da404()
    {
        var (client, _, adminToken, viewerToken, _) = await SetupAsync("SitePerm");
        var categoria = await PostCategoryAsync(client, adminToken, "Trabalho", 1);

        var siteId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO site_catalog (id, domain, display_name) VALUES (@s, @d, @d)
            """, ("s", siteId), ("d", $"perm-{Guid.NewGuid():N}"[..14] + ".com.br"));

        var viewer = await SendAsync(client, HttpMethod.Put, $"/api/v1/site-catalog/{siteId}/category",
            viewerToken, new { category_id = categoria });
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

        var inexistente = await SendAsync(client, HttpMethod.Put,
            $"/api/v1/site-catalog/{Uuid7.NewUuid7()}/category", adminToken, new { category_id = categoria });
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task SiteCatalog_Lote_AplicaTudoNumaTransacao()
    {
        var (client, _, adminToken, _, fullKey) = await SetupAsync("SiteLote");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SITE-LOTE");
        var d1 = $"lote1-{Guid.NewGuid():N}"[..15] + ".com.br";
        var d2 = $"lote2-{Guid.NewGuid():N}"[..15] + ".com.br";

        await SeedActiveAsync(client, device, "chrome.exe", T(9, 0), 4, site: d1);
        await SeedActiveAsync(client, device, "chrome.exe", T(9, 20), 4, site: d2);
        await RunIntervalizationAsync();

        var categoria = await PostCategoryAsync(client, adminToken, "Trabalho", 1);
        var body = new
        {
            items = new[]
            {
                new { site_id = await SiteIdAsync(d1), category_id = categoria },
                new { site_id = await SiteIdAsync(d2), category_id = categoria },
            },
        };

        var response = await SendAsync(client, HttpMethod.Put, "/api/v1/site-catalog/categories/batch",
            adminToken, body);
        using var doc = await ReadAsync(response, HttpStatusCode.OK);
        Assert.Equal(2, doc.RootElement.GetProperty("applied").GetInt32());
        Assert.True(doc.RootElement.GetProperty("reaggregation_days").GetInt32() >= 1);

        // um site_id inexistente derruba o lote INTEIRO (nada é aplicado)
        var ruim = new
        {
            items = new[] { new { site_id = Uuid7.NewUuid7(), category_id = categoria } },
        };
        var falha = await SendAsync(client, HttpMethod.Put, "/api/v1/site-catalog/categories/batch",
            adminToken, ruim);
        Assert.Equal(HttpStatusCode.NotFound, falha.StatusCode);
    }

    // ------------------------------------------------------------ relatórios
    [Fact]
    public async Task ReportsUsage_GroupBySite_TrazDominioECategoria()
    {
        var (client, _, adminToken, viewerToken, fullKey) = await SetupAsync("SiteRep");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SITE-REP");
        var dominio = $"rep-{Guid.NewGuid():N}"[..13] + ".com.br";

        await SeedActiveAsync(client, device, "chrome.exe", T(10, 0), 5, site: dominio);
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        var categoria = await PostCategoryAsync(client, adminToken, "Trabalho", 1);
        var siteId = await SiteIdAsync(dominio);
        (await ReadAsync(await SendAsync(client, HttpMethod.Put, $"/api/v1/site-catalog/{siteId}/category",
            adminToken, new { category_id = categoria }), HttpStatusCode.OK)).Dispose();

        var dia = LocalDate(T(10, 0));
        var response = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/reports/usage?from={dia}&to={dia}&group_by=site", viewerToken);
        using var doc = await ReadAsync(response, HttpStatusCode.OK);

        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("domain").GetString() == dominio);
        Assert.Equal(300, item.GetProperty("seconds_active").GetInt64());
        Assert.Equal(1, item.GetProperty("device_count").GetInt32());
        Assert.Equal("Trabalho", item.GetProperty("category").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ReportsDocuments_AgrupaPorArquivoEAuditaSempre()
    {
        var (client, tenantId, _, viewerToken, fullKey) = await SetupAsync("DocRep");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DOC-REP");
        var arquivo = $"Contrato {Guid.NewGuid():N}"[..20] + ".docx";

        await SeedActiveAsync(client, device, "winword.exe", T(13, 0), 4,
            document: arquivo, title: $"{arquivo} - Word");
        await SeedActiveAsync(client, device, "winword.exe", T(13, 20), 3,
            document: arquivo, title: $"{arquivo} - Word");
        await RunIntervalizationAsync();

        var dia = LocalDate(T(13, 0));
        var response = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/reports/documents?from={dia}&to={dia}", viewerToken);
        using var doc = await ReadAsync(response, HttpStatusCode.OK);

        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("document_name").GetString() == arquivo);
        Assert.Equal(420, item.GetProperty("seconds_active").GetInt64()); // 4 + 3 minutos
        Assert.Equal(2, item.GetProperty("open_count").GetInt32());
        Assert.Equal("docx", item.GetProperty("extension").GetString());
        // nome AMIGÁVEL do app, vindo do dicionário brasileiro (apps-br.csv cura winword.exe)
        Assert.Equal("Microsoft Word", item.GetProperty("app_display_name").GetString());

        // nome de arquivo é dado pessoal: a leitura SEMPRE deixa trilha
        var auditoria = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'",
            ("t", tenantId));
        Assert.True(auditoria > 0, "o relatório de documentos deveria auditar view_report");
    }

    [Fact]
    public async Task ReportsDocuments_FiltraPorNome()
    {
        var (client, _, _, viewerToken, fullKey) = await SetupAsync("DocFiltro");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DOC-FILTRO");

        await SeedActiveAsync(client, device, "excel.exe", T(15, 0), 3, document: "Orcamento 2026.xlsx");
        await SeedActiveAsync(client, device, "winword.exe", T(15, 20), 3, document: "Ata da reuniao.docx");
        await RunIntervalizationAsync();

        var dia = LocalDate(T(15, 0));
        var response = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/reports/documents?from={dia}&to={dia}&q=orcamento", viewerToken);
        using var doc = await ReadAsync(response, HttpStatusCode.OK);

        var nomes = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("document_name").GetString()).ToList();
        Assert.Contains("Orcamento 2026.xlsx", nomes);
        Assert.DoesNotContain("Ata da reuniao.docx", nomes);
    }

    // ------------------------------------------------------------ chaves de coleta e transparência
    [Fact]
    public async Task ConfigDeColeta_LigarEDesligar_ViajaNaConfigEReavisaSoAoLigar()
    {
        var org = await fixture.CreateOrganizationAsync($"Chaves {Guid.NewGuid():N}"[..20]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, owner);

        // a linha de config só nasce no primeiro enroll; o PATCH cria quando não existe
        var desliga = await SendAsync(client, HttpMethod.Patch, "/api/v1/organization/agent-config", token,
            new { site_capture = false, document_capture = false });
        using (var doc = await ReadAsync(desliga, HttpStatusCode.OK))
        {
            Assert.False(doc.RootElement.GetProperty("site_capture").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("document_capture").GetBoolean());
        }

        var noticeDepoisDeDesligar = await TestDb.ScalarAsync<int>(fixture.Database.ConnectionString,
            "SELECT notice_version FROM tenant_agent_configs WHERE tenant_id = @t", ("t", org.Id));

        // religar AUMENTA o escopo de coleta → o aviso de ciência reaparece na frota
        var liga = await SendAsync(client, HttpMethod.Patch, "/api/v1/organization/agent-config", token,
            new { site_capture = true });
        (await ReadAsync(liga, HttpStatusCode.OK)).Dispose();

        var noticeDepoisDeLigar = await TestDb.ScalarAsync<int>(fixture.Database.ConnectionString,
            "SELECT notice_version FROM tenant_agent_configs WHERE tenant_id = @t", ("t", org.Id));
        Assert.Equal(noticeDepoisDeDesligar + 1, noticeDepoisDeLigar);

        // e a config que chega ao agente carrega as duas chaves
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CHAVES");
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        using var acked = await AgentClient.ReadAckAsync(ack);
        var config = acked.RootElement.GetProperty("config");
        Assert.True(config.GetProperty("site_capture").GetBoolean());
        Assert.False(config.GetProperty("document_capture").GetBoolean());
    }

    [Fact]
    public async Task TransparenciaPublica_DeclaraColetaLigada_EOmiteDesligada()
    {
        var org = await fixture.CreateOrganizationAsync($"Transp {Guid.NewGuid():N}"[..20]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, owner);

        (await ReadAsync(await SendAsync(client, HttpMethod.Patch, "/api/v1/organization/agent-config", token,
            new { site_capture = true, document_capture = true }), HttpStatusCode.OK)).Dispose();

        var publica = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        using (var doc = await ReadAsync(publica, HttpStatusCode.OK))
        {
            var coletado = string.Join(" | ", doc.RootElement.GetProperty("coletado")
                .EnumerateArray().Select(i => i.GetString()));
            Assert.Contains("Dominio do site", coletado, StringComparison.Ordinal);
            Assert.Contains("Nome do arquivo", coletado, StringComparison.Ordinal);
        }

        (await ReadAsync(await SendAsync(client, HttpMethod.Patch, "/api/v1/organization/agent-config", token,
            new { site_capture = false, document_capture = false }), HttpStatusCode.OK)).Dispose();

        var depois = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        using (var doc = await ReadAsync(depois, HttpStatusCode.OK))
        {
            var coletado = string.Join(" | ", doc.RootElement.GetProperty("coletado")
                .EnumerateArray().Select(i => i.GetString()));
            Assert.DoesNotContain("Dominio do site", coletado, StringComparison.Ordinal);
            Assert.DoesNotContain("Nome do arquivo", coletado, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Política APP_ONLY não coleta título, e por consequência não coleta site nem arquivo — a
    /// página pública não pode anunciar coleta que o agente não faz.
    /// </summary>
    [Fact]
    public async Task TransparenciaPublica_AppOnly_NaoAnunciaSiteNemArquivo()
    {
        var org = await fixture.CreateOrganizationAsync($"TranspAO {Guid.NewGuid():N}"[..18]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, owner);

        (await ReadAsync(await SendAsync(client, HttpMethod.Patch, "/api/v1/organization/agent-config", token,
            new { window_title_policy = "APP_ONLY", site_capture = true, document_capture = true }),
            HttpStatusCode.OK)).Dispose();

        var publica = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        using var doc = await ReadAsync(publica, HttpStatusCode.OK);
        var coletado = string.Join(" | ", doc.RootElement.GetProperty("coletado")
            .EnumerateArray().Select(i => i.GetString()));
        Assert.DoesNotContain("Dominio do site", coletado, StringComparison.Ordinal);
        Assert.DoesNotContain("Nome do arquivo", coletado, StringComparison.Ordinal);
    }
}
