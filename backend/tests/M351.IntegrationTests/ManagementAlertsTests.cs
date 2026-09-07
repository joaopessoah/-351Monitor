using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Alerts;
using M351.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// ALERTAS DE GESTÃO (F6, seção 4 do spec de 07/09/2026): o motor de regras do worker
/// (ManagementAlertService) e o GET /api/v1/alerts que a Visão Geral consome.
///
/// Cobre cada uma das sete regras DISPARANDO e NÃO disparando, o opt-in da organização nas
/// duas regras de escopo pessoa (decisão 5), o mínimo de grupo de 3 pessoas (decisão 3), o
/// gate do plano Pro, a resolução automática quando a regra deixa de valer e o isolamento
/// multi-tenant.
///
/// SEMEADURA DIRETA nos agregados diários (em vez do pipeline de eventos): a regra é uma
/// função dos AGREGADOS, e semear evento a evento para chegar a "27% de improdutivo" tornaria
/// o teste sobre a intervalização, não sobre a regra.
///
/// O "AGORA" DE REFERÊNCIA é uma SEXTA-FEIRA FUTURA, nunca o relógio real, por dois motivos:
///  - sexta: a semana ISO em curso tem 5 dias, então as regras de "3 ou mais dias na semana" e
///    a projeção da meta (2 dias com dado) cabem em qualquer dia em que a suíte rode — com o
///    relógio real, todo teste desses quebraria nas segundas-feiras;
///  - futura: o corte de inscrição da regra 8 exige que o dispositivo exista há mais de um dia,
///    e o UUIDv7 dos dispositivos criados pelo teste carrega o instante REAL.
/// </summary>
[Collection(ApiCollection.Name)]
public class ManagementAlertsTests(ApiTestFixture fixture)
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private string Cs => fixture.Database.ConnectionString;

    /// <summary>A próxima sexta-feira, 14h em São Paulo (dentro de qualquer horário comercial).</summary>
    private static DateTimeOffset ReferenceNow()
    {
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz);
        var delta = (((int)DayOfWeek.Friday - (int)localNow.DayOfWeek) + 7) % 7;
        if (delta == 0)
        {
            delta = 7; // se hoje já é sexta, a próxima — o "agora" tem de ficar no futuro
        }

        var date = DateOnly.FromDateTime(localNow.Date).AddDays(delta);
        var local = date.ToDateTime(new TimeOnly(14, 0));
        return new DateTimeOffset(local, Tz.GetUtcOffset(local));
    }

    /// <summary>Data local (São Paulo) do "agora" de referência.</summary>
    private static DateOnly Today(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Tz).Date);

    /// <summary>Segunda-feira da semana ISO do "agora" (sempre 4 dias antes da sexta).</summary>
    private static DateOnly WeekStart(DateTimeOffset now) => Today(now).AddDays(-4);

    // ------------------------------------------------------------------ semeadura

    private async Task<(Guid TenantId, TestUser Viewer)> ProOrgAsync(
        string name, bool personAlerts = false, int? goalWeeklyHours = null, string? businessHours = null)
    {
        var org = await fixture.CreateOrganizationAsync(name);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);

        // plano Pro: alertas de gestão são do Pro (spec seção 4). business_hours nulo = sem
        // janela, então nada é silenciado e "fora do horário" não tem o que medir.
        await TestDb.ExecuteAsync(Cs,
            """
            UPDATE organizations
            SET plan = 'pro', person_alerts_enabled = @pessoa,
                goal_weekly_active_hours = @meta, business_hours = @janela::jsonb
            WHERE id = @id
            """,
            ("id", org.Id), ("pessoa", personAlerts),
            ("meta", (object?)goalWeeklyHours ?? DBNull.Value),
            ("janela", (object?)businessHours ?? DBNull.Value));

        return (org.Id, viewer);
    }

    /// <summary>Dispositivo com etiquetas de equipe e uma lane de titular (pessoa) pronta.</summary>
    private async Task<(Guid DeviceId, Guid LaneId, string Sid)> DeviceWithPersonAsync(
        Guid tenantId, string hostname, string sid, string[]? tags = null)
    {
        var device = await fixture.CreateDeviceAsync(tenantId, hostname);
        if (tags is not null)
        {
            await TestDb.ExecuteAsync(Cs, "UPDATE devices SET tags = @tags WHERE id = @id",
                ("id", device.Id), ("tags", tags));
        }

        var laneId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Cs,
            """
            INSERT INTO device_users (id, tenant_id, device_id, windows_sid, windows_username,
                                      first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @user, now(), now())
            """,
            ("id", laneId), ("t", tenantId), ("d", device.Id), ("sid", sid), ("user", sid));

        return (device.Id, laneId, sid);
    }

    private async Task SeedDayAsync(
        Guid tenantId, Guid deviceId, Guid laneId, DateOnly day,
        int on = 0, int active = 0, int idle = 0, int locked = 0,
        int workRelated = 0, int neutral = 0, int notWorkRelated = 0, int unclassified = 0)
    {
        await TestDb.ExecuteAsync(Cs,
            """
            INSERT INTO daily_device_summaries
                (tenant_id, summary_date, device_id, device_user_id,
                 seconds_active, seconds_idle, seconds_locked, seconds_on,
                 seconds_work_related, seconds_neutral, seconds_not_work_related,
                 seconds_unclassified, computed_at)
            VALUES (@t, @day::date, @d, @lane, @active, @idle, @locked, @on,
                    @wr, @neutral, @nwr, @unc, now())
            ON CONFLICT (tenant_id, summary_date, device_id, device_user_id) DO UPDATE
            SET seconds_active = EXCLUDED.seconds_active,
                seconds_idle = EXCLUDED.seconds_idle,
                seconds_locked = EXCLUDED.seconds_locked,
                seconds_on = EXCLUDED.seconds_on,
                seconds_work_related = EXCLUDED.seconds_work_related,
                seconds_neutral = EXCLUDED.seconds_neutral,
                seconds_not_work_related = EXCLUDED.seconds_not_work_related,
                seconds_unclassified = EXCLUDED.seconds_unclassified
            """,
            ("t", tenantId), ("day", day.ToString("yyyy-MM-dd")), ("d", deviceId), ("lane", laneId),
            ("active", active), ("idle", idle), ("locked", locked), ("on", on),
            ("wr", workRelated), ("neutral", neutral), ("nwr", notWorkRelated), ("unc", unclassified));
    }

    private async Task SeedHourAsync(
        Guid tenantId, Guid deviceId, Guid laneId, DateOnly day, int hourLocal, int secondsActive)
    {
        await TestDb.ExecuteAsync(Cs,
            """
            INSERT INTO hourly_activity
                (tenant_id, summary_date, hour_local, device_id, device_user_id,
                 seconds_active, seconds_idle)
            VALUES (@t, @day::date, @hour, @d, @lane, @active, 0)
            ON CONFLICT (tenant_id, summary_date, hour_local, device_id, device_user_id) DO UPDATE
            SET seconds_active = EXCLUDED.seconds_active
            """,
            ("t", tenantId), ("day", day.ToString("yyyy-MM-dd")), ("hour", (short)hourLocal),
            ("d", deviceId), ("lane", laneId), ("active", secondsActive));
    }

    private async Task<int> RunAsync(DateTimeOffset now)
    {
        await using var dataSource = NpgsqlDataSource.Create(Cs);
        var service = new ManagementAlertService(
            dataSource, NullLogger<ManagementAlertService>.Instance);
        return await service.RunOnceAsync(now);
    }

    /// <summary>Kinds VIVOS do tenant, lidos direto do estado (independe do endpoint).</summary>
    private async Task<HashSet<string>> LiveKindsAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(Cs);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT kind FROM management_alerts WHERE tenant_id = @t AND resolved_at IS NULL",
            connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            kinds.Add(reader.GetString(0));
        }

        return kinds;
    }

    private async Task<List<(string Kind, string ScopeKey)>> LiveAlertsAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(Cs);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT kind, scope_key FROM management_alerts WHERE tenant_id = @t AND resolved_at IS NULL",
            connection);
        cmd.Parameters.AddWithValue("t", tenantId);

        var rows = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    // ================================================================== regra 2: cobertura

    [Fact]
    public async Task Cobertura_AbaixoDe85_DisparaEAcimaNaoDispara()
    {
        var now = ReferenceNow();
        var hoje = Today(now);

        // org A: 40% do tempo ativo sem classificação → cobertura 60%, abaixo do piso de 85%
        var (baixa, _) = await ProOrgAsync("Alerta Cob Baixa");
        var a = await DeviceWithPersonAsync(baixa, "NB-COB-BAIXA", "S-COB-1");
        for (var i = 0; i < 5; i++)
        {
            await SeedDayAsync(baixa, a.DeviceId, a.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 600, unclassified: 400);
        }

        // org B: nada sem classificação → cobertura 100%, não dispara
        var (alta, _) = await ProOrgAsync("Alerta Cob Alta");
        var b = await DeviceWithPersonAsync(alta, "NB-COB-ALTA", "S-COB-2");
        for (var i = 0; i < 5; i++)
        {
            await SeedDayAsync(alta, b.DeviceId, b.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 1000);
        }

        await RunAsync(now);

        Assert.Contains(ManagementAlertService.KindCoverageBelow, await LiveKindsAsync(baixa));
        Assert.DoesNotContain(ManagementAlertService.KindCoverageBelow, await LiveKindsAsync(alta));
    }

    // ================================================================== regra 3: queda da equipe

    [Fact]
    public async Task EquipeComTempoAtivoAbaixoDaMedia_DisparaSoNaQuedaDe15Pct()
    {
        var now = ReferenceNow();
        var hoje = Today(now);
        var semana = WeekStart(now);

        var (tenantId, _) = await ProOrgAsync("Alerta Queda Equipe");

        // duas equipes de 3 pessoas (mínimo de grupo satisfeito nas duas)
        var caindo = new List<(Guid DeviceId, Guid LaneId, string Sid)>();
        var estavel = new List<(Guid DeviceId, Guid LaneId, string Sid)>();
        for (var p = 0; p < 3; p++)
        {
            caindo.Add(await DeviceWithPersonAsync(tenantId, $"NB-CAI-{p}", $"S-CAI-{p}", ["Comercial"]));
            estavel.Add(await DeviceWithPersonAsync(tenantId, $"NB-EST-{p}", $"S-EST-{p}", ["Suporte"]));
        }

        // base: as 4 semanas anteriores, 4 h ativas por pessoa-dia nas duas equipes
        for (var d = 1; d <= 28; d++)
        {
            var dia = semana.AddDays(-d);
            foreach (var pessoa in caindo.Concat(estavel))
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia,
                    on: 8 * 3600, active: 4 * 3600, workRelated: 4 * 3600);
            }
        }

        // semana em curso: Comercial cai para 2 h/pessoa-dia (50% abaixo), Suporte segue em 4 h
        for (var dia = semana; dia <= hoje; dia = dia.AddDays(1))
        {
            foreach (var pessoa in caindo)
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia,
                    on: 8 * 3600, active: 2 * 3600, workRelated: 2 * 3600);
            }

            foreach (var pessoa in estavel)
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia,
                    on: 8 * 3600, active: 4 * 3600, workRelated: 4 * 3600);
            }
        }

        await RunAsync(now);

        var vivos = await LiveAlertsAsync(tenantId);
        Assert.Contains((ManagementAlertService.KindTeamActiveDrop, "Comercial"), vivos);
        Assert.DoesNotContain((ManagementAlertService.KindTeamActiveDrop, "Suporte"), vivos);
    }

    // ================================================================== regra 7: improdutivo

    [Fact]
    public async Task ImprodutivoAcimaDe25PctDoAtivo_DisparaEOciosoNaoConta()
    {
        var now = ReferenceNow();
        var hoje = Today(now);
        var semana = WeekStart(now);

        var (tenantId, _) = await ProOrgAsync("Alerta Improdutivo");

        var alto = new List<(Guid DeviceId, Guid LaneId, string Sid)>();
        var ocioso = new List<(Guid DeviceId, Guid LaneId, string Sid)>();
        for (var p = 0; p < 3; p++)
        {
            alto.Add(await DeviceWithPersonAsync(tenantId, $"NB-IMP-{p}", $"S-IMP-{p}", ["Marketing"]));
            ocioso.Add(await DeviceWithPersonAsync(tenantId, $"NB-OCI-{p}", $"S-OCI-{p}", ["Financeiro"]));
        }

        for (var dia = semana; dia <= hoje; dia = dia.AddDays(1))
        {
            // Marketing: 40% do tempo ATIVO em app improdutivo → acima do teto de 25%
            foreach (var pessoa in alto)
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia,
                    on: 8 * 3600, active: 5 * 3600, workRelated: 3 * 3600, notWorkRelated: 2 * 3600);
            }

            // Financeiro: só 10% improdutivo, mas 6 h de OCIOSO no dia. Ocioso é estado de
            // máquina e NUNCA entra como improdutivo — se entrasse, esta equipe dispararia.
            foreach (var pessoa in ocioso)
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia,
                    on: 8 * 3600, active: 2 * 3600, idle: 6 * 3600,
                    workRelated: 1800, neutral: 4500, notWorkRelated: 900);
            }
        }

        await RunAsync(now);

        var vivos = await LiveAlertsAsync(tenantId);
        Assert.Contains((ManagementAlertService.KindTeamUnproductiveHigh, "Marketing"), vivos);
        Assert.DoesNotContain((ManagementAlertService.KindTeamUnproductiveHigh, "Financeiro"), vivos);
    }

    // ================================================================== decisão 3: mínimo de grupo

    [Fact]
    public async Task EquipeComMenosDeTresPessoas_NaoGeraAlertaComparativo()
    {
        var now = ReferenceNow();
        var hoje = Today(now);
        var semana = WeekStart(now);

        var (tenantId, _) = await ProOrgAsync("Alerta Minimo Grupo");

        // uma dupla: os números existem, mas média comparativa de equipe com 2 pessoas é
        // afirmação sobre indivíduo com outro nome — e isso só existe no opt-in de pessoa.
        var dupla = new List<(Guid DeviceId, Guid LaneId, string Sid)>
        {
            await DeviceWithPersonAsync(tenantId, "NB-DUO-0", "S-DUO-0", ["Dupla"]),
            await DeviceWithPersonAsync(tenantId, "NB-DUO-1", "S-DUO-1", ["Dupla"]),
        };

        for (var d = 1; d <= 28; d++)
        {
            foreach (var pessoa in dupla)
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, semana.AddDays(-d),
                    on: 8 * 3600, active: 4 * 3600, workRelated: 4 * 3600);
            }
        }

        // queda de 75% no ritmo E improdutivo em 100% do ativo: as duas regras estourariam
        for (var dia = semana; dia <= hoje; dia = dia.AddDays(1))
        {
            foreach (var pessoa in dupla)
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia,
                    on: 8 * 3600, active: 3600, notWorkRelated: 3600);
            }
        }

        await RunAsync(now);

        var kinds = await LiveKindsAsync(tenantId);
        Assert.DoesNotContain(ManagementAlertService.KindTeamActiveDrop, kinds);
        Assert.DoesNotContain(ManagementAlertService.KindTeamUnproductiveHigh, kinds);
    }

    // ================================================================== regra 5: dias longos

    [Fact]
    public async Task DiasLongos_SoComOptInDaOrganizacao()
    {
        var now = ReferenceNow();
        var semana = WeekStart(now);

        // MESMO dado nas duas orgs; a única diferença é o opt-in (decisão 5)
        var (semOptIn, _) = await ProOrgAsync("Alerta Dias Longos Off");
        var (comOptIn, _) = await ProOrgAsync("Alerta Dias Longos On", personAlerts: true);

        foreach (var (tenantId, sufixo) in new[] { (semOptIn, "OFF"), (comOptIn, "ON") })
        {
            var pessoa = await DeviceWithPersonAsync(tenantId, $"NB-LONGO-{sufixo}", $"S-LONGO-{sufixo}");
            // 3 dias com 11 h de máquina ligada (acima das 10 h da régra), o mínimo da regra
            for (var d = 0; d < 3; d++)
            {
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, semana.AddDays(d),
                    on: 11 * 3600, active: 6 * 3600, workRelated: 6 * 3600);
            }
        }

        await RunAsync(now);

        Assert.Contains(ManagementAlertService.KindPersonLongDays, await LiveKindsAsync(comOptIn));
        Assert.DoesNotContain(ManagementAlertService.KindPersonLongDays, await LiveKindsAsync(semOptIn));
    }

    [Fact]
    public async Task DiasLongos_ComOptInMasApenasDoisDias_NaoDispara()
    {
        var now = ReferenceNow();
        var semana = WeekStart(now);

        var (tenantId, _) = await ProOrgAsync("Alerta Dias Longos 2", personAlerts: true);
        var pessoa = await DeviceWithPersonAsync(tenantId, "NB-LONGO-2", "S-LONGO-2");

        // 2 dias longos e um terceiro de 9 h: a régua é 3 OU MAIS dias na semana
        await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, semana, on: 11 * 3600, active: 3600, workRelated: 3600);
        await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, semana.AddDays(1), on: 11 * 3600, active: 3600, workRelated: 3600);
        await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, semana.AddDays(2), on: 9 * 3600, active: 3600, workRelated: 3600);

        await RunAsync(now);

        Assert.DoesNotContain(ManagementAlertService.KindPersonLongDays, await LiveKindsAsync(tenantId));
    }

    // ================================================================== regra 4: fora do horário

    [Fact]
    public async Task ForaDoHorario_SoComOptInEComJanelaConfigurada()
    {
        var now = ReferenceNow();
        var semana = WeekStart(now);
        const string Janela = """{"days":[1,2,3,4,5],"start":"08:00","end":"18:00"}""";

        var (semOptIn, _) = await ProOrgAsync("Alerta Fora Off", businessHours: Janela);
        var (comOptIn, _) = await ProOrgAsync("Alerta Fora On", personAlerts: true, businessHours: Janela);
        var (poucas, _) = await ProOrgAsync("Alerta Fora Pouco", personAlerts: true, businessHours: Janela);

        foreach (var (tenantId, sufixo, horas) in new[] { (semOptIn, "OFF", 6), (comOptIn, "ON", 6), (poucas, "POUCO", 2) })
        {
            var pessoa = await DeviceWithPersonAsync(tenantId, $"NB-FORA-{sufixo}", $"S-FORA-{sufixo}");

            // Uma hora POR (dia, balde de hora): a PK de hourly_activity é
            // (tenant, dia, hora, device, lane), então repetir o par sobrescreveria em vez de
            // somar — e o total ficaria colado na régua de 5 h em vez de passar dela.
            for (var i = 0; i < horas; i++)
            {
                var dia = semana.AddDays(i % 5);
                var hora = 20 + (i / 5); // 20h e 21h: fora da janela 08–18 nos dois casos
                await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia,
                    on: 3600, active: 3600, workRelated: 3600);
                await SeedHourAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, dia, hora, 3600);
            }
        }

        await RunAsync(now);

        // 6 h fora do horário e opt-in ligado: dispara
        Assert.Contains(ManagementAlertService.KindPersonAfterHours, await LiveKindsAsync(comOptIn));
        // mesmo dado, opt-in desligado: nem é avaliado
        Assert.DoesNotContain(ManagementAlertService.KindPersonAfterHours, await LiveKindsAsync(semOptIn));
        // opt-in ligado mas só 2 h na semana: abaixo da régua de 5 h
        Assert.DoesNotContain(ManagementAlertService.KindPersonAfterHours, await LiveKindsAsync(poucas));
    }

    // ================================================================== regra 6: meta em risco

    [Fact]
    public async Task MetaDaSemana_DisparaSoQuandoAProjecaoFicaAbaixo()
    {
        var now = ReferenceNow();
        var semana = WeekStart(now);

        // meta de 200 h com ritmo de 1 h/dia → projeção de 5 h: em risco
        var (risco, _) = await ProOrgAsync("Alerta Meta Risco", goalWeeklyHours: 200);
        var a = await DeviceWithPersonAsync(risco, "NB-META-RISCO", "S-META-1");
        await SeedDayAsync(risco, a.DeviceId, a.LaneId, semana, on: 3600, active: 3600, workRelated: 3600);
        await SeedDayAsync(risco, a.DeviceId, a.LaneId, semana.AddDays(1), on: 3600, active: 3600, workRelated: 3600);

        // meta de 5 h com ritmo de 8 h/dia → projeção de 40 h: no ritmo
        var (ok, _) = await ProOrgAsync("Alerta Meta Ok", goalWeeklyHours: 5);
        var b = await DeviceWithPersonAsync(ok, "NB-META-OK", "S-META-2");
        await SeedDayAsync(ok, b.DeviceId, b.LaneId, semana, on: 8 * 3600, active: 8 * 3600, workRelated: 8 * 3600);
        await SeedDayAsync(ok, b.DeviceId, b.LaneId, semana.AddDays(1), on: 8 * 3600, active: 8 * 3600, workRelated: 8 * 3600);

        await RunAsync(now);

        Assert.Contains(ManagementAlertService.KindGoalAtRisk, await LiveKindsAsync(risco));
        Assert.DoesNotContain(ManagementAlertService.KindGoalAtRisk, await LiveKindsAsync(ok));
    }

    // ================================================================== regra 8: dia útil sem dados

    [Fact]
    public async Task DiaUtilSemDados_DisparaSoNoDispositivoSemNenhumSegundo()
    {
        var now = ReferenceNow();
        var ontem = Today(now).AddDays(-1); // quinta-feira, dia útil já FECHADO

        var (tenantId, _) = await ProOrgAsync("Alerta Sem Dados");

        var mudo = await DeviceWithPersonAsync(tenantId, "NB-MUDO", "S-MUDO");
        var falante = await DeviceWithPersonAsync(tenantId, "NB-FALANTE", "S-FALANTE");
        await SeedDayAsync(tenantId, falante.DeviceId, falante.LaneId, ontem,
            on: 8 * 3600, active: 4 * 3600, workRelated: 4 * 3600);

        await RunAsync(now);

        var vivos = await LiveAlertsAsync(tenantId);
        Assert.Contains((ManagementAlertService.KindBusinessDayNoData, mudo.DeviceId.ToString()), vivos);
        Assert.DoesNotContain((ManagementAlertService.KindBusinessDayNoData, falante.DeviceId.ToString()), vivos);
    }

    // ================================================================== estado: resolução

    [Fact]
    public async Task AlertaEResolvidoQuandoARegraDeixaDeValer()
    {
        var now = ReferenceNow();
        var hoje = Today(now);

        var (tenantId, _) = await ProOrgAsync("Alerta Resolve");
        var pessoa = await DeviceWithPersonAsync(tenantId, "NB-RESOLVE", "S-RESOLVE");
        for (var i = 0; i < 5; i++)
        {
            await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 500, unclassified: 500);
        }

        await RunAsync(now);
        Assert.Contains(ManagementAlertService.KindCoverageBelow, await LiveKindsAsync(tenantId));

        // classificação curada: o balde sem classificação zera e a cobertura sobe para 100%
        for (var i = 0; i < 5; i++)
        {
            await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 1000);
        }

        await RunAsync(now.AddMinutes(15));
        Assert.DoesNotContain(ManagementAlertService.KindCoverageBelow, await LiveKindsAsync(tenantId));

        // a linha FICA (resolvida): é o cooldown anti-flapping e a memória do que já ocorreu
        var resolvidos = await TestDb.ScalarAsync<long>(Cs,
            """
            SELECT count(*) FROM management_alerts
            WHERE tenant_id = @t AND kind = @k AND resolved_at IS NOT NULL
            """,
            ("t", tenantId), ("k", ManagementAlertService.KindCoverageBelow));
        Assert.Equal(1, resolvidos);
    }

    // ================================================================== plano Pro

    [Fact]
    public async Task ForaDoPlanoPro_NaoAvaliaERelataNoEndpoint()
    {
        var now = ReferenceNow();
        var hoje = Today(now);

        var org = await fixture.CreateOrganizationAsync("Alerta Trial");
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var pessoa = await DeviceWithPersonAsync(org.Id, "NB-TRIAL", "S-TRIAL");
        for (var i = 0; i < 5; i++)
        {
            await SeedDayAsync(org.Id, pessoa.DeviceId, pessoa.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 100, unclassified: 900);
        }

        await RunAsync(now);

        Assert.Empty(await LiveKindsAsync(org.Id));

        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        using var body = await GetJsonAsync(client, token, "/api/v1/alerts");
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
        // a tela precisa distinguir "está tudo bem" de "o plano não inclui a feature"
        Assert.False(body.RootElement.GetProperty("plan_includes_alerts").GetBoolean());
    }

    // ================================================================== endpoint

    [Fact]
    public async Task Endpoint_DevolveContextoLinkEOEstadoDoOptIn()
    {
        var now = ReferenceNow();
        var hoje = Today(now);

        var (tenantId, viewer) = await ProOrgAsync("Alerta Endpoint");
        var pessoa = await DeviceWithPersonAsync(tenantId, "NB-ENDPOINT", "S-ENDPOINT");
        for (var i = 0; i < 5; i++)
        {
            await SeedDayAsync(tenantId, pessoa.DeviceId, pessoa.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 300, unclassified: 700);
        }

        await RunAsync(now);

        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer); // Viewer+ basta
        using var body = await GetJsonAsync(client, token, "/api/v1/alerts");
        var root = body.RootElement;

        // opt-in desligado tem de aparecer: sem isso o gestor conclui que ninguém trabalha
        // fora do horário, quando na verdade ninguém está medindo (decisão 5)
        Assert.False(root.GetProperty("person_alerts_enabled").GetBoolean());
        Assert.True(root.GetProperty("plan_includes_alerts").GetBoolean());

        var item = root.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("kind").GetString() == ManagementAlertService.KindCoverageBelow);

        Assert.Equal("organization", item.GetProperty("scope_type").GetString());
        Assert.Equal("/configuracoes/categorias", item.GetProperty("link").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("context").GetString()));

        // vocabulário: variação e contexto, JAMAIS ranking ou julgamento
        var texto = item.GetProperty("title").GetString()! + item.GetProperty("context").GetString()!;
        Assert.DoesNotContain("ranking", texto, StringComparison.OrdinalIgnoreCase);

        // 30% de cobertura no dado semeado: o número vem do servidor, não do portal
        Assert.Equal(0.3, item.GetProperty("detail").GetProperty("coverage").GetDouble(), 3);
    }

    // ================================================================== isolamento multi-tenant

    [Fact]
    public async Task Alertas_SaoIsoladosPorTenant()
    {
        var now = ReferenceNow();
        var hoje = Today(now);

        var (tenantA, viewerA) = await ProOrgAsync("Alerta Tenant A");
        var (tenantB, viewerB) = await ProOrgAsync("Alerta Tenant B");

        // só A tem cobertura ruim; B tem tudo classificado
        var a = await DeviceWithPersonAsync(tenantA, "NB-TEN-A", "S-TEN-A");
        var b = await DeviceWithPersonAsync(tenantB, "NB-TEN-B", "S-TEN-B");
        for (var i = 0; i < 5; i++)
        {
            await SeedDayAsync(tenantA, a.DeviceId, a.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 200, unclassified: 800);
            await SeedDayAsync(tenantB, b.DeviceId, b.LaneId, hoje.AddDays(-i),
                on: 3600, active: 1000, workRelated: 1000);
        }

        await RunAsync(now);

        var client = fixture.CreateApiClient();

        var tokenA = await AuthClient.LoginAsync(client, viewerA);
        using var bodyA = await GetJsonAsync(client, tokenA, "/api/v1/alerts");
        Assert.Contains(
            bodyA.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("kind").GetString() == ManagementAlertService.KindCoverageBelow);

        // B nunca vê o alerta de A, nem que os dois foram avaliados no MESMO ciclo
        var tokenB = await AuthClient.LoginAsync(client, viewerB);
        using var bodyB = await GetJsonAsync(client, tokenB, "/api/v1/alerts");
        Assert.DoesNotContain(
            bodyB.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("kind").GetString() == ManagementAlertService.KindCoverageBelow);
    }

    [Fact]
    public async Task Endpoint_ExigeAutenticacao()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync("/api/v1/alerts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string token, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }
}
