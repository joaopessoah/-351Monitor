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
/// CRUD de /api/v1/categories (F3.3): listagem ordenada (classification desc, name asc) com
/// app_count, 201/409 do POST, PATCH parcial, 403 do viewer nas rotas Admin, 404 de id
/// inexistente, validação 400 e o gatilho de reagregação + audit update_category QUANDO a
/// classification muda (PATCH sem mudar classification NÃO reagrega nem audita).
/// </summary>
[Collection(ApiCollection.Name)]
public class CategoriesEndpointTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

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

    private static async Task<Guid> PostCategoryAsync(
        HttpClient client, string token, string name, int classification, string? color = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", token,
            new { name, classification, color });
        using var doc = await ReadAsync(response, HttpStatusCode.Created);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<long> UpdateCategoryAuditCountAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_category'", ("t", tenantId));

    private async Task<long> DirtyCountAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM dirty_days WHERE tenant_id = @t", ("t", tenantId));

    // ------------------------------------------------------------ CRUD + ordenação + app_count
    [Fact]
    public async Task Crud_ListaOrdenadaPorClassificacaoENome_ComAppCount()
    {
        var (client, tenantId, adminToken, viewerToken, _) = await SetupAsync("CatCrud");

        // criação fora de ordem de propósito: a listagem é quem ordena
        var jogosId = await PostCategoryAsync(client, adminToken, "Jogos", -1, "#dc2626");
        var betaId = await PostCategoryAsync(client, adminToken, "Beta", 1);
        await PostCategoryAsync(client, adminToken, "Alfa", 1, "#2563eb");
        await PostCategoryAsync(client, adminToken, "Musica", 0);

        // app mapeado para Jogos (semeado por SQL: app_catalog é global, mapeamento do tenant)
        var appId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO app_catalog (id, process_name, display_name) VALUES (@a, 'cat-crud-app.exe', 'cat-crud-app.exe')
            """, ("a", appId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id) VALUES (@t, @a, @c)
            """, ("t", tenantId), ("a", appId), ("c", jogosId));

        // GET é Viewer
        var list = await SendAsync(client, HttpMethod.Get, "/api/v1/categories", viewerToken);
        using (var doc = await ReadAsync(list, HttpStatusCode.OK))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(4, items.Count);
            Assert.Equal(new[] { "Alfa", "Beta", "Musica", "Jogos" },
                items.Select(i => i.GetProperty("name").GetString()).ToArray());
            Assert.Equal(new[] { 1, 1, 0, -1 },
                items.Select(i => i.GetProperty("classification").GetInt32()).ToArray());

            var jogos = items.Single(i => i.GetProperty("name").GetString() == "Jogos");
            Assert.Equal(jogosId, jogos.GetProperty("id").GetGuid());
            Assert.Equal("#dc2626", jogos.GetProperty("color").GetString());
            Assert.Equal(1, jogos.GetProperty("app_count").GetInt32());
            Assert.Equal(0, items[0].GetProperty("app_count").GetInt32());
        }

        // PATCH parcial sem mudar classification: nem reagrega nem audita
        var patch = await SendAsync(client, HttpMethod.Patch, $"/api/v1/categories/{betaId}", adminToken,
            new { name = "Beta Renomeada", color = "#ff0000" });
        using (var doc = await ReadAsync(patch, HttpStatusCode.OK))
        {
            Assert.Equal("Beta Renomeada", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("classification").GetInt32());
            Assert.Equal("#ff0000", doc.RootElement.GetProperty("color").GetString());
        }
        Assert.Equal(0L, await UpdateCategoryAuditCountAsync(tenantId));
        Assert.Equal(0L, await DirtyCountAsync(tenantId));
    }

    // ------------------------------------------------------------ 409 duplicada (POST e rename)
    [Fact]
    public async Task NomeDuplicadoNoTenant_Responde409_NoPostENoRename()
    {
        var (client, _, adminToken, _, _) = await SetupAsync("CatDup");

        await PostCategoryAsync(client, adminToken, "Duplicada", 1);
        var outraId = await PostCategoryAsync(client, adminToken, "Outra", 0);

        var post = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", adminToken,
            new { name = "Duplicada", classification = 0 });
        (await ReadAsync(post, HttpStatusCode.Conflict)).Dispose();

        var rename = await SendAsync(client, HttpMethod.Patch, $"/api/v1/categories/{outraId}", adminToken,
            new { name = "Duplicada" });
        (await ReadAsync(rename, HttpStatusCode.Conflict)).Dispose();
    }

    // ------------------------------------------------------------ viewer nas rotas Admin: 403
    [Fact]
    public async Task Viewer_NasRotasDeEscrita_Recebe403()
    {
        var (client, _, adminToken, viewerToken, _) = await SetupAsync("CatRbac");
        var id = await PostCategoryAsync(client, adminToken, "So Admin Mexe", 1);

        var post = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", viewerToken,
            new { name = "Tentativa", classification = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var patch = await SendAsync(client, HttpMethod.Patch, $"/api/v1/categories/{id}", viewerToken,
            new { name = "Tentativa" });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);

        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/v1/categories/{id}", viewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ------------------------------------------------------------ 404 inexistente
    [Fact]
    public async Task PatchEDelete_IdInexistente_Retornam404()
    {
        var (client, _, adminToken, _, _) = await SetupAsync("Cat404");
        var inexistente = Uuid7.NewUuid7();

        var patch = await SendAsync(client, HttpMethod.Patch, $"/api/v1/categories/{inexistente}", adminToken,
            new { name = "Nada" });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);

        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/v1/categories/{inexistente}", adminToken);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    // ------------------------------------------------------------ validação 400
    [Fact]
    public async Task Post_Validacao_NomeOuClassificacaoInvalidos_400()
    {
        var (client, _, adminToken, _, _) = await SetupAsync("CatVal");

        var semNome = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", adminToken,
            new { classification = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, semNome.StatusCode);

        var semClassificacao = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", adminToken,
            new { name = "Sem Classificacao" });
        Assert.Equal(HttpStatusCode.BadRequest, semClassificacao.StatusCode);

        var classificacaoInvalida = await SendAsync(client, HttpMethod.Post, "/api/v1/categories", adminToken,
            new { name = "Invalida", classification = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, classificacaoInvalida.StatusCode);
    }

    // ------------------------------------------------------------ classification muda → reagrega + audita
    [Fact]
    public async Task Patch_ClassificacaoMudou_EnfileiraReagregacaoEAuditaUpdateCategory()
    {
        var (client, tenantId, adminToken, _, fullKey) = await SetupAsync("CatReag");
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CAT-REAG");

        // intervalos reais de ontem (pipeline completo drena dirty_days antes do PATCH)
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "cat-reag.exe" }),
            f.Event("LOCK", T(9, 5)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
        await new DailyAggregationService(dataSource).RunOnceAsync();
        Assert.Equal(0L, await DirtyCountAsync(tenantId));

        var id = await PostCategoryAsync(client, adminToken, "Vai Mudar", 1);
        Assert.Equal(0L, await DirtyCountAsync(tenantId)); // criar categoria não reagrega

        // PATCH trocando classification 1 → -1: reagrega os 30 dias e audita
        var patch = await SendAsync(client, HttpMethod.Patch, $"/api/v1/categories/{id}", adminToken,
            new { classification = -1 });
        (await ReadAsync(patch, HttpStatusCode.OK)).Dispose();

        Assert.Equal(1L, await DirtyCountAsync(tenantId)); // 1 device × 1 dia com intervalos
        var dirty = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT device_id FROM dirty_days WHERE tenant_id = @t", ("t", tenantId));
        Assert.Equal(device.DeviceId, (Guid)dirty!["device_id"]!);

        Assert.Equal(1L, await UpdateCategoryAuditCountAsync(tenantId));
        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT target_type, target_id,
                   detail->>'from_classification' AS de, detail->>'to_classification' AS para
            FROM audit_log WHERE tenant_id = @t AND action = 'update_category'
            """, ("t", tenantId));
        Assert.Equal("category", (string)audit!["target_type"]!);
        Assert.Equal(id, (Guid)audit["target_id"]!);
        Assert.Equal("1", (string)audit["de"]!);
        Assert.Equal("-1", (string)audit["para"]!);

        // drena e confere que um PATCH só de nome NÃO re-enfileira nem audita de novo
        await new DailyAggregationService(dataSource).RunOnceAsync();
        Assert.Equal(0L, await DirtyCountAsync(tenantId));
        var renomeia = await SendAsync(client, HttpMethod.Patch, $"/api/v1/categories/{id}", adminToken,
            new { name = "Mudou So o Nome" });
        (await ReadAsync(renomeia, HttpStatusCode.OK)).Dispose();
        Assert.Equal(0L, await DirtyCountAsync(tenantId));
        Assert.Equal(1L, await UpdateCategoryAuditCountAsync(tenantId));
    }
}
