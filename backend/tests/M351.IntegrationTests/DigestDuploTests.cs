using M351.Domain;
using M351.Infrastructure.Digest;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// DIGEST DUPLO (F6, diferencial 4 do estudo): o resumo AGREGADO do gestor e o resumo
/// PESSOAL do colaborador saindo do mesmo job horário.
///
/// O que estes testes travam:
///  - o e-mail do gestor traz índice, variação EM PONTOS, os quatro baldes e a cobertura;
///  - o e-mail do gestor NÃO é ranking: a palavra não aparece e NENHUM nome de pessoa
///    entra no corpo, quanto mais uma lista ordenada por métrica;
///  - o e-mail pessoal só existe com o opt-in da organização, traz os dados DAQUELA
///    pessoa, a frase pedagógica do ocioso e o link /t/{token} da transparência;
///  - pessoa sem e-mail conhecido NÃO recebe palpite: ninguém é notificado e o job segue;
///  - idempotência independente dos dois carimbos.
///
/// As asserções são POR DESTINATÁRIO: o banco da suíte é compartilhado e o serviço varre
/// todas as orgs, então contagem global não é estável entre testes.
/// </summary>
[Collection(ApiCollection.Name)]
public class DigestDuploTests(ApiTestFixture fixture)
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

    private static DateOnly DayInsideClosedWeek(DateTimeOffset nowUtc, int offset = -3)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        return DateOnly.FromDateTime(local.Date.AddDays(offset));
    }

    private async Task EnablePersonalDigestAsync(Guid tenantId) =>
        await TestDb.ExecuteAsync(Cs,
            "UPDATE organizations SET person_alerts_enabled = true WHERE id = @t", ("t", tenantId));

    /// <summary>
    /// Lane de pessoa: device_users com windows_username no formato DOMÍNIO\usuário, onde
    /// "usuário" é a parte local do e-mail do usuário do portal — é exatamente o casamento
    /// que o digest pessoal exige para saber para quem mandar.
    /// </summary>
    private async Task<Guid> SeedPersonAsync(
        Guid tenantId, Guid deviceId, string displayName, string? emailToMatch)
    {
        var id = Uuid7.NewUuid7();
        var username = emailToMatch is null
            ? $"acme\\sem-usuario-{Guid.NewGuid():N}"[..30]
            : $"acme\\{emailToMatch.Split('@')[0]}";

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, @dn, now(), now())
            """,
            ("id", id), ("t", tenantId), ("d", deviceId),
            ("sid", $"S-1-5-21-DIG-{Guid.NewGuid():N}"[..40]), ("wu", username), ("dn", displayName));
        return id;
    }

    private async Task SeedSummaryAsync(
        Guid tenantId, Guid deviceId, Guid laneId, DateOnly date,
        int active, int idle, int work, int neutral = 0, int notWork = 0, int unclassified = 0)
    {
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_on,
                seconds_work_related, seconds_neutral, seconds_not_work_related,
                seconds_unclassified, computed_at)
            VALUES (@t, @day, @d, @u, @a, @i, @on, @w, @n, @nw, @unc, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", laneId),
            ("a", active), ("i", idle), ("on", active + idle),
            ("w", work), ("n", neutral), ("nw", notWork), ("unc", unclassified));
    }

    // ------------------------------------------------------------------ gestor

    [Fact]
    public async Task DigestGestor_TrazIndiceCoberturaBaldesEVariacaoEmPontos_SemRanking()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest Duplo A");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-DUPLO-A");

        var nowUtc = NextMondayEightLocalUtc();
        var thisWeek = DayInsideClosedWeek(nowUtc);
        var lastWeek = thisWeek.AddDays(-7);

        // três pessoas com desempenhos BEM diferentes: se algum dia o e-mail virar lista
        // ordenada por métrica, os nomes aparecem e o teste cai
        var ana = await SeedPersonAsync(org.Id, device.Id, "Ana Ranquiada", null);
        var bruno = await SeedPersonAsync(org.Id, device.Id, "Bruno Comparado", null);
        var carla = await SeedPersonAsync(org.Id, device.Id, "Carla Medida", null);

        // semana fechada: 6h ativas classificadas em 3h produtivo + 1h neutro + 1h improdutivo
        // + 1h sem classificação → índice 3/5 = 60 %, cobertura (6-1)/6 = 83 %
        await SeedSummaryAsync(org.Id, device.Id, ana, thisWeek,
            active: 3600 * 6, idle: 3600 * 2, work: 3600 * 3, neutral: 3600, notWork: 3600, unclassified: 3600);
        await SeedSummaryAsync(org.Id, device.Id, bruno, thisWeek, active: 0, idle: 0, work: 0);
        await SeedSummaryAsync(org.Id, device.Id, carla, thisWeek, active: 0, idle: 0, work: 0);

        // semana ANTERIOR: 2h produtivo em 4h classificadas → índice 50 %; delta = 10 pontos
        await SeedSummaryAsync(org.Id, device.Id, ana, lastWeek,
            active: 3600 * 4, idle: 3600, work: 3600 * 2, neutral: 3600, notWork: 3600);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        await NewService(dataSource).RunOnceAsync(nowUtc);

        var mail = fixture.Emails.LastFor(owner.Email);
        Assert.NotNull(mail);
        var body = mail!.Body;

        Assert.Contains("Índice de produtividade: 60%", body);
        Assert.Contains("10 ponto(s) acima da semana anterior", body);   // pontos, não %
        Assert.Contains("Cobertura da classificação: <strong>83%</strong>", body);

        // os QUATRO baldes, com o vocabulário padrão da organização
        Assert.Contains("Composição do tempo ativo", body);
        Assert.Contains("Produtivo", body);
        Assert.Contains("Neutro", body);
        Assert.Contains("Improdutivo", body);
        Assert.Contains("Sem classificação", body);

        // ocioso jamais rotulado de improdutivo
        Assert.Contains("Tempo ocioso não é tempo improdutivo", body);

        // ---- o gate anti-ranking ----
        Assert.DoesNotContain("ranking", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ana Ranquiada", body);
        Assert.DoesNotContain("Bruno Comparado", body);
        Assert.DoesNotContain("Carla Medida", body);
        Assert.Contains("não ordena pessoas por desempenho", body);
    }

    // ------------------------------------------------------------------ colaborador

    [Fact]
    public async Task DigestPessoal_ComOptInDaOrg_VaiParaOTitularComOsDadosDeleELinkDeTransparencia()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest Duplo B");
        await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var colaborador = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-DUPLO-B");

        var token = Guid.NewGuid();
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET transparency_token = @tok WHERE id = @d", ("tok", token), ("d", device.Id));
        await EnablePersonalDigestAsync(org.Id);

        var nowUtc = NextMondayEightLocalUtc();
        var day = DayInsideClosedWeek(nowUtc);
        var lane = await SeedPersonAsync(org.Id, device.Id, "Titular Duplo", colaborador.Email);

        // 5h ativas: 2h produtivo, 1h neutro, 1h improdutivo, 1h sem classificação; 3h ocioso
        // índice = 2/4 = 50 %; cobertura = (5-1)/5 = 80 %; ociosidade = 3/8 = 38 %
        await SeedSummaryAsync(org.Id, device.Id, lane, day,
            active: 3600 * 5, idle: 3600 * 3, work: 3600 * 2,
            neutral: 3600, notWork: 3600, unclassified: 3600);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        await NewService(dataSource).RunOnceAsync(nowUtc);

        var mail = fixture.Emails.LastFor(colaborador.Email);
        Assert.NotNull(mail);
        Assert.StartsWith("Seu resumo da semana", mail!.Subject);
        var body = mail.Body;

        Assert.Contains("Titular Duplo", body);
        Assert.Contains("8h00", body);                                   // horas ligadas
        Assert.Contains("5h00", body);                                   // horas ativas
        Assert.Contains("3h00", body);                                   // horas ociosas
        Assert.Contains("38% do tempo ligado", body);                    // ociosidade
        Assert.Contains("Seu índice de produtividade: 50%", body);
        Assert.Contains("Cobertura da classificação: <strong>80%</strong>", body);
        Assert.Contains("Tempo ocioso não é tempo improdutivo", body);   // frase pedagógica
        Assert.Contains($"/t/{token}", body);                            // transparência do dispositivo
        Assert.Contains("Portaria 671", body);
        Assert.DoesNotContain("ranking", body, StringComparison.OrdinalIgnoreCase);

        // carimbo próprio, separado do digest do gestor
        Assert.NotNull(await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT last_personal_digest_at FROM organizations WHERE id = @id", ("id", org.Id)));

        // idempotência: a mesma janela não reenvia
        var before = fixture.Emails.Sent.Count(m =>
            string.Equals(m.To, colaborador.Email, StringComparison.OrdinalIgnoreCase));
        await NewService(dataSource).RunOnceAsync(nowUtc.AddMinutes(30));
        Assert.Equal(before, fixture.Emails.Sent.Count(m =>
            string.Equals(m.To, colaborador.Email, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DigestPessoal_SemOptInDaOrg_NaoEnvia()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest Duplo C");
        await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var colaborador = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-DUPLO-C");

        var nowUtc = NextMondayEightLocalUtc();
        var lane = await SeedPersonAsync(org.Id, device.Id, "Titular Sem OptIn", colaborador.Email);
        await SeedSummaryAsync(org.Id, device.Id, lane, DayInsideClosedWeek(nowUtc),
            active: 3600 * 4, idle: 3600, work: 3600 * 3);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        await NewService(dataSource).RunOnceAsync(nowUtc);

        // padrão da organização é equipe: sem o opt-in, o colaborador não recebe nada
        Assert.Null(fixture.Emails.LastFor(colaborador.Email));
        Assert.Null(await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT last_personal_digest_at FROM organizations WHERE id = @id", ("id", org.Id)));
    }

    [Fact]
    public async Task DigestPessoal_SemEmailConhecido_NaoInventaDestinatario()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest Duplo D");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-DUPLO-D");
        await EnablePersonalDigestAsync(org.Id);

        var nowUtc = NextMondayEightLocalUtc();
        // windows_username que não casa com e-mail de NENHUM usuário do portal
        var lane = await SeedPersonAsync(org.Id, device.Id, "Titular Anônimo", emailToMatch: null);
        await SeedSummaryAsync(org.Id, device.Id, lane, DayInsideClosedWeek(nowUtc),
            active: 3600 * 4, idle: 3600, work: 3600 * 3);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        await NewService(dataSource).RunOnceAsync(nowUtc);

        // o Owner recebe o digest DELE (agregado) e nada mais: o resumo pessoal do titular
        // desconhecido não é despejado no gestor nem chutado para outro endereço
        var ownerMail = fixture.Emails.LastFor(owner.Email);
        Assert.NotNull(ownerMail);
        Assert.StartsWith("Resumo da semana", ownerMail!.Subject);
        Assert.DoesNotContain("Titular Anônimo", ownerMail.Body);
        Assert.DoesNotContain(fixture.Emails.Sent,
            m => m.Subject.StartsWith("Seu resumo da semana", StringComparison.Ordinal)
                 && string.Equals(m.To, owner.Email, StringComparison.OrdinalIgnoreCase));

        // o job seguiu em frente e carimbou: a próxima segunda tenta de novo, não trava aqui
        Assert.NotNull(await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT last_personal_digest_at FROM organizations WHERE id = @id", ("id", org.Id)));
    }

    [Fact]
    public async Task DigestPessoal_ColaboradorDesligouAPreferencia_NaoRecebe()
    {
        var org = await fixture.CreateOrganizationAsync("Org Digest Duplo E");
        await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var colaborador = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-DUPLO-E");
        await EnablePersonalDigestAsync(org.Id);

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO user_email_prefs (user_id, tenant_id, weekly_digest, fleet_alerts, jornada_weekly, updated_at)
            VALUES (@u, @t, false, true, false, now())
            """, ("u", colaborador.Id), ("t", org.Id));

        var nowUtc = NextMondayEightLocalUtc();
        var lane = await SeedPersonAsync(org.Id, device.Id, "Titular OptOut", colaborador.Email);
        await SeedSummaryAsync(org.Id, device.Id, lane, DayInsideClosedWeek(nowUtc),
            active: 3600 * 4, idle: 3600, work: 3600 * 3);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        await NewService(dataSource).RunOnceAsync(nowUtc);

        Assert.Null(fixture.Emails.LastFor(colaborador.Email));
    }
}
