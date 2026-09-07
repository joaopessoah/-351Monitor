using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// F7 — /api/v1/teams e /api/v1/organization/holidays: CRUD de equipe, composição por PESSOA,
/// jornada por equipe, feriados e a base de capacidade.
///
/// O que fica provado aqui: papéis (Viewer lê, Admin escreve), 409 de nome e de etiqueta
/// duplicada, 404 de recurso inexistente, a composição DECLARATIVA que MOVE a pessoa entre
/// equipes, a herança da jornada (equipe → organização) e — o ponto da fase — que o FERIADO SAI
/// do denominador da capacidade.
/// </summary>
[Collection(ApiCollection.Name)]
public class TeamsEndpointTests(ApiTestFixture fixture)
{
    private async Task<(HttpClient Client, Guid TenantId, string Admin, string Viewer)> SetupAsync(string prefix)
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

    private static async Task<Guid> CreateTeamAsync(
        HttpClient client, string token, string name, string? tag = null, object? workHours = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/teams", token,
            new { name, tag, work_hours = workHours });
        using var doc = await ReadAsync(response, HttpStatusCode.Created);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    // ------------------------------------------------------------------------------- CRUD
    [Fact]
    public async Task Crud_CriaListaEditaExclui_ComConflitosDeNomeEEtiqueta()
    {
        var (client, _, admin, viewer) = await SetupAsync("EqCrud");

        var marketing = await CreateTeamAsync(client, admin, "Marketing", tag: "mkt");
        await CreateTeamAsync(client, admin, "Financeiro");

        // nome duplicado: 409
        var duplicado = await SendAsync(client, HttpMethod.Post, "/api/v1/teams", admin, new { name = "Marketing" });
        Assert.Equal(HttpStatusCode.Conflict, duplicado.StatusCode);

        // etiqueta já reivindicada por outra equipe: 409 (uma etiqueta tem no máximo uma dona)
        var etiquetaDuplicada = await SendAsync(client, HttpMethod.Post, "/api/v1/teams", admin,
            new { name = "Growth", tag = "mkt" });
        Assert.Equal(HttpStatusCode.Conflict, etiquetaDuplicada.StatusCode);

        // Viewer LÊ (ordem alfabética) e NÃO escreve
        var lista = await SendAsync(client, HttpMethod.Get, "/api/v1/teams", viewer);
        using (var doc = await ReadAsync(lista, HttpStatusCode.OK))
        {
            var nomes = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("name").GetString()).ToList();
            Assert.Equal(new[] { "Financeiro", "Marketing" }, nomes);
            var mkt = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("name").GetString() == "Marketing");
            Assert.Equal("mkt", mkt.GetProperty("tag").GetString());
            Assert.True(mkt.GetProperty("work_hours_inherited").GetBoolean());
            Assert.Equal(0, mkt.GetProperty("member_count").GetInt32());
        }

        var proibido = await SendAsync(client, HttpMethod.Post, "/api/v1/teams", viewer, new { name = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, proibido.StatusCode);

        // PATCH parcial: renomeia e limpa a etiqueta (null = limpa, ausente = não muda)
        var patch = await SendAsync(client, HttpMethod.Patch, $"/api/v1/teams/{marketing}", admin,
            new { name = "Marketing e Growth", tag = (string?)null });
        using (var doc = await ReadAsync(patch, HttpStatusCode.OK))
        {
            Assert.Equal("Marketing e Growth", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tag").ValueKind);
        }

        var excluir = await SendAsync(client, HttpMethod.Delete, $"/api/v1/teams/{marketing}", admin);
        Assert.Equal(HttpStatusCode.NoContent, excluir.StatusCode);

        // inexistente (e de outro tenant) → 404, nunca 403
        var sumiu = await SendAsync(client, HttpMethod.Delete, $"/api/v1/teams/{marketing}", admin);
        Assert.Equal(HttpStatusCode.NotFound, sumiu.StatusCode);
    }

    [Fact]
    public async Task Jornada_DaEquipeVence_EAusenciaHerdaADaOrganizacao()
    {
        var (client, _, admin, _) = await SetupAsync("EqJornada");

        var orgHours = await SendAsync(client, HttpMethod.Patch, "/api/v1/organization", admin,
            new { business_hours = new { days = new[] { 1, 2, 3, 4, 5 }, start = "09:00", end = "18:00" } });
        (await ReadAsync(orgHours, HttpStatusCode.OK)).Dispose();

        var herdeira = await CreateTeamAsync(client, admin, "Suporte");
        await CreateTeamAsync(client, admin, "Plantão",
            workHours: new { days = new[] { 1, 2, 3, 4, 5, 6, 7 }, start = "07:00", end = "13:00" });

        var lista = await SendAsync(client, HttpMethod.Get, "/api/v1/teams", admin);
        using var doc = await ReadAsync(lista, HttpStatusCode.OK);

        var suporte = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == herdeira);
        Assert.True(suporte.GetProperty("work_hours_inherited").GetBoolean());
        Assert.Equal(JsonValueKind.Null, suporte.GetProperty("work_hours").ValueKind);
        // a jornada EFETIVA da equipe sem declaração é a da organização
        Assert.Equal("09:00", suporte.GetProperty("effective_work_hours").GetProperty("start").GetString());

        var plantao = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "Plantão");
        Assert.False(plantao.GetProperty("work_hours_inherited").GetBoolean());
        Assert.Equal("07:00", plantao.GetProperty("effective_work_hours").GetProperty("start").GetString());

        // jornada malformada não entra (mesmo parser do business_hours da organização)
        var invalida = await SendAsync(client, HttpMethod.Post, "/api/v1/teams", admin,
            new { name = "Inválida", work_hours = new { days = new[] { 1 }, start = "18:00", end = "08:00" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalida.StatusCode);
    }

    // ------------------------------------------------------------------------- composição
    [Fact]
    public async Task Composicao_EhDeclarativa_EMovePessoaEntreEquipes()
    {
        var (client, tenantId, admin, viewer) = await SetupAsync("EqPessoas");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-EQ-MEMBROS");

        const string sidA = "S-1-5-21-EQ-0001";
        const string sidB = "S-1-5-21-EQ-0002";
        foreach (var (sid, user) in new[] { (sidA, "ACME\\ana"), (sidB, "ACME\\bruno") })
        {
            await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                """
                INSERT INTO device_users (id, tenant_id, device_id, windows_sid, windows_username,
                                          first_seen_at, last_seen_at)
                VALUES (@id, @t, @d, @sid, @u, now(), now())
                """,
                ("id", Uuid7.NewUuid7()), ("t", tenantId), ("d", device.Id), ("sid", sid), ("u", user));
        }

        var alfa = await CreateTeamAsync(client, admin, "Alfa");
        var beta = await CreateTeamAsync(client, admin, "Beta");

        var comporAlfa = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{alfa}/members", admin,
            new { windows_sids = new[] { sidA, sidB } });
        using (var doc = await ReadAsync(comporAlfa, HttpStatusCode.OK))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("added").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("removed").GetInt32());
            // nome resolvido pela mesma régua da lista de colaboradores
            var nomes = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("display_name").GetString()).ToList();
            Assert.Contains("ACME\\ana", nomes);
        }

        // declarativo: reenviar só o sidA TIRA o sidB da equipe
        var reduzir = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{alfa}/members", admin,
            new { windows_sids = new[] { sidA } });
        using (var doc = await ReadAsync(reduzir, HttpStatusCode.OK))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("added").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("removed").GetInt32());
        }

        // uma pessoa em UMA equipe: pôr o sidA em Beta o MOVE de Alfa (sem erro)
        var mover = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{beta}/members", admin,
            new { windows_sids = new[] { sidA } });
        using (var doc = await ReadAsync(mover, HttpStatusCode.OK))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("added").GetInt32());
        }

        var alfaAgora = await SendAsync(client, HttpMethod.Get, $"/api/v1/teams/{alfa}/members", viewer);
        using (var doc = await ReadAsync(alfaAgora, HttpStatusCode.OK))
        {
            Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());
        }

        // Viewer não compõe equipe
        var proibido = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{beta}/members", viewer,
            new { windows_sids = new[] { sidB } });
        Assert.Equal(HttpStatusCode.Forbidden, proibido.StatusCode);

        // equipe inexistente → 404
        var inexistente = await SendAsync(client, HttpMethod.Get, $"/api/v1/teams/{Guid.NewGuid()}/members", admin);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task MudarComposicaoEEtiqueta_EnfileiraReagregacao_RenomearNao()
    {
        var (client, tenantId, admin, _) = await SetupAsync("EqReagg");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-EQ-REAGG");

        // um intervalo de ontem para haver o que reenfileirar
        var ontem = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1).Date);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO activity_intervals (id, tenant_id, device_id, device_user_id, started_at, ended_at,
                                            state, source_day)
            VALUES (@i, @t, @d, NULL, now() - interval '25 hours', now() - interval '24 hours', 'active', @day)
            """,
            ("i", Uuid7.NewUuid7()), ("t", tenantId), ("d", device.Id), ("day", ontem));

        async Task<long> DirtyAsync() => await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM dirty_days WHERE tenant_id = @t", ("t", tenantId));

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "DELETE FROM dirty_days WHERE tenant_id = @t", ("t", tenantId));

        // criar SEM etiqueta e sem pessoas não muda a equipe de lane nenhuma: não reagrega
        var time = await CreateTeamAsync(client, admin, "Reagg");
        Assert.Equal(0, await DirtyAsync());

        // compor a equipe muda a regra aplicada ao tempo daquelas pessoas: reagrega
        var compor = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{time}/members", admin,
            new { windows_sids = new[] { "S-1-5-21-EQ-REAGG" } });
        using (var doc = await ReadAsync(compor, HttpStatusCode.OK))
        {
            Assert.True(doc.RootElement.GetProperty("reaggregation_enqueued").GetInt32() >= 1);
        }

        Assert.True(await DirtyAsync() >= 1);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "DELETE FROM dirty_days WHERE tenant_id = @t", ("t", tenantId));

        // renomear é rótulo: NÃO reagrega
        var renomear = await SendAsync(client, HttpMethod.Patch, $"/api/v1/teams/{time}", admin,
            new { name = "Reagg 2" });
        (await ReadAsync(renomear, HttpStatusCode.OK)).Dispose();
        Assert.Equal(0, await DirtyAsync());

        // passar a reivindicar uma etiqueta muda a equipe resolvida das lanes: reagrega
        var etiquetar = await SendAsync(client, HttpMethod.Patch, $"/api/v1/teams/{time}", admin,
            new { tag = "reagg" });
        (await ReadAsync(etiquetar, HttpStatusCode.OK)).Dispose();
        Assert.True(await DirtyAsync() >= 1);
    }

    // ---------------------------------------------------------------------------- feriados
    [Fact]
    public async Task Feriados_SemeiaCalendarioNacional_CriaERemove()
    {
        var (client, _, admin, viewer) = await SetupAsync("EqFeriado");
        var ano = DateTime.UtcNow.Year;

        var semear = await SendAsync(client, HttpMethod.Post, "/api/v1/organization/holidays/seed", admin,
            new { years = new[] { ano } });
        using (var doc = await ReadAsync(semear, HttpStatusCode.OK))
        {
            // 10 feriados nacionais por lei (9 fixos + Sexta-feira Santa)
            Assert.Equal(10, doc.RootElement.GetProperty("inserted").GetInt32());
        }

        // idempotente: semear de novo não duplica nem ressuscita nada
        var denovo = await SendAsync(client, HttpMethod.Post, "/api/v1/organization/holidays/seed", admin,
            new { years = new[] { ano } });
        using (var doc = await ReadAsync(denovo, HttpStatusCode.OK))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("inserted").GetInt32());
        }

        var lista = await SendAsync(client, HttpMethod.Get, $"/api/v1/organization/holidays?year={ano}", viewer);
        using (var doc = await ReadAsync(lista, HttpStatusCode.OK))
        {
            var datas = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("date").GetString()).ToList();
            Assert.Contains($"{ano}-09-07", datas);   // Independência
            Assert.Contains($"{ano}-11-20", datas);   // Consciência Negra (Lei 14.759/2023)
            Assert.DoesNotContain(datas, d => d == $"{ano}-12-24"); // véspera não é feriado

            // Carnaval e Corpus Christi vêm como SUGESTÃO, não semeados (ponto facultativo)
            Assert.Equal(3, doc.RootElement.GetProperty("suggestions").GetArrayLength());
        }

        var criar = await SendAsync(client, HttpMethod.Post, "/api/v1/organization/holidays", admin,
            new { date = $"{ano}-06-24", name = "São João (municipal)" });
        (await ReadAsync(criar, HttpStatusCode.OK)).Dispose();

        var invalido = await SendAsync(client, HttpMethod.Post, "/api/v1/organization/holidays", admin,
            new { date = "24/06", name = "São João" });
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        var proibido = await SendAsync(client, HttpMethod.Post, "/api/v1/organization/holidays", viewer,
            new { date = $"{ano}-06-25", name = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, proibido.StatusCode);

        var remover = await SendAsync(client, HttpMethod.Delete,
            $"/api/v1/organization/holidays/{ano}-06-24", admin);
        Assert.Equal(HttpStatusCode.NoContent, remover.StatusCode);

        var depois = await SendAsync(client, HttpMethod.Get, $"/api/v1/organization/holidays?year={ano}", admin);
        using (var doc = await ReadAsync(depois, HttpStatusCode.OK))
        {
            Assert.DoesNotContain(doc.RootElement.GetProperty("items").EnumerateArray(),
                i => i.GetProperty("date").GetString() == $"{ano}-06-24");
        }
    }

    // -------------------------------------------------------------------------- capacidade
    [Fact]
    public async Task Capacidade_TiraFeriadoDoDenominador_EUsaAJornadaDaEquipe()
    {
        var (client, _, admin, viewer) = await SetupAsync("EqCapac");

        var orgHours = await SendAsync(client, HttpMethod.Patch, "/api/v1/organization", admin,
            new { business_hours = new { days = new[] { 1, 2, 3, 4, 5 }, start = "08:00", end = "18:00" } });
        (await ReadAsync(orgHours, HttpStatusCode.OK)).Dispose();

        await CreateTeamAsync(client, admin, "Meio período",
            workHours: new { days = new[] { 1, 2, 3, 4, 5 }, start = "08:00", end = "12:00" });

        // 01/09/2026 (terça) a 07/09/2026 (segunda): 5 dias úteis, sendo 07/09 feriado nacional
        const string from = "2026-09-01";
        const string to = "2026-09-07";
        var criarFeriado = await SendAsync(client, HttpMethod.Post, "/api/v1/organization/holidays", admin,
            new { date = "2026-09-07", name = "Independência do Brasil" });
        (await ReadAsync(criarFeriado, HttpStatusCode.OK)).Dispose();

        var capacidade = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/teams/capacity?from={from}&to={to}", viewer);
        using var doc = await ReadAsync(capacidade, HttpStatusCode.OK);
        var scopes = doc.RootElement.GetProperty("scopes").EnumerateArray().ToList();

        var org = scopes.Single(s => s.GetProperty("scope_type").GetString() == "organization");
        Assert.Equal(10, org.GetProperty("daily_hours").GetDouble());
        // 5 dias úteis na janela, MENOS o feriado de 07/09: 4
        Assert.Equal(4, org.GetProperty("business_days").GetInt32());
        Assert.Equal(1, org.GetProperty("holidays_excluded").GetInt32());
        Assert.Equal(4 * 10 * 3600, org.GetProperty("capacity_seconds_per_person").GetInt64());

        var time = scopes.Single(s => s.GetProperty("scope_type").GetString() == "team");
        // a jornada da EQUIPE (4 h) vence a da organização (10 h); os feriados são os mesmos
        Assert.Equal(4, time.GetProperty("daily_hours").GetDouble());
        Assert.Equal(4, time.GetProperty("business_days").GetInt32());
        Assert.Equal(4 * 4 * 3600, time.GetProperty("capacity_seconds_per_person").GetInt64());

        // mesma régua de período dos demais endpoints históricos (92 dias)
        var longoDemais = await SendAsync(client, HttpMethod.Get,
            "/api/v1/teams/capacity?from=2026-01-01&to=2026-12-31", admin);
        Assert.Equal(HttpStatusCode.BadRequest, longoDemais.StatusCode);
    }
}
