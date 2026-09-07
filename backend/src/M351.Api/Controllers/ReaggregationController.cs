using System.Text.Json;
using M351.Api.Auth;
using M351.Api.Services;
using M351.Domain.Entities;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace M351.Api.Controllers;

/// <summary>
/// POST /api/v1/reaggregation (F6, decisão 7 do spec de 07/09/2026): reagregação retroativa SOB
/// DEMANDA. Enfileira em dirty_days todos os (device, dia) com intervalos na janela pedida, e o
/// DailyAggregationService recomputa os baldes com a classificação VIGENTE.
///
/// Por que existe: a curadoria de categorias reagrega só 30 dias, então relatório longo mistura
/// classificação antiga e nova, e o balde seconds_unclassified (introduzido nesta fase) nasce
/// zerado no histórico. Corrigir isso é ação explícita do admin, com aviso de custo na tela —
/// jamais um UPDATE cego em migration.
///
/// Assíncrono de propósito: 12 meses de uma frota grande são milhares de pares, que o worker
/// drena em ciclos de 15 min. A resposta é 202 com quantos pares entraram na fila.
/// </summary>
[Route("api/v1/reaggregation")]
[Authorize(Policy = AuthConstants.PolicyAdminPlus)]
public class ReaggregationController(
    ReaggregationRequester requester, M351DbContext db, AuditWriter audit) : ApiControllerBase
{
    /// <summary>days ausente usa a janela default da curadoria (30 dias).</summary>
    public sealed record ReaggregationRequest(int? Days);

    /// <summary>Quantos pares (device, dia) entraram na fila e a janela efetivamente usada.</summary>
    public sealed record ReaggregationResponse(int Enqueued, int Days);

    [HttpPost]
    public async Task<IActionResult> Enqueue([FromBody] ReaggregationRequest? request, CancellationToken ct)
    {
        var days = request?.Days ?? ReaggregationRequester.WindowDays;
        if (days < 1 || days > ReaggregationRequester.MaxWindowDays)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Janela inválida: informe de 1 a {ReaggregationRequester.MaxWindowDays} dias.");
        }

        var tenantId = CurrentUser.TenantId(User);
        var enqueued = await requester.RequestAsync(tenantId, days, ct);

        audit.Add(tenantId, AuditActions.Reaggregate,
            actorUserId: CurrentUser.UserId(User),
            actorIp: HttpContext.Connection.RemoteIpAddress,
            targetType: "organization", targetId: tenantId,
            detailJson: JsonSerializer.Serialize(new { days, enqueued }));
        await db.SaveChangesAsync(ct);

        return Accepted(new ReaggregationResponse(enqueued, days));
    }
}
