using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.DemoSeed;
using M351.Infrastructure.Security;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Seed de demo (F3.6) em ESCALA REDUZIDA (4 devices × 6 dias), fim-a-fim no banco de teste:
/// o seeder injeta raw_events com o shape da ingestão, marca cursores e roda o pipeline REAL
/// (IntervalizationService + DailyAggregationService) — nada de activity_intervals/daily_*
/// na mão. Depois valida a navegação da demo pela API (timeline equipe, dashboard, jornada),
/// o device archived fora das lanes, a lacuna de seq como data_incomplete, o abort sem
/// --reset, o --reset re-semeando sem duplicar e o isolamento total de outro tenant.
/// Tudo num único Fact: cada seed custa segundos e os cenários são sequenciais por natureza.
/// </summary>
[Collection(ApiCollection.Name)]
public class DemoSeederTests(ApiTestFixture fixture)
{
    private const string Slug = "empresa-demo-teste";

    private async Task<long> CountAsync(string sql, params (string Name, object? Value)[] args) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString, sql, args);

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string token, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    [Fact]
    public async Task SeedDemo_EscalaReduzida_FimAFim()
    {
        // ----- tenant de CONTROLE criado ANTES: nada dele pode mudar (isolamento) -----
        var control = await fixture.CreateOrganizationAsync($"Controle {Guid.NewGuid():N}"[..20]);
        var controlDevice = await fixture.CreateDeviceAsync(control.Id, "NB-CONTROLE");
        await fixture.CreateUserAsync(control.Id, UserRole.Viewer);
        var controlBefore = await SnapshotTenantAsync(control.Id);

        var hasher = fixture.Services.GetRequiredService<IPasswordHasher>();
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        var seeder = new DemoSeeder(dataSource, hasher);
        var options = new DemoSeedOptions
        {
            DeviceCount = 4,
            Days = 6,
            Slug = Slug,
            OrgName = "Empresa Demo Teste",
        };

        var result = await seeder.RunAsync(options);

        // ----- raw_events inseridos; intervalos e agregados construídos PELO PIPELINE -----
        var rawCount = await CountAsync("SELECT count(*) FROM raw_events WHERE tenant_id = @t", ("t", result.TenantId));
        Assert.True(rawCount > 500, $"esperava centenas de raw_events; veio {rawCount}");
        Assert.Equal(rawCount, result.EventCount);

        var intervalCount = await CountAsync("SELECT count(*) FROM activity_intervals WHERE tenant_id = @t", ("t", result.TenantId));
        Assert.True(intervalCount > 50, $"esperava intervalos do pipeline; veio {intervalCount}");

        // evidência de que o pipeline REAL convergiu (não inserts manuais): cursores limpos
        // e fila de agregação vazia para o tenant
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM ingest_cursors WHERE tenant_id = @t AND dirty_from IS NOT NULL", ("t", result.TenantId)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM dirty_days WHERE tenant_id = @t", ("t", result.TenantId)));

        var summaryCount = await CountAsync("SELECT count(*) FROM daily_device_summaries WHERE tenant_id = @t", ("t", result.TenantId));
        Assert.True(summaryCount > 0, "daily_device_summaries vazias");

        // o pré-mapeamento app_catalog → tenant_app_categories classifica já na 1ª agregação
        var workSeconds = await CountAsync(
            "SELECT COALESCE(sum(seconds_work_related), 0)::bigint FROM daily_device_summaries WHERE tenant_id = @t",
            ("t", result.TenantId));
        Assert.True(workSeconds > 0, "nenhum segundo classificado como trabalho — mapeamento de categorias não pegou");

        // badge "relógio dessincronizado" (Seção 8.7): o device especial precisa estar ACIMA
        // do limiar do portal (DispositivosPage.CLOCK_SKEW_LIMIT_MS = 120.000 ms), senão o
        // badge do aceite jamais aparece na demo
        Assert.NotNull(result.ClockSkewDeviceId);
        var skewMs = await CountAsync(
            "SELECT clock_offset_ms FROM devices WHERE tenant_id = @t AND id = @d",
            ("t", result.TenantId), ("d", result.ClockSkewDeviceId));
        Assert.True(Math.Abs(skewMs) > 120_000, $"|clock_offset_ms| = {Math.Abs(skewMs)} não acende o badge (> 120000 exigido)");

        // presença "AGORA" (N6 = 180 s): o refresh pós-pipeline deixa os vivos dentro da janela
        var online = await CountAsync("""
            SELECT count(*) FROM device_current_state s
            JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.tenant_id = @t AND d.status = 'active' AND s.last_contact_at > now() - interval '180 seconds'
            """, ("t", result.TenantId));
        Assert.Equal(3, online); // 4 devices − 1 archived (sem "sem comunicação" nesta escala)

        // shape idêntico à ingestão: HEARTBEAT com foreground_process no payload tem a COLUNA
        // process_name preenchida (ParseEvent), e gerenciador de senhas NUNCA aparece cru —
        // o agente real o reporta como "(privado)" (TitleMasker.FactoryIgnoredProcesses)
        Assert.Equal(0, await CountAsync("""
            SELECT count(*) FROM raw_events WHERE tenant_id = @t AND event_type = 'HEARTBEAT'
              AND payload->>'foreground_process' IS NOT NULL AND process_name IS NULL
            """, ("t", result.TenantId)));
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM raw_events WHERE tenant_id = @t AND process_name = 'keepass.exe'", ("t", result.TenantId)));
        Assert.True(await CountAsync(
            "SELECT count(*) FROM raw_events WHERE tenant_id = @t AND process_name = '(privado)'", ("t", result.TenantId)) > 0,
            "persona dev deveria emitir o processo privado '(privado)'");

        // ----- navegação da demo pela API, logado como o viewer semeado -----
        var client = fixture.CreateApiClient();
        var viewer = new TestUser(Guid.Empty, result.TenantId, result.ViewerEmail, result.ViewerPassword, null);
        var token = await AuthClient.LoginAsync(client, viewer);

        // timeline de equipe no dia da lacuna de seq: lanes presentes, archived FORA,
        // device com lacuna marcado data_incomplete
        using (var team = await GetJsonAsync(client, token, $"/api/v1/timeline/team?date={result.SeqGapDay:yyyy-MM-dd}"))
        {
            var lanes = team.RootElement.GetProperty("lanes").EnumerateArray().ToList();
            Assert.Equal(3, lanes.Count); // 4 devices − 1 archived
            Assert.DoesNotContain(lanes, l => l.GetProperty("device_id").GetGuid() == result.ArchivedDeviceId);
            Assert.Contains(lanes, l => l.GetProperty("intervals").GetArrayLength() > 0);

            var gapLane = lanes.Single(l => l.GetProperty("device_id").GetGuid() == result.SeqGapDeviceId);
            Assert.True(gapLane.GetProperty("data_incomplete").GetBoolean(),
                "lane do device com lacuna de seq deveria estar data_incomplete");
        }

        // dashboard com dias (range cobre a janela semeada)
        var to = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(-3).DateTime);
        var from = to.AddDays(-7);
        using (var summary = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))
        {
            Assert.True(summary.RootElement.GetProperty("days").GetArrayLength() > 0, "dashboard sem dias");
            Assert.True(summary.RootElement.GetProperty("totals").GetProperty("seconds_on").GetInt64() > 0);
        }

        // relatório de jornada com linhas e horas
        using (var jornada = await GetJsonAsync(client, token,
            $"/api/v1/reports/jornada?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&page_size=100"))
        {
            var items = jornada.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.True(items.Count > 0, "jornada sem linhas");
            Assert.Contains(items, i => i.GetProperty("seconds_on").GetInt64() > 0);
        }

        // ----- rodar de novo SEM --reset: aborta com mensagem clara e sem efeito -----
        var ex = await Assert.ThrowsAsync<DemoSeedException>(() => seeder.RunAsync(options));
        Assert.Contains("--reset", ex.Message);
        Assert.Equal(rawCount, await CountAsync("SELECT count(*) FROM raw_events WHERE tenant_id = @t", ("t", result.TenantId)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations WHERE slug = @s", ("s", Slug)));

        // ----- --reset: apaga o tenant demo e re-semeia sem duplicar -----
        var reseeded = await seeder.RunAsync(options with { Reset = true });
        Assert.NotEqual(result.TenantId, reseeded.TenantId);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM organizations WHERE slug = @s", ("s", Slug)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM raw_events WHERE tenant_id = @t", ("t", result.TenantId)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM devices WHERE tenant_id = @t", ("t", result.TenantId)));
        Assert.Equal(4, await CountAsync("SELECT count(*) FROM devices WHERE tenant_id = @t", ("t", reseeded.TenantId)));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM users WHERE tenant_id = @t", ("t", reseeded.TenantId)));
        Assert.True(await CountAsync("SELECT count(*) FROM activity_intervals WHERE tenant_id = @t", ("t", reseeded.TenantId)) > 50);

        // login funciona com as credenciais NOVAS do re-seed
        var viewer2 = new TestUser(Guid.Empty, reseeded.TenantId, reseeded.ViewerEmail, reseeded.ViewerPassword, null);
        await AuthClient.LoginAsync(fixture.CreateApiClient(), viewer2);

        // ----- NADA vazou para o tenant de controle -----
        var controlAfter = await SnapshotTenantAsync(control.Id);
        Assert.Equal(controlBefore, controlAfter);
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM devices WHERE tenant_id = @t AND id = @d", ("t", control.Id), ("d", controlDevice.Id)));
    }

    /// <summary>Contagens por tabela do tenant — comparadas antes/depois para provar isolamento.</summary>
    private async Task<string> SnapshotTenantAsync(Guid tenantId)
    {
        string[] tables =
        [
            "organizations:id", "users:tenant_id", "devices:tenant_id", "device_users:tenant_id",
            "raw_events:tenant_id", "activity_intervals:tenant_id", "daily_device_summaries:tenant_id",
            "daily_app_usage:tenant_id", "categories:tenant_id", "tenant_app_categories:tenant_id",
            "ingest_cursors:tenant_id", "device_current_state:tenant_id",
        ];
        var parts = new List<string>();
        foreach (var entry in tables)
        {
            var split = entry.Split(':');
            var count = await CountAsync($"SELECT count(*) FROM {split[0]} WHERE {split[1]} = @t", ("t", tenantId));
            parts.Add($"{split[0]}={count}");
        }

        return string.Join(";", parts);
    }
}
