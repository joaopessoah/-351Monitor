using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Maintenance;
using M351.IntegrationTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// F4.7 — Auditoria de acesso. Cobre:
///  - GET /api/v1/audit-logs: lista/filtra por actor/action/período/página (Owner e Admin);
///  - Viewer recebe 403 (aba só de Owner/Admin);
///  - isolamento: trilha de outro tenant NÃO aparece;
///  - "teste de middleware" (DoD 11.3): cada endpoint de leitura de dado pessoal ANOTADO
///    (timeline device+team, reports jornada/usage, app-catalog titles) gera linha em audit_log,
///    com actor_ip PREENCHIDO (via filter, do RemoteIpAddress que reflete ForwardedHeaders);
///  - trigger append-only: UPDATE e DELETE em audit_log FALHAM (Assert que lança);
///  - retenção: PartitionMaintenance ainda DROPA partição de audit_log com o trigger ativo.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuditLogTests(ApiTestFixture fixture)
{
    private async Task<(HttpClient Client, Guid TenantId, TestUser Owner, string Token)> SetupAsync(
        string prefix, UserRole role = UserRole.Owner)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        // Owner e Admin exigem MFA (RequiresMfa); Viewer não — o helper de login resolve ambos
        var user = await fixture.CreateUserAsync(org.Id, role, mfaEnabled: role != UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, user);
        return (client, org.Id, user, token);
    }

    private async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string url)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        return await client.SendAsync(request);
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient client, string token, string url)
    {
        var response = await GetAsync(client, token, url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"esperado OK, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>Insere uma linha de trilha direto (controle fino de ator/ação/período nos testes de listagem).</summary>
    private async Task SeedAuditAsync(
        Guid tenantId, string action, Guid? actorUserId, DateTimeOffset occurredAt,
        string? targetType = null, Guid? targetId = null, string? ip = null)
    {
        // a InitialCreate só cria as partições do mês corrente/próximo; datas fixas (ex.: 200d
        // atrás) precisam da partição mensal criada sob demanda (mesmo padrão dos demais testes)
        var month = new DateOnly(occurredAt.Year, occurredAt.Month, 1);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, $"""
            CREATE TABLE IF NOT EXISTS audit_log_{month:yyyyMM} PARTITION OF audit_log
            FOR VALUES FROM ('{month:yyyy-MM-dd}') TO ('{month.AddMonths(1):yyyy-MM-dd}')
            """);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO audit_log (id, tenant_id, actor_user_id, actor_ip, action, target_type, target_id, occurred_at)
            VALUES (@id, @t, @a, @ip::inet, @act, @tt, @ti, @occ)
            """,
            ("id", Uuid7.NewUuid7()), ("t", tenantId), ("a", actorUserId),
            ("ip", ip), ("act", action), ("tt", targetType), ("ti", targetId), ("occ", occurredAt));
    }

    // ============================================================ GET /audit-logs — listagem e filtros
    [Fact]
    public async Task ListaFiltraPorAtorAcaoPeriodoEPagina()
    {
        var (client, tenantId, owner, token) = await SetupAsync("AudList");
        var outroAtor = await fixture.CreateUserAsync(tenantId, UserRole.Admin);

        // hoje (fuso do tenant GMT-3): três ações do owner + uma de outro ator + uma antiga (fora da janela)
        var now = DateTimeOffset.UtcNow;
        await SeedAuditAsync(tenantId, AuditActions.ViewReport, owner.Id, now.AddMinutes(-5), "device", Uuid7.NewUuid7(), "203.0.113.7");
        await SeedAuditAsync(tenantId, AuditActions.ViewTimeline, owner.Id, now.AddMinutes(-4), "device", Uuid7.NewUuid7(), "203.0.113.8");
        await SeedAuditAsync(tenantId, AuditActions.ExportCsv, owner.Id, now.AddMinutes(-3), "export_job", Uuid7.NewUuid7());
        await SeedAuditAsync(tenantId, AuditActions.ViewReport, outroAtor.Id, now.AddMinutes(-2), "team", null, "198.51.100.1");
        await SeedAuditAsync(tenantId, AuditActions.Login, owner.Id, now.AddDays(-200), "user", owner.Id); // fora da janela default

        // sem filtros: default últimos 30 dias → 4 linhas semeadas recentes + 1 login do owner
        // (o login do SetupAsync grava action='login'); a linha de 200d atrás fica fora da janela.
        using (var doc = await GetJsonAsync(client, token, "/api/v1/audit-logs"))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("total").GetInt64());
            var items = doc.RootElement.GetProperty("items");
            Assert.Equal(5, items.GetArrayLength());
            // ordenado occurred_at desc (a 1ª é a mais recente; não dependemos de QUAL é)
            var datas = items.EnumerateArray().Select(i => i.GetProperty("occurred_at").GetDateTimeOffset()).ToList();
            for (var k = 1; k < datas.Count; k++) Assert.True(datas[k - 1] >= datas[k], "items não estão em occurred_at desc");
            // actor_name vem do join com users (display_name) — toda linha deste tenant tem ator conhecido
            Assert.All(items.EnumerateArray(), i => Assert.False(string.IsNullOrEmpty(i.GetProperty("actor_name").GetString())));
        }

        // actor_ip aparece como string no item semeado com IP
        using (var doc = await GetJsonAsync(client, token, "/api/v1/audit-logs?action=view_timeline"))
        {
            var item = doc.RootElement.GetProperty("items")[0];
            Assert.Equal("203.0.113.8", item.GetProperty("actor_ip").GetString());
            Assert.Equal("device", item.GetProperty("target_type").GetString());
        }

        // filtro por ATOR (outroAtor): só a linha dele
        using (var doc = await GetJsonAsync(client, token, $"/api/v1/audit-logs?actor={outroAtor.Id}"))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
            Assert.Equal(outroAtor.Id, doc.RootElement.GetProperty("items")[0].GetProperty("actor_user_id").GetGuid());
        }

        // filtro por AÇÃO (view_report exato): duas linhas (owner + outroAtor)
        using (var doc = await GetJsonAsync(client, token, "/api/v1/audit-logs?action=view_report"))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt64());
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                Assert.Equal("view_report", item.GetProperty("action").GetString());
        }

        // filtro por PERÍODO que inclui a linha antiga (200d atrás)
        var oldDate = now.AddDays(-200).AddHours(-3).ToString("yyyy-MM-dd"); // GMT-3
        using (var doc = await GetJsonAsync(client, token, $"/api/v1/audit-logs?from={oldDate}&to={oldDate}"))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt64());
            Assert.Equal("login", doc.RootElement.GetProperty("items")[0].GetProperty("action").GetString());
        }

        // PAGINAÇÃO: page_size 2 sobre 5 linhas → páginas de 2, 2 e 1
        using (var doc = await GetJsonAsync(client, token, "/api/v1/audit-logs?page=1&page_size=2"))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("total").GetInt64());
            Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(2, doc.RootElement.GetProperty("page_size").GetInt32());
        }
        using (var doc = await GetJsonAsync(client, token, "/api/v1/audit-logs?page=3&page_size=2"))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(3, doc.RootElement.GetProperty("page").GetInt32());
        }
    }

    [Fact]
    public async Task Admin_PodeListar()
    {
        var (client, tenantId, admin, token) = await SetupAsync("AudAdmin", UserRole.Admin);
        await SeedAuditAsync(tenantId, AuditActions.ViewReport, admin.Id, DateTimeOffset.UtcNow.AddMinutes(-1), "device", Uuid7.NewUuid7());

        using var doc = await GetJsonAsync(client, token, "/api/v1/audit-logs");
        Assert.True(doc.RootElement.GetProperty("total").GetInt64() >= 1);
    }

    [Fact]
    public async Task Viewer_Recebe403()
    {
        var (client, _, _, token) = await SetupAsync("AudViewer", UserRole.Viewer);
        var response = await GetAsync(client, token, "/api/v1/audit-logs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RangeMaiorQue92Dias_400ProblemDetails()
    {
        var (client, _, _, token) = await SetupAsync("AudRange");
        var response = await GetAsync(client, token, "/api/v1/audit-logs?from=2026-01-01&to=2026-12-31");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ============================================================ isolamento multi-tenant
    [Fact]
    public async Task TrilhaDeOutroTenant_NaoAparece()
    {
        var (client, tenantA, ownerA, token) = await SetupAsync("AudIsoA");
        var orgB = await fixture.CreateOrganizationAsync($"AudIsoB {Guid.NewGuid():N}"[..20]);
        var ownerB = await fixture.CreateUserAsync(orgB.Id, UserRole.Owner, mfaEnabled: true);

        var now = DateTimeOffset.UtcNow;
        await SeedAuditAsync(tenantA, AuditActions.ViewReport, ownerA.Id, now.AddMinutes(-1), "device", Uuid7.NewUuid7());
        await SeedAuditAsync(orgB.Id, AuditActions.ViewReport, ownerB.Id, now.AddMinutes(-1), "device", Uuid7.NewUuid7());

        using var doc = await GetJsonAsync(client, token, "/api/v1/audit-logs");
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var actorId = item.GetProperty("actor_user_id");
            Assert.NotEqual(ownerB.Id, actorId.ValueKind == JsonValueKind.Null ? Guid.Empty : actorId.GetGuid());
        }
        // filtrar pelo ator de B (cross-tenant) também não vaza
        using var docB = await GetJsonAsync(client, token, $"/api/v1/audit-logs?actor={ownerB.Id}");
        Assert.Equal(0, docB.RootElement.GetProperty("total").GetInt64());
    }

    // ============================================================ trigger append-only
    [Fact]
    public async Task TriggerAppendOnly_BarraUpdateEDelete()
    {
        var (client, tenantId, owner, _) = await SetupAsync("AudTrig");
        await SeedAuditAsync(tenantId, AuditActions.ViewReport, owner.Id, DateTimeOffset.UtcNow.AddMinutes(-1), "device", Uuid7.NewUuid7());

        // UPDATE de linha barrado pelo trigger (mesmo conectando como owner/superuser)
        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                "UPDATE audit_log SET action = 'tamper' WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Contains("append-only", update.MessageText, StringComparison.OrdinalIgnoreCase);

        // DELETE de linha barrado pelo trigger
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                "DELETE FROM audit_log WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Contains("append-only", delete.MessageText, StringComparison.OrdinalIgnoreCase);

        // a linha continua lá (a tentativa de adulteração não teve efeito)
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'", ("t", tenantId)));
    }

    // ============================================================ retenção dropa partição com trigger ativo
    /// <summary>
    /// DoD/contrato 4: o trigger append-only barra UPDATE/DELETE de LINHA, mas o DROP de partição
    /// da retenção (N13) é DDL — não dispara o trigger. Prova que a purga de audit_log continua
    /// dropando a partição expirada com o trigger ativo.
    /// </summary>
    [Fact]
    public async Task RetencaoDropaParticaoDeAuditLog_ComTriggerAtivo()
    {
        _ = fixture.Services; // garante migrations aplicadas (trigger criado)
        var now = DateTimeOffset.UtcNow;
        var expired = new DateOnly(now.Year, now.Month, 1).AddMonths(-25); // > 24m (N13)

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, $"""
            CREATE TABLE IF NOT EXISTS audit_log_{expired:yyyyMM} PARTITION OF audit_log
            FOR VALUES FROM ('{expired:yyyy-MM-dd}') TO ('{expired.AddMonths(1):yyyy-MM-dd}')
            """);
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM pg_class WHERE relname = @n", ("n", $"audit_log_{expired:yyyyMM}")));

        await using var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        var result = await new PartitionMaintenanceService(ds).RunOnceAsync();

        Assert.True(result.AuditDropped >= 1, "a partição de audit_log expirada devia ser dropada com o trigger ativo");
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM pg_class WHERE relname = @n", ("n", $"audit_log_{expired:yyyyMM}")));
    }

    // ============================================================ teste de middleware (DoD 11.3)
    /// <summary>
    /// Cada endpoint de leitura de dado pessoal ANOTADO ([AuditRead]) gera UMA linha em audit_log
    /// pelo AuditReadFilter, com actor_ip PREENCHIDO. O TestServer não define RemoteIpAddress por
    /// default, então injetamos um IP fixo via IStartupFilter (middleware no início do pipeline,
    /// antes do MVC) — mesma posição lógica do RemoteIpAddress real que o ForwardedHeaders preenche.
    /// </summary>
    [Fact]
    public async Task FilterDeLeitura_CadaEndpointAnotado_GravaTrilhaComActorIp()
    {
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new FixedRemoteIpStartupFilter("203.0.113.250"))));

        var org = await fixture.CreateOrganizationAsync($"AudMw {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var token = await AuthClient.LoginAsync(client, viewer);
        var cs = fixture.Database.ConnectionString;

        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-AUD-MW");
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3).Date).ToString("yyyy-MM-dd");

        // 1) timeline/device → view_timeline (target device), actor_ip preenchido
        (await GetJsonAsync(client, token, $"/api/v1/timeline/device?device_id={device.DeviceId}&date={date}")).Dispose();
        await AssertAuditedWithIpAsync(cs, org.Id, AuditActions.ViewTimeline, "device", device.DeviceId);

        // 2) timeline/team → view_timeline (target team, sem alvo individual)
        (await GetJsonAsync(client, token, $"/api/v1/timeline/team?date={date}")).Dispose();
        await AssertTeamAuditedWithIpAsync(cs, org.Id);

        // 3) reports/jornada → view_report SEMPRE
        var rangeFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3).Date).AddDays(-1).ToString("yyyy-MM-dd");
        (await GetJsonAsync(client, token, $"/api/v1/reports/jornada?from={rangeFrom}&to={date}")).Dispose();

        // 4) reports/usage group_by=device → view_report (recorte pessoal)
        (await GetJsonAsync(client, token, $"/api/v1/reports/usage?from={rangeFrom}&to={date}&group_by=device")).Dispose();

        // 5) app-catalog/titles → view_report SEMPRE. Precisamos de um app no catálogo global.
        var appId = await SeedAppAsync(cs);
        (await GetJsonAsync(client, token, $"/api/v1/app-catalog/{appId}/titles?from={rangeFrom}&to={date}")).Dispose();
        await AssertAuditedWithIpAsync(cs, org.Id, AuditActions.ViewReport, "app", appId);

        // todas as linhas de leitura deste tenant têm actor_ip preenchido (DoD)
        var semIp = await TestDb.ScalarAsync<long>(cs, """
            SELECT count(*) FROM audit_log
            WHERE tenant_id = @t AND action IN ('view_timeline','view_report') AND actor_ip IS NULL
            """, ("t", org.Id));
        Assert.Equal(0L, semIp);

        // o actor de toda leitura é o viewer logado
        var atorErrado = await TestDb.ScalarAsync<long>(cs, """
            SELECT count(*) FROM audit_log
            WHERE tenant_id = @t AND action IN ('view_timeline','view_report') AND actor_user_id <> @u
            """, ("t", org.Id), ("u", viewer.Id));
        Assert.Equal(0L, atorErrado);
    }

    /// <summary>404 (recurso inexistente) NÃO gera trilha de leitura — o filter só grava em 2xx.</summary>
    [Fact]
    public async Task FilterDeLeitura_404NaoGravaTrilha()
    {
        var (client, tenantId, _, token) = await SetupAsync("AudMw404");
        var inexistente = Uuid7.NewUuid7();

        var response = await GetAsync(client, token, $"/api/v1/app-catalog/{inexistente}/titles?from=2026-06-01&to=2026-06-01");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report' AND target_id = @a",
            ("t", tenantId), ("a", inexistente)));
    }

    private static async Task AssertAuditedWithIpAsync(string cs, Guid tenantId, string action, string targetType, Guid targetId)
    {
        var row = await TestDb.RowAsync(cs, """
            SELECT host(actor_ip) AS ip, count(*) OVER () AS n
            FROM audit_log
            WHERE tenant_id = @t AND action = @a AND target_type = @tt AND target_id = @ti
            LIMIT 1
            """, ("t", tenantId), ("a", action), ("tt", targetType), ("ti", targetId));
        Assert.NotNull(row);
        Assert.Equal("203.0.113.250", (string)row!["ip"]!);
    }

    private static async Task AssertTeamAuditedWithIpAsync(string cs, Guid tenantId)
    {
        var ip = await TestDb.ScalarAsync<string>(cs, """
            SELECT host(actor_ip) FROM audit_log
            WHERE tenant_id = @t AND action = 'view_timeline' AND target_type = 'team' AND target_id IS NULL
            LIMIT 1
            """, ("t", tenantId));
        Assert.Equal("203.0.113.250", ip);
    }

    /// <summary>Insere um app no catálogo GLOBAL (app_catalog não tem tenant_id) para o drill-down de títulos.</summary>
    private static async Task<Guid> SeedAppAsync(string cs)
    {
        var appId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(cs, """
            INSERT INTO app_catalog (id, process_name, display_name) VALUES (@id, @pn, @dn)
            """,
            ("id", appId), ("pn", $"aud-mw-{Guid.NewGuid():N}.exe"[..20]), ("dn", "Audit MW App"));
        return appId;
    }

    /// <summary>Middleware de teste: fixa o RemoteIpAddress da conexão antes do MVC (e do AuditReadFilter).</summary>
    private sealed class FixedRemoteIpStartupFilter(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMw) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                await nextMw(context);
            });
            next(app);
        };
    }
}
