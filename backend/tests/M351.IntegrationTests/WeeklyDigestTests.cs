using M351.Domain;
using M351.Infrastructure.Digest;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// Digest semanal (F5): envio na segunda 08h do FUSO DA ORG para Owner/Admin ativos,
/// idempotência por last_weekly_digest_at, opt-out por user_email_prefs e conteúdo com
/// vocabulário neutro. As asserções são POR DESTINATÁRIO (o banco da suíte é compartilhado
/// e o serviço varre todas as orgs — contagens globais não são estáveis entre testes).
/// </summary>
[Collection(ApiCollection.Name)]
public class WeeklyDigestTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private WeeklyDigestService NewService(NpgsqlDataSource dataSource) => new(
        dataSource, fixture.Emails, "http://localhost:5173", NullLogger<WeeklyDigestService>.Instance);

    /// <summary>Segunda-feira 08h em America/Sao_Paulo (UTC-3) = 11:00 UTC.</summary>
    private static DateTimeOffset NextMondayEightLocalUtc()
    {
        var today = DateTime.UtcNow.Date;
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var monday = today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);
        return new DateTimeOffset(monday.AddHours(11), TimeSpan.Zero);
    }

    private int MailCountFor(string email) =>
        fixture.Emails.Sent.Count(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));

    private async Task SeedSummaryAsync(Guid tenantId, Guid deviceId, DateOnly date, int active, int idle, int work)
    {
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_work_related, seconds_on, computed_at)
            VALUES (@t, @day, @d, @u, @a, @i, @w, @a, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", Uuid7.NewUuid7()),
            ("a", active), ("i", idle), ("w", work));
    }

    [Fact]
    public async Task Digest_SegundaOitoLocal_EnviaParaOwnerEAdmin_EIdempotente()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer); // viewer NÃO recebe
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-DIGEST");

        var nowUtc = NextMondayEightLocalUtc();
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        var weekDay = DateOnly.FromDateTime(local.Date.AddDays(-3)); // dentro da semana fechada
        await SeedSummaryAsync(org.Id, device.Id, weekDay, active: 3600 * 6, idle: 3600, work: 3600 * 4);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);

        await service.RunOnceAsync(nowUtc);

        var ownerMail = fixture.Emails.LastFor(owner.Email);
        Assert.NotNull(ownerMail);
        Assert.True(ownerMail!.IsHtml);
        Assert.Contains("Resumo da semana", ownerMail.Subject);
        Assert.Contains("6h00", ownerMail.Body);                  // horas ativas da semana
        Assert.Contains("Portaria 671", ownerMail.Body);          // disclaimer fixo
        Assert.Contains("/visao-geral", ownerMail.Body);          // deep-link
        Assert.NotNull(fixture.Emails.LastFor(admin.Email));
        Assert.Null(fixture.Emails.LastFor(viewer.Email));

        var stamped = await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT last_weekly_digest_at FROM organizations WHERE id = @id", ("id", org.Id));
        Assert.NotNull(stamped);

        // mesma janela: NÃO reenvia (idempotência por last_weekly_digest_at)
        var before = MailCountFor(owner.Email);
        await service.RunOnceAsync(nowUtc.AddMinutes(30));
        Assert.Equal(before, MailCountFor(owner.Email));
    }

    [Fact]
    public async Task Digest_ForaDaJanela_NaoEnvia()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest Fora");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);

        // terça 11:00 UTC (08h local, mas não é segunda) e segunda 12h local: nada sai
        await service.RunOnceAsync(NextMondayEightLocalUtc().AddDays(1));
        await service.RunOnceAsync(NextMondayEightLocalUtc().AddHours(4));

        Assert.Equal(0, MailCountFor(owner.Email));
    }

    [Fact]
    public async Task Digest_OptOut_RespeitaPreferenciaDoUsuario()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest OptOut");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO user_email_prefs (user_id, tenant_id, weekly_digest, fleet_alerts, jornada_weekly, updated_at)
            VALUES (@u, @t, false, true, false, now())
            """, ("u", admin.Id), ("t", org.Id));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);

        await service.RunOnceAsync(NextMondayEightLocalUtc());

        Assert.NotNull(fixture.Emails.LastFor(owner.Email));
        Assert.Null(fixture.Emails.LastFor(admin.Email));
    }
}
