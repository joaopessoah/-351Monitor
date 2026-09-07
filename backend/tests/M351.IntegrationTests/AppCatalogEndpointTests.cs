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
/// /api/v1/app-catalog (F3.3): recorte do TENANT sobre o catálogo global (app de outro tenant
/// não aparece), métricas de 30 dias, filtros q/uncategorized e uncategorized_count; PUT do
/// mapeamento reagregando 30 dias e mudando os baldes de classificação (e DELETE da categoria
/// devolvendo os apps ao neutro); drill-down de títulos com masked_seconds e auditoria SEMPRE.
/// </summary>
[Collection(ApiCollection.Name)]
public class AppCatalogEndpointTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, Guid TenantId, string AdminToken, string ViewerToken, string FullKey)> SetupAsync(string prefix)
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

    private async Task<Guid> AppIdAsync(string processName) =>
        await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM app_catalog WHERE process_name = @p", ("p", processName));

    private static async Task<Guid> PostCategoryAsync(
        HttpClient client, string token, string name, int classification)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", token,
            new { name, classification });
        using var doc = await ReadAsync(response, HttpStatusCode.Created);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<long> DirtyCountAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM dirty_days WHERE tenant_id = @t", ("t", tenantId));

    // ------------------------------------------------------------ recorte do tenant + filtros
    [Fact]
    public async Task Lista_RecorteDoTenant_MetricasFiltrosEContadorDeNaoCategorizados()
    {
        var (client, _, adminToken, viewerToken, fullKey) = await SetupAsync("CatalogoA");
        var device1 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CATALOGO-1");
        var device2 = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CATALOGO-2");

        // tenant B usa um app que JAMAIS pode aparecer no recorte de A
        var (clientB, _, _, _, fullKeyB) = await SetupAsync("CatalogoB");
        var deviceB = await AgentClient.EnrollAsync(clientB, fullKeyB, hostname: "NB-CATALOGO-B");

        await SeedActiveAsync(client, device1, "catalogo-x.exe", T(9, 0), 9);  // 540 s
        await SeedActiveAsync(client, device2, "catalogo-x.exe", T(10, 0), 5); // 300 s
        await SeedActiveAsync(client, device2, "catalogo-y.exe", T(11, 0), 3); // 180 s
        await SeedActiveAsync(clientB, deviceB, "catalogo-z.exe", T(12, 0), 5);
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        // recorte do tenant: x e y, ordenados por seconds_active_30d desc; z (tenant B) fora
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog", viewerToken), HttpStatusCode.OK))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(new[] { "catalogo-x.exe", "catalogo-y.exe" },
                items.Select(i => i.GetProperty("process_name").GetString()).ToArray());

            var x = items[0];
            Assert.Equal(840, x.GetProperty("seconds_active_30d").GetInt64());
            Assert.Equal(2, x.GetProperty("device_count_30d").GetInt32());
            Assert.Equal(JsonValueKind.Null, x.GetProperty("category").ValueKind);

            Assert.Equal(180, items[1].GetProperty("seconds_active_30d").GetInt64());
            Assert.Equal(1, items[1].GetProperty("device_count_30d").GetInt32());

            Assert.Equal(2, doc.RootElement.GetProperty("uncategorized_count").GetInt32());
        }

        // mapeia x com nome custom: some do filtro uncategorized e o contador cai
        var categoriaId = await PostCategoryAsync(client, adminToken, "Navegacao", 1);
        var appX = await AppIdAsync("catalogo-x.exe");
        var put = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appX}/category", adminToken,
            new { category_id = categoriaId, custom_display_name = "Navegador Corporativo" });
        using (var doc = await ReadAsync(put, HttpStatusCode.OK))
        {
            Assert.Equal(appX, doc.RootElement.GetProperty("app_id").GetGuid());
            Assert.Equal("Navegador Corporativo", doc.RootElement.GetProperty("custom_display_name").GetString());
            Assert.Equal(categoriaId, doc.RootElement.GetProperty("category").GetProperty("id").GetGuid());
        }

        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog", viewerToken), HttpStatusCode.OK))
        {
            var x = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("process_name").GetString() == "catalogo-x.exe");
            Assert.Equal("Navegador Corporativo", x.GetProperty("custom_display_name").GetString());
            Assert.Equal("Navegacao", x.GetProperty("category").GetProperty("name").GetString());
            Assert.Equal(1, x.GetProperty("category").GetProperty("classification").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("uncategorized_count").GetInt32());
        }

        // uncategorized=true: só o y
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog?uncategorized=true", viewerToken), HttpStatusCode.OK))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("catalogo-y.exe", item.GetProperty("process_name").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("uncategorized_count").GetInt32());
        }

        // q ILIKE em process_name e em custom_display_name
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog?q=catalogo-y", viewerToken), HttpStatusCode.OK))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("catalogo-y.exe", item.GetProperty("process_name").GetString());
        }

        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog?q=navegador", viewerToken), HttpStatusCode.OK))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("catalogo-x.exe", item.GetProperty("process_name").GetString());
        }
    }

    // ------------------------------------------------------------ PUT muda baldes; DELETE volta ao neutro
    [Fact]
    public async Task PutMapeamento_ReagregaEMudaBaldes_DeleteDaCategoriaVoltaASemClassificacao()
    {
        var (client, tenantId, adminToken, _, fullKey) = await SetupAsync("CatalogoFlx");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CATALOGO-FLX");

        await SeedActiveAsync(client, device, "catalogo-flx.exe", T(9, 0), 9); // 540 s
        await RunIntervalizationAsync();
        await RunAggregationAsync();
        Assert.Equal(0L, await DirtyCountAsync(tenantId));

        var summaryBefore = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT seconds_active, seconds_work_related, seconds_neutral, seconds_not_work_related,
                   seconds_unclassified
            FROM daily_device_summaries WHERE tenant_id = @t AND device_id = @d
            """, ("t", tenantId), ("d", device.DeviceId));
        Assert.Equal(540, Convert.ToInt32(summaryBefore!["seconds_active"]));
        // F6: sem mapeamento o tempo é "sem classificação", não neutro
        Assert.Equal(540, Convert.ToInt32(summaryBefore["seconds_unclassified"]));
        Assert.Equal(0, Convert.ToInt32(summaryBefore["seconds_neutral"]));

        // mapeia para uma categoria não relacionada ao trabalho
        var categoriaId = await PostCategoryAsync(client, adminToken, "Jogos", -1);
        var appId = await AppIdAsync("catalogo-flx.exe");
        var put = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", adminToken,
            new { category_id = categoriaId });
        (await ReadAsync(put, HttpStatusCode.OK)).Dispose();

        // dirty_days ganhou o dia com intervalos (a janela de 30 dias do tenant)
        Assert.Equal(1L, await DirtyCountAsync(tenantId));
        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT target_id, detail->>'process_name' AS process_name,
                   detail->>'from_category_id' AS de, detail->>'to_category_id' AS para
            FROM audit_log WHERE tenant_id = @t AND action = 'update_category'
            """, ("t", tenantId));
        Assert.Equal(appId, (Guid)audit!["target_id"]!);
        Assert.Equal("catalogo-flx.exe", (string)audit["process_name"]!);
        Assert.Null(audit["de"]);
        Assert.Equal(categoriaId.ToString(), (string)audit["para"]!);

        // agregação consome e os baldes mudam: sem classificação → não relacionado ao trabalho
        await RunAggregationAsync();
        var afterMap = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT seconds_active, seconds_work_related, seconds_neutral, seconds_not_work_related,
                   seconds_unclassified
            FROM daily_device_summaries WHERE tenant_id = @t AND device_id = @d
            """, ("t", tenantId), ("d", device.DeviceId));
        Assert.Equal(540, Convert.ToInt32(afterMap!["seconds_active"]));
        Assert.Equal(540, Convert.ToInt32(afterMap["seconds_not_work_related"]));
        Assert.Equal(0, Convert.ToInt32(afterMap["seconds_neutral"]));
        Assert.Equal(0, Convert.ToInt32(afterMap["seconds_unclassified"]));

        // DELETE da categoria: mapeamentos saem (app volta a não categorizado) e reagrega
        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/v1/categories/{categoriaId}", adminToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM tenant_app_categories WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(1L, await DirtyCountAsync(tenantId));

        await RunAggregationAsync();
        var afterDelete = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT seconds_active, seconds_neutral, seconds_not_work_related, seconds_unclassified
            FROM daily_device_summaries WHERE tenant_id = @t AND device_id = @d
            """, ("t", tenantId), ("d", device.DeviceId));
        // apagar a categoria devolve o app ao balde "sem classificação" (F6), não ao neutro
        Assert.Equal(540, Convert.ToInt32(afterDelete!["seconds_unclassified"]));
        Assert.Equal(0, Convert.ToInt32(afterDelete["seconds_neutral"]));
        Assert.Equal(0, Convert.ToInt32(afterDelete["seconds_not_work_related"]));
    }

    // ------------------------------------------------------------ PUT: 404 e desmapear com null
    [Fact]
    public async Task Put_AppOuCategoriaInexistentes404_ECategoryIdNullDesmapeia()
    {
        var (client, tenantId, adminToken, _, _) = await SetupAsync("CatalogoPut");

        // app direto no catálogo global (sem pipeline: o PUT não exige uso prévio)
        var appId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO app_catalog (id, process_name, display_name) VALUES (@a, 'catalogo-put.exe', 'catalogo-put.exe')
            """, ("a", appId));
        var categoriaId = await PostCategoryAsync(client, adminToken, "Mapeavel", 1);

        // app inexistente: 404
        var appInexistente = await SendAsync(client, HttpMethod.Put,
            $"/api/v1/app-catalog/{Uuid7.NewUuid7()}/category", adminToken, new { category_id = categoriaId });
        Assert.Equal(HttpStatusCode.NotFound, appInexistente.StatusCode);

        // categoria inexistente: 404 e nada mapeado
        var categoriaInexistente = await SendAsync(client, HttpMethod.Put,
            $"/api/v1/app-catalog/{appId}/category", adminToken, new { category_id = Uuid7.NewUuid7() });
        Assert.Equal(HttpStatusCode.NotFound, categoriaInexistente.StatusCode);
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM tenant_app_categories WHERE tenant_id = @t", ("t", tenantId)));

        // mapeia e desmapeia com category_id null (linha sai inteira)
        var mapeia = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", adminToken,
            new { category_id = categoriaId, custom_display_name = "Nome Custom" });
        (await ReadAsync(mapeia, HttpStatusCode.OK)).Dispose();
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM tenant_app_categories WHERE tenant_id = @t", ("t", tenantId)));

        var desmapeia = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", adminToken,
            new { category_id = (Guid?)null });
        using (var doc = await ReadAsync(desmapeia, HttpStatusCode.OK))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("category").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("custom_display_name").ValueKind);
        }
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM tenant_app_categories WHERE tenant_id = @t", ("t", tenantId)));
    }

    // ------------------------------------------------------------ títulos: top, mascarados, audit SEMPRE
    [Fact]
    public async Task Titles_TopPorTempo_MaskedSeconds_AuditoriaSempre_E404()
    {
        var (client, tenantId, _, viewerToken, fullKey) = await SetupAsync("CatalogoTit");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CATALOGO-TIT");

        // mesmo app, três janelas: "Doc A" 300 s, "Doc B" 180 s e SEM título 120 s (mascarado)
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?>
                { ["process_name"] = "catalogo-tit.exe", ["window_title"] = "Doc A" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 5), new Dictionary<string, object?>
                { ["process_name"] = "catalogo-tit.exe", ["window_title"] = "Doc B" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 8), new Dictionary<string, object?>
                { ["process_name"] = "catalogo-tit.exe" }),
            f.Event("LOCK", T(9, 10)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunIntervalizationAsync();

        var appId = await AppIdAsync("catalogo-tit.exe");
        var date = LocalDate(T(9, 0));

        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get,
                $"/api/v1/app-catalog/{appId}/titles?from={date}&to={date}", viewerToken), HttpStatusCode.OK))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal("Doc A", items[0].GetProperty("window_title").GetString());
            Assert.Equal(300, items[0].GetProperty("seconds_active").GetInt64());
            Assert.Equal("Doc B", items[1].GetProperty("window_title").GetString());
            Assert.Equal(180, items[1].GetProperty("seconds_active").GetInt64());
            Assert.Equal(120, doc.RootElement.GetProperty("masked_seconds").GetInt64());
            Assert.Equal(600, doc.RootElement.GetProperty("total_seconds").GetInt64());
        }

        // drill-down de apps é dado pessoal: audita SEMPRE (uma linha por chamada)
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND target_id = @a",
            ("t", tenantId), ("a", appId)));
        (await ReadAsync(await SendAsync(client, HttpMethod.Get,
            $"/api/v1/app-catalog/{appId}/titles?from={date}&to={date}", viewerToken), HttpStatusCode.OK)).Dispose();
        Assert.Equal(2L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND target_id = @a",
            ("t", tenantId), ("a", appId)));

        // app desconhecido: 404 sem rastro de auditoria
        var inexistente = Uuid7.NewUuid7();
        var notFound = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/app-catalog/{inexistente}/titles?from={date}&to={date}", viewerToken);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE action = 'view_report' AND target_id = @a", ("a", inexistente)));

        // validação de período: mesma régua do dashboard (máx. 92 dias)
        var rangeGrande = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/app-catalog/{appId}/titles?from=2026-01-01&to=2026-04-03", viewerToken);
        Assert.Equal(HttpStatusCode.BadRequest, rangeGrande.StatusCode);
    }

    // ------------------------------------------------------------ cobertura em TEMPO + fila por impacto (F6)

    /// <summary>
    /// F6 — a curadoria passa a ter KPI: cobertura da classificação em TEMPO, não em contagem
    /// de apps. uncategorized_count diz "faltam 2 apps" tanto para 2 minutos quanto para 200
    /// horas; uncategorized_seconds_active / total_seconds_active dizem o tamanho real do
    /// buraco, na MESMA janela de 30 dias das métricas por item (o cabeçalho fecha com a soma
    /// das linhas da tela).
    /// </summary>
    [Fact]
    public async Task Lista_CoberturaEmTempo_SomaOsDoisLadosNaJanelaDe30Dias()
    {
        var (client, tenantId, adminToken, viewerToken, fullKey) = await SetupAsync("CatalogoCob");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-COB-1");

        // tenant vizinho com uso próprio: JAMAIS pode entrar na conta de cobertura de A
        var (clientB, _, _, _, fullKeyB) = await SetupAsync("CatalogoCobB");
        var deviceB = await AgentClient.EnrollAsync(clientB, fullKeyB, hostname: "NB-COB-B");

        await SeedActiveAsync(client, device, "cob-produtivo.exe", T(9, 0), 6);  // 360 s
        await SeedActiveAsync(client, device, "cob-semcat-a.exe", T(10, 0), 8);  // 480 s
        await SeedActiveAsync(client, device, "cob-semcat-b.exe", T(11, 0), 4);  // 240 s
        await SeedActiveAsync(clientB, deviceB, "cob-vizinho.exe", T(12, 0), 9);
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        // sem nenhuma curadoria: o buraco é o tempo inteiro (cobertura 0%)
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog", viewerToken), HttpStatusCode.OK))
        {
            Assert.Equal(1080, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
            Assert.Equal(1080, doc.RootElement.GetProperty("uncategorized_seconds_active").GetInt64());
        }

        var categoria = await PostCategoryAsync(client, adminToken, "Cob Trabalho", 1);
        var appId = await AppIdAsync("cob-produtivo.exe");
        (await ReadAsync(await SendAsync(client, HttpMethod.Put,
            $"/api/v1/app-catalog/{appId}/category", adminToken,
            new { category_id = categoria }), HttpStatusCode.OK)).Dispose();

        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog", viewerToken), HttpStatusCode.OK))
        {
            // denominador não muda com a curadoria (é todo o tempo ativo do período)...
            Assert.Equal(1080, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
            // ...e o buraco encolhe exatamente o tempo do app categorizado: 1080 − 360
            Assert.Equal(720, doc.RootElement.GetProperty("uncategorized_seconds_active").GetInt64());
            Assert.Equal(2, doc.RootElement.GetProperty("uncategorized_count").GetInt32());
        }

        // o filtro da listagem NÃO mexe no KPI: cobertura é do recorte inteiro do tenant
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog?uncategorized=true", viewerToken),
            HttpStatusCode.OK))
        {
            Assert.Equal(1080, doc.RootElement.GetProperty("total_seconds_active").GetInt64());
            Assert.Equal(720, doc.RootElement.GetProperty("uncategorized_seconds_active").GetInt64());
        }

        Assert.NotEqual(Guid.Empty, tenantId);
    }

    /// <summary>
    /// F6 — sort=impacto é a ordem da FILA DE CLASSIFICAÇÃO: sem categoria primeiro, e dentro
    /// do grupo por tempo ativo desc. O default continua sendo só tempo ativo desc (a tabela de
    /// mapeamento, onde o gestor procura um app específico e não a próxima pendência).
    /// </summary>
    [Fact]
    public async Task Lista_SortImpacto_PoeSemCategoriaNoTopo_DefaultSegueOTempoAtivo()
    {
        var (client, _, adminToken, viewerToken, fullKey) = await SetupAsync("CatalogoFila");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-FILA-1");

        await SeedActiveAsync(client, device, "fila-alta.exe", T(9, 0), 8);  // 480 s, sem categoria
        await SeedActiveAsync(client, device, "fila-media.exe", T(10, 0), 6); // 360 s, CATEGORIZADO
        await SeedActiveAsync(client, device, "fila-baixa.exe", T(11, 0), 4); // 240 s, sem categoria
        await RunIntervalizationAsync();
        await RunAggregationAsync();

        var categoria = await PostCategoryAsync(client, adminToken, "Fila Trabalho", 1);
        var mediaId = await AppIdAsync("fila-media.exe");
        (await ReadAsync(await SendAsync(client, HttpMethod.Put,
            $"/api/v1/app-catalog/{mediaId}/category", adminToken,
            new { category_id = categoria }), HttpStatusCode.OK)).Dispose();

        // default: puro tempo ativo desc
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog", viewerToken), HttpStatusCode.OK))
        {
            Assert.Equal(new[] { "fila-alta.exe", "fila-media.exe", "fila-baixa.exe" },
                doc.RootElement.GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("process_name").GetString()).ToArray());
        }

        // impacto: os dois sem categoria sobem, entre si por tempo desc
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog?sort=impacto", viewerToken),
            HttpStatusCode.OK))
        {
            Assert.Equal(new[] { "fila-alta.exe", "fila-baixa.exe", "fila-media.exe" },
                doc.RootElement.GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("process_name").GetString()).ToArray());
        }

        // sort desconhecido cai no default em vez de 400: ordenação é preferência de tela,
        // não contrato de dado — quebrar a listagem por um parâmetro estranho seria pior
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog?sort=xyz", viewerToken),
            HttpStatusCode.OK))
        {
            Assert.Equal("fila-media.exe",
                doc.RootElement.GetProperty("items").EnumerateArray().ToList()[1]
                    .GetProperty("process_name").GetString());
        }
    }
}
