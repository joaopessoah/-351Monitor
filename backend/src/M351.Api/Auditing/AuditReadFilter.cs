using M351.Api.Services;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace M351.Api.Auditing;

/// <summary>
/// Anota uma ação de LEITURA de dado pessoal cuja auditoria é INCONDICIONAL (DoD 11.3 / Seção
/// 9.5): a ação chama <see cref="AuditReadContext.Record"/> e o <see cref="AuditReadFilter"/>
/// grava a linha em audit_log APÓS a resposta 2xx. Sem o atributo o filter é inerte (não há
/// custo nas demais rotas).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AuditReadAttribute : Attribute;

/// <summary>
/// IAsyncActionFilter que CONSOLIDA a gravação dos audits de leitura incondicional (contrato 2 da
/// F4.7). Roda DEPOIS da ação; só grava quando:
///  (1) a ação está anotada com [AuditRead] E preencheu o AuditReadContext (o controller decide o
///      alvo/detalhe — específico por endpoint), e
///  (2) o resultado é 2xx — um 400/404 NÃO registra acesso (preserva os gates de
///      TenantIsolationTests: probe cross-tenant que leva 404 não deixa rastro de view_report).
///
/// actor_ip: HttpContext.Connection.RemoteIpAddress — que JÁ reflete o X-Forwarded-For do Caddy
/// porque UseForwardedHeaders roda no início do pipeline (Program.cs). Em testes (TestServer) é
/// tipicamente null e a linha grava actor_ip NULL — aceitável; o que importa ao DoD é a LINHA
/// existir com action/target/actor.
///
/// A leitura de GET /audit-logs NÃO é anotada (não se audita — evita recursão; documentado no
/// AuditLogsController).
/// </summary>
public sealed class AuditReadFilter(AuditReadContext context, AuditWriter audit, M351DbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext executing, ActionExecutionDelegate next)
    {
        var executed = await next();

        // só grava em 2xx: 304 (timeline com ETag), 4xx e 5xx não registram acesso a dado
        if (!IsSuccess(executed) || context.Pending is not { } entry)
        {
            return;
        }

        var actorIp = entry.ActorIp ?? executing.HttpContext.Connection.RemoteIpAddress;

        audit.Add(
            entry.TenantId, entry.Action,
            actorUserId: entry.ActorUserId,
            actorIp: actorIp,
            targetType: entry.TargetType,
            targetId: entry.TargetId,
            detailJson: entry.DetailJson);

        await db.SaveChangesAsync(executing.HttpContext.RequestAborted);
    }

    /// <summary>
    /// 2xx = acesso concedido. Resultado MVC com StatusCode null (ex.: OkObjectResult padrão) é
    /// 200; exceção não tratada (Exception != null e não Handled) NÃO grava.
    /// </summary>
    private static bool IsSuccess(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return false;
        }

        var status = executed.Result switch
        {
            IStatusCodeActionResult coded => coded.StatusCode ?? StatusCodes.Status200OK,
            null => StatusCodes.Status200OK,
            _ => StatusCodes.Status200OK,
        };

        return status is >= 200 and < 300;
    }
}
