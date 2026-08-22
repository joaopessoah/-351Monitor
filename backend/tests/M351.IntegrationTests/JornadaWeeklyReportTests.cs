using System.Globalization;
using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Exports;
using M351.Infrastructure.Reports;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// Relatório de jornada semanal por e-mail (F5): toda segunda 07h NO FUSO DA ORG o worker
/// enfileira o export jornada_csv da semana fechada no pipeline assíncrono que já existe e, num
/// ciclo seguinte, manda o LINK do download autenticado.
///
/// Os testes travam o que a verificação adversarial levantou e o que não pode regredir:
///  - NUNCA anexo: o corpo leva link do portal, e o disclaimer da Portaria 671 vai VERBATIM;
///  - requested_by = o próprio assinante (a coluna é NOT NULL e a trilha export_csv precisa
///    responder "quem gerou" com o mesmo rigor do POST /exports feito na tela);
///  - GATE DE PLANO: relatório agendado é exclusivo do Pro, no job e no PATCH da preferência;
///  - estado vazio (org sem dispositivo) ainda recebe e-mail, dizendo que não houve atividade;
///  - export que falhou não vira e-mail com link quebrado.
///
/// As asserções são POR TENANT/DESTINATÁRIO: o banco da suíte é compartilhado e o serviço varre
/// todas as orgs, então contagens globais não seriam estáveis entre testes.
/// </summary>
[Collection(ApiCollection.Name)]
public class JornadaWeeklyReportTests(ApiTestFixture fixture)
{
    /// <summary>Texto VERBATIM do DoD 11.3, literal no teste de propósito (não referencia a constante do produto).</summary>
    private const string Disclaimer =
        "Relatório gerencial de uso da estação de trabalho. Não constitui registro eletrônico de "
        + "ponto (Portaria 671/MTE) e não substitui o controle de jornada do art. 74 da CLT.";

    private string Cs => fixture.Database.ConnectionString;

    private JornadaWeeklyReportService NewService(NpgsqlDataSource dataSource) => new(
        dataSource, fixture.Emails, "http://localhost:5173",
        NullLogger<JornadaWeeklyReportService>.Instance);

