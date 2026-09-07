namespace M351.Api.Contracts;

// =============================================================================
// /api/v1/people/{sid}/notes (F6, decisão 6 do spec de 07/09/2026): ANOTAÇÃO DE
// PERÍODO e CONTESTAÇÃO DE CLASSIFICAÇÃO, com revisão do gestor.
//
// A medição sabe QUANTO, nunca POR QUE. Reunião presencial, treinamento, visita
// a cliente ou máquina em manutenção aparecem como ausência de atividade, e o
// número sozinho mente por omissão. Estes contratos carregam o contexto que
// falta — escrito por gente, revisado por gente, e SEM tocar em nenhum agregado.
// =============================================================================

/// <summary>Valores aceitos em <c>kind</c> (espelho do CHECK da tabela person_notes).</summary>
public static class PersonNoteKinds
{
    /// <summary>Contexto de um período ("reunião presencial", "treinamento").</summary>
    public const string Anotacao = "anotacao";

    /// <summary>Discordância sobre COMO um aplicativo foi classificado.</summary>
    public const string Contestacao = "contestacao";

    public static bool IsValid(string? value) => value is Anotacao or Contestacao;
}

/// <summary>Valores aceitos em <c>status</c> (espelho do CHECK da tabela person_notes).</summary>
public static class PersonNoteStatuses
{
    /// <summary>Nasce assim; aguarda o gestor.</summary>
    public const string Aberta = "aberta";

    public const string Aceita = "aceita";
    public const string Recusada = "recusada";

    /// <summary>Estados de REVISÃO — os únicos que o PATCH do gestor aceita.</summary>
    public static bool IsReviewed(string? value) => value is Aceita or Recusada;
}

/// <summary>
/// GET /api/v1/people/{sid}/notes?from&amp;to — as anotações e contestações da pessoa cujo
/// período CRUZA o recorte da tela (não apenas as que começam nele: uma anotação de segunda a
/// sexta precisa aparecer quando a tela mostra a quarta-feira).
///
/// Mais recentes primeiro. Sem paginação: o teto é o produto de uma pessoa por uma janela de
/// no máximo 92 dias, e a lista é escrita por gente — não é volume de máquina.
/// </summary>
public sealed record PersonNotesResponse(IReadOnlyList<PersonNoteResponse> Items);

/// <summary>
/// Uma anotação ou contestação. Os nomes de quem criou e de quem revisou vêm JÁ resolvidos do
/// servidor (display_name de users; null quando o usuário foi removido do tenant) — renderize
/// estes campos, não cruze por id no cliente.
///
/// app_process_name e app_display_name só existem na contestação com app_id — são o aplicativo
/// cuja classificação se contesta, resolvidos do catálogo global.
/// </summary>
public sealed record PersonNoteResponse(
    Guid Id,
    string WindowsSid,
    // "anotacao" ou "contestacao" (PersonNoteKinds).
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    Guid? AppId,
    string? AppProcessName,
    string? AppDisplayName,
    string Body,
    // "aberta", "aceita" ou "recusada" (PersonNoteStatuses).
    string Status,
    Guid CreatedByUserId,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    Guid? ReviewedByUserId,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

/// <summary>
/// POST /api/v1/people/{sid}/notes (Viewer+): cria SEMPRE com status "aberta" — o corpo não
/// escolhe status nem quem revisou; isso é decisão do gestor, no PATCH.
///
/// app_id só é aceito em kind "contestacao" (numa anotação de período ele não significa nada e
/// vira 400, em vez de gravar um campo mudo).
/// </summary>
public sealed record PersonNoteCreateRequest(
    string? Kind,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    Guid? AppId,
    string? Body);

/// <summary>
/// PATCH /api/v1/people/{sid}/notes/{id} (Admin+): a revisão do gestor. status precisa ser
/// "aceita" ou "recusada" — voltar para "aberta" não existe de propósito: a revisão é um fato
/// datado e assinado, e desfazer isso apagaria quem decidiu o quê.
///
/// review_note é a RESPOSTA ao colaborador. Obrigatória na recusa (recusar sem dizer por quê é
/// o oposto do que esta feature existe para fazer) e opcional na aceitação.
/// </summary>
public sealed record PersonNoteReviewRequest(string? Status, string? ReviewNote);

/// <summary>
/// Resposta da revisão: a anotação já no estado novo mais <c>effect</c> — a frase que a tela
/// mostra ao gestor dizendo, em português, O QUE a decisão dele fez.
///
/// POR QUE O CAMPO EXISTE: aceitar uma contestação NÃO altera nenhum agregado. O gestor precisa
/// saber disso no momento em que clica, senão vai esperar o número mudar sozinho — e concluir
/// que o produto está quebrado quando ele não mudar. O texto vem do SERVIDOR para que portal,
/// e-mail e export digam exatamente a mesma coisa.
/// </summary>
public sealed record PersonNoteReviewResponse(PersonNoteResponse Note, string Effect);
