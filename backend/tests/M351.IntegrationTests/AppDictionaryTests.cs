using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Data.AppDictionary;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// F1.1, curadoria assistida por dicionário brasileiro:
///  - o dicionário embutido (apps-br.csv) é válido (nenhuma linha descartada, toda
///    default_category dentro da lista canônica de CreateOrgCommand);
///  - o seeder é IDEMPOTENTE e JAMAIS sobrescreve a decisão do tenant
///    (tenant_app_categories / custom_display_name);
///  - GET /app-catalog expõe default_category como sugestão;
///  - PUT /app-catalog/categories/batch aplica N mapeamentos numa ÚNICA transação
///    (logo, UMA única reagregação de 30 dias) com auditoria update_category por app.
/// </summary>
[Collection(ApiCollection.Name)]
public class AppDictionaryTests(ApiTestFixture fixture)
{
    private async Task<(HttpClient Client, Guid TenantId, string AdminToken, string ViewerToken)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        return (client, org.Id, await AuthClient.LoginAsync(client, admin), await AuthClient.LoginAsync(client, viewer));
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

    private static async Task<Guid> PostCategoryAsync(HttpClient client, string token, string name, int classification)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", token, new { name, classification });
        using var doc = await ReadAsync(response, HttpStatusCode.Created);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> AppIdAsync(string processName) =>
        await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM app_catalog WHERE process_name = @p", ("p", processName));

