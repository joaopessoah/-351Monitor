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
/// /api/v1/people/{sid}/notes (F6, decisão 6 do spec de 07/09/2026): ANOTAÇÃO DE PERÍODO e
/// CONTESTAÇÃO DE CLASSIFICAÇÃO, com revisão do gestor.
///
/// O que estes testes travam, além do CRUD: (a) criar é Viewer+, revisar é Admin+ — Viewer que
/// tenta revisar toma 403, não 404, porque o recurso é do tenant dele e o que falta é poder;
/// (b) nota de outro tenant responde 404, jamais 403; (c) ACEITAR UMA CONTESTAÇÃO NÃO MUDA
/// daily_device_summaries — a promessa central da feature, verificada comparando os agregados
/// antes e depois da revisão.
/// </summary>
[Collection(ApiCollection.Name)]
public class PersonNotesEndpointTests(ApiTestFixture fixture)
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

    /// <summary>Bloco ativo de N minutos numa lane (gaps sempre &lt; 600 s: N7 viraria no_data).</summary>
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

    private static async Task<JsonDocument> SendJsonAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body,
        HttpStatusCode expected)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, token);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }

    private static Task<JsonDocument> GetNotesAsync(
        HttpClient client, string token, string sid, string day) =>
        SendJsonAsync(client, HttpMethod.Get,
            $"/api/v1/people/{Uri.EscapeDataString(sid)}/notes?from={day}&to={day}",
            token, null, HttpStatusCode.OK);

    // --------------------------------------------------------------- criar uma ANOTAÇÃO
    [Fact]
    public async Task Notes_ViewerCriaAnotacaoDePeriodo_AparecerNoGet_EAudita()
    {
        var org = await fixture.CreateOrganizationAsync($"Nota {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-NOTA-1");

        const string sid = "S-1-5-21-9100-9100-9100-1001";
        await SeedActiveAsync(client, device, sid, "ACME\\carla", "nota-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        using var created = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{sid}/notes", token,
            new
            {
                kind = "anotacao",
                started_at = T(13, 0),
                ended_at = T(15, 0),
                body = "Reunião presencial com o cliente, fora da estação de trabalho.",
            },
            HttpStatusCode.Created);

        Assert.Equal("anotacao", created.RootElement.GetProperty("kind").GetString());
        // nasce SEMPRE aberta: o corpo não escolhe status
        Assert.Equal("aberta", created.RootElement.GetProperty("status").GetString());
        Assert.Equal(sid, created.RootElement.GetProperty("windows_sid").GetString());
        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("reviewed_at").ValueKind);
        // display_name do users, resolvido no servidor (ApiTestFixture cria "Usuário {role}")
        Assert.Equal("Usuário viewer", created.RootElement.GetProperty("created_by_name").GetString());

        var day = LocalDate(T(13, 0));
        using var listed = await GetNotesAsync(client, token, sid, day);
        var item = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal("Reunião presencial com o cliente, fora da estação de trabalho.",
            item.GetProperty("body").GetString());

        // criar deixa rastro próprio; ler grava view_report (dado pessoal, decisão 2)
        var escritas = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'create_person_note'",
            ("t", org.Id));
        Assert.Equal(1, escritas);

        var leituras = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND target_type = 'person_notes'",
            ("t", org.Id));
        Assert.Equal(1, leituras);

        // o TEXTO da anotação não vai para a trilha append-only (art. 18 LGPD)
        var vazouTexto = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND detail::text ILIKE '%Reunião presencial%'",
            ("t", org.Id));
        Assert.Equal(0, vazouTexto);
    }

    // ------------------------------------------------------------ criar uma CONTESTAÇÃO
    [Fact]
    public async Task Notes_ContestacaoComApp_GuardaOAplicativoContestado()
    {
        var org = await fixture.CreateOrganizationAsync($"Cont {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CONT-1");

        const string sid = "S-1-5-21-9200-9200-9200-2001";
        await SeedActiveAsync(client, device, sid, "ACME\\diego", "cont-zap.exe", T(12, 0), 6);
        await RunPipelineAsync();

        var appId = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM app_catalog WHERE process_name = @p", ("p", "cont-zap.exe"));

        using var created = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{sid}/notes", token,
            new
            {
                kind = "contestacao",
                started_at = T(12, 0),
                ended_at = T(12, 6),
                app_id = appId,
                body = "Uso do aplicativo foi para atendimento a cliente, não pessoal.",
            },
            HttpStatusCode.Created);

        Assert.Equal("contestacao", created.RootElement.GetProperty("kind").GetString());
        Assert.Equal(appId, created.RootElement.GetProperty("app_id").GetGuid());
        Assert.Equal("cont-zap.exe", created.RootElement.GetProperty("app_process_name").GetString());
        Assert.Equal("aberta", created.RootElement.GetProperty("status").GetString());

        // app_id numa ANOTAÇÃO de período não significa nada: 400 em vez de campo mudo
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Post, $"/api/v1/people/{sid}/notes", token);
        request.Content = JsonContent.Create(new
        {
            kind = "anotacao",
            started_at = T(12, 0),
            ended_at = T(12, 6),
            app_id = appId,
            body = "Treinamento.",
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    // ---------------------------------------- gestor ACEITA — e o agregado NÃO muda
    [Fact]
    public async Task Notes_GestorAceitaContestacao_NaoAlteraDailyDeviceSummaries()
    {
        var org = await fixture.CreateOrganizationAsync($"Aceita {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var viewerToken = await AuthClient.LoginAsync(client, viewer);
        var adminToken = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-ACEITA-1");

        const string sid = "S-1-5-21-9300-9300-9300-3001";
        await SeedActiveAsync(client, device, sid, "ACME\\erica", "aceita-zap.exe", T(12, 0), 7);
        await RunPipelineAsync();

        var appId = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM app_catalog WHERE process_name = @p", ("p", "aceita-zap.exe"));

        // fotografia dos agregados ANTES da revisão
        const string snapshotSql = """
            SELECT coalesce(string_agg(
                summary_date::text || '|' || seconds_on || '|' || seconds_active || '|' ||
                seconds_idle || '|' || seconds_work_related || '|' || seconds_neutral || '|' ||
                seconds_not_work_related || '|' || seconds_unclassified, ',' ORDER BY summary_date), '')
            FROM daily_device_summaries WHERE tenant_id = @t
            """;
        var antes = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            snapshotSql, ("t", org.Id));
        Assert.False(string.IsNullOrEmpty(antes)); // sem dado, o teste não provaria nada

        using var created = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{sid}/notes", viewerToken,
            new
            {
                kind = "contestacao",
                started_at = T(12, 0),
                ended_at = T(12, 7),
                app_id = appId,
                body = "Este aplicativo é usado para suporte, deveria contar como trabalho.",
            },
            HttpStatusCode.Created);
        var noteId = created.RootElement.GetProperty("id").GetGuid();

        using var reviewed = await SendJsonAsync(client, HttpMethod.Patch,
            $"/api/v1/people/{sid}/notes/{noteId}", adminToken,
            new { status = "aceita", review_note = "Procede; vamos remapear a categoria." },
            HttpStatusCode.OK);

        var note = reviewed.RootElement.GetProperty("note");
        Assert.Equal("aceita", note.GetProperty("status").GetString());
        Assert.Equal("Usuário admin", note.GetProperty("reviewed_by_name").GetString());
        Assert.NotEqual(JsonValueKind.Null, note.GetProperty("reviewed_at").ValueKind);

        // a resposta DIZ ao gestor que aceitar não move número
        var effect = reviewed.RootElement.GetProperty("effect").GetString() ?? "";
        Assert.Contains("NÃO altera os números já agregados", effect);

        // A PROMESSA DA FEATURE: nenhum agregado mudou
        var depois = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            snapshotSql, ("t", org.Id));
        Assert.Equal(antes, depois);

        var auditada = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'review_person_note'",
            ("t", org.Id));
        Assert.Equal(1, auditada);
    }

    // ------------------------------------------------------------------ gestor RECUSA
    [Fact]
    public async Task Notes_GestorRecusa_ExigeRespostaEGuardaQuemDecidiu()
    {
        var org = await fixture.CreateOrganizationAsync($"Recusa {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var viewerToken = await AuthClient.LoginAsync(client, viewer);
        var adminToken = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-RECUSA-1");

        const string sid = "S-1-5-21-9400-9400-9400-4001";
        await SeedActiveAsync(client, device, sid, "ACME\\fabio", "recusa-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        using var created = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{sid}/notes", viewerToken,
            new
            {
                kind = "anotacao",
                started_at = T(9, 0),
                ended_at = T(11, 0),
                body = "Treinamento interno na sala de reuniões.",
            },
            HttpStatusCode.Created);
        var noteId = created.RootElement.GetProperty("id").GetGuid();

        // recusar SEM resposta é 400: quem abriu precisa saber o motivo
        using var semResposta = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, $"/api/v1/people/{sid}/notes/{noteId}", adminToken);
        semResposta.Content = JsonContent.Create(new { status = "recusada" });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(semResposta)).StatusCode);

        using var recusada = await SendJsonAsync(client, HttpMethod.Patch,
            $"/api/v1/people/{sid}/notes/{noteId}", adminToken,
            new { status = "recusada", review_note = "O período já estava registrado como ausência." },
            HttpStatusCode.OK);

        var note = recusada.RootElement.GetProperty("note");
        Assert.Equal("recusada", note.GetProperty("status").GetString());
        Assert.Equal("O período já estava registrado como ausência.",
            note.GetProperty("review_note").GetString());
        Assert.Equal("Usuário admin", note.GetProperty("reviewed_by_name").GetString());

        // revisar duas vezes não sobrescreve um fato datado e assinado
        using var denovo = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, $"/api/v1/people/{sid}/notes/{noteId}", adminToken);
        denovo.Content = JsonContent.Create(new { status = "aceita" });
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(denovo)).StatusCode);
    }

    // ------------------------------------------------------------------ Viewer não revisa
    [Fact]
    public async Task Notes_ViewerTentaRevisar_403()
    {
        var org = await fixture.CreateOrganizationAsync($"NotaAuth {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-NAUTH-1");

        const string sid = "S-1-5-21-9500-9500-9500-5001";
        await SeedActiveAsync(client, device, sid, "ACME\\gisele", "nauth-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        using var created = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{sid}/notes", token,
            new { kind = "anotacao", started_at = T(12, 0), ended_at = T(12, 30), body = "Visita técnica." },
            HttpStatusCode.Created);
        var noteId = created.RootElement.GetProperty("id").GetGuid();

        // 403, NÃO 404: o recurso é do tenant dele; o que falta é poder
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, $"/api/v1/people/{sid}/notes/{noteId}", token);
        request.Content = JsonContent.Create(new { status = "aceita" });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);

        // e a anotação continua aberta
        var status = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM person_notes WHERE id = @i", ("i", noteId));
        Assert.Equal("aberta", status);
    }

    // ------------------------------------------------------------------- isolamento
    [Fact]
    public async Task Notes_DeOutroTenant_404_NuncaOutroCodigo()
    {
        var orgA = await fixture.CreateOrganizationAsync($"NotaA {Guid.NewGuid():N}"[..20]);
        var orgB = await fixture.CreateOrganizationAsync($"NotaB {Guid.NewGuid():N}"[..20]);
        var (_, keyA) = await fixture.CreateEnrollmentKeyWithSecretAsync(orgA.Id);
        var (_, keyB) = await fixture.CreateEnrollmentKeyWithSecretAsync(orgB.Id);
        var adminA = await fixture.CreateUserAsync(orgA.Id, UserRole.Admin, mfaEnabled: true);
        var adminB = await fixture.CreateUserAsync(orgB.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var tokenA = await AuthClient.LoginAsync(client, adminA);
        var tokenB = await AuthClient.LoginAsync(client, adminB);

        var deviceA = await AgentClient.EnrollAsync(client, keyA, hostname: "NB-XT-A");
        var deviceB = await AgentClient.EnrollAsync(client, keyB, hostname: "NB-XT-B");

        const string sidA = "S-1-5-21-9600-9600-9600-6001";
        const string sidB = "S-1-5-21-9600-9600-9600-6002";
        await SeedActiveAsync(client, deviceA, sidA, "A\\helena", "xt-erp.exe", T(12, 0), 5);
        await SeedActiveAsync(client, deviceB, sidB, "B\\igor", "xt-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        using var createdB = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{sidB}/notes", tokenB,
            new { kind = "anotacao", started_at = T(12, 0), ended_at = T(12, 30), body = "Interno do tenant B." },
            HttpStatusCode.Created);
        var noteB = createdB.RootElement.GetProperty("id").GetGuid();

        // A lendo a pessoa de B: 404 (a pessoa não existe no tenant de A)
        using var leitura = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/people/{sidB}/notes?from={LocalDate(T(12, 0))}&to={LocalDate(T(12, 0))}", tokenA);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(leitura)).StatusCode);

        // A criando na pessoa de B: 404
        using var escrita = AuthClient.AuthorizedRequest(
            HttpMethod.Post, $"/api/v1/people/{sidB}/notes", tokenA);
        escrita.Content = JsonContent.Create(new
        {
            kind = "anotacao",
            started_at = T(12, 0),
            ended_at = T(12, 30),
            body = "Não deveria entrar.",
        });
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(escrita)).StatusCode);

        // A revisando a nota de B, mesmo pela pessoa DELE: 404, nunca 403
        using var revisao = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, $"/api/v1/people/{sidA}/notes/{noteB}", tokenA);
        revisao.Content = JsonContent.Create(new { status = "aceita" });
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(revisao)).StatusCode);

        // e nada do tenant B foi tocado
        var statusB = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM person_notes WHERE id = @i", ("i", noteB));
        Assert.Equal("aberta", statusB);
    }

    // ------------------------------------------------- rota pelo device_user_id do portal
    [Fact]
    public async Task Notes_RotaAceitaDeviceUserId_ResolveOSidDaPessoa()
    {
        var org = await fixture.CreateOrganizationAsync($"NotaDU {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-DU-1");

        const string sid = "S-1-5-21-9700-9700-9700-7001";
        await SeedActiveAsync(client, device, sid, "ACME\\joana", "du-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        // é assim que a página /pessoas/:id navega: por device_user_id, porque o contrato de
        // GET /device-users/{id} não devolve o windows_sid
        var deviceUserId = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM device_users WHERE tenant_id = @t AND windows_sid = @s",
            ("t", org.Id), ("s", sid));

        using var created = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{deviceUserId}/notes", token,
            new { kind = "anotacao", started_at = T(12, 0), ended_at = T(12, 30), body = "Onboarding." },
            HttpStatusCode.Created);

        // gravou no SID canônico, e a leitura pelo SID encontra a mesma nota
        Assert.Equal(sid, created.RootElement.GetProperty("windows_sid").GetString());

        using var listed = await GetNotesAsync(client, token, sid, LocalDate(T(12, 0)));
        var item = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal("Onboarding.", item.GetProperty("body").GetString());
    }

    // --------------------------------------------------------------------- validações
    [Fact]
    public async Task Notes_CorpoInvalido_400_ESidDesconhecido_404()
    {
        var org = await fixture.CreateOrganizationAsync($"NotaVal {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-VAL-1");

        const string sid = "S-1-5-21-9800-9800-9800-8001";
        await SeedActiveAsync(client, device, sid, "ACME\\lucas", "val-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        // kind fora do vocabulário
        using var kindRuim = AuthClient.AuthorizedRequest(HttpMethod.Post, $"/api/v1/people/{sid}/notes", token);
        kindRuim.Content = JsonContent.Create(new
        {
            kind = "reclamacao",
            started_at = T(12, 0),
            ended_at = T(12, 30),
            body = "x",
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(kindRuim)).StatusCode);

        // período invertido
        using var invertido = AuthClient.AuthorizedRequest(HttpMethod.Post, $"/api/v1/people/{sid}/notes", token);
        invertido.Content = JsonContent.Create(new
        {
            kind = "anotacao",
            started_at = T(15, 0),
            ended_at = T(12, 0),
            body = "x",
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invertido)).StatusCode);

        // corpo vazio
        using var vazio = AuthClient.AuthorizedRequest(HttpMethod.Post, $"/api/v1/people/{sid}/notes", token);
        vazio.Content = JsonContent.Create(new
        {
            kind = "anotacao",
            started_at = T(12, 0),
            ended_at = T(12, 30),
            body = "   ",
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(vazio)).StatusCode);

        // pessoa que não existe neste tenant: 404
        using var semPessoa = AuthClient.AuthorizedRequest(
            HttpMethod.Post, "/api/v1/people/S-1-5-21-0000-0000-0000-9999/notes", token);
        semPessoa.Content = JsonContent.Create(new
        {
            kind = "anotacao",
            started_at = T(12, 0),
            ended_at = T(12, 30),
            body = "Ninguém.",
        });
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(semPessoa)).StatusCode);

        // janela de leitura acima de 92 dias: 400 (mesma régua dos outros endpoints)
        using var janela = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/people/{sid}/notes?from=2026-01-01&to=2026-12-31", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(janela)).StatusCode);
    }

    // ------------------------------------------------------- período que CRUZA o recorte
    [Fact]
    public async Task Notes_AnotacaoQueAtravessaOPeriodo_ApareceNoDiaDoMeio()
    {
        var org = await fixture.CreateOrganizationAsync($"NotaCruz {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-CRUZ-1");

        const string sid = "S-1-5-21-9900-9900-9900-9001";
        await SeedActiveAsync(client, device, sid, "ACME\\marta", "cruz-erp.exe", T(12, 0), 5);
        await RunPipelineAsync();

        // de D-2 a D+2, com a tela mostrando só o dia do meio
        using var _ = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/people/{sid}/notes", token,
            new
            {
                kind = "anotacao",
                started_at = T(12, 0).AddDays(-2),
                ended_at = T(12, 0).AddDays(2),
                body = "Semana de treinamento fora do escritório.",
            },
            HttpStatusCode.Created);

        using var listed = await GetNotesAsync(client, token, sid, LocalDate(T(12, 0)));
        Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray().ToList());

        // e some quando o recorte não cruza o período
        var longe = LocalDate(T(12, 0).AddDays(30));
        using var fora = await SendJsonAsync(client, HttpMethod.Get,
            $"/api/v1/people/{sid}/notes?from={longe}&to={longe}", token, null, HttpStatusCode.OK);
        Assert.Empty(fora.RootElement.GetProperty("items").EnumerateArray().ToList());
    }
}
