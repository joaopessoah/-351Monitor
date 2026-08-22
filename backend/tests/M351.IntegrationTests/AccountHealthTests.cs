using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.AccountHealth;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace M351.IntegrationTests;

/// <summary>
/// Score de saúde de conta (F5, telemetria INTERNA de CS): sinais de risco por organização,
/// e-mail interno com CSV no formato do importador do CRM e carência por idade da conta.
///
/// O serviço varre TODAS as orgs ativas e o banco da suíte é compartilhado, então as asserções
/// são SEMPRE pela org do próprio teste (presença ou ausência do id na lista) e por um endereço
/// de CS exclusivo por teste. Contagens globais não seriam estáveis.
/// </summary>
[Collection(ApiCollection.Name)]
public class AccountHealthTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private AccountHealthService NewService(NpgsqlDataSource dataSource, string alertEmail, string? excludedSlug = null) =>
        new(dataSource, fixture.Emails, alertEmail, excludedSlug, NullLogger<AccountHealthService>.Instance);

    private static string NewCsEmail() => $"cs-{Guid.NewGuid():N}@mais351monitor.com.br";

    /// <summary>Envelhece a org: a carência por idade silencia toda regra numa conta recém-criada.</summary>
    private Task AgeOrganizationAsync(Guid tenantId, int days) => TestDb.ExecuteAsync(Cs,
        "UPDATE organizations SET created_at = now() - make_interval(days => @d) WHERE id = @id",
        ("d", days), ("id", tenantId));

    private Task TouchLoginAsync(Guid userId, DateTimeOffset at) => TestDb.ExecuteAsync(Cs,
        "UPDATE users SET last_login_at = @at WHERE id = @id", ("at", at), ("id", userId));

    private Task TouchDeviceAsync(Guid deviceId, DateTimeOffset at) => TestDb.ExecuteAsync(Cs,
        "UPDATE devices SET last_seen_at = @at WHERE id = @id", ("at", at), ("id", deviceId));

    /// <summary>
    /// Uma leitura de relatório na trilha. Gravada com o instante ATUAL de propósito: audit_log
    /// é particionada por mês e o banco da suíte só tem as partições do mês corrente e do
    /// próximo, então uma data retroativa quebraria o teste na virada de mês.
    /// </summary>
    private Task SeedReadAuditAsync(Guid tenantId, Guid actorUserId) =>
        TestDb.ExecuteAsync(Cs, """
            INSERT INTO audit_log (id, tenant_id, actor_user_id, action, target_type, occurred_at)
            VALUES (@id, @t, @a, @act, 'team', now())
            """,
            ("id", Uuid7.NewUuid7()), ("t", tenantId), ("a", actorUserId),
            ("act", AuditActions.ViewReport));

    /// <summary>Uma linha de agregado diário: é o que conta como "dispositivo com dados no dia".</summary>
    private Task SeedSummaryAsync(Guid tenantId, Guid deviceId, DateOnly date) => TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_on, computed_at)
            VALUES (@t, @day, @d, @u, 3600, 3600, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", Uuid7.NewUuid7()));

    /// <summary>Hoje no fuso do tenant: as janelas do serviço são em datas LOCAIS.</summary>
    private static DateOnly LocalToday() => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).Date);

    [Fact]
    public async Task SaudeConta_ContaAbandonada_EntraNaListaComSinaisEEnviaCsvAoCs()
    {
        var alertEmail = NewCsEmail();
        var org = await fixture.CreateOrganizationAsync($"Conta Abandonada {Guid.NewGuid():N}"[..24]);
        await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AgeOrganizationAsync(org.Id, 60);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var report = await NewService(dataSource, alertEmail).RunOnceAsync(DateTimeOffset.UtcNow);

        var row = Assert.Single(report.AtRisk, r => r.TenantId == org.Id);
        Assert.Contains(row.Signals, s => s.Code == AccountHealthService.SignalNoLogin);
        Assert.Contains(row.Signals, s => s.Code == AccountHealthService.SignalNoEvents);
        Assert.Contains(row.Signals, s => s.Code == AccountHealthService.SignalNoReads);
        Assert.Equal(30, row.HealthScore);   // 100 menos 30 + 30 + 10 de risco
        Assert.Equal("crítico", row.Faixa);

        var mail = fixture.Emails.LastFor(alertEmail);
        Assert.NotNull(mail);
        Assert.True(mail!.IsHtml);
        Assert.Contains("Saúde das contas", mail.Subject);
        Assert.Contains(org.Name, mail.Body);
        Assert.Contains("Não encaminhar para o cliente", mail.Body);

        var anexo = Assert.Single(mail.Attachments!);
        Assert.EndsWith(".csv", anexo.FileName);
        Assert.Equal("text/csv", anexo.ContentType);
        var csv = System.Text.Encoding.UTF8.GetString(anexo.Content);
        Assert.Contains(AccountHealthService.CsvHeader, csv);
        Assert.Contains(org.Name, csv);

        // a execução fica registrada como os demais jobs do worker
        var run = await TestDb.RowAsync(Cs, """
            SELECT status, detail->>'email_enviado' AS enviado FROM maintenance_runs
            WHERE job_name = 'AccountHealth' ORDER BY started_at DESC LIMIT 1
            """);
        Assert.NotNull(run);
        Assert.Equal("ok", (string)run!["status"]!);
        Assert.Equal("true", (string)run["enviado"]!);
    }

    [Fact]
    public async Task SaudeConta_ContaRecemCriada_NaoEntraNaLista()
    {
        // carência por idade: conta de ontem não tem como ter login de 14 dias nem histórico
        var alertEmail = NewCsEmail();
        var org = await fixture.CreateOrganizationAsync($"Conta Nova {Guid.NewGuid():N}"[..24]);
        await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AgeOrganizationAsync(org.Id, 1);

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var report = await NewService(dataSource, alertEmail).EvaluateAsync(DateTimeOffset.UtcNow);

        Assert.DoesNotContain(report.AtRisk, r => r.TenantId == org.Id);
    }

    [Fact]
    public async Task SaudeConta_ContaEngajada_NaoEntraNaLista()
    {
        var alertEmail = NewCsEmail();
        var org = await fixture.CreateOrganizationAsync($"Conta Engajada {Guid.NewGuid():N}"[..24]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-ENGAJADA");
        await AgeOrganizationAsync(org.Id, 90);
        await TouchLoginAsync(owner.Id, DateTimeOffset.UtcNow.AddDays(-1));
        await TouchDeviceAsync(device.Id, DateTimeOffset.UtcNow.AddHours(-1));
        await SeedReadAuditAsync(org.Id, owner.Id);

        var today = LocalToday();
        await SeedSummaryAsync(org.Id, device.Id, today.AddDays(-2));
        await SeedSummaryAsync(org.Id, device.Id, today.AddDays(-9));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var report = await NewService(dataSource, alertEmail).EvaluateAsync(DateTimeOffset.UtcNow);

        Assert.DoesNotContain(report.AtRisk, r => r.TenantId == org.Id);
    }

    [Fact]
    public async Task SaudeConta_QuedaDeDispositivos_GeraSinalDeQueda()
    {
        var alertEmail = NewCsEmail();
        var org = await fixture.CreateOrganizationAsync($"Conta Queda {Guid.NewGuid():N}"[..24]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AgeOrganizationAsync(org.Id, 90);
        await TouchLoginAsync(owner.Id, DateTimeOffset.UtcNow.AddDays(-1));
        await SeedReadAuditAsync(org.Id, owner.Id);

        var today = LocalToday();
        var devices = new List<Device>();
        for (var i = 0; i < 5; i++)
        {
            var device = await fixture.CreateDeviceAsync(org.Id, $"NB-QUEDA-{i}");
            await TouchDeviceAsync(device.Id, DateTimeOffset.UtcNow.AddHours(-1));
            devices.Add(device);
            await SeedSummaryAsync(org.Id, device.Id, today.AddDays(-10));   // semana anterior
        }

        // nesta semana só 1 dos 5 continua mandando dados: queda de 80%
        await SeedSummaryAsync(org.Id, devices[0].Id, today.AddDays(-2));

        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var report = await NewService(dataSource, alertEmail).EvaluateAsync(DateTimeOffset.UtcNow);

        var row = Assert.Single(report.AtRisk, r => r.TenantId == org.Id);
        var sinal = Assert.Single(row.Signals, s => s.Code == AccountHealthService.SignalDeviceDrop);
        Assert.Contains("80%", sinal.Label);
        Assert.Equal(5, row.DevicesWithDataPrevious);
        Assert.Equal(1, row.DevicesWithDataCurrent);
    }

    [Fact]
    public async Task SaudeConta_TenantDemo_FicaDeForaDaApuracao()
    {
        // a demo pública é re-semeada toda semana: ela seria um falso churn permanente
        var alertEmail = NewCsEmail();
        var org = await fixture.CreateOrganizationAsync($"Conta Demo {Guid.NewGuid():N}"[..24]);
        await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        await AgeOrganizationAsync(org.Id, 60);

        await using var dataSource = NpgsqlDataSource.Create(Cs);

        var semExclusao = await NewService(dataSource, alertEmail).EvaluateAsync(DateTimeOffset.UtcNow);
        Assert.Contains(semExclusao.AtRisk, r => r.TenantId == org.Id);

        var comExclusao = await NewService(dataSource, alertEmail, org.Slug).EvaluateAsync(DateTimeOffset.UtcNow);
        Assert.DoesNotContain(comExclusao.AtRisk, r => r.TenantId == org.Id);
    }

    [Fact]
    public async Task SaudeConta_SemContaEmRisco_NaoEnviaEmail()
    {
        // ESTADO VAZIO (o normal antes do piloto): nenhuma conta em risco, nenhum e-mail.
        // O banco da suíte é compartilhado e o serviço varre TODAS as orgs ativas, então o
        // teste suspende temporariamente as orgs ativas e as restaura no finally. A coleção
        // roda em série, nenhum outro teste enxerga a janela.
        var alertEmail = NewCsEmail();
        var suspensas = new List<Guid>();
        await using var connection = new NpgsqlConnection(Cs);
        await connection.OpenAsync();

        await using (var cmd = new NpgsqlCommand(
            "UPDATE organizations SET status = 'suspended' WHERE status = 'active' RETURNING id", connection))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                suspensas.Add(reader.GetGuid(0));
            }
        }

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(Cs);
            var report = await NewService(dataSource, alertEmail).RunOnceAsync(DateTimeOffset.UtcNow);

            Assert.Empty(report.AtRisk);
            Assert.Null(fixture.Emails.LastFor(alertEmail));

            var enviado = await TestDb.ScalarAsync<string>(Cs, """
                SELECT detail->>'email_enviado' FROM maintenance_runs
                WHERE job_name = 'AccountHealth' ORDER BY started_at DESC LIMIT 1
                """);
            Assert.Equal("false", enviado);
        }
        finally
        {
            await TestDb.ExecuteAsync(Cs,
                "UPDATE organizations SET status = 'active' WHERE id = ANY(@ids)", ("ids", suspensas));
        }
    }

    [Fact]
    public void Csv_SegueOFormatoDoImportadorDoCrm()
    {
        // o import.php lê LINHA A LINHA e espera 8 colunas separadas por ';', com cabeçalho
        // começando em "empresa". Quebra de linha nas observações destruiria o arquivo.
        var row = new AccountHealthRow(
            Uuid7.NewUuid7(), "Empresa; com ponto e vírgula", "empresa-teste", "pro",
            DateTimeOffset.UtcNow.AddDays(-90), "Fulano da Silva", "fulano@empresa.com.br",
            null, null, 12, 3, 1, 8, 0, 14,
            [new AccountHealthSignal(AccountHealthService.SignalNoLogin, "nenhum login no portal desde 01/07/2026", 30)]);

        var csv = AccountHealthService.BuildCsv([row]);
        var linhas = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(AccountHealthService.CsvHeader, linhas[0]);
        Assert.Equal(2, linhas.Length);
        Assert.Equal(8, AccountHealthService.CsvHeader.Split(';').Length);

        // nome com ';' sai entre aspas (RFC 4180 adaptado ao ';'), então o campo não se parte
        Assert.StartsWith("\"Empresa; com ponto e vírgula\";Fulano da Silva;fulano@empresa.com.br;;12;outro;", linhas[1]);
        Assert.EndsWith(";", linhas[1]);        // coluna cnpj vazia: o produto não guarda CNPJ da org
        Assert.Contains("Saúde da conta 70/100 (risco atenção)", linhas[1]);
        Assert.Contains("Telemetria interna de CS", linhas[1]);

        // BOM: o import aceita e o Excel pt-BR precisa
        var bytes = AccountHealthService.CsvBytes([row]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        // lista vazia: só o cabeçalho, sem linha de dado
        Assert.Equal(AccountHealthService.CsvHeader + "\r\n", AccountHealthService.BuildCsv([]));
    }
}
