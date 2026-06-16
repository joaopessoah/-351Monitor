namespace M351.Api.Contracts;

/// <summary>
/// Manifesto de auto-update (Seção 6.7 + tabela 7.4 l.815). Resposta de
/// GET /api/v1/agent/update-manifest?current=. Serialização snake_case automática (Program.cs),
/// então os campos saem como version, url, sha256, min_version — o contrato fixo do agente.
/// Sem release publicado para o canal -> 204 No Content (sem corpo): o agente não faz nada.
/// </summary>
public record UpdateManifestResponse(
    string Version,
    string Url,
    string Sha256,
    string MinVersion);
