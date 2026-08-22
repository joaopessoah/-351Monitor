using System.Text.Json;

namespace M351.Api.Contracts;

// ----- POST/GET /api/v1/exports (F3.5, Seções 7.4/8.6 — CSV assíncrono) -----

/// <summary>
/// Body de POST /exports. kind: usage_csv | jornada_csv | fora_horario_csv (dsr_* são F4, 400 aqui);
/// params validados com os MESMOS validadores dos endpoints de leitura.
/// </summary>
public sealed record ExportCreateRequest(string? Kind, ExportParamsRequest? Params);

/// <summary>
/// group_by só vale (e é obrigatório) para usage_csv. tag (F5) é o recorte de equipe por
/// etiqueta, com a MESMA semântica dos endpoints de leitura: vazio equivale a sem filtro e
/// etiqueta inexistente gera um CSV vazio, nunca um erro.
/// </summary>
public sealed record ExportParamsRequest(string? From, string? To, string[]? DeviceIds, string? GroupBy, string? Tag);

/// <summary>202 do POST — o job entrou na fila do worker.</summary>
public sealed record ExportCreateResponse(Guid Id, string Kind, string Status, DateTimeOffset CreatedAt);

/// <summary>
/// Item de GET /exports — trilha dos últimos 30 dias do tenant ("quem gerou, quando",
/// spec linha 949; todos os papéis veem os exports do tenant). expired = job done com
/// prazo vencido OU arquivo já removido pelo sweep (download responde 410).
/// truncated = o teto de 500 k linhas foi atingido: o CSV é PARCIAL e o portal avisa
/// (jamais truncamento silencioso — o usuário estreita o filtro para o restante).
/// </summary>
public sealed record ExportJobItemResponse(
    Guid Id,
    string Kind,
    string Status,
    DateTimeOffset CreatedAt,
    string RequestedByName,
    JsonElement Params,
    int? RowCount,
    bool Truncated,
    DateTimeOffset? ExpiresAt,
    bool Expired);

public sealed record ExportsResponse(IReadOnlyList<ExportJobItemResponse> Items);
