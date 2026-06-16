using System.Text.Json.Serialization;

namespace M351.Agent.Core.Update;

/// <summary>
/// Resposta 200 de GET /api/v1/agent/update-manifest?current=… (Secao 6.7). Nomes EXATOS
/// (snake_case) — contrato fixo entre backend e agente. 204 No Content => sem release publicado
/// para o canal => o agente nao faz nada (modelado como manifesto null no UpdateClient).
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>URL absoluta do MSI (aponta para GET /api/v1/agent/releases/{fileName} no MVP).</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>SHA-256 hex (64 chars) do MSL; verificado antes de instalar.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>Abaixo desta versao o update e FORCADO imediatamente (ignora janela).</summary>
    [JsonPropertyName("min_version")] public string MinVersion { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UpdateManifest))]
public partial class UpdateJsonContext : JsonSerializerContext
{
}