    private async Task<int> RunSeederAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        return await new AppDictionarySeeder(dataSource).RunOnceAsync();
    }

    /// <summary>
    /// Intervalo active de ontem para o tenant (matéria-prima da reagregação de 30 dias:
    /// sem intervalos, dirty_days não recebe nada e o lote não teria o que enfileirar).
    /// </summary>
    private async Task SeedActiveIntervalAsync(Guid tenantId, Guid deviceId, Guid appId)
    {
        var started = DateTimeOffset.UtcNow.AddDays(-1);
        var monthStart = new DateOnly(started.Year, started.Month, 1);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, $"""
            CREATE TABLE IF NOT EXISTS activity_intervals_{monthStart:yyyyMM} PARTITION OF activity_intervals
            FOR VALUES FROM ('{monthStart:yyyy-MM-dd}') TO ('{monthStart.AddMonths(1):yyyy-MM-dd}')
            """);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO activity_intervals (
                id, tenant_id, device_id, device_user_id, started_at, ended_at, state, app_id, source_day)
            VALUES (@id, @t, @d, NULL, @s, @e, 'active', @app, (@s AT TIME ZONE 'America/Sao_Paulo')::date)
            """,
            ("id", Uuid7.NewUuid7()), ("t", tenantId), ("d", deviceId), ("app", appId),
            ("s", started), ("e", started.AddMinutes(5)));
    }

    // ------------------------------------------------------------ dicionário válido + idempotência
    [Fact]
    public async Task Dicionario_ValidoIdempotente_ENaoSobrescreveDecisaoDoTenant()
    {
        // 1) o CSV embutido é válido: nada descartado e toda sugestão dentro do canônico
        var entries = AppDictionarySeeder.Load(out var rejected);
        Assert.Empty(rejected);
        Assert.InRange(entries.Count, 200, 300);
        Assert.All(entries, e => Assert.Contains(e.DefaultCategory, AppDictionarySeeder.CanonicalCategories));
        Assert.All(entries, e => Assert.Equal(e.ProcessName.Trim().ToLowerInvariant(), e.ProcessName));

        // 2) o startup da API (Database:AutoMigrate) já aplicou o dicionário. O SetupAsync vem
        // ANTES das leituras diretas: é ele que instancia o host (migrations + seeder rodam no
        // boot da WebApplicationFactory, que é lazy).
        var (client, tenantId, adminToken, _) = await SetupAsync("DicTenant");

        var excel = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT display_name, default_category, curated FROM app_catalog WHERE process_name = 'excel.exe'");
        Assert.NotNull(excel);
        Assert.Equal("Microsoft Excel", (string)excel["display_name"]!);
        Assert.Equal("Escritório/Documentos", (string)excel["default_category"]!);
        Assert.True((bool)excel["curated"]!);

        // nenhuma sugestão órfã no catálogo (nome fora das categorias semeadas por tenant)
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM app_catalog WHERE default_category IS NOT NULL AND NOT (default_category = ANY(@c))",
            ("c", AppDictionarySeeder.CanonicalCategories)));

        // 3) decisão do tenant sobre um app do dicionário
        var categoriaId = await PostCategoryAsync(client, adminToken, "Planilhas do Financeiro", 1);
        var appId = await AppIdAsync("excel.exe");
        (await ReadAsync(await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", adminToken,
            new { category_id = categoriaId, custom_display_name = "Excel do Financeiro" }), HttpStatusCode.OK)).Dispose();

        var appsBefore = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString, "SELECT count(*) FROM app_catalog");

        // 4) reaplicar o dicionário: idempotente, e a decisão do tenant SOBREVIVE
        var applied = await RunSeederAsync();
        Assert.Equal(entries.Count, applied);
        Assert.Equal(appsBefore, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM app_catalog"));

        var mapping = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT category_id, custom_display_name FROM tenant_app_categories WHERE tenant_id = @t AND app_id = @a",
            ("t", tenantId), ("a", appId));
        Assert.NotNull(mapping);
        Assert.Equal(categoriaId, (Guid)mapping["category_id"]!);
        Assert.Equal("Excel do Financeiro", (string)mapping["custom_display_name"]!);

        // o id do app não mudou (o ON CONFLICT preserva a linha e, com ela, os mapeamentos)
        Assert.Equal(appId, await AppIdAsync("excel.exe"));

        // 5) a listagem expõe a sugestão e a decisão do tenant lado a lado
        using var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog?q=excel.exe", adminToken), HttpStatusCode.OK);
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Microsoft Excel", item.GetProperty("display_name").GetString());
        Assert.Equal("Escritório/Documentos", item.GetProperty("default_category").GetString());
        Assert.Equal("Excel do Financeiro", item.GetProperty("custom_display_name").GetString());
        Assert.Equal(categoriaId, item.GetProperty("category").GetProperty("id").GetGuid());
    }

    // ------------------------------------------------------------ lote: N mapeamentos, 1 reagregação
    [Fact]
    public async Task Batch_AplicaNMapeamentos_UmaTransacaoUmaReagregacao_EAuditaPorApp()
    {
        var (client, tenantId, adminToken, viewerToken) = await SetupAsync("DicLote");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DIC-LOTE");

        var escritorio = await PostCategoryAsync(client, adminToken, "Escritório/Documentos", 1);
        var navegacao = await PostCategoryAsync(client, adminToken, "Navegação", 1);

        var word = await AppIdAsync("winword.exe");
        var chrome = await AppIdAsync("chrome.exe");
        var vlc = await AppIdAsync("vlc.exe");
        await SeedActiveIntervalAsync(tenantId, device.Id, word);

        // mapeamento anterior semeado DIRETO no banco (audit_log é append-only: um PUT aqui
        // deixaria trilha e a asserção de "1 transação" do lote perderia o sentido)
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id, custom_display_name)
            VALUES (@t, @a, @c, 'Navegador Homologado')
            """, ("t", tenantId), ("a", chrome), ("c", navegacao));

        // Viewer não aplica lote
        var forbidden = await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", viewerToken,
            new { items = new[] { new { app_id = word, category_id = escritorio } } });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // corpo vazio e app_id repetido: 400
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", adminToken,
                new { items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", adminToken,
                new { items = new[] { new { app_id = word, category_id = escritorio }, new { app_id = word, category_id = navegacao } } })).StatusCode);

        // app inexistente e categoria de outro tenant: 404 e NADA aplicado
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", adminToken,
                new { items = new[] { new { app_id = Uuid7.NewUuid7(), category_id = escritorio } } })).StatusCode);
        var (_, _, outroAdmin, _) = await SetupAsync("DicOutro");
        var categoriaDeOutro = await PostCategoryAsync(client, outroAdmin, "Categoria de Outro", 1);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", adminToken,
                new { items = new[] { new { app_id = vlc, category_id = categoriaDeOutro } } })).StatusCode);
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM tenant_app_categories WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_category'", ("t", tenantId)));

        // ----- o lote de verdade: 3 mapeamentos (um deles desmapeando) -----
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", adminToken, new
            {
                items = new object[]
                {
                    new { app_id = word, category_id = (Guid?)escritorio },
                    new { app_id = vlc, category_id = (Guid?)navegacao },
                    new { app_id = chrome, category_id = (Guid?)null },
                },
            }), HttpStatusCode.OK))
        {
            Assert.Equal(3, doc.RootElement.GetProperty("applied").GetInt32());
            Assert.Equal(3, doc.RootElement.GetProperty("items").GetArrayLength());
            // a reagregação rodou UMA vez e enfileirou o dia com intervalos
            Assert.True(doc.RootElement.GetProperty("reaggregation_days").GetInt32() >= 1);
        }

        // mapeamentos finais: word e vlc mapeados, chrome desmapeado (linha saiu inteira)
        Assert.Equal(2L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM tenant_app_categories WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(escritorio, await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT category_id FROM tenant_app_categories WHERE tenant_id = @t AND app_id = @a",
            ("t", tenantId), ("a", word)));

        // uma linha de auditoria POR APP, com de→para
        Assert.Equal(3L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_category'", ("t", tenantId)));
        var chromeAudit = await TestDb.RowAsync(fixture.Database.ConnectionString, """
            SELECT detail->>'from_category_id' AS de, detail->>'to_category_id' AS para, detail->>'batch' AS lote
            FROM audit_log WHERE tenant_id = @t AND action = 'update_category' AND target_id = @a
            """, ("t", tenantId), ("a", chrome));
        Assert.Equal(navegacao.ToString(), (string)chromeAudit!["de"]!);
        Assert.Null(chromeAudit["para"]);
        Assert.Equal("true", (string)chromeAudit["lote"]!);

        // GATE DO ITEM: as 3 escritas + a reagregação saíram na MESMA transação (1 xmin),
        // e não em 3 transações como seriam 3 PUTs individuais (3 reagregações)
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(DISTINCT xmin::text) FROM audit_log WHERE tenant_id = @t AND action = 'update_category'",
            ("t", tenantId)));
        Assert.True(await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM dirty_days WHERE tenant_id = @t", ("t", tenantId)) >= 1L);

        // e o lote NÃO apaga o nome custom de um app que continua mapeado
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE tenant_app_categories SET custom_display_name = 'Player Corporativo' WHERE tenant_id = @t AND app_id = @a",
            ("t", tenantId), ("a", vlc));
        using (var doc = await ReadAsync(
            await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", adminToken, new
            {
                items = new object[] { new { app_id = vlc, category_id = (Guid?)escritorio } },
            }), HttpStatusCode.OK))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("Player Corporativo", item.GetProperty("custom_display_name").GetString());
            Assert.Equal(escritorio, item.GetProperty("category").GetProperty("id").GetGuid());
        }
        Assert.Equal("Player Corporativo", await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT custom_display_name FROM tenant_app_categories WHERE tenant_id = @t AND app_id = @a",
            ("t", tenantId), ("a", vlc)));
    }
}
