namespace M351.Api.Contracts;

// ====================================================================== Titulares (device_users)
//
// Contrato dos endpoints de TITULAR (Seção 7.4 linha 801). O titular é um device_user: o par
// (dispositivo, usuário do Windows) — NÃO um usuário do portal. O modelo é POR DISPOSITIVO: a
// mesma pessoa em duas máquinas tem dois registros, com ids distintos. Nenhuma tela pode
// prometer que um registro atravessa dispositivos.
//
// A tabela device_users não tem entidade EF (leitura/escrita por Dapper, padrão das daily_*),
// então os contratos vivem aqui como records simples. snake_case na serialização.

/// <summary>
/// Titular na listagem e na visão individual. device_name vem do COALESCE(display_name,
/// hostname) do dispositivo — campo ALÉM do mínimo do contrato, exigido pelas telas (busca de
/// titular da Privacidade e cabeçalho da página da pessoa) para não obrigar uma segunda
/// chamada por linha.
/// </summary>
public record DeviceUserResponse(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    string WindowsUsername,
    string? DisplayName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Body do PATCH /device-users/{id} (AdminPlus): display_name null/vazio limpa o apelido (as
/// telas voltam a exibir o windows_username). Campo ausente é indistinguível de null neste
/// contrato — o único campo editável é o nome, então "enviar o corpo" já significa "definir o
/// nome" (decisão documentada; o PATCH de devices usa JsonElement porque lá há vários campos).
/// </summary>
public record DeviceUserPatchRequest(string? DisplayName);