    /// <summary>Segunda-feira 07h em America/Sao_Paulo (UTC-3) = 10:00 UTC.</summary>
    private static DateTimeOffset NextMondaySevenLocalUtc()
    {
        var today = DateTime.UtcNow.Date;
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var monday = today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);
        return new DateTimeOffset(monday.AddHours(10), TimeSpan.Zero);
    }

    /// <summary>Org no plano Pro (o gate das features pagas é a flag de plano do tenant).</summary>
    private async Task PromoverParaProAsync(Guid tenantId) =>
        await TestDb.ExecuteAsync(Cs,
            "UPDATE organizations SET plan = 'pro' WHERE id = @id", ("id", tenantId));

    private async Task AssinarAsync(Guid tenantId, Guid userId) =>
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO user_email_prefs (user_id, tenant_id, weekly_digest, fleet_alerts, jornada_weekly, updated_at)
            VALUES (@u, @t, true, true, true, now())
            """, ("u", userId), ("t", tenantId));

    private async Task<int> ExportJobsDoTenantAsync(Guid tenantId) =>
        await TestDb.ScalarAsync<int>(Cs,
            "SELECT count(*)::int FROM export_jobs WHERE tenant_id = @t AND kind = 'jornada_csv'",
            ("t", tenantId));

    private int MailCountFor(string email) =>
        fixture.Emails.Sent.Count(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Drena a fila de exports até esvaziar (o ExportJob do worker faz o mesmo por ciclo): a fila
    /// é GLOBAL no banco compartilhado da suíte e o claim pega o job mais antigo, então sobras de
    /// outros testes não podem desviar as asserções.
    /// </summary>
    private async Task DrenarExportsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var worker = new ExportService(dataSource, fixture.ExportsDirectory);
        while (await worker.RunOnceAsync() > 0) { }
    }

    private async Task SeedSummaryAsync(Guid tenantId, Guid deviceId, DateOnly date)
    {
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_idle, seconds_locked, seconds_on,
                first_event_at, last_event_at, data_incomplete, computed_at)
            VALUES (@t, @day, @d, @u, 21600, 3600, 1800, 27000, now(), now(), false, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", Uuid7.NewUuid7()));
    }

    [Fact]
    public async Task Jornada_SegundaSeteLocalNoPro_EnfileiraExportDoAssinante_EEntregaOLink()
    {
        var org = await fixture.CreateOrganizationAsync("Org Jornada Semanal");
        await PromoverParaProAsync(org.Id);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AssinarAsync(org.Id, owner.Id);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-JORNADA-SEMANAL");

        var nowUtc = NextMondaySevenLocalUtc();
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        var weekStart = DateOnly.FromDateTime(local.Date.AddDays(-7));
        var weekEnd = DateOnly.FromDateTime(local.Date.AddDays(-1));
        await SeedSummaryAsync(org.Id, device.Id, weekStart.AddDays(2));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);

        // 1ª passada: só ENFILEIRA (o arquivo ainda não existe, então nada de e-mail)
        await service.RunOnceAsync(nowUtc);
        Assert.Equal(0, MailCountFor(owner.Email));

        var job = await TestDb.RowAsync(Cs, """
            SELECT id, status, requested_by, params::text AS params_json
            FROM export_jobs WHERE tenant_id = @t AND kind = 'jornada_csv'
            """, ("t", org.Id));
        Assert.NotNull(job);
        Assert.Equal("queued", job!["status"]);
        // requested_by é NOT NULL e aponta para o ASSINANTE: a trilha de auditoria continua
        // respondendo quem gerou o arquivo, mesmo com o job nascendo do agendamento
        Assert.Equal(owner.Id, job["requested_by"]);
        using (var doc = JsonDocument.Parse((string)job["params_json"]!))
        {
            Assert.Equal(weekStart.ToString("yyyy-MM-dd"), doc.RootElement.GetProperty("from").GetString());
            Assert.Equal(weekEnd.ToString("yyyy-MM-dd"), doc.RootElement.GetProperty("to").GetString());
            Assert.False(doc.RootElement.TryGetProperty("group_by", out _)); // não se aplica a jornada_csv
        }

        // trilha export_csv gravada na MESMA transação do INSERT, com o assinante como ator
        var trilha = await TestDb.ScalarAsync<int>(Cs, """
            SELECT count(*)::int FROM audit_log
            WHERE tenant_id = @t AND action = 'export_csv' AND actor_user_id = @u
              AND target_id = @job AND detail->>'source' = 'assinatura_semanal'
            """, ("t", org.Id), ("u", owner.Id), ("job", (Guid)job["id"]!));
        Assert.Equal(1, trilha);

        // o worker de exports gera o arquivo; a passada seguinte entrega o link
        await DrenarExportsAsync();
        await service.RunOnceAsync(nowUtc.AddMinutes(5));
        Assert.Equal(1, MailCountFor(owner.Email));

        var mail = fixture.Emails.LastFor(owner.Email);
        Assert.NotNull(mail);
        Assert.True(mail!.IsHtml);
        Assert.Contains("Relatório de jornada da semana", mail.Subject);
        Assert.Contains(weekStart.ToString("dd/MM", CultureInfo.InvariantCulture), mail.Subject);
        // LINK para o download autenticado, JAMAIS anexo com dado pessoal
        Assert.Contains("/relatorios/exportacoes", mail.Body);
        Assert.Contains("não vai anexado", mail.Body);
        Assert.Contains(Disclaimer, mail.Body);
        // vocabulário: nada de entrada/saída nem controle de ponto
        Assert.DoesNotContain("hora extra", mail.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Entrada", mail.Body, StringComparison.Ordinal);

        var emailedAt = await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT emailed_at FROM jornada_report_deliveries WHERE user_id = @u", ("u", owner.Id));
        Assert.NotNull(emailedAt);

        // ciclo seguinte não reenvia nem reenfileira (idempotência de ponta a ponta)
        var antes = MailCountFor(owner.Email);
        await service.RunOnceAsync(nowUtc.AddMinutes(10));
        Assert.Equal(antes, MailCountFor(owner.Email));
        Assert.Equal(1, await ExportJobsDoTenantAsync(org.Id));
    }

    [Fact]
    public async Task Jornada_ForaDoPlanoPro_NaoEnfileiraNemEnvia()
    {
        // org fica no plano trial da criação: relatório agendado é exclusivo do Pro
        var org = await fixture.CreateOrganizationAsync("Org Jornada Trial");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AssinarAsync(org.Id, owner.Id);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        await NewService(dataSource).RunOnceAsync(NextMondaySevenLocalUtc());

        Assert.Equal(0, await ExportJobsDoTenantAsync(org.Id));
        Assert.Equal(0, MailCountFor(owner.Email));
    }

    [Fact]
    public async Task Jornada_SemAssinatura_NaoEnfileira()
    {
        // sem linha em user_email_prefs a assinatura está DESLIGADA (default do campo, ao
        // contrário do digest semanal, que é opt-out)
        var org = await fixture.CreateOrganizationAsync("Org Jornada Sem Assin");
        await PromoverParaProAsync(org.Id);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        await NewService(dataSource).RunOnceAsync(NextMondaySevenLocalUtc());

        Assert.Equal(0, await ExportJobsDoTenantAsync(org.Id));
        Assert.Equal(0, MailCountFor(owner.Email));
    }

    [Fact]
    public async Task Jornada_ForaDaJanelaDeSegundaSete_NaoEnfileira()
    {
        var org = await fixture.CreateOrganizationAsync("Org Jornada Fora");
        await PromoverParaProAsync(org.Id);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AssinarAsync(org.Id, owner.Id);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);

        // terça 07h local e segunda 11h local: nada é enfileirado
        await service.RunOnceAsync(NextMondaySevenLocalUtc().AddDays(1));
        await service.RunOnceAsync(NextMondaySevenLocalUtc().AddHours(4));

        Assert.Equal(0, await ExportJobsDoTenantAsync(org.Id));
    }

    [Fact]
    public async Task Jornada_MesmaJanela_EnfileiraUmaVezSo()
    {
        var org = await fixture.CreateOrganizationAsync("Org Jornada Idemp");
        await PromoverParaProAsync(org.Id);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AssinarAsync(org.Id, owner.Id);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);

        // o job roda de 5 em 5 min: a janela das 07h é visitada 12 vezes, e o UNIQUE
        // (user_id, week_start) precisa segurar todas as repetições
        var nowUtc = NextMondaySevenLocalUtc();
        await service.RunOnceAsync(nowUtc);
        await service.RunOnceAsync(nowUtc.AddMinutes(5));
        await service.RunOnceAsync(nowUtc.AddMinutes(50));

        Assert.Equal(1, await ExportJobsDoTenantAsync(org.Id));
    }

    [Fact]
    public async Task Jornada_SemanaSemNenhumDispositivo_EnviaEmailComEstadoVazio()
    {
        // org sem device: o CSV sai só com cabeçalho (row_count = 0). O e-mail SAI mesmo assim,
        // dizendo o que houve, porque silêncio na segunda seria lido como produto quebrado.
        var org = await fixture.CreateOrganizationAsync("Org Jornada Vazia");
        await PromoverParaProAsync(org.Id);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AssinarAsync(org.Id, owner.Id);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);

        await service.RunOnceAsync(NextMondaySevenLocalUtc());
        await DrenarExportsAsync();
        await service.RunOnceAsync(NextMondaySevenLocalUtc().AddMinutes(5));

        var job = await TestDb.RowAsync(Cs,
            "SELECT status, row_count FROM export_jobs WHERE tenant_id = @t AND kind = 'jornada_csv'",
            ("t", org.Id));
        Assert.NotNull(job);
        Assert.Equal("done", job!["status"]);
        Assert.Equal(0, (int)job["row_count"]!);

        var mail = fixture.Emails.LastFor(owner.Email);
        Assert.NotNull(mail);
        Assert.Contains("Nenhum dispositivo registrou atividade", mail!.Body);
        Assert.Contains(Disclaimer, mail.Body);
    }

    [Fact]
    public async Task Jornada_ExportQueFalhou_NaoViraEmailComLinkQuebrado()
    {
        var org = await fixture.CreateOrganizationAsync("Org Jornada Falha");
        await PromoverParaProAsync(org.Id);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AssinarAsync(org.Id, owner.Id);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = NewService(dataSource);
        await service.RunOnceAsync(NextMondaySevenLocalUtc());

        // simula o que o ExportService faz quando a geração estoura (sem coluna de erro no
        // schema: o status é o sinal, e o Serilog é a fonte do motivo)
        await TestDb.ExecuteAsync(Cs,
            "UPDATE export_jobs SET status = 'failed' WHERE tenant_id = @t AND kind = 'jornada_csv'",
            ("t", org.Id));

        await service.RunOnceAsync(NextMondaySevenLocalUtc().AddMinutes(5));
        Assert.Equal(0, MailCountFor(owner.Email));

        var gaveUp = await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT gave_up_at FROM jornada_report_deliveries WHERE user_id = @u", ("u", owner.Id));
        Assert.NotNull(gaveUp);
    }

    [Fact]
    public async Task EmailPrefs_LigarJornadaSemanalForaDoPro_Retorna403_EDesligarSemprePode()
    {
        var org = await fixture.CreateOrganizationAsync("Org Jornada Gate");
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        // plano trial: LIGAR é 403 (o portal já desabilita o toggle, o backend não confia nisso)
        using (var ligar = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, "/api/v1/me/email-prefs", token, new { jornada_weekly = true }))
        {
            var negado = await client.SendAsync(ligar);
            Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
        }

        Assert.Equal(0, await TestDb.ScalarAsync<int>(Cs,
            "SELECT count(*)::int FROM user_email_prefs WHERE user_id = @u AND jornada_weekly",
            ("u", viewer.Id)));

        // no Pro, liga normalmente
        await PromoverParaProAsync(org.Id);
        using (var ligar = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, "/api/v1/me/email-prefs", token, new { jornada_weekly = true }))
        {
            var aceito = await client.SendAsync(ligar);
            Assert.Equal(HttpStatusCode.OK, aceito.StatusCode);
            using var body = JsonDocument.Parse(await aceito.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("jornada_weekly").GetBoolean());
        }

        // downgrade não pode prender ninguém numa assinatura que não consegue cancelar
        await TestDb.ExecuteAsync(Cs,
            "UPDATE organizations SET plan = 'essencial' WHERE id = @id", ("id", org.Id));
        using (var desligar = AuthClient.AuthorizedRequest(
            HttpMethod.Patch, "/api/v1/me/email-prefs", token, new { jornada_weekly = false }))
        {
            var aceito = await client.SendAsync(desligar);
            Assert.Equal(HttpStatusCode.OK, aceito.StatusCode);
            using var body = JsonDocument.Parse(await aceito.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("jornada_weekly").GetBoolean());
        }
    }
}
