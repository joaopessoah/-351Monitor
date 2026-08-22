// =============================================================================
// Auditoria de acesso (F4.7) — traduções pt-BR das ações e resumo do detalhe
// (jsonb) para a tela /configuracoes/auditoria. As chaves espelham as constantes
// AuditActions do backend (AuditLogEntry.cs): login, invite_accept,
// update_user_role, revoke_key, revoke_device, view_report, update_category,
// export_csv, update_device, publish_agent_release, rollback_agent_release,
// dsr_export, dsr_delete, view_timeline (F4.7), password_reset, mfa_reset,
// mfa_recovery_codes e update_device_user (F5). Ações desconhecidas caem num
// rótulo neutro derivado do próprio verbo — a tela nunca quebra com um verbo novo.
// Vocabulário NEUTRO, sem travessão.
// =============================================================================

/** Rótulo legível pt-BR de cada ação auditada (chave = verbo do backend). */
export const auditActionLabels: Record<string, string> = {
  login: "Login",
  invite_accept: "Aceitou convite",
  update_user_role: "Alterou papel de usuário",
  revoke_key: "Revogou chave de instalação",
  revoke_device: "Revogou dispositivo",
  update_device: "Alterou dispositivo",
  update_category: "Alterou categoria",
  view_report: "Visualizou relatório",
  view_timeline: "Visualizou linha do tempo",
  export_csv: "Exportou CSV",
  dsr_export: "Exportou dados de titular (DSR)",
  dsr_delete: "Excluiu dados de titular (DSR)",
  publish_agent_release: "Publicou versão do agente",
  rollback_agent_release: "Reverteu versão do agente",
  password_reset: "Redefiniu a senha por link de recuperação",
  mfa_reset: "Redefiniu a MFA de um usuário",
  mfa_recovery_codes: "Gerou códigos de recuperação de MFA",
  update_privacy_config: "Alterou configuração de privacidade",
  collection_window_choice: "Escolheu a janela de coleta",
  update_device_user: "Alterou nome de pessoa",
};

/**
 * Opções do filtro por ação (select), na ordem que faz sentido para um
 * operador: leitura de dados pessoais primeiro, depois mutações e
 * autenticação. Reaproveita os rótulos acima.
 */
export const AUDIT_ACTION_FILTER_OPTIONS: { value: string; label: string }[] = [
  "view_timeline",
  "view_report",
  "export_csv",
  "dsr_export",
  "dsr_delete",
  "update_category",
  "update_device",
  "update_device_user",
  "update_privacy_config",
  "collection_window_choice",
  "revoke_device",
  "revoke_key",
  "update_user_role",
  "invite_accept",
  "login",
  "password_reset",
  "mfa_reset",
  "mfa_recovery_codes",
  "publish_agent_release",
  "rollback_agent_release",
].map((value) => ({ value, label: auditActionLabels[value] ?? value }));

/** Rótulo da ação — cai num neutro derivado do verbo para ações desconhecidas. */
export function auditActionLabel(action: string): string {
  const known = auditActionLabels[action];
  if (known !== undefined) return known;
  // Verbo desconhecido (ex.: ação nova do backend): "view_something" -> "View something".
  const words = action.replace(/_/g, " ").trim();
  return words.length > 0 ? words.charAt(0).toUpperCase() + words.slice(1) : action;
}

/** Rótulos pt-BR dos tipos de alvo conhecidos (target_type do jsonb). */
const targetTypeLabels: Record<string, string> = {
  device: "Dispositivo",
  team: "Equipe",
  report: "Relatório",
  user: "Usuário",
  category: "Categoria",
  app: "Aplicativo",
  enrollment_key: "Chave de instalação",
  device_user: "Titular",
  agent_release: "Versão do agente",
  export: "Exportação",
};

/**
 * Alvo resumido: "Dispositivo · 7f3a…". target_type ausente devolve apenas o id
 * (ou um traço quando não há alvo algum). O id longo (UUID) é encurtado.
 */
