using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Domain.Privacy;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// GET/PATCH /organization/agent-config (F5, spec §7.4/§8.7): a config de coleta vira operável
/// pela CONTROLADORA — bump transacional de config_version, trilha de→para, registro próprio
/// collection_window_choice, FULL restrito ao backoffice e regex validada no servidor.
/// </summary>
[Collection(ApiCollection.Name)]
public class AgentConfigEndpointTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string OwnerToken, string AdminToken)>
        SetupAsync(string orgName)
    {
        var org = await fixture.CreateOrganizationAsync(orgName);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        return (client, org.Id,
            await AuthClient.LoginAsync(client, owner),
            await AuthClient.LoginAsync(client, admin));
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string token, object body)
    {
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, "/api/v1/organization/agent-config", token, body);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Get_SemLinhaAinda_RetornaDefaultsDeFabrica()
    {
        var (client, _, _, adminToken) = await SetupAsync("Org Config Defaults");

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/organization/agent-config", adminToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("config_version").GetInt32());
        Assert.Equal("MASKED_PATTERNS", body.RootElement.GetProperty("window_title_policy").GetString());
        Assert.Equal(300, body.RootElement.GetProperty("idle_threshold_sec").GetInt32());
        Assert.Equal("ALWAYS", body.RootElement.GetProperty("collection_window").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Patch_IdleEPolitica_BumpaVersaoEGravaTrilhaDePara()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Config Patch");

        var response = await PatchAsync(client, ownerToken,
            new { idle_threshold_sec = 600, window_title_policy = "APP_ONLY" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("config_version").GetInt32());
        Assert.Equal("APP_ONLY", body.RootElement.GetProperty("window_title_policy").GetString());
        Assert.Equal(600, body.RootElement.GetProperty("idle_threshold_sec").GetInt32());

        var detail = await TestDb.ScalarAsync<string>(Cs,
            "SELECT detail::text FROM audit_log WHERE tenant_id = @t AND action = 'update_privacy_config' AND target_type = 'agent_config'",
            ("t", tenantId));
        Assert.NotNull(detail);
        Assert.Contains("idle_threshold_sec", detail);
        Assert.Contains("\"from\":300", detail!.Replace(" ", ""));
        Assert.Contains("APP_ONLY", detail);
    }

    [Fact]
    public async Task Patch_Full_Retorna400ExigindoDpa()
    {
        var (client, _, ownerToken, _) = await SetupAsync("Org Config Full");

        var response = await PatchAsync(client, ownerToken, new { window_title_policy = "FULL" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("full_requires_dpa", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_RegexInvalida_Retorna400SemSalvar()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Config Regex");

        var response = await PatchAsync(client, ownerToken,
            new { masked_patterns = new[] { "(?i)senha", "[invalida" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_pattern", await response.Content.ReadAsStringAsync());

        var rows = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM tenant_agent_configs WHERE tenant_id = @t AND config_version > 1",
            ("t", tenantId));
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task Patch_CollectionWindow_RegistraEscolhaPropria()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Config Janela");

        var response = await PatchAsync(client, ownerToken, new
        {
            collection_window = new { mode = "BUSINESS_HOURS", days = new[] { 1, 2, 3, 4, 5 }, start = "08:00", end = "18:00" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var window = body.RootElement.GetProperty("collection_window");
        Assert.Equal("BUSINESS_HOURS", window.GetProperty("mode").GetString());

        var choice = await TestDb.ScalarAsync<string>(Cs,
            "SELECT detail::text FROM audit_log WHERE tenant_id = @t AND action = 'collection_window_choice'",
            ("t", tenantId));
        Assert.NotNull(choice);
        Assert.Contains("BUSINESS_HOURS", choice);
    }

    [Fact]
    public async Task Patch_Admin_Retorna403_OwnerOnly()
    {
        var (client, _, _, adminToken) = await SetupAsync("Org Config 403");

        var response = await PatchAsync(client, adminToken, new { idle_threshold_sec = 900 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Patch_ConfigNovaChegaNoProximoAckDoDevice()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Config Ack");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(tenantId);
        var device = await AgentClient.EnrollAsync(client, fullKey);
        Assert.Equal(1, device.ConfigVersion);

        var patch = await PatchAsync(client, ownerToken, new { idle_threshold_sec = 900 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // batch com config_version antigo: o ack traz a config nova (transporte exclusivo)
        var batch = await AgentClient.SendBatchAsync(client, device.DeviceToken, [], configVersion: 1);
        using var ack = await AgentClient.ReadAckAsync(batch);
        Assert.Equal(2, ack.RootElement.GetProperty("config_version").GetInt32());
        var config = ack.RootElement.GetProperty("config");
        Assert.Equal(JsonValueKind.Object, config.ValueKind);
        Assert.Equal(900, config.GetProperty("idle_threshold_sec").GetInt32());
    }

    // =====================================================================================
    // Aviso de ciência gerenciado pela controladora (notice_text, Seções 6.5/9.4). O tenant
    // escreve o CORPO; o enquadramento fixo é concatenado pelo agente e nenhum texto salvo
    // aqui consegue removê-lo. O servidor recusa antes de a config chegar à frota: marcação,
    // texto que não cabe na janela do aviso e texto que imita pedido de consentimento.
    // =====================================================================================

    private const string AvisoDaAcme =
        "A ACME monitora os notebooks corporativos durante o horário de trabalho.";

    [Fact]
    public async Task Get_ExpoeOEnquadramentoFixoEOLimiteParaOPortalMontarOPreview()
    {
        var (client, _, _, adminToken) = await SetupAsync("Org Aviso Preview");

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/organization/agent-config", adminToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("notice_text").ValueKind);
        Assert.Equal(1, body.RootElement.GetProperty("notice_version").GetInt32());
        Assert.Equal(NoticeTextPolicy.FixedFraming, body.RootElement.GetProperty("notice_fixed_framing").GetString());
        Assert.Equal(NoticeTextPolicy.DefaultBody, body.RootElement.GetProperty("notice_default_body").GetString());
        Assert.Equal(NoticeTextPolicy.MaxBodyLength, body.RootElement.GetProperty("notice_max_length").GetInt32());
    }

    [Fact]
    public async Task Patch_NoticeText_BumpaAsDuasVersoesEGravaTrilhaDePara()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Aviso Patch");

        var response = await PatchAsync(client, ownerToken, new { notice_text = AvisoDaAcme });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(AvisoDaAcme, body.RootElement.GetProperty("notice_text").GetString());
        // config_version propaga a config; notice_version reexibe o aviso na frota
        Assert.Equal(2, body.RootElement.GetProperty("config_version").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("notice_version").GetInt32());

        var detail = await TestDb.ScalarAsync<string>(Cs,
            "SELECT detail::text FROM audit_log WHERE tenant_id = @t AND action = 'update_privacy_config' AND target_type = 'agent_config'",
            ("t", tenantId));
        Assert.NotNull(detail);
        Assert.Contains("notice_text", detail);
        Assert.Contains("notice_version", detail);
        Assert.Contains(AvisoDaAcme, detail);
    }

    [Fact]
    public async Task Patch_NoticeTextAdmin_Retorna403_OwnerOnly()
    {
        var (client, tenantId, _, adminToken) = await SetupAsync("Org Aviso 403");

        var response = await PatchAsync(client, adminToken, new { notice_text = AvisoDaAcme });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var salvos = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM tenant_agent_configs WHERE tenant_id = @t AND notice_text IS NOT NULL",
            ("t", tenantId));
        Assert.Equal(0, salvos);
    }

    [Fact]
    public async Task Patch_NoticeTextComHtml_Retorna400SemSalvar()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Aviso HTML");

        var response = await PatchAsync(client, ownerToken, new
        {
            notice_text = "<b>Aviso</b> da ACME.<br><a href=\"https://exemplo.com.br\">detalhes</a>",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("notice_text_markup", await response.Content.ReadAsStringAsync());

        var linhas = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM tenant_agent_configs WHERE tenant_id = @t AND config_version > 1",
            ("t", tenantId));
        Assert.Equal(0, linhas);
    }

    [Fact]
    public async Task Patch_NoticeTextImitandoConsentimento_Retorna400()
    {
        var (client, _, ownerToken, _) = await SetupAsync("Org Aviso Consentimento");

        var response = await PatchAsync(client, ownerToken, new
        {
            notice_text = "Ao clicar em Entendi você consente com o monitoramento total da máquina.",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("notice_text_consent", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_NoticeTextAcimaDoLimiteDaJanela_Retorna400()
    {
        var (client, _, ownerToken, _) = await SetupAsync("Org Aviso Limite");

        // o limite do corpo já desconta o enquadramento fixo: um caractere a mais estoura a janela
        var noLimite = new string('a', NoticeTextPolicy.MaxBodyLength);
        var passandoUm = new string('a', NoticeTextPolicy.MaxBodyLength + 1);

        var aceito = await PatchAsync(client, ownerToken, new { notice_text = noLimite });
        Assert.Equal(HttpStatusCode.OK, aceito.StatusCode);

        var recusado = await PatchAsync(client, ownerToken, new { notice_text = passandoUm });
        Assert.Equal(HttpStatusCode.BadRequest, recusado.StatusCode);
        var problema = await recusado.Content.ReadAsStringAsync();
        Assert.Contains("notice_text_too_long", problema);
        Assert.Contains("enquadramento fixo", problema);
    }

    [Fact]
    public async Task Patch_NoticeTextNull_VoltaAoAvisoPadraoDoAgente()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Aviso Null");

        var definido = await PatchAsync(client, ownerToken, new { notice_text = AvisoDaAcme });
        Assert.Equal(HttpStatusCode.OK, definido.StatusCode);

        var limpo = await PatchAsync(client, ownerToken, new { notice_text = (string?)null });

        Assert.Equal(HttpStatusCode.OK, limpo.StatusCode);
        using var body = JsonDocument.Parse(await limpo.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("notice_text").ValueKind);
        Assert.Equal(3, body.RootElement.GetProperty("config_version").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("notice_version").GetInt32());

        var noBanco = await TestDb.ScalarAsync<string>(Cs,
            "SELECT notice_text FROM tenant_agent_configs WHERE tenant_id = @t", ("t", tenantId));
        Assert.Null(noBanco);
    }

    [Fact]
    public async Task Patch_NoticeText_ChegaNoProximoAckDoDevice()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("Org Aviso Ack");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(tenantId);
        var device = await AgentClient.EnrollAsync(client, fullKey);

        var patch = await PatchAsync(client, ownerToken, new { notice_text = AvisoDaAcme });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var batch = await AgentClient.SendBatchAsync(client, device.DeviceToken, [], configVersion: device.ConfigVersion);
        using var ack = await AgentClient.ReadAckAsync(batch);
        var config = ack.RootElement.GetProperty("config");
        Assert.Equal(AvisoDaAcme, config.GetProperty("notice_text").GetString());
        Assert.Equal(2, config.GetProperty("notice_version").GetInt32());
    }
}
