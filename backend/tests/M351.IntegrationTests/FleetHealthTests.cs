using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Alerts;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// Saúde de frota SERVER-SIDE (F5): GET /devices/health-summary e ?health=alert contam a
/// FROTA INTEIRA (antes a derivação era client-side e valia só para a página de 50), e os
/// alertas de frota por e-mail com toda a calibragem anti-fadiga (cooldown de 24 h,
/// quiet hours, opt-out, gate do plano Pro).
/// </summary>
[Collection(ApiCollection.Name)]
public class FleetHealthTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string OwnerToken)> SetupAsync(string orgName)
    {
        var org = await fixture.CreateOrganizationAsync(orgName);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        return (client, org.Id, await AuthClient.LoginAsync(client, owner));
    }

    private async Task SetHealthAsync(
        Guid deviceId, DateTimeOffset? lastSeen, DateTimeOffset? noticeAcked = null,
        long clockOffsetMs = 0, DateTimeOffset? tamperAt = null)
    {
        // casts explícitos: sem eles o Postgres não infere o tipo de um parâmetro nulo
        await TestDb.ExecuteAsync(Cs, """
            UPDATE devices
            SET last_seen_at = @seen::timestamptz, notice_acked_at = @notice::timestamptz,
                clock_offset_ms = @clock, last_tamper_at = @tamper::timestamptz,
                last_tamper_reason = CASE WHEN @tamper::timestamptz IS NULL THEN NULL ELSE 'helper_killed' END
            WHERE id = @id
            """,
            ("id", deviceId), ("seen", (object?)lastSeen ?? DBNull.Value),
            ("notice", (object?)noticeAcked ?? DBNull.Value), ("clock", clockOffsetMs),
            ("tamper", (object?)tamperAt ?? DBNull.Value));
    }

    [Fact]
    public async Task HealthSummary_ContaFrotaInteira_PorDimensao()
    {
        var (client, tenantId, ownerToken) = await SetupAsync("Org Saúde Frota");
        var agora = DateTimeOffset.UtcNow;

        var saudavel = await fixture.CreateDeviceAsync(tenantId, "NB-SAUDAVEL");
        await SetHealthAsync(saudavel.Id, agora.AddSeconds(-30), noticeAcked: agora.AddDays(-10));

        var offline = await fixture.CreateDeviceAsync(tenantId, "NB-OFFLINE");
        await SetHealthAsync(offline.Id, agora.AddHours(-3), noticeAcked: agora.AddDays(-10));

        var relogio = await fixture.CreateDeviceAsync(tenantId, "NB-RELOGIO");
        await SetHealthAsync(relogio.Id, agora.AddSeconds(-20), noticeAcked: agora.AddDays(-10), clockOffsetMs: 300_000);

        var tamper = await fixture.CreateDeviceAsync(tenantId, "NB-TAMPER");
        await SetHealthAsync(tamper.Id, agora.AddSeconds(-20), noticeAcked: agora.AddDays(-10), tamperAt: agora.AddHours(-2));

        var ciencia = await fixture.CreateDeviceAsync(tenantId, "NB-CIENCIA");
        await SetHealthAsync(ciencia.Id, agora.AddSeconds(-20)); // notice_acked_at null

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/devices/health-summary", ownerToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(5, root.GetProperty("active_devices").GetInt32());
        Assert.Equal(1, root.GetProperty("offline").GetInt32());
        Assert.Equal(1, root.GetProperty("offline_severe").GetInt32()); // 3 h > 30 min
        Assert.Equal(1, root.GetProperty("clock_skewed").GetInt32());
        Assert.Equal(1, root.GetProperty("tampered").GetInt32());
        Assert.Equal(1, root.GetProperty("notice_pending").GetInt32());
        Assert.Equal(4, root.GetProperty("with_alert").GetInt32()); // todos menos o saudável
    }

    [Fact]
    public async Task ListaComHealthAlert_FiltraFrotaInteira_NaoSoAPagina()
    {
        var (client, tenantId, ownerToken) = await SetupAsync("Org Filtro Saúde");
        var agora = DateTimeOffset.UtcNow;

        // 3 saudáveis e 2 com alerta
        for (var i = 0; i < 3; i++)
        {
            var ok = await fixture.CreateDeviceAsync(tenantId, $"NB-OK-{i}");
            await SetHealthAsync(ok.Id, agora.AddSeconds(-20), noticeAcked: agora.AddDays(-5));
        }

        var problema1 = await fixture.CreateDeviceAsync(tenantId, "NB-PROBLEMA-1");
        await SetHealthAsync(problema1.Id, agora.AddHours(-5), noticeAcked: agora.AddDays(-5));
        var problema2 = await fixture.CreateDeviceAsync(tenantId, "NB-PROBLEMA-2");
        await SetHealthAsync(problema2.Id, agora.AddSeconds(-10)); // ciência pendente

        // page_size=1 prova que o total considera TODA a frota, não a página
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/devices?health=alert&page_size=1", ownerToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("total").GetInt32());
        Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Alertas_SoNoPlanoPro_ComCooldownDe24h()
    {
        var org = await fixture.CreateOrganizationAsync("Org Alertas Pro");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var agora = DateTimeOffset.UtcNow;

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-ALERTA");
        await SetHealthAsync(device.Id, agora.AddHours(-4), noticeAcked: agora.AddDays(-5));

        // business_hours nulo = sempre dentro (não suprime alerta); plano começa como trial
        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new FleetAlertService(
            dataSource, fixture.Emails, "http://localhost:5173", NullLogger<FleetAlertService>.Instance);

        // plano trial: NÃO recebe alertas de frota (exclusivo do Pro)
        await service.RunOnceAsync(agora);
        Assert.Null(fixture.Emails.LastFor(owner.Email));

        // promovido para Pro: alerta sai
        await TestDb.ExecuteAsync(Cs, "UPDATE organizations SET plan = 'pro' WHERE id = @id", ("id", org.Id));
        await service.RunOnceAsync(agora);

        var mail = fixture.Emails.LastFor(owner.Email);
        Assert.NotNull(mail);
        Assert.True(mail!.IsHtml);
        Assert.Contains("precisa", mail.Subject);
        Assert.Contains("NB-ALERTA", mail.Body);
        Assert.Contains("sem comunicação", mail.Body);

        // cooldown de 24 h: o MESMO device+tipo não gera segundo e-mail no ciclo seguinte
        var antes = fixture.Emails.Sent.Count(m => string.Equals(m.To, owner.Email, StringComparison.OrdinalIgnoreCase));
        await service.RunOnceAsync(agora.AddMinutes(15));
        var depois = fixture.Emails.Sent.Count(m => string.Equals(m.To, owner.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(antes, depois);

        // passadas 25 h, volta a alertar
        await service.RunOnceAsync(agora.AddHours(25));
        var final = fixture.Emails.Sent.Count(m => string.Equals(m.To, owner.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(depois + 1, final);
    }

    [Fact]
    public async Task Alertas_ForaDoHorarioDeTrabalho_NaoEnvia()
    {
        var org = await fixture.CreateOrganizationAsync("Org Quiet Hours");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var agora = DateTimeOffset.UtcNow;

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-QUIET");
        await SetHealthAsync(device.Id, agora.AddHours(-4), noticeAcked: agora.AddDays(-5));

        // janela de trabalho impossível de conter "agora": domingo 03:00 às 03:01
        await TestDb.ExecuteAsync(Cs,
            """
            UPDATE organizations
            SET plan = 'pro', business_hours = '{"days":[7],"start":"03:00","end":"03:01"}'::jsonb
            WHERE id = @id
            """, ("id", org.Id));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new FleetAlertService(
            dataSource, fixture.Emails, "http://localhost:5173", NullLogger<FleetAlertService>.Instance);

        // segunda-feira 12:00 UTC está FORA da janela declarada (domingo 03:00-03:01)
        var segundaMeioDia = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        await service.RunOnceAsync(segundaMeioDia);

        Assert.Null(fixture.Emails.LastFor(owner.Email));
    }

    [Fact]
    public async Task Alertas_OptOutDoUsuario_NaoRecebe()
    {
        var org = await fixture.CreateOrganizationAsync("Org Alertas OptOut");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var agora = DateTimeOffset.UtcNow;

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-OPTOUT");
        await SetHealthAsync(device.Id, agora.AddHours(-4), noticeAcked: agora.AddDays(-5));
        await TestDb.ExecuteAsync(Cs, "UPDATE organizations SET plan = 'pro' WHERE id = @id", ("id", org.Id));
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO user_email_prefs (user_id, tenant_id, weekly_digest, fleet_alerts, jornada_weekly, updated_at)
            VALUES (@u, @t, true, false, false, now())
            """, ("u", admin.Id), ("t", org.Id));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new FleetAlertService(
            dataSource, fixture.Emails, "http://localhost:5173", NullLogger<FleetAlertService>.Instance);
        await service.RunOnceAsync(agora);

        Assert.NotNull(fixture.Emails.LastFor(owner.Email));
        Assert.Null(fixture.Emails.LastFor(admin.Email));
    }
}
