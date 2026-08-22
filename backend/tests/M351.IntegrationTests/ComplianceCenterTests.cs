using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Página pública do funcionário por TOKEN e Central de Conformidade (F5).
///
/// GET /api/v1/public/t/{token} (AllowAnonymous) prova:
///  - 200 SEM auth com o MESMO payload da rota por slug MAIS o bloco device (hostname, ciência,
///    último contato, status);
///  - ZERO dado pessoal do dia: nenhuma hora ativa/ociosa, nenhum aplicativo, nenhum usuário do
///    Windows — a URL não tem autenticação e o link circula;
///  - token inexistente → 404 opaco; token de OUTRO tenant devolve a política DAQUELE tenant e
///    nunca mistura os dois.
///
/// GET /api/v1/compliance/summary (AdminPlus) prova as contagens (ciência, trilha do mês, pacotes
/// DSR), a ausência do detail jsonb de maintenance_runs (tabela GLOBAL) e o gate de papel.
/// </summary>
[Collection(ApiCollection.Name)]
public class ComplianceCenterTests(ApiTestFixture fixture)
{
    private string Conn => fixture.Database.ConnectionString;

    /// <summary>transparency_token do device (backfillado pela migration para todo device novo).</summary>
    private async Task<Guid> TransparencyTokenAsync(Guid deviceId)
    {
        var token = await TestDb.ScalarAsync<Guid>(Conn,
            "SELECT transparency_token FROM devices WHERE id = @id", ("id", deviceId));
        if (token == Guid.Empty)
        {
            // device criado pelo EF não passa pelo enroll: garante o token como a migration faz
            token = Guid.NewGuid();
            await TestDb.ExecuteAsync(Conn,
                "UPDATE devices SET transparency_token = @tok WHERE id = @id",
                ("tok", token), ("id", deviceId));
        }

        return token;
    }

    private async Task SeedAgentConfigAsync(Guid tenantId, string policy, string collectionWindowJson)
    {
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO tenant_agent_configs (tenant_id, window_title_policy, masked_patterns, ignored_processes, collection_window)
            VALUES (@t, @policy, @mp, @ip, @cw::jsonb)
            ON CONFLICT (tenant_id) DO UPDATE
              SET window_title_policy = excluded.window_title_policy,
                  collection_window = excluded.collection_window
            """,
            ("t", tenantId), ("policy", policy),
            ("mp", new[] { "(?i)PADRAO-CRU-DO-TENANT" }), ("ip", new[] { "keepass.exe" }),
            ("cw", collectionWindowJson));
    }

    // ============================================================ público por token
    [Fact]
    public async Task PublicoPorToken_SemAuth_TrazPoliticaEBlocoDoDispositivo_SemDadoPessoal()
    {
        var org = await fixture.CreateOrganizationAsync($"TokPub {Guid.NewGuid():N}"[..20]);
        await SeedAgentConfigAsync(org.Id, "MASKED_PATTERNS",
            """{"mode":"BUSINESS_HOURS","days":[1,2,3,4,5],"start":"08:00","end":"18:00"}""");

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-TOKEN-PUB");
        var token = await TransparencyTokenAsync(device.Id);

        var ackedAt = DateTimeOffset.UtcNow.AddDays(-3);
        await TestDb.ExecuteAsync(Conn,
            "UPDATE devices SET notice_acked_at = @ack, last_seen_at = @seen WHERE id = @id",
            ("ack", ackedAt), ("seen", DateTimeOffset.UtcNow.AddMinutes(-2)), ("id", device.Id));

        // dado pessoal plantado no tenant: NADA disso pode aparecer na resposta pública
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, 'acme\\pessoa.secreta', 'Pessoa Secreta', now(), now())
            """,
            ("id", Uuid7.NewUuid7()), ("t", org.Id), ("d", device.Id),
            ("sid", $"S-1-5-21-TOK-{Guid.NewGuid():N}"[..40]));

