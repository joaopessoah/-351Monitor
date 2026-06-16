using Npgsql;

namespace M351.Infrastructure.Privacy;


/// <summary>
/// Serviço de EXCLUSÃO do titular (F4.5, Seção 9.3 — direito de eliminação, art. 18 V LGPD).
/// É a operação mais sensível do produto: hard delete IRREVERSÍVEL. Vive na Infrastructure
/// (padrão ExportService): trabalha direto sobre NpgsqlConnection/Transaction; o
/// PrivacyController valida confirmation/reason e grava a trilha na MESMA transação.
///
/// REGRA DE EXCLUSÃO (decisão para o silêncio da spec — DEVE SER VALIDADA PELO JURÍDICO):
///
///  1. HARD DELETE dos dados pessoais IDENTIFICÁVEIS do titular:
///     - raw_events do titular: chaveados por (tenant_id, device_id, windows_sid) — é onde
///       vivem window_title e o detalhe bruto. raw_events NÃO tem device_user_id, então o
///       recorte é pelo windows_sid do device_user no device dele;
///     - activity_intervals do titular: por (tenant_id, device_user_id) — é onde vive
///       window_title intervalizado.
///
///  2. ANONIMIZAR a linha device_users do titular, preservando o device_user_id como chave:
///     - windows_username -> marcador neutro (a coluna é NOT NULL, não pode virar NULL);
///     - windows_sid     -> marcador neutro (some o vínculo com a conta Windows real);
///     - display_name    -> "Usuário removido (DSR)".
///     Assim os agregados de equipe JÁ COMPUTADOS (daily_device_summaries / daily_app_usage,
///     chaveados por device_user_id) continuam somando SEM identificar a pessoa — cumpre a
///     regra crítica da Seção 9.3 linha 995 ("a exclusão de titular NÃO apaga agregados de
///     equipe já computados", documentado no DPA).
///
///  3. MANTER os agregados diários (daily_*): não têm PII direto (o nome vinha de
///     device_users, agora anonimizado). O recibo conta quantas linhas foram preservadas.
///
/// Tudo numa ÚNICA transação: ou o titular some por inteiro (com o device_user anonimizado e
/// a trilha gravada), ou nada muda. A trilha dsr_delete NÃO é apagada — é a evidência da
/// operação.
/// </summary>
public sealed class DsrService
{
    /// <summary>windows_username após anonimização (a coluna é NOT NULL — não pode virar NULL).</summary>
    public const string AnonymizedUsername = "[removido-dsr]";

    /// <summary>
    /// Prefixo do windows_sid anonimizado: vem concatenado com o id da linha para permanecer
    /// ÚNICO por device (UNIQUE tenant_id+device_id+windows_sid) ao excluir vários titulares do
    /// mesmo device. O SID real (vínculo com a conta Windows) some.
    /// </summary>
    public const string AnonymizedSidPrefix = "[removido-dsr]-";

    /// <summary>display_name após anonimização — rótulo neutro exibido nos agregados de equipe.</summary>
    public const string AnonymizedDisplayName = "Usuário removido (DSR)";

    /// <summary>
    /// Nota fixa gravada no recibo e na trilha: explicita a decisão (preserva agregados de
    /// equipe) e marca que a regra precisa de validação jurídica.
    /// </summary>
    public const string ReceiptNote =
        "Hard delete irreversível dos dados pessoais identificáveis do titular (raw_events e "
        + "activity_intervals); a linha de identidade foi anonimizada e os agregados de equipe já "
        + "computados foram preservados sem identificar a pessoa (Seção 9.3, documentado no DPA). "
        + "Regra de exclusão sujeita a validação jurídica.";

    /// <summary>Contagens do hard delete — viram o recibo do contrato e o detail da trilha.</summary>
    public sealed record DeleteReceipt(
        int RawEventsDeleted,
        int IntervalsDeleted,
        int DeviceUsersAnonymized,
        int DailyRowsKept);

