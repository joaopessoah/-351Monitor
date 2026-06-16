using System.Globalization;
using M351.Domain;
using M351.Infrastructure.Maintenance;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Jobs de retencao/purga (F4.6 — Secao 7.6/7.2/9.6; DoD F4 linha 1043 "expurgo comprovado com
/// dado antigo plantado"). Planta particao/linha ANTIGA e prova que:
///  - PartitionMaintenance DROPa exatamente as particoes alem da retencao (raw 90d N10, intervals
///    12m N11, audit 24m N13) e PRESERVA as dentro da retencao e as correntes/futuras;
///  - PartitionMaintenance cria particoes futuras (raw D+3, intervals/audit mes+2), idempotente em
///    2 rodadas;
///  - RetentionPurge DELETA daily_* alem de 24m (N12) e preserva o recente;
///  - Housekeeping expira invitation/refresh_token/export_job vencidos e preserva validos, sem
///    apagar export_job cujo arquivo ainda nao foi varrido (file_path != NULL);
///  - cada execucao grava maintenance_runs (a fonte de "data da ultima purga" da F4.8).
///
/// Usa a ApiTestFixture (banco descartavel ja migrado no boot) e instancia os servicos contra a
/// mesma connection string — mesmo padrao de DailyAggregationTests. Plantio de particoes antigas
/// e por SQL cru (CREATE TABLE ... PARTITION OF + INSERT), pois sao datas fora da janela que
/// nenhuma migration/ingest cria.
/// </summary>
[Collection(ApiCollection.Name)]
public class RetentionJobsTests(ApiTestFixture fixture)
{
    private string Conn => fixture.Database.ConnectionString;

    // ------------------------------------------------------------ helpers de plantio
    /// <summary>Cria a particao diaria de raw_events do dia dado (fora da janela das migrations).</summary>
    private async Task CreateRawDailyPartitionAsync(DateOnly day)
    {
        var lo = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hi = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await TestDb.ExecuteAsync(Conn,
            $"CREATE TABLE IF NOT EXISTS raw_events_{day:yyyyMMdd} PARTITION OF raw_events FOR VALUES FROM ('{lo}') TO ('{hi}')");
    }

    private async Task CreateMonthlyPartitionAsync(string table, DateOnly month)
    {
        var lo = month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hi = month.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await TestDb.ExecuteAsync(Conn,
            $"CREATE TABLE IF NOT EXISTS {table}_{month:yyyyMM} PARTITION OF {table} FOR VALUES FROM ('{lo}') TO ('{hi}')");
    }

    private async Task<bool> PartitionExistsAsync(string name) =>
        await TestDb.ScalarAsync<bool>(Conn, "SELECT to_regclass(@n) IS NOT NULL", ("n", name));

    private async Task<long> CountAsync(string table, string where = "", params (string, object?)[] args) =>
        await TestDb.ScalarAsync<long>(Conn, $"SELECT count(*) FROM {table} {where}", args);

    private NpgsqlDataSource NewDataSource() => NpgsqlDataSource.Create(Conn);

    /// <summary>
    /// Forca o boot do host da WebApplicationFactory (que roda as migrations via Database:AutoMigrate
    /// no Program.cs da API). Os testes que so usam fixture.Database.ConnectionString — e nunca
    /// fixture.Services / CreateApiClient — nao disparariam o boot e veriam um banco SEM tabelas.
    /// Tocar Services (idempotente; o host so e construido uma vez) garante o schema antes do plantio.
    /// </summary>
    private void EnsureMigrated() => _ = fixture.Services;

    // ============================================================ PartitionMaintenance — drop
    [Fact]
    public async Task PartitionMaintenance_DropaExpiradas_PreservaDentroDaRetencao()
    {
        EnsureMigrated();
        var testStart = DateTimeOffset.UtcNow;
        var now = DateTimeOffset.UtcNow;

        // ----- raw_events: 200 dias atras (expira, N10=90d) e 30 dias atras (dentro) -----
        var rawOld = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-200);
        var rawRecent = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-30);
        await CreateRawDailyPartitionAsync(rawOld);
        await CreateRawDailyPartitionAsync(rawRecent);
        await PlantRawRowAsync(rawOld);
        await PlantRawRowAsync(rawRecent);

        // ----- activity_intervals: 13 meses atras (expira, N11=12m) e 2 meses atras (dentro) -----
        var intOld = FirstOfMonth(now).AddMonths(-13);
        var intRecent = FirstOfMonth(now).AddMonths(-2);
        await CreateMonthlyPartitionAsync("activity_intervals", intOld);
        await CreateMonthlyPartitionAsync("activity_intervals", intRecent);

        // ----- audit_log: 25 meses atras (expira, N13=24m) e 10 meses atras (dentro) -----
        var auditOld = FirstOfMonth(now).AddMonths(-25);
        var auditRecent = FirstOfMonth(now).AddMonths(-10);
        await CreateMonthlyPartitionAsync("audit_log", auditOld);
        await CreateMonthlyPartitionAsync("audit_log", auditRecent);

        // sanidade: tudo existe antes
        Assert.True(await PartitionExistsAsync($"raw_events_{rawOld:yyyyMMdd}"));
        Assert.True(await PartitionExistsAsync($"activity_intervals_{intOld:yyyyMM}"));
        Assert.True(await PartitionExistsAsync($"audit_log_{auditOld:yyyyMM}"));

        await using var ds = NewDataSource();
        var result = await new PartitionMaintenanceService(ds).RunOnceAsync();

        // contagens do ciclo (deterministicas — vem do retorno, nao da trilha global)
        Assert.Equal(1, result.RawDropped);
        Assert.Equal(1, result.IntervalsDropped);
        Assert.Equal(1, result.AuditDropped);

        // EXPIRADAS dropadas
        Assert.False(await PartitionExistsAsync($"raw_events_{rawOld:yyyyMMdd}"),
            "particao raw de 200d atras devia ter sido dropada (N10)");
        Assert.False(await PartitionExistsAsync($"activity_intervals_{intOld:yyyyMM}"),
            "particao intervals de 13m atras devia ter sido dropada (N11)");
        Assert.False(await PartitionExistsAsync($"audit_log_{auditOld:yyyyMM}"),
            "particao audit de 25m atras devia ter sido dropada (N13)");

        // DENTRO da retencao preservadas (e com o dado vivo)
        Assert.True(await PartitionExistsAsync($"raw_events_{rawRecent:yyyyMMdd}"),
            "particao raw de 30d atras NAO podia ser dropada");
        Assert.Equal(1, await CountAsync("raw_events", "WHERE occurred_at >= @d AND occurred_at < @d2",
            ("d", new DateTimeOffset(rawRecent.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
            ("d2", new DateTimeOffset(rawRecent.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))));
        Assert.True(await PartitionExistsAsync($"activity_intervals_{intRecent:yyyyMM}"));
        Assert.True(await PartitionExistsAsync($"audit_log_{auditRecent:yyyyMM}"));

        // a particao CORRENTE (criada na InitialCreate) jamais sai
        var thisMonth = FirstOfMonth(now);
        Assert.True(await PartitionExistsAsync($"activity_intervals_{thisMonth:yyyyMM}"),
            "particao corrente de intervals jamais pode ser dropada");

        // trilha gravada: ao menos uma execucao ok com a forma esperada de detail desde o inicio
        // do teste (maintenance_runs e GLOBAL/compartilhada — as contagens exatas ja foram
        // asseridas pelo retorno do servico acima, deterministico).
        var run = await TestDb.RowAsync(Conn, """
            SELECT status, detail::text AS detail FROM maintenance_runs
            WHERE job_name = 'PartitionMaintenance' AND status = 'ok' AND started_at >= @from
            ORDER BY started_at DESC LIMIT 1
            """, ("from", testStart));
        Assert.NotNull(run);
        Assert.Equal("ok", (string)run!["status"]!);
        var detail = (string)run["detail"]!;
        Assert.Contains("\"raw_events_dropped\":", detail);
        Assert.Contains("\"activity_intervals_dropped\":", detail);
        Assert.Contains("\"audit_log_dropped\":", detail);
    }

    // ============================================================ PartitionMaintenance — drop nao trava a ingestao
    /// <summary>
    /// Regressao do achado de revisao F4.6 (severidade media, classe do commit 3c7ea3e): o DROP de
    /// uma particao ALEM da retencao exige AccessExclusiveLock no PARENT raw_events, que conflita com
    /// o RowExclusiveLock de QUALQUER INSERT da ingestao (ainda que na particao corrente, nao na
    /// antiga). Com uma txn de ingestao aberta segurando o lock do parent, o DROP NAO pode travar a
    /// ingestao: o lock_timeout curto faz o Postgres abortar com 55P03 e o servico PULA a particao
    /// (sem estourar o CommandTimeout de ~30s). A particao expirada sobrevive e a proxima execucao
    /// diaria a dropa. Prova que o tempo do RunOnce fica perto do lock_timeout, nao do CommandTimeout.
    /// </summary>
    [Fact]
    public async Task PartitionMaintenance_DropComLockDoParentOcupado_PulaSemTravarIngestao()
    {
        EnsureMigrated();
        var now = DateTimeOffset.UtcNow;
        var rawOld = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-200); // alem de N10=90d
        await CreateRawDailyPartitionAsync(rawOld);
        await PlantRawRowAsync(rawOld);
        Assert.True(await PartitionExistsAsync($"raw_events_{rawOld:yyyyMMdd}"));

        // sessao concorrente segurando RowExclusiveLock no PARENT raw_events (espelha um INSERT da
        // ingestao em txn aberta). LOCK explicito no modo do INSERT, mantido ate o fim do teste.
        await using var blocker = new NpgsqlConnection(Conn);
        await blocker.OpenAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        await using (var lockCmd = blocker.CreateCommand())
        {
            lockCmd.Transaction = blockerTx;
            lockCmd.CommandText = "LOCK TABLE raw_events IN ROW EXCLUSIVE MODE";
            await lockCmd.ExecuteNonQueryAsync();
        }

        await using var ds = NewDataSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new PartitionMaintenanceService(ds).RunOnceAsync();
        sw.Stop();

        // o DROP da raw expirada foi PULADO (lock do parent ocupado) — nao dropou
        Assert.Equal(0, result.RawDropped);
        Assert.True(await PartitionExistsAsync($"raw_events_{rawOld:yyyyMMdd}"),
            "particao expirada deve sobreviver quando o lock do parent esta ocupado (retenta amanha)");

        // nao travou ate o CommandTimeout (~30s): abortou perto do lock_timeout (2s). Folga ampla
        // para criacao de futuras + intervals/audit no mesmo RunOnce, mas << 30s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"RunOnce nao podia aproximar o CommandTimeout; levou {sw.Elapsed.TotalSeconds:F1}s");

        // libera o lock e prova que a proxima execucao dropa a particao que ficou
        await blockerTx.RollbackAsync();
        var second = await new PartitionMaintenanceService(ds).RunOnceAsync();
        Assert.Equal(1, second.RawDropped);
        Assert.False(await PartitionExistsAsync($"raw_events_{rawOld:yyyyMMdd}"),
            "com o lock livre, a execucao seguinte dropa a particao expirada");
    }

    // ============================================================ PartitionMaintenance — cria futuras + idempotencia
    [Fact]
    public async Task PartitionMaintenance_CriaFuturas_Idempotente()
    {
        EnsureMigrated();
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var thisMonth = FirstOfMonth(now);

        await using var ds = NewDataSource();
        await new PartitionMaintenanceService(ds).RunOnceAsync();

        // raw D+1..D+3 (D+0 pode ja existir da migration; checa as alem do mes corrente tambem)
        for (var d = 1; d <= PartitionMaintenanceService.RawFutureDays; d++)
            Assert.True(await PartitionExistsAsync($"raw_events_{today.AddDays(d):yyyyMMdd}"),
                $"particao raw futura D+{d} devia existir");

        // intervals/audit mes+1 e mes+2 (mes+1 ja vem da migration; mes+2 e o novo)
        Assert.True(await PartitionExistsAsync($"activity_intervals_{thisMonth.AddMonths(2):yyyyMM}"),
            "particao intervals mes+2 devia existir");
        Assert.True(await PartitionExistsAsync($"audit_log_{thisMonth.AddMonths(2):yyyyMM}"),
            "particao audit mes+2 devia existir");

        // segunda rodada: idempotente (nenhuma criada de novo, sem excecao). Asserido pelo
        // RETORNO do servico — maintenance_runs e GLOBAL/compartilhada com os demais testes da
        // colecao (ordem nao-deterministica), entao reler a trilha por job_name e fragil.
        var second = await new PartitionMaintenanceService(ds).RunOnceAsync();
        Assert.Equal(0, second.RawCreated);
        Assert.Equal(0, second.IntervalsCreated);
        Assert.Equal(0, second.AuditCreated);
    }

    // ============================================================ RetentionPurge — N12
    [Fact]
    public async Task RetentionPurge_DeletaAgregadosAntigos_PreservaRecentes()
    {
        var testStart = DateTimeOffset.UtcNow;
        var org = await fixture.CreateOrganizationAsync($"Purge {Guid.NewGuid():N}"[..18]);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-PURGE");
        var now = DateTimeOffset.UtcNow;
        var oldDay = DateOnly.FromDateTime(now.UtcDateTime).AddMonths(-25); // alem de 24m
        var recentDay = DateOnly.FromDateTime(now.UtcDateTime).AddMonths(-1); // dentro

        await PlantSummaryAsync(org.Id, device.Id, oldDay);
        await PlantSummaryAsync(org.Id, device.Id, recentDay);
        await PlantAppUsageAsync(org.Id, device.Id, oldDay);
        await PlantAppUsageAsync(org.Id, device.Id, recentDay);

        await using var ds = NewDataSource();
        var result = await new RetentionPurgeService(ds).RunOnceAsync();

        // contagens deterministicas do ciclo (do retorno): so este teste planta 25m atras
        Assert.True(result.LockAcquired);
        Assert.Equal(1, result.SummariesDeleted);
        Assert.Equal(1, result.AppUsageDeleted);

        // antigo deletado, recente vivo
        Assert.Equal(0, await CountAsync("daily_device_summaries", "WHERE device_id = @d AND summary_date = @day",
            ("d", device.Id), ("day", oldDay)));
        Assert.Equal(1, await CountAsync("daily_device_summaries", "WHERE device_id = @d AND summary_date = @day",
            ("d", device.Id), ("day", recentDay)));
        Assert.Equal(0, await CountAsync("daily_app_usage", "WHERE device_id = @d AND summary_date = @day",
            ("d", device.Id), ("day", oldDay)));
        Assert.Equal(1, await CountAsync("daily_app_usage", "WHERE device_id = @d AND summary_date = @day",
            ("d", device.Id), ("day", recentDay)));

        var run = await TestDb.RowAsync(Conn, """
            SELECT status, detail::text AS detail FROM maintenance_runs
            WHERE job_name = 'RetentionPurge' AND status = 'ok' AND started_at >= @from
            ORDER BY started_at DESC LIMIT 1
            """, ("from", testStart));
        Assert.NotNull(run);
        Assert.Equal("ok", (string)run!["status"]!);
        Assert.Contains("\"daily_device_summaries_deleted\":", (string)run["detail"]!);
        Assert.Contains("\"daily_app_usage_deleted\":", (string)run["detail"]!);
    }

    // ============================================================ Housekeeping
    [Fact]
    public async Task Housekeeping_ExpiraVencidos_PreservaValidos()
    {
        var testStart = DateTimeOffset.UtcNow;
        var org = await fixture.CreateOrganizationAsync($"House {Guid.NewGuid():N}"[..18]);
        var now = DateTimeOffset.UtcNow;

        // convites: 1 vencido nao-aceito (expira), 1 vencido JA aceito (fica), 1 valido (fica)
        var (invExpired, _, _) = await fixture.CreateInvitationAsync(org.Id, UserRole.Viewer, now.AddDays(-1));
        var (invAcceptedExpired, _, _) = await fixture.CreateInvitationAsync(org.Id, UserRole.Viewer, now.AddDays(-1));
        await TestDb.ExecuteAsync(Conn, "UPDATE invitations SET accepted_at = now() WHERE id = @id",
            ("id", invAcceptedExpired.Id));
        var (invValid, _, _) = await fixture.CreateInvitationAsync(org.Id, UserRole.Viewer, now.AddDays(5));

        // refresh tokens: 1 vencido, 1 revogado, 1 valido
        var user = await fixture.CreateUserAsync(org.Id, UserRole.Admin);
        var rtExpired = await PlantRefreshTokenAsync(org.Id, user.Id, now.AddDays(-1), revoked: false);
        var rtRevoked = await PlantRefreshTokenAsync(org.Id, user.Id, now.AddDays(10), revoked: true);
        var rtValid = await PlantRefreshTokenAsync(org.Id, user.Id, now.AddDays(10), revoked: false);

        // export_jobs: 1 vencido com arquivo JA varrido (file_path NULL -> expira), 1 vencido com
        // arquivo AINDA presente (file_path != NULL -> NAO expira ainda), 1 valido (fica)
        var ejSwept = await PlantExportJobAsync(org.Id, now.AddDays(-1), filePath: null);
        var ejFileStill = await PlantExportJobAsync(org.Id, now.AddDays(-1), filePath: "x/y.csv");
        var ejValid = await PlantExportJobAsync(org.Id, now.AddDays(3), filePath: "a/b.csv");

        await using var ds = NewDataSource();
        await new HousekeepingService(ds).RunOnceAsync();

        // invitations
        Assert.Equal(0, await CountAsync("invitations", "WHERE id = @id", ("id", invExpired.Id)));
        Assert.Equal(1, await CountAsync("invitations", "WHERE id = @id", ("id", invAcceptedExpired.Id)));
        Assert.Equal(1, await CountAsync("invitations", "WHERE id = @id", ("id", invValid.Id)));

        // refresh tokens
        Assert.Equal(0, await CountAsync("refresh_tokens", "WHERE id = @id", ("id", rtExpired)));
        Assert.Equal(0, await CountAsync("refresh_tokens", "WHERE id = @id", ("id", rtRevoked)));
        Assert.Equal(1, await CountAsync("refresh_tokens", "WHERE id = @id", ("id", rtValid)));

        // export_jobs: coordenacao com o sweep do ExportService (file_path != NULL ainda vive)
        Assert.Equal(0, await CountAsync("export_jobs", "WHERE id = @id", ("id", ejSwept)));
        Assert.Equal(1, await CountAsync("export_jobs", "WHERE id = @id", ("id", ejFileStill)));
        Assert.Equal(1, await CountAsync("export_jobs", "WHERE id = @id", ("id", ejValid)));

        // a trilha foi gravada (status ok). As contagens exatas do detail NAO sao asseridas aqui:
        // maintenance_runs e GLOBAL e o Housekeeping deleta linhas vencidas de toda a colecao de
        // testes (outras suites deixam invitations/refresh_tokens vencidos) — as contagens por
        // linha acima (IDs unicos deste teste) sao a prova forte; o detail so reflete o agregado.
        var run = await TestDb.RowAsync(Conn, """
            SELECT status, detail::text AS detail FROM maintenance_runs
            WHERE job_name = 'Housekeeping' AND started_at >= @from
            ORDER BY started_at DESC LIMIT 1
            """, ("from", testStart));
        Assert.NotNull(run);
        Assert.Equal("ok", (string)run!["status"]!);
        Assert.Contains("\"invitations_deleted\":", (string)run["detail"]!);
        Assert.Contains("\"refresh_tokens_deleted\":", (string)run["detail"]!);
        Assert.Contains("\"export_jobs_deleted\":", (string)run["detail"]!);
    }

    // ------------------------------------------------------------ plantio low-level
    private async Task PlantRawRowAsync(DateOnly day)
    {
        var ts = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO raw_events (tenant_id, device_id, event_id, seq, occurred_at, event_type)
            VALUES (@t, @d, @e, 1, @at, 'HEARTBEAT')
            """,
            ("t", Guid.NewGuid()), ("d", Guid.NewGuid()), ("e", Guid.NewGuid()), ("at", ts));
    }

    private async Task PlantSummaryAsync(Guid tenantId, Guid deviceId, DateOnly day)
    {
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO daily_device_summaries
              (tenant_id, summary_date, device_id, seconds_active, seconds_on, computed_at)
            VALUES (@t, @day, @d, 100, 100, now())
            """, ("t", tenantId), ("day", day), ("d", deviceId));
    }

    private async Task PlantAppUsageAsync(Guid tenantId, Guid deviceId, DateOnly day)
    {
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO daily_app_usage
              (tenant_id, summary_date, device_id, app_id, seconds_active, focus_count)
            VALUES (@t, @day, @d, @app, 100, 1)
            """, ("t", tenantId), ("day", day), ("d", deviceId), ("app", Uuid7.NewUuid7()));
    }

    private async Task<Guid> PlantRefreshTokenAsync(Guid tenantId, Guid userId, DateTimeOffset expiresAt, bool revoked)
    {
        var id = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO refresh_tokens (id, tenant_id, user_id, token_hash, expires_at, revoked_at)
            VALUES (@id, @t, @u, @h, @exp, @rev)
            """,
            ("id", id), ("t", tenantId), ("u", userId), ("h", Guid.NewGuid().ToByteArray()),
            ("exp", expiresAt), ("rev", revoked ? DateTimeOffset.UtcNow : (object?)null));
        return id;
    }

    private async Task<Guid> PlantExportJobAsync(Guid tenantId, DateTimeOffset expiresAt, string? filePath)
    {
        var id = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO export_jobs (id, tenant_id, requested_by, kind, params, status, file_path, expires_at)
            VALUES (@id, @t, @rb, 'usage_csv', '{}'::jsonb, 'done', @fp, @exp)
            """,
            ("id", id), ("t", tenantId), ("rb", Guid.NewGuid()), ("fp", (object?)filePath), ("exp", expiresAt));
        return id;
    }

    private static DateOnly FirstOfMonth(DateTimeOffset t) => new(t.Year, t.Month, 1);
}