        // client SEM token, SEM cookie
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/t/{token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;

        // mesmo payload da rota por slug
        Assert.Equal(org.Name, root.GetProperty("organization_name").GetString());
        Assert.Equal("MASKED_PATTERNS", root.GetProperty("window_title_policy").GetProperty("mode").GetString());
        Assert.Equal("BUSINESS_HOURS", root.GetProperty("collection_window").GetProperty("mode").GetString());
        Assert.Equal(90, root.GetProperty("retencoes").GetProperty("eventos_dias").GetInt32());
        Assert.True(root.GetProperty("coletado").GetArrayLength() > 0);
        Assert.True(root.GetProperty("nunca_coletado").GetArrayLength() > 0);

        // MAIS o bloco do dispositivo
        var deviceBlock = root.GetProperty("device");
        Assert.Equal("NB-TOKEN-PUB", deviceBlock.GetProperty("hostname").GetString());
        Assert.Equal("active", deviceBlock.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, deviceBlock.GetProperty("notice_acked_at").ValueKind);
        Assert.Equal(JsonValueKind.String, deviceBlock.GetProperty("last_seen_at").ValueKind);

        // ZERO dado pessoal e ZERO segredo de config
        Assert.DoesNotContain("pessoa.secreta", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pessoa Secreta", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PADRAO-CRU-DO-TENANT", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keepass", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windows_username", raw, StringComparison.OrdinalIgnoreCase);
        // horas do dia ficam de FORA por decisão (dado pessoal em URL sem auth)
        Assert.DoesNotContain("seconds_active", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seconds_idle", raw, StringComparison.OrdinalIgnoreCase);

        // rota anônima: sem cookies; cache PRIVADO (a URL carrega um segredo)
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var cacheControl = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("max-age", cacheControl);
        Assert.Contains("private", cacheControl);
    }

    [Fact]
    public async Task PublicoPorToken_TokenInexistente_404()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/t/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicoPorToken_NaoMisturaTenants()
    {
        var orgA = await fixture.CreateOrganizationAsync($"TokIsoA {Guid.NewGuid():N}"[..20]);
        var orgB = await fixture.CreateOrganizationAsync($"TokIsoB {Guid.NewGuid():N}"[..20]);
        await SeedAgentConfigAsync(orgA.Id, "APP_ONLY", """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");
        await SeedAgentConfigAsync(orgB.Id, "FULL", """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");
        await TestDb.ExecuteAsync(Conn,
            "UPDATE organizations SET finalidade_declarada = @f WHERE id = @id",
            ("f", "Segredo da Org B"), ("id", orgB.Id));

        var deviceA = await fixture.CreateDeviceAsync(orgA.Id, "NB-ISO-A");
        var deviceB = await fixture.CreateDeviceAsync(orgB.Id, "NB-ISO-B");
        var tokenA = await TransparencyTokenAsync(deviceA.Id);
        var tokenB = await TransparencyTokenAsync(deviceB.Id);

        var client = fixture.CreateApiClient();

        var responseA = await client.GetAsync($"/api/v1/public/t/{tokenA}");
        var rawA = await responseA.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(rawA))
        {
            // o token de A resolve a política de A e o dispositivo de A
            Assert.Equal(orgA.Name, body.RootElement.GetProperty("organization_name").GetString());
            Assert.Equal("APP_ONLY", body.RootElement.GetProperty("window_title_policy").GetProperty("mode").GetString());
            Assert.Equal("NB-ISO-A", body.RootElement.GetProperty("device").GetProperty("hostname").GetString());
        }
        // nada de B atravessa
        Assert.DoesNotContain("Segredo da Org B", rawA, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NB-ISO-B", rawA, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(orgB.Name, rawA, StringComparison.OrdinalIgnoreCase);

        var responseB = await client.GetAsync($"/api/v1/public/t/{tokenB}");
        using (var body = JsonDocument.Parse(await responseB.Content.ReadAsStringAsync()))
        {
            Assert.Equal("NB-ISO-B", body.RootElement.GetProperty("device").GetProperty("hostname").GetString());
            Assert.Equal("FULL", body.RootElement.GetProperty("window_title_policy").GetProperty("mode").GetString());
        }
    }

    [Fact]
    public async Task PublicoPorSlug_ContinuaSemBlocoDeDispositivo()
    {
        var org = await fixture.CreateOrganizationAsync($"SlugSemDev {Guid.NewGuid():N}"[..20]);
        await SeedAgentConfigAsync(org.Id, "MASKED_PATTERNS", """{"mode":"ALWAYS","days":null,"start":null,"end":null}""");
        await fixture.CreateDeviceAsync(org.Id, "NB-SLUG-SEM-DEV");

        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/public/transparencia/{org.Slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // a página da ORGANIZAÇÃO não fala de máquina alguma
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("device").ValueKind);
        // e segue com cache público (link divulgável, sem segredo na URL)
        Assert.Contains("public", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    // ============================================================ Central de Conformidade
    [Fact]
    public async Task ComplianceSummary_TrazEvidencias_SemDetailGlobal()
    {
        var org = await fixture.CreateOrganizationAsync($"Conf {Guid.NewGuid():N}"[..20]);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var adminToken = await AuthClient.LoginAsync(client, admin);

        // frota: 2 ativos com ciência, 1 ativo pendente, 1 arquivado (fora da conta)
        var comAck1 = await fixture.CreateDeviceAsync(org.Id, "NB-CONF-ACK-1");
        var comAck2 = await fixture.CreateDeviceAsync(org.Id, "NB-CONF-ACK-2");
        var pendente = await fixture.CreateDeviceAsync(org.Id, "NB-CONF-PEND");
        var arquivado = await fixture.CreateDeviceAsync(org.Id, "NB-CONF-ARQ");
        await TestDb.ExecuteAsync(Conn,
            "UPDATE devices SET notice_acked_at = now() WHERE id = ANY(@ids)",
            ("ids", new[] { comAck1.Id, comAck2.Id }));
        await TestDb.ExecuteAsync(Conn,
            "UPDATE devices SET status = 'archived', notice_acked_at = now() WHERE id = @id", ("id", arquivado.Id));

        // manutenção GLOBAL com detail que JAMAIS pode sair num endpoint por tenant
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO maintenance_runs (id, job_name, started_at, finished_at, status, detail)
            VALUES (@id, 'RetentionPurge', now() - interval '5 minutes', now(), 'ok', @detail::jsonb)
            """,
            ("id", Uuid7.NewUuid7()),
            ("detail", """{"linhas_apagadas_de_outro_tenant":"SEGREDO-GLOBAL-XYZ"}"""));

        // trilha do mês: uma leitura de dado pessoal e um pedido de titular
        var titular = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, 'acme\\conf.user', NULL, now(), now())
            """,
            ("id", titular), ("t", org.Id), ("d", pendente.Id),
            ("sid", $"S-1-5-21-CONF-{Guid.NewGuid():N}"[..40]));

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from={today}&to={today}&device_user_id={titular}", adminToken))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post,
            $"/api/v1/privacy/subjects/{titular}/export", adminToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(request)).StatusCode);
        }

        // ---- o dossiê
        using var summaryRequest = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/compliance/summary", adminToken);
        var response = await client.SendAsync(summaryRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;

        Assert.Equal(org.Name, root.GetProperty("organization_name").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("generated_at").ValueKind);

        // manutenção: os três jobs listados, com status, e NENHUM detail
        var runs = root.GetProperty("maintenance_runs").EnumerateArray().ToList();
        Assert.Equal(3, runs.Count);
        var purge = runs.Single(r => r.GetProperty("job_name").GetString() == "RetentionPurge");
        Assert.Equal("ok", purge.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, purge.GetProperty("finished_at").ValueKind);
        // job que nunca rodou continua na lista (a AUSÊNCIA de purga é a evidência que importa)
        var housekeeping = runs.Single(r => r.GetProperty("job_name").GetString() == "Housekeeping");
        Assert.Equal("never_run", housekeeping.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, housekeeping.GetProperty("finished_at").ValueKind);
        // o detail jsonb da tabela GLOBAL não sai daqui
        Assert.DoesNotContain("SEGREDO-GLOBAL-XYZ", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"detail\"", raw, StringComparison.Ordinal);

        // cobertura de ciência: só a frota ATIVA (o arquivado não conta)
        var coverage = root.GetProperty("notice_coverage");
        Assert.Equal(3, coverage.GetProperty("active_devices").GetInt32());
        Assert.Equal(2, coverage.GetProperty("acknowledged").GetInt32());
        Assert.Equal(1, coverage.GetProperty("pending").GetInt32());

        // trilha do mês corrente
        var activity = root.GetProperty("audit_activity");
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM"), activity.GetProperty("month").GetString());
        Assert.Equal(1, activity.GetProperty("view_report").GetInt32());
        Assert.Equal(1, activity.GetProperty("dsr_export").GetInt32());
        Assert.Equal(0, activity.GetProperty("dsr_delete").GetInt32());
        Assert.Equal(0, activity.GetProperty("export_csv").GetInt32());

        // pacotes DSR por status (o export acabou de entrar na fila)
        var dsr = root.GetProperty("dsr_exports").EnumerateArray().ToList();
        var queued = Assert.Single(dsr);
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        Assert.Equal(1, queued.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ComplianceSummary_Viewer_Recebe403_EAnonimo401()
    {
        var org = await fixture.CreateOrganizationAsync($"ConfGate {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var viewerToken = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/compliance/summary", viewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/compliance/summary")).StatusCode);
    }

    [Fact]
    public async Task ComplianceSummary_IsolamentoCrossTenant()
    {
        var orgA = await fixture.CreateOrganizationAsync($"ConfIsoA {Guid.NewGuid():N}"[..20]);
        var orgB = await fixture.CreateOrganizationAsync($"ConfIsoB {Guid.NewGuid():N}"[..20]);
        var adminA = await fixture.CreateUserAsync(orgA.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var tokenA = await AuthClient.LoginAsync(client, adminA);

        // frota só em B: o dossiê de A não pode contar nada dela
        await fixture.CreateDeviceAsync(orgB.Id, "NB-CONF-ISO-B1");
        await fixture.CreateDeviceAsync(orgB.Id, "NB-CONF-ISO-B2");

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/compliance/summary", tokenA);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(orgA.Name, body.RootElement.GetProperty("organization_name").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("notice_coverage").GetProperty("active_devices").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("dsr_exports").EnumerateArray());
    }
}