    /// <summary>
    /// Exclusão de UM titular (device_user) DENTRO de uma transação aberta pelo chamador (o
    /// controller grava a trilha dsr_delete na MESMA transação antes do commit). Pressupõe que
    /// o controller já verificou que o device_user existe no tenant (404 caso contrário).
    /// </summary>
    public async Task<DeleteReceipt> DeleteSubjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId, Guid deviceUserId, CancellationToken ct)
    {
        // SID + device do titular: recorte dos raw_events (que não têm device_user_id)
        Guid deviceId;
        string windowsSid;
        await using (var lookup = new NpgsqlCommand(
            "SELECT device_id, windows_sid FROM device_users WHERE tenant_id = @t AND id = @id", connection, tx))
        {
            lookup.Parameters.AddWithValue("t", tenantId);
            lookup.Parameters.AddWithValue("id", deviceUserId);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"device_user {deviceUserId} não encontrado no tenant {tenantId}.");
            deviceId = reader.GetGuid(0);
            windowsSid = reader.GetString(1);
        }

        return await DeleteSubjectsAsync(connection, tx, tenantId,
            [(deviceUserId, deviceId, windowsSid)], ct);
    }

    /// <summary>
    /// Exclusão de TODOS os titulares de um device (DELETE /privacy/devices/{id}/data): aplica
    /// a mesma regra a cada device_user do device + apaga raw_events do device inteiro.
    /// Pressupõe que o controller já verificou que o device existe no tenant.
    /// </summary>
    public async Task<DeleteReceipt> DeleteDeviceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId, Guid deviceId, CancellationToken ct)
    {
        var subjects = new List<(Guid DeviceUserId, Guid DeviceId, string WindowsSid)>();
        await using (var lookup = new NpgsqlCommand(
            "SELECT id, windows_sid FROM device_users WHERE tenant_id = @t AND device_id = @d", connection, tx))
        {
            lookup.Parameters.AddWithValue("t", tenantId);
            lookup.Parameters.AddWithValue("d", deviceId);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                subjects.Add((reader.GetGuid(0), deviceId, reader.GetString(1)));
        }

        return await DeleteSubjectsAsync(connection, tx, tenantId, subjects, ct);
    }

    /// <summary>
    /// Núcleo: hard delete + anonimização para um conjunto de titulares (1 para subject, N para
    /// device), tudo na transação do chamador.
    /// </summary>
    private static async Task<DeleteReceipt> DeleteSubjectsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId,
        IReadOnlyList<(Guid DeviceUserId, Guid DeviceId, string WindowsSid)> subjects, CancellationToken ct)
    {
        var deviceUserIds = subjects.Select(s => s.DeviceUserId).ToArray();

        // 1a. raw_events do titular: por (tenant_id, device_id, windows_sid). raw_events não
        // tem device_user_id; o recorte é o SID do titular no device dele.
        var rawEventsDeleted = 0;
        foreach (var (_, deviceId, windowsSid) in subjects)
        {
            await using var del = new NpgsqlCommand(
                "DELETE FROM raw_events WHERE tenant_id = @t AND device_id = @d AND windows_sid = @sid",
                connection, tx);
            del.Parameters.AddWithValue("t", tenantId);
            del.Parameters.AddWithValue("d", deviceId);
            del.Parameters.AddWithValue("sid", windowsSid);
            rawEventsDeleted += await del.ExecuteNonQueryAsync(ct);
        }

        // 1b. activity_intervals do titular: por (tenant_id, device_user_id) — window_title vive aqui.
        var intervalsDeleted = await ExecCountAsync(connection, tx,
            "DELETE FROM activity_intervals WHERE tenant_id = @t AND device_user_id = ANY(@ids)",
            tenantId, deviceUserIds, ct);

        // 3. quantas linhas de agregado serão PRESERVADAS (recibo): contadas ANTES de anonimizar
        var dailyRowsKept =
            await ExecCountAsync(connection, tx,
                "SELECT count(*)::int FROM daily_device_summaries WHERE tenant_id = @t AND device_user_id = ANY(@ids)",
                tenantId, deviceUserIds, ct, scalar: true)
            + await ExecCountAsync(connection, tx,
                "SELECT count(*)::int FROM daily_app_usage WHERE tenant_id = @t AND device_user_id = ANY(@ids)",
                tenantId, deviceUserIds, ct, scalar: true);

        // 2. anonimiza a IDENTIDADE preservando o device_user_id (chave dos agregados de equipe).
        // windows_sid recebe um marcador ÚNICO por linha (id sufixado) porque há UNIQUE
        // (tenant_id, device_id, windows_sid): excluir N titulares do MESMO device colidiria se
        // todos virassem o mesmo marcador. O windows_sid real (vínculo com a conta Windows) some.
        var deviceUsersAnonymized = await ExecCountAsync(connection, tx,
            """
            UPDATE device_users
            SET windows_username = @user, windows_sid = @sid || id::text, display_name = @name
            WHERE tenant_id = @t AND id = ANY(@ids)
            """,
            tenantId, deviceUserIds, ct,
            extra: cmd =>
            {
                cmd.Parameters.AddWithValue("user", AnonymizedUsername);
                cmd.Parameters.AddWithValue("sid", AnonymizedSidPrefix);
                cmd.Parameters.AddWithValue("name", AnonymizedDisplayName);
            });

        return new DeleteReceipt(rawEventsDeleted, intervalsDeleted, deviceUsersAnonymized, dailyRowsKept);
    }

    private static async Task<int> ExecCountAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql,
        Guid tenantId, Guid[] deviceUserIds, CancellationToken ct,
        bool scalar = false, Action<NpgsqlCommand>? extra = null)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("ids", deviceUserIds);
        extra?.Invoke(cmd);
        if (scalar)
            return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
