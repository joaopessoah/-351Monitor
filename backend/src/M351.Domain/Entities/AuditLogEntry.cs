using System.Net;

namespace M351.Domain.Entities;

public static class AuditActions
{
    public const string Login = "login";
    public const string InviteAccept = "invite_accept";
    public const string UpdateUserRole = "update_user_role";
    public const string RevokeKey = "revoke_key";
    public const string RevokeDevice = "revoke_device";

    /// <summary>Visualização de relatório/dashboard FILTRADO por um titular (device ou device_user).</summary>
    public const string ViewReport = "view_report";

    /// <summary>
    /// Visualização da linha do tempo (F3.4, Seção 7.4): GET /timeline/device (alvo device) e
    /// /timeline/team (dado pessoal de VÁRIAS pessoas — target_type "team", sem alvo individual).
    /// Era gravado como string literal "view_timeline" nos controllers (F3.4); F4.7 promove a
    /// constante e CONSOLIDA a gravação no filter de auditoria de leitura (AuditReadFilter).
    /// </summary>
    public const string ViewTimeline = "view_timeline";

    /// <summary>
    /// Mudança de categorização que dispara a reagregação de 30 dias (F3.3): PATCH de categoria
    /// com troca de classification, DELETE de categoria e PUT de mapeamento app→categoria.
    /// </summary>
    public const string UpdateCategory = "update_category";

    /// <summary>Solicitação de export CSV (F3.5): POST /exports, detail {kind, params}.</summary>
    public const string ExportCsv = "export_csv";

    /// <summary>
    /// PATCH /devices/{id} (F3.7): edição de display_name/tags/status, detail com de→para por
    /// campo alterado. Ação FORA da lista de exemplos da spec (que só ilustra revoke_device,
    /// update_category etc.) — adotada pelo padrão verbo_alvo das demais; decisão documentada.
    /// </summary>
    public const string UpdateDevice = "update_device";

    /// <summary>
    /// CLI backoffice publish-agent-release (F4.2): publicação de um novo release do agente no
    /// canal de auto-update, detail {channel, version, min_version, sha256, file_name}. Ação de
    /// operação GLOBAL (sem tenant) — gravada sob o tenant-sentinela Guid.Empty.
    /// </summary>
    public const string PublishAgentRelease = "publish_agent_release";

    /// <summary>
    /// CLI backoffice rollback-agent-release (F4.2): rollback do canal para uma versão já
    /// publicada (move is_current sem redeploy), detail {channel, from_version, to_version}.
    /// </summary>
    public const string RollbackAgentRelease = "rollback_agent_release";

    /// <summary>
    /// Direito de ACESSO/PORTABILIDADE do titular (F4.5, Seção 9.3): solicitação de pacote DSR
    /// — POST /privacy/subjects/{id}/export, /privacy/devices/{id}/export e
    /// /privacy/tenant/full-export. detail {device_user_id} ou {device_id} ou {scope:"tenant"}
    /// conforme o alvo. Insumo da resposta da controladora em 15 dias (art. 19 LGPD).
    /// </summary>
    public const string DsrExport = "dsr_export";

    /// <summary>
    /// Direito de EXCLUSÃO do titular (F4.5, Seção 9.3): hard delete irreversível dos dados
    /// pessoais identificáveis — DELETE /privacy/subjects/{id}/data e
    /// /privacy/devices/{id}/data. detail {device_user_id|device_id, reason, receipt} — o
    /// motivo e o recibo de contagens ficam na trilha (a própria trilha NÃO é apagada: é a
    /// evidência de que a exclusão ocorreu).
    /// </summary>
    public const string DsrDelete = "dsr_delete";

    /// <summary>
    /// PATCH /api/v1/organization (F4.8, Seções 8.8/9.5): edição dos campos de transparência da
    /// org (finalidade_declarada, contato_dpo, data_vigencia e business_hours). detail com de→para
    /// por campo alterado — mudança de config de privacidade exige a trilha (de→para) da Seção 9.5.
    /// Owner/Admin (AdminPlus) editam; Viewer recebe 403.
    /// </summary>
    public const string UpdatePrivacyConfig = "update_privacy_config";

    /// <summary>
    /// POST /auth/reset-password com sucesso (Seção 7.4): senha redefinida via link de
    /// recuperação; todas as sessões (refresh tokens) do usuário são revogadas no ato.
    /// </summary>
    public const string PasswordReset = "password_reset";

    /// <summary>
    /// POST /users/{id}/mfa/reset: recuperação assistida de MFA (usuário perdeu o TOTP e os
    /// recovery codes) — Owner/Admin zera segredo e códigos; o próximo login exige novo setup.
    /// Mexer em Owner exige Owner, espelhando as demais rotas de usuários.
    /// </summary>
    public const string MfaReset = "mfa_reset";

    /// <summary>
    /// POST /auth/mfa/recovery-codes (Seção 7.5): (re)geração dos 10 códigos de recuperação de
    /// MFA do próprio usuário; os códigos anteriores são invalidados. detail {count}.
    /// </summary>
    public const string MfaRecoveryCodes = "mfa_recovery_codes";