export function auditTargetSummary(targetType: string | null, targetId: string | null): string {
  const typeLabel = targetType !== null ? (targetTypeLabels[targetType] ?? targetType) : null;
  const id = targetId !== null ? shortenId(targetId) : null;
  if (typeLabel !== null && id !== null) return `${typeLabel} · ${id}`;
  if (typeLabel !== null) return typeLabel;
  if (id !== null) return id;
  return "—";
}

/** UUID/identificador longo encurtado para o primeiro bloco; mantém ids curtos. */
function shortenId(id: string): string {
  if (id.length <= 12) return id;
  const head = id.split("-")[0] ?? id.slice(0, 8);
  return `${head}…`;
}

/**
 * Resumo legível do jsonb de detalhe. Reconhece os campos mais comuns gravados
 * pelas ações de leitura/mutação (período from/to, group_by, device_ids/filtros,
 * date da timeline, from/to de papel) e cai num "chave: valor" genérico para o
 * resto. Devolve "—" quando não há detalhe relevante.
 */
export function auditDetailSummary(detail: Record<string, unknown> | null): string {
  if (detail === null || typeof detail !== "object") return "—";

  const parts: string[] = [];

  // from/to são chaves AMBÍGUAS: relatórios/timeline de equipe/exports gravam um PERÍODO
  // (datas ISO yyyy-MM-dd), mas update_user_role grava a transição de PAPEL ("viewer"→"admin")
  // com as MESMAS chaves. Desambiguar pelo formato: só é período quando ambos são datas ISO;
  // caso contrário (papéis ou outros escalares) é a transição "De X para Y".
  const from = asText(detail.from);
  const to = asText(detail.to);
  if (from !== null && to !== null) {
    if (isIsoDate(from) && isIsoDate(to)) parts.push(`Período ${ddmmFromIsoDate(from)} a ${ddmmFromIsoDate(to)}`);
    else parts.push(`De ${from} para ${to}`);
  } else if (from !== null && isIsoDate(from)) {
    parts.push(`A partir de ${ddmmFromIsoDate(from)}`);
  }

  // Dia único (timeline de dispositivo).
  const date = asText(detail.date);
  if (date !== null) parts.push(`Dia ${ddmmFromIsoDate(date)}`);

  // Agrupamento de relatório de uso.
  const groupBy = asText(detail.group_by);
  if (groupBy !== null) parts.push(`Agrupado por ${groupByLabel(groupBy)}`);

  // Filtro de dispositivos.
  const deviceIds = detail.device_ids;
  if (Array.isArray(deviceIds) && deviceIds.length > 0) {
    parts.push(deviceIds.length === 1 ? "1 dispositivo filtrado" : `${deviceIds.length} dispositivos filtrados`);
  }

  if (parts.length > 0) return parts.join(" · ");

  // Genérico: até 3 pares chave/valor escalares.
  const generic = Object.entries(detail)
    .filter(([, v]) => v !== null && (typeof v === "string" || typeof v === "number" || typeof v === "boolean"))
    .slice(0, 3)
    .map(([k, v]) => `${k}: ${String(v)}`);
  return generic.length > 0 ? generic.join(" · ") : "—";
}

function asText(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

const GROUP_BY_LABELS: Record<string, string> = {
  app: "aplicativo",
  category: "categoria",
  device: "dispositivo",
  device_user: "pessoa",
};

function groupByLabel(value: string): string {
  return GROUP_BY_LABELS[value] ?? value;
}

/** true quando o valor começa com uma data ISO yyyy-MM-dd (distingue período de papel). */
function isIsoDate(value: string): boolean {
  return /^\d{4}-\d{2}-\d{2}/.test(value);
}

/** "2026-06-10" -> "10/06"; devolve o valor cru se não for uma data ISO. */
function ddmmFromIsoDate(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (match === null) return value;
  return `${match[3]}/${match[2]}`;
}
