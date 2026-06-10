using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Cenários nomeados da Seção 11.2 fim-a-fim: ingestão pela API real (POST /ingest/batch)
/// → IntervalizationService (o mesmo código que o worker agenda) → activity_intervals.
/// Complementa os testes unitários do motor: aqui se prova o encanamento (payload jsonb,
/// cursores, delete-and-rebuild, clock_offset, source_day, partições).
/// </summary>
[Collection(ApiCollection.Name)]
public class IntervalizationPipelineTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    private static DateTimeOffset T(int h, int m, int s = 0) => Base.AddHours(h).AddMinutes(m).AddSeconds(s);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o");

    private async Task<(HttpClient Client, EnrolledDevice Device)> SetupAsync(string hostname)
    {
        var org = await fixture.CreateOrganizationAsync($"Pipeline {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var client = fixture.CreateApiClient();
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: hostname);
        return (client, device);
    }

    /// <summary>
    /// zeroClockOffsets: a ingestão real calcula um skew de poucos ms (EMA) que deslocaria
    /// as asserções de timestamp — zera antes de rodar, exceto no teste do próprio offset.
    /// </summary>
    private async Task RunPipelineAsync(bool zeroClockOffsets = true)
    {
        if (zeroClockOffsets)
        {
            await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        }
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
    }

    private async Task<List<Dictionary<string, object?>>> IntervalsAsync(Guid deviceId)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT state, started_at, ended_at, window_title, data_incomplete, device_user_id, app_id, source_day
            FROM activity_intervals WHERE device_id = @d ORDER BY started_at, state
            """, connection);
        command.Parameters.AddWithValue("d", deviceId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static DateTimeOffset Ts(object? v) => v switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
        _ => throw new InvalidOperationException($"timestamp inesperado: {v?.GetType().Name}"),
    };

    private static DateOnly Day(object? v) => v switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => throw new InvalidOperationException($"date inesperado: {v?.GetType().Name}"),
    };

    // ------------------------------------------------------------ idle-retroativo (11.2)
    [Fact]
    public async Task IdleRetroativo_FimAFim_ActiveFechaEmLastInputAt()
    {
        var (client, device) = await SetupAsync("NB-IDLE-RETRO");
        var factory = new EventFactory();

        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(14, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe", ["window_title"] = "Orcamento_2026.xlsx - Excel",
            }),
            factory.Event("HEARTBEAT", T(14, 8), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(14, 16), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(14, 24), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("IDLE_START", T(14, 31, 40), new Dictionary<string, object?>
            {
                ["last_input_at"] = Iso(T(14, 26, 40)),
            }),
            factory.Event("IDLE_END", T(14, 40), new Dictionary<string, object?> { ["idle_duration_ms"] = 800_000 }),
            factory.Event("LOCK", T(14, 45)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await RunPipelineAsync();
        var intervals = await IntervalsAsync(device.DeviceId);

        var active = intervals.First(r => (string)r["state"]! == "active");
        Assert.Equal(T(14, 0), Ts(active["started_at"]));
        Assert.Equal(T(14, 26, 40), Ts(active["ended_at"])); // N5: NUNCA 14:31:40
        Assert.NotNull(active["device_user_id"]);            // resolvido por (device, windows_sid)
        Assert.NotNull(active["app_id"]);                    // auto-insert no app_catalog

        var idle = intervals.First(r => (string)r["state"]! == "idle");
        Assert.Equal(T(14, 26, 40), Ts(idle["started_at"]));
        Assert.Equal(T(14, 40), Ts(idle["ended_at"]));
    }

    // ------------------------------------------------------------ lock-vence-idle (11.2)
    [Fact]
    public async Task LockVenceIdle_FimAFim()
    {
        var (client, device) = await SetupAsync("NB-LOCK-IDLE");
        var factory = new EventFactory();

        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "word.exe",
            }),
            factory.Event("IDLE_START", T(9, 8), new Dictionary<string, object?>
            {
                ["last_input_at"] = Iso(T(9, 4)),
            }),
            factory.Event("LOCK", T(9, 12)),
            factory.Event("HEARTBEAT", T(9, 18), new Dictionary<string, object?> { ["state"] = "locked" }),
            factory.Event("UNLOCK", T(9, 25)),
            factory.Event("SESSION_END", T(9, 30)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await RunPipelineAsync();
        var intervals = await IntervalsAsync(device.DeviceId);

        var idle = intervals.First(r => (string)r["state"]! == "idle");
        Assert.Equal(T(9, 12), Ts(idle["ended_at"])); // idle termina no LOCK

        var locked = intervals.First(r => (string)r["state"]! == "locked");
        Assert.Equal(T(9, 12), Ts(locked["started_at"]));
        Assert.Equal(T(9, 25), Ts(locked["ended_at"]));

        Assert.Contains(intervals, r => (string)r["state"]! == "active" && Ts(r["started_at"]) == T(9, 25));
    }

    // ------------------------------------------------------------ gap-no-data (11.2)
    [Fact]
    public async Task GapNoData_FimAFim_SemDesligamentoLimpo()
    {
        var (client, device) = await SetupAsync("NB-GAP");
        var factory = new EventFactory();

        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(9, 51), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
            }),
            factory.Event("HEARTBEAT", T(10, 0), new Dictionary<string, object?> { ["state"] = "active" }),
            // buraco de 20 min sem desligamento limpo
            factory.Event("ACTIVE_WINDOW_CHANGED", T(10, 20), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
            }),
            factory.Event("LOCK", T(10, 25)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await RunPipelineAsync();
        var intervals = await IntervalsAsync(device.DeviceId);

        var noData = intervals.First(r => (string)r["state"]! == "no_data");
        Assert.Equal(T(10, 0), Ts(noData["started_at"]));  // fecha NO último evento, sem grace
        Assert.Equal(T(10, 20), Ts(noData["ended_at"]));
        Assert.Null(noData["device_user_id"]);              // intervalo de máquina
    }

    // ------------------------------------------------------------ desligamento-limpo (11.2)
    [Fact]
    public async Task DesligamentoLimpo_FimAFim_OffClean_JamaisNoData()
    {
        var (client, device) = await SetupAsync("NB-SUSPEND");
        var factory = new EventFactory();

        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(11, 55), new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe",
            }),
            factory.Event("SYSTEM_SUSPEND", T(12, 0), windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("SYSTEM_RESUME", T(13, 0), new Dictionary<string, object?>
            {
                ["sleep_duration_ms"] = 3_600_000,
            }, windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("ACTIVE_WINDOW_CHANGED", T(13, 0, 10), new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe",
            }),
            factory.Event("LOCK", T(13, 5)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await RunPipelineAsync();
        var intervals = await IntervalsAsync(device.DeviceId);

        var off = intervals.First(r => (string)r["state"]! == "off_clean");
        Assert.Equal(T(12, 0), Ts(off["started_at"]));
        Assert.Equal(T(13, 0), Ts(off["ended_at"]));
        Assert.DoesNotContain(intervals, r => (string)r["state"]! == "no_data"); // JAMAIS no_data
    }

    // ------------------------------------------------------------ fora-de-ordem (11.2)
    [Fact]
    public async Task ForaDeOrdem_RebuildIdempotente_IntervalosIdenticosAEntregaEmOrdem()
    {
        // mesmo conteúdo para dois devices: X recebe fora de ordem, Y em ordem
        var (client, deviceX) = await SetupAsync("NB-FORA-DE-ORDEM");
        var (_, deviceY) = await SetupAsync("NB-EM-ORDEM");

        Dictionary<string, object?>[] Lote(EventFactory f) =>
        [
            f.Event("ACTIVE_WINDOW_CHANGED", T(8, 0), new Dictionary<string, object?> { ["process_name"] = "a.exe" }),
            f.Event("HEARTBEAT", T(8, 5), new Dictionary<string, object?> { ["state"] = "active" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(8, 10), new Dictionary<string, object?> { ["process_name"] = "b.exe" }),
            f.Event("LOCK", T(8, 15)),
        ];

        var fx = new EventFactory();
        var loteX = Lote(fx);
        // X: segunda metade chega primeiro; pipeline roda; primeira metade chega depois
        var ack1 = await AgentClient.SendBatchAsync(client, deviceX.DeviceToken, loteX.Skip(2));
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        await RunPipelineAsync();
        var ack2 = await AgentClient.SendBatchAsync(client, deviceX.DeviceToken, loteX.Take(2));
        (await AgentClient.ReadAckAsync(ack2)).Dispose();
        await RunPipelineAsync(); // dirty_from retrocedeu; janela cobre tudo; rebuild

        var fy = new EventFactory();
        var ack3 = await AgentClient.SendBatchAsync(client, deviceY.DeviceToken, Lote(fy));
        (await AgentClient.ReadAckAsync(ack3)).Dispose();
        await RunPipelineAsync();

        static List<string> Shape(List<Dictionary<string, object?>> rows) => rows
            .Select(r => $"{r["state"]}|{r["started_at"]:O}|{r["ended_at"]:O}")
            .ToList();

        Assert.Equal(Shape(await IntervalsAsync(deviceY.DeviceId)), Shape(await IntervalsAsync(deviceX.DeviceId)));
    }

    // ------------------------------------------------------------ duplicata (11.2)
    [Fact]
    public async Task Duplicata_MesmoLote2x_IntervalosInalterados()
    {
        var (client, device) = await SetupAsync("NB-DUP");
        var factory = new EventFactory();
        var lote = new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(15, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "code.exe",
            }),
            factory.Event("LOCK", T(15, 9)),
        };

        var ack1 = await AgentClient.SendBatchAsync(client, device.DeviceToken, lote);
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        await RunPipelineAsync();
        var antes = await IntervalsAsync(device.DeviceId);

        var ack2 = await AgentClient.SendBatchAsync(client, device.DeviceToken, lote); // MESMO lote
        using (var doc = await AgentClient.ReadAckAsync(ack2))
            Assert.Equal(2, doc.RootElement.GetProperty("duplicates").GetInt32());
        await RunPipelineAsync();
        var depois = await IntervalsAsync(device.DeviceId);

        Assert.Equal(antes.Count, depois.Count);
        Assert.Equal(
            antes.Select(r => $"{r["state"]}|{r["started_at"]:O}|{r["ended_at"]:O}"),
            depois.Select(r => $"{r["state"]}|{r["started_at"]:O}|{r["ended_at"]:O}"));
    }

    // ------------------------------------------------------------ lacuna-de-seq (11.2)
    [Fact]
    public async Task LacunaDeSeq_MarcaDataIncomplete()
    {
        var (client, device) = await SetupAsync("NB-LACUNA");
        var f1 = new EventFactory(startSeq: 100);
        var lote1 = new[]
        {
            f1.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "a.exe" }),
            f1.Event("HEARTBEAT", T(9, 1), new Dictionary<string, object?> { ["state"] = "active" }),
        };
        var f2 = new EventFactory(startSeq: 105); // seq 102-104 nunca chegam
        var lote2 = new[]
        {
            f2.Event("ACTIVE_WINDOW_CHANGED", T(9, 5), new Dictionary<string, object?> { ["process_name"] = "b.exe" }),
            f2.Event("LOCK", T(9, 9)),
        };

        var ack1 = await AgentClient.SendBatchAsync(client, device.DeviceToken, lote1);
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        var ack2 = await AgentClient.SendBatchAsync(client, device.DeviceToken, lote2);
        (await AgentClient.ReadAckAsync(ack2)).Dispose();

        await RunPipelineAsync();
        var intervals = await IntervalsAsync(device.DeviceId);

        var afetado = intervals.First(r => (string)r["state"]! == "active" && Ts(r["started_at"]) == T(9, 0));
        Assert.True((bool)afetado["data_incomplete"]!);

        var posterior = intervals.First(r => Ts(r["started_at"]) == T(9, 5));
        Assert.False((bool)posterior["data_incomplete"]!);
    }

    // ------------------------------------------------------------ timezone (11.2)
    [Fact]
    public async Task Timezone_IntervaloCruzaMeiaNoiteDoTenant_SourceDayDividido()
    {
        var (client, device) = await SetupAsync("NB-TIMEZONE");
        var factory = new EventFactory();

        // meia-noite de America/Sao_Paulo (GMT-3) = 03:00 UTC; o intervalo 02:30→03:30 UTC cruza
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(2, 30), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
            }),
            factory.Event("HEARTBEAT", T(2, 38), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(2, 46), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(2, 54), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(3, 2), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(3, 10), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("LOCK", T(3, 30)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await RunPipelineAsync();
        var intervals = await IntervalsAsync(device.DeviceId);

        var ativos = intervals.Where(r => (string)r["state"]! == "active").ToList();
        Assert.Equal(2, ativos.Count); // dividido na meia-noite local
        Assert.Equal(T(3, 0), Ts(ativos[0]["ended_at"]));
        Assert.Equal(T(3, 0), Ts(ativos[1]["started_at"]));

        Assert.Equal(Day(ativos[0]["source_day"]).AddDays(1), Day(ativos[1]["source_day"]));
    }

    // ------------------------------------------------------------ clock_offset (relogio, 7.3)
    [Fact]
    public async Task ClockOffset_CorrigeTimestampsDosIntervalos()
    {
        var (client, device) = await SetupAsync("NB-CLOCK");
        var factory = new EventFactory();

        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(10, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "a.exe",
            }),
            factory.Event("LOCK", T(10, 9)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        // relógio do device 2 min atrasado: o servidor corrige somando o offset
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 120000 WHERE id = @d", ("d", device.DeviceId));

        await RunPipelineAsync(zeroClockOffsets: false);
        var intervals = await IntervalsAsync(device.DeviceId);

        var active = intervals.First(r => (string)r["state"]! == "active");
        Assert.Equal(T(10, 2), Ts(active["started_at"]));  // +120 s
        Assert.Equal(T(10, 11), Ts(active["ended_at"]));
    }

    // ------------------------------------------------------------ janela cruzando intervalo existente
    [Fact]
    public async Task JanelaQueCruzaIntervaloExistente_EstendeERebuildaSemErro()
    {
        // Regressão do bug de produção (10/06): quando a janela R cruza um intervalo já
        // materializado, a query do ponto-fixo retorna timestamptz (DateTime no scalar do
        // Npgsql) e a conversão para DateTimeOffset? lançava InvalidCastException — o worker
        // falhava a cada ciclo e a timeline congelava com o cursor sujo para sempre.
        var (client, device) = await SetupAsync("NB-CRUZA-JANELA");
        var f = new EventFactory();

        // lote 1: intervalo LONGO 10:00→11:00 (heartbeats sustentam) — materializado
        var eventos1 = new List<Dictionary<string, object?>>
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(10, 0), new Dictionary<string, object?> { ["process_name"] = "a.exe" }),
        };
        for (var m = 8; m <= 56; m += 8) // < 600 s entre eventos (600 exatos dispararia o gap N7)
            eventos1.Add(f.Event("HEARTBEAT", T(10, m), new Dictionary<string, object?> { ["state"] = "active" }));
        eventos1.Add(f.Event("HEARTBEAT", T(11, 0), new Dictionary<string, object?> { ["state"] = "active" }));
        var ack1 = await AgentClient.SendBatchAsync(client, device.DeviceToken, eventos1);
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        await RunPipelineAsync();

        // lote 2: dirty_from 11:45 → R.start = 10:45, que CRUZA o intervalo 10:00→11:00
        var ack2 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(11, 45), new Dictionary<string, object?> { ["process_name"] = "b.exe" }),
            f.Event("LOCK", T(11, 50)),
        });
        (await AgentClient.ReadAckAsync(ack2)).Dispose();
        await RunPipelineAsync(); // antes do fix: InvalidCastException; cursor ficava sujo

        var dirty = await TestDb.ScalarAsync<DateTime?>(fixture.Database.ConnectionString,
            "SELECT dirty_from FROM ingest_cursors WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Null(dirty); // processou de verdade

        var intervals = await IntervalsAsync(device.DeviceId);
        // o intervalo longo sobrevive INTEIRO (janela estendida até o started_at dele)
        Assert.Contains(intervals, r => (string)r["state"]! == "active"
            && Ts(r["started_at"]) == T(10, 0) && Ts(r["ended_at"]) == T(11, 0));
        // e o gap 11:00→11:45 vira no_data, seguido do active do lote 2
        Assert.Contains(intervals, r => (string)r["state"]! == "no_data" && Ts(r["started_at"]) == T(11, 0));
        Assert.Contains(intervals, r => (string)r["state"]! == "active" && Ts(r["started_at"]) == T(11, 45));
    }

    // ------------------------------------------------------------ cursor
    [Fact]
    public async Task Cursor_LimpoAposProcessar_ResujadoPorNovoLote()
    {
        var (client, device) = await SetupAsync("NB-CURSOR");
        var factory = new EventFactory();

        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(16, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "a.exe",
            }),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await RunPipelineAsync();
        var dirty = await TestDb.ScalarAsync<DateTime?>(fixture.Database.ConnectionString,
            "SELECT dirty_from FROM ingest_cursors WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Null(dirty);

        var ack2 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("HEARTBEAT", T(16, 1), new Dictionary<string, object?> { ["state"] = "active" }),
        });
        (await AgentClient.ReadAckAsync(ack2)).Dispose();
        var dirty2 = await TestDb.ScalarAsync<DateTime?>(fixture.Database.ConnectionString,
            "SELECT dirty_from FROM ingest_cursors WHERE device_id = @d", ("d", device.DeviceId));
        Assert.NotNull(dirty2);
    }
}
