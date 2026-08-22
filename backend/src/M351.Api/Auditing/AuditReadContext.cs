using System.Net;

namespace M351.Api.Auditing;

/// <summary>
/// Estado por-request (scoped) do audit de LEITURA de dado pessoal. O controller descreve O QUE
/// foi acessado (action/target/detail — lógica que difere por endpoint e por isso fica no
/// controller); o <see cref="AuditReadFilter"/> grava a linha APÓS a resposta 2xx, completando
/// actor (JWT) e actor_ip (HttpContext — honra ForwardedHeaders, Program.cs).
///
/// POR QUE descriptor-no-controller + write-no-filter (decisão F4.7, consolidação do contrato 2):
/// a auditoria INCONDICIONAL de leitura (timeline device/team, reports jornada/usage, app-catalog
/// titles) tinha 5 gravações manuais duplicadas (audit.Add + SaveChanges + actor_ip ausente). O
/// filter unifica o WRITE — uma única linha de gravação, actor_ip SEMPRE preenchido, e só em 2xx
/// (um 404/400 não registra acesso — preserva os gates de TenantIsolationTests). Mas o ALVO/detalhe
/// é específico (device vs team vs app, detail {date} vs {from,to,group_by,device_ids}); mantê-lo no
/// controller evita um filter que reabre a query string e replica a lógica de cada endpoint.
///
/// O caso CONDICIONAL do dashboard/summary (audita só com filtro individual) e TODAS as mutações
/// transacionais (update_category, revoke_*, dsr_*, update_device, export_csv) permanecem MANUAIS —
/// não passam por aqui (ver decisões nos respectivos controllers).
///
/// CHAVE DO EXTRATO DE ACESSOS (F5): quando a leitura é recortada por UM titular, o detail
/// PRECISA levar device_user_id. É por esse campo — não pelo target — que o extrato de acessos
/// entregue ao titular no pacote DSR ("quem consultou meus dados") seleciona as linhas de
/// view_report. Hoje isso vale para GET /dashboard/summary?device_user_id= (gravação manual) e
/// GET /device-users/{id} (por aqui). As demais leituras auditadas são de DISPOSITIVO ou de
/// EQUIPE: numa máquina compartilhada elas não identificam titular individualmente, então NÃO
/// entram no extrato — e o relatório entregue diz isso explicitamente em vez de fingir cobertura
/// total. Nenhuma delas muda de targetType por causa disto (compatibilidade da trilha).
/// </summary>
public sealed class AuditReadContext
{
    /// <summary>Preenchido pelo controller quando a leitura DEVE ser auditada; null = não auditar.</summary>
    public AuditReadEntry? Pending { get; private set; }

    /// <summary>
    /// Registra a leitura a auditar. tenantId/action obrigatórios; target/detail conforme o
    /// endpoint. Idempotente por request (a última chamada vence — um endpoint só audita uma vez).
    /// </summary>
    public void Record(
        Guid tenantId,
        string action,
        Guid actorUserId,
        string? targetType = null,
        Guid? targetId = null,
        string? detailJson = null) =>
        Pending = new AuditReadEntry(tenantId, action, actorUserId, targetType, targetId, detailJson);
}

/// <summary>Descritor imutável de uma leitura a auditar (o actor_ip é resolvido no filter).</summary>
public sealed record AuditReadEntry(
    Guid TenantId,
    string Action,
    Guid ActorUserId,
    string? TargetType,
    Guid? TargetId,
    string? DetailJson)
{
    public IPAddress? ActorIp { get; init; }
}