    /// <summary>
    /// Escolha explícita da janela de coleta (spec Seção 8.3 passo 1, linha 726): registro
    /// próprio, além do de→para de update_privacy_config — quem decide é a CONTROLADORA e a
    /// decisão precisa ser evidenciável por si só. detail = collection_window escolhida.
    /// </summary>
    public const string CollectionWindowChoice = "collection_window_choice";

    /// <summary>
    /// PATCH /device-users/{id} (Seção 7.4 linha 801): edição do display_name do TITULAR (o
    /// nome amigável que substitui o usuário do Windows nas telas), detail com o de→para.
    /// Renomear uma pessoa muda como ela aparece em todo relatório, então a mudança precisa
    /// de trilha — mesmo padrão verbo_alvo do update_device.
    /// </summary>
    public const string UpdateDeviceUser = "update_device_user";

    /// <summary>
    /// F6 — PATCH /people/{sid}: apelido e mesclagem de PESSOA (identidade por windows_sid).
    /// detail {windows_sid, display_name, merged_into_sid}. Irmão de update_device_user, mas o
    /// alvo é a pessoa do tenant, não o par (dispositivo, usuário).
    /// </summary>
    public const string UpdatePerson = "update_person";

    /// <summary>
    /// F6 — POST /reaggregation: reagregação retroativa sob demanda (decisão 7 do spec).
    /// detail {days, enqueued}. Alvo é a própria organização: a operação reescreve agregados de
    /// TODOS os dispositivos dela.
    /// </summary>
    public const string Reaggregate = "reaggregate";

    /// <summary>
    /// F6 — PATCH /organization com person_alerts_enabled (decisão 5 do spec de 07/09/2026):
    /// a organização LIGA ou DESLIGA as regras de alerta de escopo pessoa. detail
    /// {person_alerts_enabled: de→para}. Registro exigido pela própria decisão ("opt-in da
    /// organização, registrado em auditoria"): quem autorizou olhar o dia de uma pessoa
    /// isolada, e quando, precisa ser evidenciável.
    ///
    /// TODO(F6/Configurações › Alertas): o toggle de interface e a gravação desta ação entram
    /// junto com a tela de Configurações › Alertas, no PATCH /organization — a constante fica
    /// aqui desde já porque a coluna já existe (migration AlertasGestaoF6) e o GET /alerts já
    /// informa quando as regras de pessoa estão desligadas.
    /// </summary>
    public const string UpdateAlertPrefs = "update_alert_prefs";

    /// <summary>
    /// F6 — POST /people/{sid}/notes (decisão 6 do spec de 07/09/2026): registro de uma
    /// ANOTAÇÃO DE PERÍODO ou de uma CONTESTAÇÃO DE CLASSIFICAÇÃO sobre uma pessoa.
    /// detail {note_id, windows_sid, kind, started_at, ended_at, app_id}. O CORPO do texto NÃO
    /// vai para a trilha de propósito: ele já está em person_notes e duplicá-lo numa tabela
    /// append-only de 24 meses de retenção espalharia conteúdo escrito pelo titular por um
    /// lugar de onde ele não pode ser removido a pedido dele (art. 18 LGPD).
    ///
    /// Verbo próprio, não update_person: o alvo é o registro de contexto, não a identidade.
    /// </summary>
    public const string CreatePersonNote = "create_person_note";

    /// <summary>
    /// F6 — PATCH /people/{sid}/notes/{id} (decisão 6): a REVISÃO do gestor — aceitar ou
    /// recusar uma anotação/contestação. detail {note_id, windows_sid, kind, status: de→para}.
    ///
    /// Registro EXIGIDO pela própria decisão ("com revisão do gestor"): quem decidiu, quando e
    /// o que decidiu é o que dá valor ao instrumento — uma contestação recusada sem rastro de
    /// autoria seria pior do que não ter contestação. A resposta ao colaborador (review_note)
    /// fica em person_notes, pela mesma razão do corpo em create_person_note.
    /// </summary>
    public const string ReviewPersonNote = "review_person_note";

    /// <summary>
    /// F7 — /api/v1/teams: criação, edição, exclusão de EQUIPE e mudança de composição
    /// (vínculo de pessoas). detail {team_id, name, changes} ou {team_id, added, removed}.
    ///
    /// Auditado porque a equipe define DUAS coisas com efeito sobre a medição: a jornada
    /// declarada (denominador da capacidade) e a regra de classificação que vale para aquelas
    /// pessoas. Mover alguém de equipe muda os baldes do histórico recente dela — quem moveu, e
    /// quando, precisa ser evidenciável. A mudança de composição também enfileira reagregação.
    /// </summary>
    public const string UpdateTeam = "update_team";

    /// <summary>
    /// F7 — /api/v1/organization/holidays: feriado criado, removido ou o calendário nacional
    /// semeado. detail {holiday_date, name} ou {seeded, years}.
    ///
    /// Feriado NÃO reagrega nada (não muda balde nenhum): ele sai do DENOMINADOR da capacidade
    /// utilizada. Fica auditado porque mexe num número que embasa decisão de contratação.
    /// </summary>
    public const string UpdateHolidays = "update_holidays";
}

/// <summary>Tabela audit_log — append-only, particionada por mês, retenção 24 meses (N13).</summary>
public class AuditLogEntry : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public IPAddress? ActorIp { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }

    /// <summary>JSON (jsonb) com contexto: período consultado, filtros, de→para de config etc.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
