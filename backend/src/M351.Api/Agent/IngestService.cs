using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using M351.Api.Contracts;
using M351.Domain;
using M351.Domain.Entities;
using Npgsql;

namespace M351.Api.Agent;

/// <summary>
/// POST /api/v1/ingest/batch — caminho quente (Seções 5.4–5.6 e 7.1), todo em Dapper/Npgsql:
/// validação por evento (janela N9; tipo desconhecido = ignorar + métrica), idempotência por
/// INSERT multi-row ON CONFLICT DO NOTHING, skew de relógio servidor (received_at − sent_at,
/// média móvel ~5 lotes), last_seen_at/seq_max/tz, device_users vistos, projeção
/// device_current_state e ack pós-commit com config/comandos.
/// </summary>
public sealed class IngestService(
    NpgsqlDataSource dataSource,
    AgentConfigService configService,
    RawEventPartitionManager partitions,
    TimeProvider clock,
    ILogger<IngestService> logger)
{
    private const int WindowTitleMaxLength = 256; // revalidação servidor (Seção 5.6)
    private static readonly TimeSpan MaxPast = TimeSpan.FromDays(14);     // N9
    private static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(5); // N9

    private static readonly string[] HeartbeatStates = ["active", "idle", "locked", "no_session"];

    public async Task<IngestAckResponse> ProcessAsync(
        Guid tenantId, Guid deviceId, IngestBatch batch, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rejected = new List<RejectedEventDto>();
        var valid = new List<ParsedEvent>();
        var unknownCount = 0;

        foreach (var element in batch.Events)
        {
            switch (ParseEvent(element, now, out var parsed, out var rejection))
            {
                case ParseOutcome.Valid:
                    valid.Add(parsed!);
                    break;
                case ParseOutcome.Rejected:
                    rejected.Add(rejection!);
                    break;
                case ParseOutcome.UnknownType:
                    unknownCount++;
                    break;
            }
        }

        if (unknownCount > 0)
        {
            IngestMetrics.UnknownTypeTotal.Add(unknownCount);
            logger.LogWarning(
                "Ingestão: {Count} evento(s) de tipo desconhecido ignorado(s) no lote do device {DeviceId}",
                unknownCount, deviceId);
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // partições diárias dos dias tocados — DDL idempotente FORA da transação do lote
        if (valid.Count > 0)
        {
            await partitions.EnsureDaysAsync(
                connection, RawEventPartitionManager.DaysFor(valid.Select(v => v.OccurredAt)), ct);
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);

        // serializa lotes concorrentes do mesmo device (seq_max/projeção consistentes)
        var before = await connection.QuerySingleAsync<(long SeqMax, DateTime? LastSeenAt)>(
            new CommandDefinition(
                "SELECT seq_max, last_seen_at FROM devices WHERE id = @DeviceId AND tenant_id = @TenantId FOR UPDATE",
                new { DeviceId = deviceId, TenantId = tenantId }, transaction, cancellationToken: ct));

        var inserted = valid.Count == 0
            ? 0
            : await InsertRawEventsAsync(connection, transaction, tenantId, deviceId, valid, now, ct);
        var duplicates = valid.Count - inserted;

        await UpdateDeviceAsync(connection, transaction, tenantId, deviceId, batch, valid, now, ct);
        await UpsertDeviceUsersAsync(connection, transaction, tenantId, deviceId, valid, ct);
        await UpsertCurrentStateAsync(connection, transaction, tenantId, deviceId, valid, before.SeqMax, now, ct);

        if (valid.Count > 0)
        {
            // cursor dirty para o pipeline de intervalização (Seção 7.3, consumido na F2)
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ingest_cursors (tenant_id, device_id, processed_until, dirty_from, updated_at)
                VALUES (@TenantId, @DeviceId, to_timestamp(0), @DirtyFrom, @Now)
                ON CONFLICT (device_id) DO UPDATE SET
                  dirty_from = LEAST(COALESCE(ingest_cursors.dirty_from, EXCLUDED.dirty_from), EXCLUDED.dirty_from),
                  updated_at = EXCLUDED.updated_at
                """,
                new { TenantId = tenantId, DeviceId = deviceId, DirtyFrom = valid.Min(v => v.OccurredAt), Now = now },
                transaction, cancellationToken: ct));
        }

        var (config, slug) = await LoadConfigAsync(connection, transaction, tenantId, ct);
        var commands = await TakePendingCommandsAsync(connection, transaction, tenantId, deviceId, now, ct);

        // ack somente APÓS commit (perda pós-ack = 0 — Princípio 6)
        await transaction.CommitAsync(ct);

        IngestMetrics.EventsTotal.Add(valid.Count);
        IngestMetrics.DuplicatesTotal.Add(duplicates);
        foreach (var group in rejected.GroupBy(r => r.Reason))
        {
            IngestMetrics.RejectedTotal.Add(group.Count(), new KeyValuePair<string, object?>("reason", group.Key));
        }

        var configOutdated = batch.ConfigVersion is null || batch.ConfigVersion.Value != config.ConfigVersion;

        return new IngestAckResponse(
            Accepted: inserted,
            Duplicates: duplicates,
            Rejected: rejected,
            ServerTime: now,
            ConfigVersion: config.ConfigVersion,
            Config: configOutdated ? configService.Build(config, slug) : null,
            Commands: commands);
    }

    // ----- parsing/validação por evento -----

    private enum ParseOutcome
    {
        Valid,
        Rejected,
        UnknownType,
    }

    private static ParseOutcome ParseEvent(
        JsonElement element, DateTimeOffset now, out ParsedEvent? parsed, out RejectedEventDto? rejection)
    {
        parsed = null;
        rejection = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            rejection = new RejectedEventDto(string.Empty, RejectReasons.InvalidEvent);
            return ParseOutcome.Rejected;
        }

        var eventIdRaw = GetString(element, "event_id") ?? string.Empty;
        if (!Guid.TryParse(eventIdRaw, out var eventId)
            || GetString(element, "type") is not { Length: > 0 } type
            || !TryGetInt64(element, "seq", out var seq)
            || !TryGetTimestamp(element, "occurred_at", out var occurredAt))
        {
            rejection = new RejectedEventDto(eventIdRaw, RejectReasons.InvalidEvent);
            return ParseOutcome.Rejected;
        }

        // tipo desconhecido: ignorar + métrica — JAMAIS rejeitar o lote (Seção 5.3)
        if (!EventTypes.Known.Contains(type))
        {
            return ParseOutcome.UnknownType;
        }

        if (occurredAt < now - MaxPast)
        {
            rejection = new RejectedEventDto(eventIdRaw, RejectReasons.TimestampTooOld);
            return ParseOutcome.Rejected;
        }

        if (occurredAt > now + MaxFuture)
        {
            rejection = new RejectedEventDto(eventIdRaw, RejectReasons.TimestampInFuture);
            return ParseOutcome.Rejected;
        }

        JsonElement data = default;
        var hasData = element.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;

        string? processName = null;
        string? windowTitle = null;
        DateTimeOffset? lastInputAt = null;
        string? heartbeatState = null;
        int? appliedConfigVersion = null;

        if (hasData)
        {
            processName = (GetString(data, "process_name") ?? GetString(data, "foreground_process"))
                ?.Trim().ToLowerInvariant();
            windowTitle = GetString(data, "window_title");
            if (windowTitle is { Length: > WindowTitleMaxLength })
            {
                windowTitle = windowTitle[..WindowTitleMaxLength];
            }

            if (type == EventTypes.IdleStart && TryGetTimestamp(data, "last_input_at", out var lia))
            {
                lastInputAt = lia;
            }

            if (type == EventTypes.Heartbeat
                && GetString(data, "state") is { } hb && HeartbeatStates.Contains(hb))
            {
                heartbeatState = hb;
            }

            if (type == EventTypes.PolicyApplied && TryGetInt64(data, "config_version", out var cv))
            {
                appliedConfigVersion = (int)cv;
            }
        }

        parsed = new ParsedEvent(
            EventId: eventId,
            Seq: seq,
            Type: type,
            OccurredAt: occurredAt,
            TzOffsetMin: TryGetInt64(element, "tz_offset_min", out var tz) ? (int)tz : null,
            MonoMs: TryGetInt64(element, "mono_ms", out var mono) ? mono : null,
            BootId: Guid.TryParse(GetString(element, "boot_id"), out var bootId) ? bootId : null,
            SessionId: TryGetInt64(element, "session_id", out var session) ? (int)session : null,
            WindowsSid: GetString(element, "windows_sid"),
            WindowsUser: GetString(element, "windows_user"),
            PayloadJson: hasData ? data.GetRawText() : "{}",
            ProcessName: processName,
            WindowTitle: windowTitle,
            LastInputAt: lastInputAt,
            HeartbeatState: heartbeatState,
            AppliedConfigVersion: appliedConfigVersion);

        return ParseOutcome.Valid;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out value);
    }

    private static bool TryGetTimestamp(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (element.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(prop.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedValue))
        {
            value = parsedValue.ToUniversalTime();
            return true;
        }

        return false;
    }

    // ----- escrita -----

    private static async Task<int> InsertRawEventsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, Guid deviceId, IReadOnlyList<ParsedEvent> events, DateTimeOffset now, CancellationToken ct)
    {
        var sql = new StringBuilder(
            """
            INSERT INTO raw_events
              (tenant_id, device_id, event_id, seq, occurred_at, event_type, tz_offset_min, mono_ms, boot_id,
               session_id, windows_sid, windows_username, process_name, window_title, payload, received_at)
            VALUES
            """);

        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId, DbType.Guid);
        parameters.Add("DeviceId", deviceId, DbType.Guid);
        parameters.Add("Now", now, DbType.DateTimeOffset);

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            sql.Append(i == 0 ? "\n" : ",\n");
            sql.Append(CultureInfo.InvariantCulture,
                $"(@TenantId, @DeviceId, @e{i}_id, @e{i}_seq, @e{i}_at, @e{i}_type, @e{i}_tz, @e{i}_mono, @e{i}_boot, @e{i}_sess, @e{i}_sid, @e{i}_user, @e{i}_proc, @e{i}_title, CAST(@e{i}_payload AS jsonb), @Now)");

            parameters.Add($"e{i}_id", e.EventId, DbType.Guid);
            parameters.Add($"e{i}_seq", e.Seq, DbType.Int64);
            parameters.Add($"e{i}_at", e.OccurredAt, DbType.DateTimeOffset);
            parameters.Add($"e{i}_type", e.Type, DbType.String);
            parameters.Add($"e{i}_tz", e.TzOffsetMin, DbType.Int32);
            parameters.Add($"e{i}_mono", e.MonoMs, DbType.Int64);
            parameters.Add($"e{i}_boot", e.BootId, DbType.Guid);
            parameters.Add($"e{i}_sess", e.SessionId, DbType.Int32);
            parameters.Add($"e{i}_sid", e.WindowsSid, DbType.String);
            parameters.Add($"e{i}_user", e.WindowsUser, DbType.String);
            parameters.Add($"e{i}_proc", e.ProcessName, DbType.String);
            parameters.Add($"e{i}_title", e.WindowTitle, DbType.String);
            parameters.Add($"e{i}_payload", e.PayloadJson, DbType.String);
        }

        // idempotência por event_id (Seção 5.6): reenvio após timeout nunca duplica
        sql.Append("\nON CONFLICT (device_id, event_id, occurred_at) DO NOTHING");

        return await connection.ExecuteAsync(
            new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct));
    }

    private static async Task UpdateDeviceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, Guid deviceId, IngestBatch batch, IReadOnlyList<ParsedEvent> valid,
        DateTimeOffset now, CancellationToken ct)
    {
        // skew calculado no servidor: received_at − sent_at, média móvel ~5 lotes (Seção 5.4)
        long? skewMs = batch.SentAt is { } sentAt ? (long)(now - sentAt).TotalMilliseconds : null;
        var lastBySeq = valid.Count > 0 ? valid.MaxBy(v => v.Seq) : null;
        var noticeAckedAt = valid
            .Where(v => v.Type == EventTypes.NoticeAck)
            .Select(v => (DateTimeOffset?)v.OccurredAt)
            .Max();
        var appliedConfigVersion = valid
            .Where(v => v.Type == EventTypes.PolicyApplied && v.AppliedConfigVersion is not null)
            .Select(v => v.AppliedConfigVersion)
            .Max();

        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId, DbType.Guid);
        parameters.Add("DeviceId", deviceId, DbType.Guid);
        parameters.Add("Now", now, DbType.DateTimeOffset);
        parameters.Add("AgentVersion", batch.AgentVersion, DbType.String);
        parameters.Add("MaxSeq", valid.Count > 0 ? valid.Max(v => v.Seq) : 0L, DbType.Int64);
        parameters.Add("Tz", lastBySeq?.TzOffsetMin, DbType.Int32);
        parameters.Add("NoticeAt", noticeAckedAt, DbType.DateTimeOffset);
        parameters.Add("AppliedConfigVersion", appliedConfigVersion, DbType.Int32);
        parameters.Add("Skew", skewMs, DbType.Int64);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE devices SET
              last_seen_at = @Now,
              agent_version = COALESCE(@AgentVersion, agent_version),
              seq_max = GREATEST(seq_max, @MaxSeq),
              tz_offset_min = COALESCE(@Tz, tz_offset_min),
              notice_acked_at = COALESCE(@NoticeAt, notice_acked_at),
              config_version = COALESCE(@AppliedConfigVersion, config_version),
              clock_offset_ms = CASE
                WHEN @Skew IS NULL THEN clock_offset_ms
                WHEN last_seen_at IS NULL THEN @Skew
                ELSE (clock_offset_ms * 4 + @Skew) / 5
              END
            WHERE id = @DeviceId AND tenant_id = @TenantId
            """,
            parameters, transaction, cancellationToken: ct));
    }

    private static async Task UpsertDeviceUsersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, Guid deviceId, IReadOnlyList<ParsedEvent> valid, CancellationToken ct)
    {
        var seen = valid
            .Where(v => v.WindowsSid is { Length: > 0 } && v.WindowsUser is { Length: > 0 })
            .GroupBy(v => v.WindowsSid!)
            .Select(g => new
            {
                TenantId = tenantId,
                DeviceId = deviceId,
                Id = Uuid7.NewUuid7(),
                Sid = g.Key,
                Username = g.OrderBy(v => v.Seq).Last().WindowsUser!,
                FirstSeen = g.Min(v => v.OccurredAt),
                LastSeen = g.Max(v => v.OccurredAt),
            })
            .ToList();

        if (seen.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO device_users (id, tenant_id, device_id, windows_sid, windows_username, first_seen_at, last_seen_at)
            VALUES (@Id, @TenantId, @DeviceId, @Sid, @Username, @FirstSeen, @LastSeen)
            ON CONFLICT (tenant_id, device_id, windows_sid) DO UPDATE SET
              windows_username = EXCLUDED.windows_username,
              first_seen_at = LEAST(device_users.first_seen_at, EXCLUDED.first_seen_at),
              last_seen_at = GREATEST(device_users.last_seen_at, EXCLUDED.last_seen_at)
            """,
            seen, transaction, cancellationToken: ct));
    }

    private static async Task UpsertCurrentStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, Guid deviceId, IReadOnlyList<ParsedEvent> valid, long seqMaxBefore,
        DateTimeOffset now, CancellationToken ct)
    {
        // só eventos NOVOS (seq acima do já visto), em ordem de seq — reenvio de lote antigo
        // (duplicatas) não regride a projeção
        var toApply = valid.Where(v => v.Seq > seqMaxBefore).OrderBy(v => v.Seq).ToList();

        var row = await connection.QuerySingleOrDefaultAsync<CurrentStateRow>(new CommandDefinition(
            """
            SELECT state, windows_sid, windows_username, foreground_process, foreground_title, state_since, app_since
            FROM device_current_state
            WHERE device_id = @DeviceId AND tenant_id = @TenantId
            """,
            new { DeviceId = deviceId, TenantId = tenantId }, transaction, cancellationToken: ct));

        if (toApply.Count == 0)
        {
            // lote vazio/sem evento novo = keep-alive: só refresca o último contato (Seção 5.4)
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO device_current_state
                  (tenant_id, device_id, state, windows_sid, windows_username, foreground_process,
                   foreground_title, state_since, app_since, last_contact_at, updated_at)
                VALUES (@TenantId, @DeviceId, 'no_data', NULL, NULL, NULL, NULL, NULL, NULL, @Now, @Now)
                ON CONFLICT (device_id) DO UPDATE SET
                  last_contact_at = EXCLUDED.last_contact_at,
                  updated_at = EXCLUDED.updated_at
                """,
                new { TenantId = tenantId, DeviceId = deviceId, Now = now }, transaction, cancellationToken: ct));
            return;
        }

        row ??= new CurrentStateRow();
        foreach (var e in toApply)
        {
            CurrentStateProjector.Apply(row, e);
        }

        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId, DbType.Guid);
        parameters.Add("DeviceId", deviceId, DbType.Guid);
        parameters.Add("State", row.State, DbType.String);
        parameters.Add("WindowsSid", row.WindowsSid, DbType.String);
        parameters.Add("WindowsUsername", row.WindowsUsername, DbType.String);
        parameters.Add("ForegroundProcess", row.ForegroundProcess, DbType.String);
        parameters.Add("ForegroundTitle", row.ForegroundTitle, DbType.String);
        parameters.Add("StateSince", row.StateSince, DbType.DateTimeOffset);
        parameters.Add("AppSince", row.AppSince, DbType.DateTimeOffset);
        parameters.Add("Now", now, DbType.DateTimeOffset);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO device_current_state
              (tenant_id, device_id, state, windows_sid, windows_username, foreground_process,
               foreground_title, state_since, app_since, last_contact_at, updated_at)
            VALUES (@TenantId, @DeviceId, @State, @WindowsSid, @WindowsUsername, @ForegroundProcess,
                    @ForegroundTitle, @StateSince, @AppSince, @Now, @Now)
            ON CONFLICT (device_id) DO UPDATE SET
              state = EXCLUDED.state,
              windows_sid = EXCLUDED.windows_sid,
              windows_username = EXCLUDED.windows_username,
              foreground_process = EXCLUDED.foreground_process,
              foreground_title = EXCLUDED.foreground_title,
              state_since = EXCLUDED.state_since,
              app_since = EXCLUDED.app_since,
              last_contact_at = EXCLUDED.last_contact_at,
              updated_at = EXCLUDED.updated_at
            """,
            parameters, transaction, cancellationToken: ct));
    }

    // ----- config e comandos do ack -----

    private async Task<(TenantAgentConfig Config, string Slug)> LoadConfigAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, CancellationToken ct)
    {
        var slug = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT slug FROM organizations WHERE id = @TenantId",
            new { TenantId = tenantId }, transaction, cancellationToken: ct)) ?? string.Empty;

        var config = await connection.QuerySingleOrDefaultAsync<TenantAgentConfig>(new CommandDefinition(
            """
            SELECT tenant_id, config_version, heartbeat_sec, active_window_poll_sec, idle_threshold_sec,
                   window_title_policy, masked_patterns, ignored_processes, collection_window
            FROM tenant_agent_configs
            WHERE tenant_id = @TenantId
            """,
            new { TenantId = tenantId }, transaction, cancellationToken: ct));

        return (config ?? new TenantAgentConfig { TenantId = tenantId }, slug);
    }

    private static async Task<IReadOnlyList<DeviceCommandDto>> TakePendingCommandsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var pending = (await connection.QueryAsync<(Guid Id, string Type, string Payload)>(new CommandDefinition(
            """
            SELECT id, type, payload FROM device_commands
            WHERE tenant_id = @TenantId AND device_id = @DeviceId AND delivered_at IS NULL
            ORDER BY created_at
            FOR UPDATE
            """,
            new { TenantId = tenantId, DeviceId = deviceId }, transaction, cancellationToken: ct))).ToList();

        if (pending.Count == 0)
        {
            return [];
        }

        // o servidor marca a entrega ao incluir no ack (Seção 5.5); reentrega é idempotente
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE device_commands SET delivered_at = @Now WHERE tenant_id = @TenantId AND id = ANY(@Ids)",
            new { Now = now, TenantId = tenantId, Ids = pending.Select(p => p.Id).ToArray() },
            transaction, cancellationToken: ct));

        return pending
            .Select(p => new DeviceCommandDto(p.Id, p.Type, ParsePayload(p.Payload)))
            .ToList();
    }

    private static JsonElement ParsePayload(string? payloadJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        return doc.RootElement.Clone();
    }
}
