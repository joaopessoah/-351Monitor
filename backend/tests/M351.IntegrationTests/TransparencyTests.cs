using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Transparência pública por slug (F4.8, Seção 8.8) + edição autenticada da config de transparência.
///
/// GET /api/v1/public/transparencia/{slug} (AllowAnonymous) prova:
///  - SEM auth e SEM cookie retorna 200 com a política do slug e os campos certos;
///  - ZERO PII: nenhum window_title, nenhum masked_pattern cru, nenhum nome de usuário/device;
///  - reflete a window_title_policy e a collection_window REAIS do tenant;
///  - ultima_purga vem da última execução ok de RetentionPurge em maintenance_runs;
///  - slug inexistente → 404.
///
/// GET/PATCH /api/v1/organization prova: Admin edita finalidade/dpo/vigência (trilha
/// update_privacy_config com de→para); Viewer não edita (403); isolamento cross-tenant.
/// </summary>
[Collection(ApiCollection.Name)]
public class TransparencyTests(ApiTestFixture fixture)
{
    private string Conn => fixture.Database.ConnectionString;

    /// <summary>Cria a config canônica do agente do tenant com policy/janela específicas (SQL cru).</summary>
    private async Task SeedAgentConfigAsync(
        Guid tenantId, string windowTitlePolicy, string collectionWindowJson,
        string[]? maskedPatterns = null)
    {
        maskedPatterns ??= ["(?i)senha", "(?i)\\bbanco\\b", "\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}"];
        string[] ignoredProcesses = ["keepass.exe", "1password.exe", "lockapp.exe"];
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO tenant_agent_configs (tenant_id, window_title_policy, masked_patterns, ignored_processes, collection_window)
            VALUES (@t, @policy, @mp, @ip, @cw::jsonb)
            ON CONFLICT (tenant_id) DO UPDATE
              SET window_title_policy = excluded.window_title_policy,
                  masked_patterns = excluded.masked_patterns,
                  ignored_processes = excluded.ignored_processes,
                  collection_window = excluded.collection_window
            """,
            ("t", tenantId), ("policy", windowTitlePolicy), ("mp", maskedPatterns),
            ("ip", ignoredProcesses), ("cw", collectionWindowJson));
    }

    /// <summary>Planta uma execução ok de RetentionPurge em maintenance_runs (fonte da última purga).</summary>
    private async Task<DateTimeOffset> SeedRetentionPurgeRunAsync(DateTimeOffset finishedAt)
    {
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO maintenance_runs (id, job_name, started_at, finished_at, status, detail)
            VALUES (@id, 'RetentionPurge', @started, @finished, 'ok', '{}'::jsonb)
            """,
            ("id", Uuid7.NewUuid7()), ("started", finishedAt.AddMinutes(-1)), ("finished", finishedAt));
        return finishedAt;
    }

    // ============================================================ público — caminho feliz + PII

    [Fact]
    public async Task Publico_SemAuth_RetornaPoliticaDoSlug_SemPII()
    {
        var org = await fixture.CreateOrganizationAsync("Transp Publica");
        await SeedAgentConfigAsync(org.Id, "MASKED_PATTERNS",
            """{"mode":"ALWAYS","days":null,"start":null,"end":null}""",
            maskedPatterns: ["(?i)SEGREDO-INTERNO-XYZ"]);

        // client SEM token, SEM cookie
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;

        Assert.Equal("Transp Publica", root.GetProperty("organization_name").GetString());

        // política de títulos: modo cru + descrição amigável (NUNCA os masked_patterns crus)
        var policy = root.GetProperty("window_title_policy");
        Assert.Equal("MASKED_PATTERNS", policy.GetProperty("mode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(policy.GetProperty("descricao").GetString()));

        // retenções fixas N10–N13 (Seção 9.6)
        var ret = root.GetProperty("retencoes");
        Assert.Equal(90, ret.GetProperty("eventos_dias").GetInt32());
        Assert.Equal(12, ret.GetProperty("intervalos_meses").GetInt32());
        Assert.Equal(24, ret.GetProperty("agregados_meses").GetInt32());
        Assert.Equal(24, ret.GetProperty("auditoria_meses").GetInt32());

        // listas pt-BR presentes e não vazias
        Assert.True(root.GetProperty("coletado").GetArrayLength() > 0);
        Assert.True(root.GetProperty("nunca_coletado").GetArrayLength() > 0);

        // ZERO PII / ZERO segredo de config no corpo INTEIRO. window_title_policy e o MODO
        // MASKED_PATTERNS sao campos/valores do contrato — o que JAMAIS pode vazar e: o conteudo
        // cru dos masked_patterns (regex), os ignored_processes, titulos crus e nomes/SIDs.
        // (snake_case dos campos: comparacao sensivel a caixa, para nao colidir com MASKED_PATTERNS.)
        Assert.DoesNotContain("\"masked_patterns\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("SEGREDO-INTERNO-XYZ", raw, StringComparison.OrdinalIgnoreCase); // masked_pattern cru plantado
        Assert.DoesNotContain("windows_username", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windows_sid", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ignored_processes\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("keepass", raw, StringComparison.OrdinalIgnoreCase); // ignored_process cru plantado
        Assert.DoesNotContain("\"window_title\"", raw, StringComparison.Ordinal); // titulo cru (≠ window_title_policy)

        // sem cookies (rota anônima)
        Assert.False(response.Headers.Contains("Set-Cookie"));
        // cache público
        Assert.Contains("max-age", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Publico_RefleteWindowTitlePolicyDoTenant_AppOnly()
    {
        var org = await fixture.CreateOrganizationAsync("Transp AppOnly");
        await SeedAgentConfigAsync(org.Id, "APP_ONLY",
            """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");

        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("APP_ONLY", body.RootElement.GetProperty("window_title_policy").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Publico_RefleteCollectionWindowDoTenant_BusinessHours()
    {
        var org = await fixture.CreateOrganizationAsync("Transp Horario");
        await SeedAgentConfigAsync(org.Id, "FULL",
            """{"mode":"BUSINESS_HOURS","days":[1,2,3,4,5],"start":"08:00","end":"18:00"}""");

        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var window = body.RootElement.GetProperty("collection_window");
        Assert.Equal("BUSINESS_HOURS", window.GetProperty("mode").GetString());
        Assert.Equal("08:00", window.GetProperty("start").GetString());
        Assert.Equal("18:00", window.GetProperty("end").GetString());
        Assert.Equal(5, window.GetProperty("days").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(window.GetProperty("descricao").GetString()));

        // FULL no foco coletado, descrição amigável
        Assert.Equal("FULL", body.RootElement.GetProperty("window_title_policy").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Publico_UltimaPurga_VemDoMaintenanceRuns()
    {
        var org = await fixture.CreateOrganizationAsync("Transp Purga");
        await SeedAgentConfigAsync(org.Id, "MASKED_PATTERNS",
            """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");

        // execução ok de RetentionPurge recente — é GLOBAL, então plantamos a MAIS nova
        var purgeAt = await SeedRetentionPurgeRunAsync(DateTimeOffset.UtcNow);

        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ultima = body.RootElement.GetProperty("ultima_purga");
        Assert.NotEqual(JsonValueKind.Null, ultima.ValueKind);
        var returned = ultima.GetDateTimeOffset();
        // a mais recente é a que plantamos (>= a plantada, dentro de uma janela curta)
        Assert.True(returned >= purgeAt.AddSeconds(-2));
    }

    [Fact]
    public async Task Publico_SlugInexistente_Retorna404()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/transparencia/nao-existe-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Publico_ExpoeCamposEditaveisDaOrg()
    {
        var org = await fixture.CreateOrganizationAsync("Transp Campos");
        await SeedAgentConfigAsync(org.Id, "MASKED_PATTERNS",
            """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");
        await TestDb.ExecuteAsync(Conn, """
            UPDATE organizations
            SET finalidade_declarada = @f, contato_dpo = @d, data_vigencia = @v
            WHERE id = @id
            """,
            ("f", "Gestao transparente de uso das estacoes"), ("d", "dpo@empresa.com.br"),
            ("v", new DateOnly(2026, 1, 1)), ("id", org.Id));

        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Gestao transparente de uso das estacoes",
            body.RootElement.GetProperty("finalidade_declarada").GetString());
        Assert.Equal("dpo@empresa.com.br", body.RootElement.GetProperty("contato_dpo").GetString());
        Assert.Equal("2026-01-01", body.RootElement.GetProperty("vigencia").GetString());
    }

    // ============================================================ org autenticado — GET/PATCH

    [Fact]
    public async Task Organization_Get_RetornaCamposDeTransparencia()
    {
        var org = await fixture.CreateOrganizationAsync("Org Get");
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/organization", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Org Get", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(org.Slug, body.RootElement.GetProperty("slug").GetString());
        Assert.True(body.RootElement.TryGetProperty("finalidade_declarada", out _));
        Assert.True(body.RootElement.TryGetProperty("contato_dpo", out _));
        Assert.True(body.RootElement.TryGetProperty("data_vigencia", out _));
    }

    [Fact]
    public async Task Organization_Patch_AdminEdita_GravaAuditDeEPara()
    {
        var org = await fixture.CreateOrganizationAsync("Org Patch");
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Patch, "/api/v1/organization", token, new
        {
            finalidade_declarada = "Monitoramento por legitimo interesse",
            contato_dpo = "encarregado@empresa.com.br",
            data_vigencia = "2026-03-15",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Monitoramento por legitimo interesse",
            body.RootElement.GetProperty("finalidade_declarada").GetString());
        Assert.Equal("encarregado@empresa.com.br", body.RootElement.GetProperty("contato_dpo").GetString());
        Assert.Equal("2026-03-15", body.RootElement.GetProperty("data_vigencia").GetString());

        // persistido
        Assert.Equal("Monitoramento por legitimo interesse", await TestDb.ScalarAsync<string>(Conn,
            "SELECT finalidade_declarada FROM organizations WHERE id = @id", ("id", org.Id)));

        // trilha update_privacy_config com de→para
        var audit = await TestDb.RowAsync(Conn, """
            SELECT action, target_type, detail::text AS detail
            FROM audit_log WHERE tenant_id = @t AND action = 'update_privacy_config'
            ORDER BY occurred_at DESC LIMIT 1
            """, ("t", org.Id));
        Assert.NotNull(audit);
        Assert.Equal("organization", (string)audit!["target_type"]!);
        using var detail = JsonDocument.Parse((string)audit["detail"]!);
        var finalidade = detail.RootElement.GetProperty("finalidade_declarada");
        Assert.Equal(JsonValueKind.Null, finalidade.GetProperty("from").ValueKind); // de: null
        Assert.Equal("Monitoramento por legitimo interesse", finalidade.GetProperty("to").GetString());
        Assert.Equal("2026-03-15", detail.RootElement.GetProperty("data_vigencia").GetProperty("to").GetString());
    }

    [Fact]
    public async Task Organization_Patch_RefletidoNaPaginaPublica()
    {
        var org = await fixture.CreateOrganizationAsync("Org Espelho");
        await SeedAgentConfigAsync(org.Id, "MASKED_PATTERNS",
            """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        using var patch = AuthClient.AuthorizedRequest(HttpMethod.Patch, "/api/v1/organization", token, new
        {
            finalidade_declarada = "Finalidade publicada XYZ",
        });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(patch)).StatusCode);

        // página pública anônima reflete o valor editado
        var pubClient = fixture.CreateApiClient();
        var pub = await pubClient.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        using var body = JsonDocument.Parse(await pub.Content.ReadAsStringAsync());
        Assert.Equal("Finalidade publicada XYZ", body.RootElement.GetProperty("finalidade_declarada").GetString());
    }

    [Fact]
    public async Task Organization_Patch_Viewer_Recebe403()
    {
        var org = await fixture.CreateOrganizationAsync("Org Viewer");
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Patch, "/api/v1/organization", token, new
        {
            finalidade_declarada = "Tentativa do viewer",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // nada persistido, nenhuma trilha
        Assert.Null(await TestDb.ScalarAsync<string>(Conn,
            "SELECT finalidade_declarada FROM organizations WHERE id = @id", ("id", org.Id)));
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(Conn,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_privacy_config'",
            ("t", org.Id)));
    }

    [Fact]
    public async Task Organization_Get_IsolamentoCrossTenant()
    {
        // dois tenants; cada token só enxerga a própria org (filtro global por tenant)
        var orgA = await fixture.CreateOrganizationAsync("Org A Iso");
        var orgB = await fixture.CreateOrganizationAsync("Org B Iso");
        await TestDb.ExecuteAsync(Conn,
            "UPDATE organizations SET finalidade_declarada = @f WHERE id = @id",
            ("f", "Segredo da Org B"), ("id", orgB.Id));

        var viewerA = await fixture.CreateUserAsync(orgA.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var tokenA = await AuthClient.LoginAsync(client, viewerA);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/organization", tokenA);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // vê a própria org A — nunca o segredo da B
        Assert.Equal("Org A Iso", body.RootElement.GetProperty("name").GetString());
        Assert.NotEqual("Segredo da Org B", body.RootElement.GetProperty("finalidade_declarada").GetString());
    }

    [Fact]
    public async Task Organization_Patch_NaoIsolado_AdminNaoEditaOutroTenant()
    {
        // PATCH sempre edita a org do TOKEN (não há id na rota) — Admin de A jamais toca B
        var orgA = await fixture.CreateOrganizationAsync("Org A Patch Iso");
        var orgB = await fixture.CreateOrganizationAsync("Org B Patch Iso");
        var adminA = await fixture.CreateUserAsync(orgA.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var tokenA = await AuthClient.LoginAsync(client, adminA);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Patch, "/api/v1/organization", tokenA, new
        {
            finalidade_declarada = "Editado pela A",
        });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

        // A mudou, B intacta
        Assert.Equal("Editado pela A", await TestDb.ScalarAsync<string>(Conn,
            "SELECT finalidade_declarada FROM organizations WHERE id = @id", ("id", orgA.Id)));
        Assert.Null(await TestDb.ScalarAsync<string>(Conn,
            "SELECT finalidade_declarada FROM organizations WHERE id = @id", ("id", orgB.Id)));
    }
}
