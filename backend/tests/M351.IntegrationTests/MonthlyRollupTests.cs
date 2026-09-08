using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.Infrastructure.Maintenance;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Rollup mensal (item 3 do estudo, "agregados mensais"): monthly_summaries resume
/// daily_device_summaries por mês para que trimestre e ano deixem de ser impossíveis — o teto
/// de 92 dias dos endpoints históricos existe porque varrer a diária num ano inteiro não é
/// leitura de tela.
///
/// O que estes testes travam: o mensal SOMA a diária (não estima), é IDEMPOTENTE (o job roda de
/// hora em hora e não pode duplicar), acompanha a REAGREGAÇÃO retroativa (decisão 7 do spec) e
/// morre na MESMA purga de 24 meses da diária — a página pública de transparência promete
/// "Agregados: 24 meses", e um mensal sobrevivente transformaria a promessa em mentira.
/// </summary>
[Collection(ApiCollection.Name)]
public class MonthlyRollupTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
        await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    private async Task<int> RunRollupAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        return await new MonthlyRollupService(dataSource).RunOnceAsync();
    }

    private async Task MapAppAsync(Guid tenantId, string processName, int classification)
    {
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, @n, @cls)",
            ("c", categoryId), ("t", tenantId), ("n", $"Cat{classification}-{processName}"), ("cls", classification));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = @p
            ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
            """,
            ("t", tenantId), ("c", categoryId), ("p", processName));
    }

    /// <summary>Uma organização com 15 min de um app num dia e 10 min noutro, já agregados.</summary>
    private async Task<Guid> SeedDoisDiasAsync(string prefixo)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefixo} {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var client = fixture.CreateApiClient();
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: $"NB-{prefixo}");

        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 0), new Dictionary<string, object?> { ["process_name"] = "mrl-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 5), new Dictionary<string, object?> { ["process_name"] = "mrl-erp.exe" }),
            f.Event("LOCK", T(-24 + 12, 10)),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 0), new Dictionary<string, object?> { ["process_name"] = "mrl-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 5), new Dictionary<string, object?> { ["process_name"] = "mrl-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 10), new Dictionary<string, object?> { ["process_name"] = "mrl-erp.exe" }),
            f.Event("LOCK", T(12, 15)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        await MapAppAsync(org.Id, "mrl-erp.exe", 1);
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(org.Id);
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        return org.Id;
    }

    // ------------------------------------------------------------------ o mensal soma a diária

    [Fact]
    public async Task Rollup_Soma_A_Diaria_Do_Mes()
    {
        var tenantId = await SeedDoisDiasAsync("MrlSoma");
        await RunRollupAsync();

        // comparação direta: o que o mensal guarda tem de ser, balde a balde, o que a diária
        // soma no mesmo mês. Qualquer estimativa ou arredondamento apareceria aqui.
        var divergencias = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT count(*)::bigint FROM (
                SELECT date_trunc('month', d.summary_date)::date AS mes,
                       sum(d.seconds_active) AS a, sum(d.seconds_idle) AS i,
                       sum(d.seconds_locked) AS l, sum(d.seconds_on) AS o,
                       sum(d.seconds_work_related) AS w, sum(d.seconds_neutral) AS n,
                       sum(d.seconds_not_work_related) AS nw, sum(d.seconds_unclassified) AS u
                FROM daily_device_summaries d WHERE d.tenant_id = @t GROUP BY 1
            ) diaria
            FULL JOIN (
                SELECT m.month_start AS mes,
                       sum(m.seconds_active) AS a, sum(m.seconds_idle) AS i,
                       sum(m.seconds_locked) AS l, sum(m.seconds_on) AS o,
                       sum(m.seconds_work_related) AS w, sum(m.seconds_neutral) AS n,
                       sum(m.seconds_not_work_related) AS nw, sum(m.seconds_unclassified) AS u
                FROM monthly_summaries m WHERE m.tenant_id = @t GROUP BY 1
            ) mensal USING (mes)
            WHERE diaria.a IS DISTINCT FROM mensal.a OR diaria.i IS DISTINCT FROM mensal.i
               OR diaria.l IS DISTINCT FROM mensal.l OR diaria.o IS DISTINCT FROM mensal.o
               OR diaria.w IS DISTINCT FROM mensal.w OR diaria.n IS DISTINCT FROM mensal.n
               OR diaria.nw IS DISTINCT FROM mensal.nw OR diaria.u IS DISTINCT FROM mensal.u
            """,
            ("t", tenantId));

        Assert.Equal(0, divergencias);

        // e há mesmo linha para conferir (um FULL JOIN de dois vazios também daria zero)
        Assert.True(await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*)::bigint FROM monthly_summaries WHERE tenant_id = @t", ("t", tenantId)) > 0);
    }

    [Fact]
    public async Task Rollup_Conta_Os_Dias_Com_Dado_Da_Lane()
    {
        var tenantId = await SeedDoisDiasAsync("MrlDias");
        await RunRollupAsync();

        // dois dias com tempo ligado, e os dois caem no mesmo mês ou em meses vizinhos:
        // a soma de days_with_data no tenant tem de ser 2 de qualquer jeito
        var dias = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT COALESCE(sum(days_with_data), 0)::bigint FROM monthly_summaries
            WHERE tenant_id = @t AND device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
            """,
            ("t", tenantId));

        Assert.Equal(2, dias);
    }

    // ------------------------------------------------------------------ idempotência

    [Fact]
    public async Task Rollup_E_Idempotente()
    {
        var tenantId = await SeedDoisDiasAsync("MrlIdem");
        await RunRollupAsync();

        var antes = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*)::bigint FROM monthly_summaries WHERE tenant_id = @t", ("t", tenantId));
        var somaAntes = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_active), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t",
            ("t", tenantId));

        await RunRollupAsync();
        await RunRollupAsync();

        Assert.Equal(antes, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*)::bigint FROM monthly_summaries WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(somaAntes, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_active), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t",
            ("t", tenantId)));
    }

    [Fact]
    public async Task Segunda_Passada_Nao_Revisita_Mes_Antigo()
    {
        var tenantId = await SeedDoisDiasAsync("MrlMarca");

        // um mês ANTIGO, com carimbo bem fora da margem de segurança
        var device = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM devices WHERE tenant_id = @t LIMIT 1", ("t", tenantId));
        var mesAntigo = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-6);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id, seconds_on, computed_at)
            VALUES (@t, @d, @dev, '00000000-0000-0000-0000-000000000000'::uuid, 3600,
                    now() - INTERVAL '2 days')
            """,
            ("t", tenantId), ("d", mesAntigo), ("dev", device));

        var primeira = await RunRollupAsync();
        Assert.True(primeira >= 2, $"a primeira passada tinha de pegar o mês antigo e o recente; pegou {primeira}");

        // A MARCA-D'ÁGUA É O QUE IMPEDE o job de hora em hora de revarrer 24 meses de diária a
        // cada ciclo. A segunda passada pode revisitar o que foi escrito dentro da margem de
        // segurança (SafetyMargin), mas o mês de seis meses atrás NÃO pode voltar.
        var segunda = await RunRollupAsync();
        Assert.True(segunda < primeira,
            $"a segunda passada tinha de revisitar menos meses que a primeira: {segunda} vs {primeira}");

        // e o mês antigo continua correto depois de tudo
        Assert.Equal(3600, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_on), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t AND month_start = @m",
            ("t", tenantId), ("m", mesAntigo)));
    }

    // ------------------------------------------------------------------ reagregação retroativa

    [Fact]
    public async Task Reagregacao_Retroativa_Reflete_No_Mensal()
    {
        var tenantId = await SeedDoisDiasAsync("MrlReag");
        await RunRollupAsync();

        var produtivoAntes = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_work_related), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t",
            ("t", tenantId));
        Assert.True(produtivoAntes > 0, "o cenário começa com tempo produtivo");

        // o gestor reclassifica o app de produtivo para IMPRODUTIVO e recalcula o histórico
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            UPDATE categories SET classification = -1
            WHERE tenant_id = @t AND id IN (SELECT category_id FROM tenant_app_categories WHERE tenant_id = @t)
            """,
            ("t", tenantId));
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(tenantId);
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        await RunRollupAsync();

        Assert.Equal(0, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_work_related), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t",
            ("t", tenantId)));
        Assert.Equal(produtivoAntes, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_not_work_related), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t",
            ("t", tenantId)));
    }

    [Fact]
    public async Task Diaria_Escrita_Com_Carimbo_Levemente_Anterior_Nao_E_Perdida()
    {
        var tenantId = await SeedDoisDiasAsync("MrlCorr");
        await RunRollupAsync();

        var antes = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_active), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t",
            ("t", tenantId));

        // A CORRIDA QUE A MARGEM FECHA: computed_at é now(), que no Postgres é o INÍCIO da
        // transação. Uma agregação diária que começou antes de o rollup ler max(computed_at) e
        // só commitou depois grava um carimbo ANTERIOR à marca — e sem margem de segurança essa
        // linha ficaria para sempre fora do mensal, em silêncio. Simulamos exatamente isso:
        // muda a diária carimbando um minuto no passado.
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            UPDATE daily_device_summaries
            SET seconds_active = seconds_active + 600,
                computed_at = now() - INTERVAL '1 minute'
            WHERE tenant_id = @t
            """,
            ("t", tenantId));

        await RunRollupAsync();

        var depois = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT COALESCE(sum(seconds_active), 0)::bigint FROM monthly_summaries WHERE tenant_id = @t",
            ("t", tenantId));

        Assert.True(depois > antes,
            $"o mensal tinha de acompanhar a diária reescrita: antes {antes}, depois {depois}");
    }

    // ------------------------------------------------------------------ retenção

    [Fact]
    public async Task Purga_Remove_O_Mensal_Alem_De_24_Meses_E_Preserva_O_Recente()
    {
        var org = await fixture.CreateOrganizationAsync($"MrlPur {Guid.NewGuid():N}"[..20]);
        var antigo = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-30);
        var recente = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

        foreach (var mes in new[] { antigo, recente })
        {
            await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                """
                INSERT INTO monthly_summaries (tenant_id, month_start, device_id, device_user_id, computed_at)
                VALUES (@t, @m, @d, '00000000-0000-0000-0000-000000000000'::uuid, now())
                """,
                ("t", org.Id), ("m", mes), ("d", Uuid7.NewUuid7()));
        }

        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new RetentionPurgeService(ds).RunOnceAsync();
        }

        Assert.Equal(0, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*)::bigint FROM monthly_summaries WHERE tenant_id = @t AND month_start = @m",
            ("t", org.Id), ("m", antigo)));
        Assert.Equal(1, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*)::bigint FROM monthly_summaries WHERE tenant_id = @t AND month_start = @m",
            ("t", org.Id), ("m", recente)));
    }
}
