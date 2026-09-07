using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// organizations.classification_vocabulary (F6, decisão 1 do spec de 07/09/2026): a organização
/// escolhe COMO os rótulos de classificação aparecem — "produtividade" (Produtivo / Neutro /
/// Improdutivo / Sem classificação, o default) ou "trabalho" (Relacionado ao trabalho / Neutro /
/// Não relacionado ao trabalho / Não categorizado).
///
/// O que estes testes travam:
///  - o default de toda org nova é "produtividade" (o rótulo aprovado como padrão);
///  - o valor aparece no GET /organization E no GET /me (o portal resolve rótulo antes do
///    primeiro render, e o /me já é a query de sessão);
///  - PATCH troca, ecoa e AUDITA com de→para (update_privacy_config, igual aos vizinhos);
///  - valor fora do conjunto e null são 400 e não escrevem nada (o CHECK do banco é a segunda
///    trava, não a primeira — o cliente merece a mensagem explicando os dois valores);
///  - Viewer → 403 (rotulagem é decisão da organização, não de quem só lê);
///  - trocar vocabulário NÃO enfileira reagregação: é só rótulo, nenhum balde muda de valor —
///    ao contrário de mudar a classification de uma categoria.
/// </summary>
[Collection(ApiCollection.Name)]
public class ClassificationVocabularyTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string AdminToken, string ViewerToken)> SetupAsync(string orgName)
    {
        var org = await fixture.CreateOrganizationAsync(orgName);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        return (client, org.Id, await AuthClient.LoginAsync(client, admin), await AuthClient.LoginAsync(client, viewer));
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string token, object body)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Patch, "/api/v1/organization", token, body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string token, string url)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"esperado 200 em {url}, veio {response.StatusCode}: {raw}");
        return JsonDocument.Parse(raw);
    }

    private async Task<string?> VocabularioAsync(Guid tenantId) => await TestDb.ScalarAsync<string>(Cs,
        "SELECT classification_vocabulary FROM organizations WHERE id = @id", ("id", tenantId));

    private async Task<long> TrilhasAsync(Guid tenantId) => await TestDb.ScalarAsync<long>(Cs,
        "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_privacy_config'",
        ("t", tenantId));

    private async Task<long> DirtyCountAsync(Guid tenantId) => await TestDb.ScalarAsync<long>(Cs,
        "SELECT count(*) FROM dirty_days WHERE tenant_id = @t", ("t", tenantId));

    // ------------------------------------------------------------ default + eco no GET e no /me
    [Fact]
    public async Task Organizacao_Nova_NasceEmProdutividade_EApareceNoGetENoMe()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("Voc Default");

        Assert.Equal("produtividade", await VocabularioAsync(tenantId));

        using var org = await GetAsync(client, adminToken, "/api/v1/organization");
        Assert.Equal("produtividade", org.RootElement.GetProperty("classification_vocabulary").GetString());

        using var me = await GetAsync(client, adminToken, "/api/v1/me");
        Assert.Equal("produtividade",
            me.RootElement.GetProperty("organization").GetProperty("classification_vocabulary").GetString());
    }

    // ------------------------------------------------------------ troca + trilha de→para
    [Fact]
    public async Task Patch_TrocaParaTrabalho_PersisteEcoaEAudita()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("Voc Troca");
        var trilhasAntes = await TrilhasAsync(tenantId);

        var response = await PatchAsync(client, adminToken, new { classification_vocabulary = "trabalho" });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);
        using (var echo = JsonDocument.Parse(raw))
        {
            Assert.Equal("trabalho", echo.RootElement.GetProperty("classification_vocabulary").GetString());
        }

        Assert.Equal("trabalho", await VocabularioAsync(tenantId));
        Assert.Equal(trilhasAntes + 1, await TrilhasAsync(tenantId));

        var detail = await TestDb.ScalarAsync<string>(Cs,
            """
            SELECT detail::text FROM audit_log
            WHERE tenant_id = @t AND action = 'update_privacy_config'
            ORDER BY occurred_at DESC LIMIT 1
            """, ("t", tenantId));
        Assert.Contains("classification_vocabulary", detail);
        Assert.Contains("produtividade", detail); // o "de"
        Assert.Contains("trabalho", detail);      // o "para"

        // e o /me da sessão já responde com o rótulo novo (o portal não precisa de reload)
        using var me = await GetAsync(client, adminToken, "/api/v1/me");
        Assert.Equal("trabalho",
            me.RootElement.GetProperty("organization").GetProperty("classification_vocabulary").GetString());
    }

    // ------------------------------------------------------------ rotulagem NÃO reagrega
    [Fact]
    public async Task Patch_TrocaDeVocabulario_NaoEnfileiraReagregacao()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("Voc SemReagreg");
        var dirtyAntes = await DirtyCountAsync(tenantId);

        var response = await PatchAsync(client, adminToken, new { classification_vocabulary = "trabalho" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // trocar RÓTULO não muda nenhum balde: reagregar seria custo puro sem efeito nenhum
        Assert.Equal(dirtyAntes, await DirtyCountAsync(tenantId));
    }

    // ------------------------------------------------------------ validação
    [Theory]
    [InlineData("produtivo")]
    [InlineData("PRODUTIVIDADE ")] // trim resolve o espaço, mas o caixa alta não é o valor do contrato
    [InlineData("")]
    public async Task Patch_ValorForaDoConjunto_400_ENaoEscreve(string valor)
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("Voc Invalido");

        var response = await PatchAsync(client, adminToken, new { classification_vocabulary = valor });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("produtividade", await VocabularioAsync(tenantId));
    }

    [Fact]
    public async Task Patch_Null_400_PoisNaoExisteOrgSemVocabulario()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("Voc Null");

        var response = await PatchAsync(client, adminToken, new { classification_vocabulary = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("produtividade", await VocabularioAsync(tenantId));
    }

    [Fact]
    public async Task Patch_CampoAusente_NaoMudaOValorGravado()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("Voc Ausente");
        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, adminToken, new { classification_vocabulary = "trabalho" })).StatusCode);

        // outro campo do mesmo PATCH parcial: o vocabulário fica como estava
        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, adminToken, new { contato_dpo = "dpo@exemplo.com.br" })).StatusCode);
        Assert.Equal("trabalho", await VocabularioAsync(tenantId));
    }

    // ------------------------------------------------------------ gate de papel
    [Fact]
    public async Task Patch_Viewer_403()
    {
        var (client, tenantId, _, viewerToken) = await SetupAsync("Voc Viewer");

        var response = await PatchAsync(client, viewerToken, new { classification_vocabulary = "trabalho" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("produtividade", await VocabularioAsync(tenantId));
    }

    // ------------------------------------------------------------ isolamento
    [Fact]
    public async Task Patch_NaoVazaParaOutroTenant()
    {
        var (clientA, tenantA, tokenA, _) = await SetupAsync("Voc TenantA");
        var (_, tenantB, _, _) = await SetupAsync("Voc TenantB");

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(clientA, tokenA, new { classification_vocabulary = "trabalho" })).StatusCode);

        Assert.Equal("trabalho", await VocabularioAsync(tenantA));
        Assert.Equal("produtividade", await VocabularioAsync(tenantB)); // vizinho intacto
    }
}
