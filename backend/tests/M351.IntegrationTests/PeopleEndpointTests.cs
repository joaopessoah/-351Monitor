using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET/PATCH /api/v1/people (F6): a lista de colaboradores que o portal não tinha. Identidade =
/// windows_sid do tenant, então a MESMA pessoa em duas máquinas vira UMA linha — o que
/// device_users, chaveado por (device, sid), não consegue prometer. Ordem alfabética por padrão,
/// leitura auditada como view_report (decisão 2 do spec), apelido e mesclagem só para Admin+.
/// </summary>
[Collection(ApiCollection.Name)]
public class PeopleEndpointTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
        await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    /// <summary>Bloco ativo de N minutos na lane de um SID específico (gaps sempre &lt; 600 s).</summary>
    private static async Task SeedActiveAsync(
        HttpClient client, EnrolledDevice device, string sid, string username,
        string process, DateTimeOffset start, int minutes)
    {
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", start,
                new Dictionary<string, object?> { ["process_name"] = process },
                windowsSid: sid, windowsUser: username),
            f.Event("LOCK", start.AddMinutes(minutes), windowsSid: sid, windowsUser: username),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string token, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    // ------------------------------------------------------------ identidade cross-device
    [Fact]
    public async Task People_MesmaPessoaEmDuasMaquinas_UmaLinhaSomada()
    {
        var org = await fixture.CreateOrganizationAsync($"Pess {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        var notebook = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-PESS-1");
        var desktop = await AgentClient.EnrollAsync(client, fullKey, hostname: "PC-PESS-2");

        const string sid = "S-1-5-21-1111-2222-3333-1001";
        await SeedActiveAsync(client, notebook, sid, "ACME\\ana.oliveira", "pess-erp.exe", T(12, 0), 5);
        await SeedActiveAsync(client, desktop, sid, "ACME\\ana.oliveira", "pess-excel.exe", T(14, 0), 8);
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}");

        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        var ana = items.Single(i => i.GetProperty("windows_sid").GetString() == sid);

        // 5 min numa máquina + 8 min na outra: uma linha, 13 min, dois dispositivos
        Assert.Equal(13 * 60, ana.GetProperty("seconds_active").GetInt64());
        Assert.Equal(2, ana.GetProperty("device_count").GetInt32());
        Assert.Equal(1, ana.GetProperty("days_with_data").GetInt32());
        Assert.Equal("ACME\\ana.oliveira", ana.GetProperty("display_name").GetString());

        // sem categoria mapeada: tudo sem classificação, cobertura zero, índice null
        Assert.Equal(13 * 60, ana.GetProperty("seconds_unclassified").GetInt64());
        Assert.Equal(0.0, ana.GetProperty("classification_coverage").GetDouble(), 4);
        Assert.Equal(JsonValueKind.Null, ana.GetProperty("productivity_index").ValueKind);

        // decisão 2: a lista com métricas é dado pessoal e deixa rastro
        var audited = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND target_type = 'people'",
            ("t", org.Id));
        Assert.Equal(1, audited);
    }

    // ------------------------------------------------------------ índice por pessoa
    [Fact]
    public async Task People_IndiceECobertura_PorPessoa()
    {
        var org = await fixture.CreateOrganizationAsync($"PessIdx {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-PESS-IDX");

        const string sid = "S-1-5-21-4444-5555-6666-1002";
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 0),
                new Dictionary<string, object?> { ["process_name"] = "pessidx-erp.exe" },
                windowsSid: sid, windowsUser: "ACME\\bruno"),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 6),
                new Dictionary<string, object?> { ["process_name"] = "pessidx-zap.exe" },
                windowsSid: sid, windowsUser: "ACME\\bruno"),
            f.Event("LOCK", T(12, 8), windowsSid: sid, windowsUser: "ACME\\bruno"),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        // erp produtivo (+1), zap improdutivo (−1): 360 s e 120 s → índice 0,75, cobertura 1
        await MapAppAsync(org.Id, "pessidx-erp.exe", 1);
        await MapAppAsync(org.Id, "pessidx-zap.exe", -1);
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(org.Id);
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}");
        var row = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("windows_sid").GetString() == sid);

        Assert.Equal(0.75, row.GetProperty("productivity_index").GetDouble(), 4);
        Assert.Equal(1.0, row.GetProperty("classification_coverage").GetDouble(), 4);
    }

    // ------------------------------------------------------------ apelido e mesclagem
    [Fact]
    public async Task People_ApelidoDoAdmin_VenceOUsuarioDoWindows_EAudita()
    {
        var org = await fixture.CreateOrganizationAsync($"PessNick {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-NICK-1");

        const string sid = "S-1-5-21-9999-8888-7777-1003";
        await SeedActiveAsync(client, device, sid, "ACME\\b.santos", "nick-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        using var patch = AuthClient.AuthorizedRequest(HttpMethod.Patch, $"/api/v1/people/{sid}", token);
        patch.Content = JsonContent.Create(new { display_name = "Bruno Santos" });
        var patchResponse = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}");
        var row = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("windows_sid").GetString() == sid);
        Assert.Equal("Bruno Santos", row.GetProperty("display_name").GetString());

        var audited = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_person'", ("t", org.Id));
        Assert.Equal(1, audited);
    }

    [Fact]
    public async Task People_Mesclagem_SomaAsDuasContasNumaLinha()
    {
        var org = await fixture.CreateOrganizationAsync($"PessMrg {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-MRG-1");

        const string sidDominio = "S-1-5-21-1010-2020-3030-2001";
        const string sidLocal = "S-1-5-21-1010-2020-3030-2002";
        await SeedActiveAsync(client, device, sidDominio, "ACME\\carla", "mrg-erp.exe", T(12, 0), 5);
        await SeedActiveAsync(client, device, sidLocal, "PC-LOCAL\\carla", "mrg-erp.exe", T(13, 0), 7);
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));
        using (var antes = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}"))
        {
            Assert.Equal(2, antes.RootElement.GetProperty("items").EnumerateArray().Count());
        }

        // a conta local é mesclada na conta de domínio
        using var patch = AuthClient.AuthorizedRequest(HttpMethod.Patch, $"/api/v1/people/{sidLocal}", token);
        patch.Content = JsonContent.Create(new { merged_into_sid = sidDominio });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(patch)).StatusCode);

        using var depois = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}");
        var linha = Assert.Single(depois.RootElement.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(sidDominio, linha.GetProperty("windows_sid").GetString());
        Assert.Equal(12 * 60, linha.GetProperty("seconds_active").GetInt64());
    }

    [Fact]
    public async Task People_MesclagemEmSiMesma_400()
    {
        var org = await fixture.CreateOrganizationAsync($"PessSelf {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SELF-1");

        const string sid = "S-1-5-21-7070-8080-9090-3001";
        await SeedActiveAsync(client, device, sid, "ACME\\diego", "self-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        using var patch = AuthClient.AuthorizedRequest(HttpMethod.Patch, $"/api/v1/people/{sid}", token);
        patch.Content = JsonContent.Create(new { merged_into_sid = sid });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(patch)).StatusCode);
    }

    [Fact]
    public async Task People_PatchDeViewer_403_EDeSidDesconhecido_404()
    {
        var org = await fixture.CreateOrganizationAsync($"PessAuth {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var viewerToken = await AuthClient.LoginAsync(client, viewer);
        var adminToken = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-AUTH-1");

        const string sid = "S-1-5-21-6060-5050-4040-4001";
        await SeedActiveAsync(client, device, sid, "ACME\\elisa", "auth-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        using var comoViewer = AuthClient.AuthorizedRequest(HttpMethod.Patch, $"/api/v1/people/{sid}", viewerToken);
        comoViewer.Content = JsonContent.Create(new { display_name = "Elisa" });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(comoViewer)).StatusCode);

        using var sidInexistente = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, "/api/v1/people/S-1-5-21-0000-0000-0000-9999", adminToken);
        sidInexistente.Content = JsonContent.Create(new { display_name = "Ninguém" });
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(sidInexistente)).StatusCode);
    }

    // ------------------------------------------------------------ ordenação, busca, equipes
    [Fact]
    public async Task People_OrdemDefaultAlfabetica_ESortPorMetricaQuandoPedido()
    {
        var org = await fixture.CreateOrganizationAsync($"PessSort {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-SORT-1");

        const string sidZeca = "S-1-5-21-3030-3030-3030-5001";
        const string sidAna = "S-1-5-21-3030-3030-3030-5002";
        await SeedActiveAsync(client, device, sidZeca, "ACME\\zeca", "sort-erp.exe", T(12, 0), 9);
        await SeedActiveAsync(client, device, sidAna, "ACME\\aline", "sort-erp.exe", T(13, 0), 4);
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));

        using (var alfabetica = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}"))
        {
            var nomes = alfabetica.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("display_name").GetString()).ToList();
            Assert.Equal(new[] { "ACME\\aline", "ACME\\zeca" }, nomes);
            Assert.Equal(2, alfabetica.RootElement.GetProperty("total").GetInt32());
        }

        using (var porTempo = await GetJsonAsync(client, token,
            $"/api/v1/people?from={day}&to={day}&sort=seconds_active&dir=desc"))
        {
            var primeiro = porTempo.RootElement.GetProperty("items").EnumerateArray().First();
            Assert.Equal(sidZeca, primeiro.GetProperty("windows_sid").GetString());
        }

        using (var busca = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}&q=aline"))
        {
            var achado = Assert.Single(busca.RootElement.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(sidAna, achado.GetProperty("windows_sid").GetString());
        }
    }

    [Fact]
    public async Task People_EtiquetasDeEquipe_VemNaLinhaEFiltram()
    {
        var org = await fixture.CreateOrganizationAsync($"PessTag {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-TAG-1");

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET tags = ARRAY['financeiro','matriz'] WHERE id = @d", ("d", device.DeviceId));

        const string sid = "S-1-5-21-2020-2020-2020-6001";
        await SeedActiveAsync(client, device, sid, "ACME\\fabio", "tag-erp.exe", T(12, 0), 6);
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));
        using (var comTag = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}&tag=financeiro"))
        {
            var row = Assert.Single(comTag.RootElement.GetProperty("items").EnumerateArray().ToList());
            var teams = row.GetProperty("teams").EnumerateArray().Select(t => t.GetString()).ToList();
            Assert.Equal(new[] { "financeiro", "matriz" }, teams);
        }

        using (var outraTag = await GetJsonAsync(client, token, $"/api/v1/people?from={day}&to={day}&tag=comercial"))
        {
            Assert.Empty(outraTag.RootElement.GetProperty("items").EnumerateArray());
        }
    }

    // ------------------------------------------------------------ isolamento entre tenants
    [Fact]
    public async Task People_NaoVazaPessoaDeOutroTenant()
    {
        var orgA = await fixture.CreateOrganizationAsync($"PessTA {Guid.NewGuid():N}"[..20]);
        var orgB = await fixture.CreateOrganizationAsync($"PessTB {Guid.NewGuid():N}"[..20]);
        var (_, keyA) = await fixture.CreateEnrollmentKeyWithSecretAsync(orgA.Id);
        var (_, keyB) = await fixture.CreateEnrollmentKeyWithSecretAsync(orgB.Id);
        var adminA = await fixture.CreateUserAsync(orgA.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var tokenA = await AuthClient.LoginAsync(client, adminA);

        var deviceA = await AgentClient.EnrollAsync(client, keyA, hostname: "NB-TA-1");
        var deviceB = await AgentClient.EnrollAsync(client, keyB, hostname: "NB-TB-1");

        const string sidA = "S-1-5-21-1212-1212-1212-7001";
        const string sidB = "S-1-5-21-3434-3434-3434-7002";
        await SeedActiveAsync(client, deviceA, sidA, "A\\gustavo", "iso-erp.exe", T(12, 0), 5);
        await SeedActiveAsync(client, deviceB, sidB, "B\\helena", "iso-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, tokenA, $"/api/v1/people?from={day}&to={day}");
        var sids = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("windows_sid").GetString()).ToList();

        Assert.Contains(sidA, sids);
        Assert.DoesNotContain(sidB, sids);

        // PATCH em SID do outro tenant: 404, nunca 403 (Princípio 4)
        using var patch = AuthClient.AuthorizedRequest(HttpMethod.Patch, $"/api/v1/people/{sidB}", tokenA);
        patch.Content = JsonContent.Create(new { display_name = "Invasor" });
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(patch)).StatusCode);
    }

    // ------------------------------------------------------------ validação do período
    [Fact]
    public async Task People_RangeAcimaDe92Dias_400()
    {
        var org = await fixture.CreateOrganizationAsync($"PessErr {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/people?from=2026-01-01&to=2026-12-31", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    // ------------------------------------------------------------ helper de categoria
    private async Task MapAppAsync(Guid tenantId, string processName, int classification)
    {
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, @n, @cls)",
            ("c", categoryId), ("t", tenantId), ("n", $"Cat{classification}-{processName}"), ("cls", classification));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = @p
            ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
            """,
            ("t", tenantId), ("c", categoryId), ("p", processName));
    }
}
