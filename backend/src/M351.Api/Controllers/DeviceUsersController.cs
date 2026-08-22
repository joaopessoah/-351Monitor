using System.Text.Json;
using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Api.Services;
using M351.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// /api/v1/device-users/* (Seção 7.4 linha 801) — os TITULARES do tenant. Um titular é um
/// device_user: o par (dispositivo, usuário do Windows). O modelo é POR DISPOSITIVO — a mesma
/// pessoa em duas máquinas tem dois registros distintos, e nenhuma resposta daqui promete o
/// contrário.
///
/// Para que serve: dar um ENDPOINT PRÓPRIO de titular onde antes só havia o contorno de
/// descobri-los pelo relatório de uso (GET /reports/usage?group_by=device_user), que só enxerga
/// quem teve atividade agregada no período. A busca daqui varre device_users direto: encontra
/// também o titular silencioso (dentro da retenção) que o relatório esconderia. É a fonte da
/// busca de titular da tela de Privacidade (DSR) e da visão individual da pessoa.
///
/// Dapper/NpgsqlDataSource (device_users NÃO tem entidade EF, padrão das daily_*): sem o filtro
/// global por tenant do EF, então tenant_id vai MANUSCRITO em TODO WHERE — inclusive nos
/// lookups do PATCH. Recurso inexistente OU de outro tenant responde 404, jamais 403
/// (Princípio 4).
///
/// Papéis: leitura Viewer+ (a lista de nomes é insumo de qualquer tela de relatório); edição do
/// display_name AdminPlus, com trilha update_device_user (de→para) na MESMA transação da
/// mutação — renomear uma pessoa muda como ela aparece em todos os relatórios.
///
/// VOCABULÁRIO: "titular"/"pessoa". A ordenação é alfabética, JAMAIS por tempo/atividade — esta
/// listagem não é e não pode virar um ranking de pessoas.
/// </summary>
[Route("api/v1/device-users")]
[Authorize] // Viewer+ nas leituras; o PATCH exige AdminPlus
public class DeviceUsersController(NpgsqlDataSource dataSource) : ApiControllerBase
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    private const int MaxDisplayNameLength = 200;

    /// <summary>
    /// UUID zero = lane-máquina (spec linha 652): intervalos SEM sessão de usuário, sintética e
    /// sem linha em device_users. Excluída explicitamente por segurança — se um dia uma linha
    /// com esse id existir, ela não é uma pessoa e não pode aparecer como titular.
    /// </summary>
    private static readonly Guid MachineLane = Guid.Empty;

    /// <summary>Projeção compartilhada pela listagem e pela leitura individual.</summary>
    private const string SelectColumns = """
        SELECT du.id, du.device_id,
               COALESCE(d.display_name, d.hostname) AS device_name,
               du.windows_username, du.display_name,
               du.first_seen_at, du.last_seen_at
        FROM device_users du
        JOIN devices d ON d.id = du.device_id AND d.tenant_id = du.tenant_id
        """;

    /// <summary>
    /// GET /api/v1/device-users?device_id=&amp;q=&amp;page=&amp;page_size= (Viewer+): titulares do
    /// tenant, ordenados pelo nome exibido (display_name, senão windows_username) — ordem
    /// ALFABÉTICA, nunca por tempo de uso. q busca em windows_username e display_name (ILIKE);
    /// device_id restringe a um dispositivo (e responde 404 se o dispositivo for de outro
    /// tenant ou inexistente). page_size default 50, teto 100 (mesma régua dos relatórios).
    ///
    /// Devices archived NÃO são excluídos (decisão documentada): esta é a tela de identidade e
    /// de atendimento a DSR — o titular de uma máquina arquivada continua sendo um titular com
    /// direitos, e escondê-lo aqui inviabilizaria responder ao pedido dele.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "device_id")] Guid? deviceId,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var tenantId = CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // filtro aponta para um recurso: inexistente OU de outro tenant → 404 (mesmo gate do
        // dashboard/summary com device_id de outro tenant)
        if (deviceId is not null)
        {
            var deviceExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM devices WHERE tenant_id = @TenantId AND id = @Id)",
                new { TenantId = tenantId, Id = deviceId }, cancellationToken: ct));
            if (!deviceExists) return NotFoundProblem();
        }

        var args = new
        {
            TenantId = tenantId,
            DeviceId = deviceId,
            Pattern = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%",
            MachineLane,
            Limit = pageSize,
            Offset = (page - 1) * pageSize,
        };

        const string where = """
            WHERE du.tenant_id = @TenantId
              AND du.id <> @MachineLane
              AND (@DeviceId::uuid IS NULL OR du.device_id = @DeviceId)
              AND (@Pattern::text IS NULL
                   OR du.windows_username ILIKE @Pattern
                   OR du.display_name ILIKE @Pattern)
            """;

        var rows = (await connection.QueryAsync<DeviceUserRow>(new CommandDefinition(
            $"""
            {SelectColumns}
            {where}
            ORDER BY lower(COALESCE(du.display_name, du.windows_username)), du.id
            LIMIT @Limit OFFSET @Offset
            """,
            args, cancellationToken: ct))).ToList();

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
            SELECT count(*)::int
            FROM device_users du
            JOIN devices d ON d.id = du.device_id AND d.tenant_id = du.tenant_id
            {where}
            """,
            args, cancellationToken: ct));

        return Ok(new PagedResponse<DeviceUserResponse>(
            rows.Select(ToResponse).ToList(), total, page, pageSize));
    }

    /// <summary>
    /// GET /api/v1/device-users/{id} (Viewer+): um titular. Rota ALÉM do mínimo do contrato,
    /// necessária para a página da pessoa carregar o cabeçalho por deep-link (a listagem não
    /// filtra por id, e o PATCH não serve para leitura). Inexistente/outro tenant → 404.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<DeviceUserRow>(new CommandDefinition(
            $"{SelectColumns}\nWHERE du.tenant_id = @TenantId AND du.id = @Id AND du.id <> @MachineLane",
            new { TenantId = tenantId, Id = id, MachineLane }, cancellationToken: ct));
        if (row is null) return NotFoundProblem();

        return Ok(ToResponse(row));
    }

    /// <summary>
    /// PATCH /api/v1/device-users/{id} (AdminPlus): define o nome amigável do titular.
    /// display_name null/vazio limpa o apelido (as telas voltam ao windows_username). Nome sem
    /// mudança efetiva NÃO gera trilha (mesmo critério do PATCH de devices). Mutação + trilha
    /// update_device_user (de→para) na MESMA transação: o nome jamais muda sem registro.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = AuthConstants.PolicyAdminPlus)]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] DeviceUserPatchRequest? request, CancellationToken ct)
    {
        var newDisplayName = string.IsNullOrWhiteSpace(request?.DisplayName)
            ? null
            : request.DisplayName.Trim();
        if (newDisplayName is { Length: > MaxDisplayNameLength })
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"display_name inválido (máximo {MaxDisplayNameLength} caracteres, ou null para limpar o apelido).");
        }

        var tenantId = CurrentUser.TenantId(User);

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<DeviceUserRow>(new CommandDefinition(
            $"{SelectColumns}\nWHERE du.tenant_id = @TenantId AND du.id = @Id AND du.id <> @MachineLane",
            new { TenantId = tenantId, Id = id, MachineLane }, cancellationToken: ct));
        if (row is null) return NotFoundProblem(); // inexistente OU de outro tenant

        if (row.DisplayName == newDisplayName)
        {
            return Ok(ToResponse(row)); // sem mudança efetiva: nada a gravar, nada a auditar
        }

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE device_users SET display_name = @DisplayName WHERE tenant_id = @TenantId AND id = @Id",
                new { TenantId = tenantId, Id = id, DisplayName = newDisplayName },
                transaction: tx, cancellationToken: ct));

            await AuditWriter.AddInTransactionAsync(connection, tx, tenantId, AuditActions.UpdateDeviceUser,
                actorUserId: CurrentUser.UserId(User),
                actorIp: HttpContext.Connection.RemoteIpAddress,
                targetType: "device_user", targetId: id,
                detailJson: JsonSerializer.Serialize(new
                {
                    device_user_id = id,
                    display_name = new { from = row.DisplayName, to = newDisplayName },
                }), ct: ct);

            await tx.CommitAsync(ct);
        }

        return Ok(ToResponse(row with { DisplayName = newDisplayName }));
    }

    // ------------------------------------------------------------ helpers
    private static DeviceUserResponse ToResponse(DeviceUserRow r) =>
        new(r.Id, r.DeviceId, r.DeviceName, r.WindowsUsername, r.DisplayName, r.FirstSeenAt, r.LastSeenAt);

    private sealed record DeviceUserRow(
        Guid Id,
        Guid DeviceId,
        string DeviceName,
        string WindowsUsername,
        string? DisplayName,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt);
}
