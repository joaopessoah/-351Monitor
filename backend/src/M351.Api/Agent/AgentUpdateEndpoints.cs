using Dapper;
using M351.Api.Auth;
using M351.Api.Contracts;
using M351.Infrastructure.Exports;
using Microsoft.Extensions.Options;
using Npgsql;

namespace M351.Api.Agent;

/// <summary>
/// Auto-update de canal único (Seção 6.7 + tabela 7.4 l.815). Dois GET autenticados por DEVICE
/// TOKEN (policy PolicyDevice — a única exceção GET do device, Seção 5.1):
///  - GET /api/v1/agent/update-manifest?current= : manifesto GLOBAL do canal 'stable'
///    (fonte da verdade = tabela agent_releases, linha WHERE channel='stable' AND is_current).
///    Sem release publicado -> 204 No Content (o agente não faz nada). NÃO há lookup do device
///    além da autenticação: o manifesto é o mesmo para todos.
///  - GET /api/v1/agent/releases/{fileName} : hospedagem do MSI no MVP — streaming
///    (PhysicalFile, nunca em memória) a partir de Releases:Directory; 404 se não existe.
///    A url do manifesto aponta para cá; em produção pode trocar por CDN.
/// </summary>
public static class AgentUpdateEndpoints
{
    /// <summary>Canal único do MVP (sem canary/beta — corte 3 da Seção 6.7).</summary>
    public const string DefaultChannel = "stable";

    public static IEndpointRouteBuilder MapAgentUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/agent/update-manifest", GetUpdateManifestAsync)
            .RequireAuthorization(AuthConstants.PolicyDevice);

        app.MapGet("/api/v1/agent/releases/{fileName}", GetReleaseFileAsync)
            .RequireAuthorization(AuthConstants.PolicyDevice);

        return app;
    }

    // current= é informativo (o agente já manda a versão dele): a DECISÃO de atualizar é do
    // agente comparando semver(version)/semver(min_version) com a current. O backend só devolve
    // o release vigente do canal — não filtra por current nem por device.
    private static async Task<IResult> GetUpdateManifestAsync(
        NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ManifestRow>(new CommandDefinition(
            """
            SELECT version, url, sha256, min_version
            FROM agent_releases
            WHERE channel = @Channel AND is_current
            """,
            new { Channel = DefaultChannel },
            cancellationToken: ct));

        // sem release publicado para o canal -> 204 (sem corpo): o agente não faz nada
        if (row is null)
        {
            return Results.NoContent();
        }

        return Results.Ok(new UpdateManifestResponse(row.Version, row.Url, row.Sha256, row.MinVersion));
    }

    private static IResult GetReleaseFileAsync(
        string fileName, IOptions<ReleaseOptions> releaseOptions)
    {
        // path traversal: aceitamos só o nome do arquivo (sem separadores/.. — o template de
        // rota já não captura '/', mas barramos qualquer tentativa de subir de diretório)
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.IndexOfAny(['/', '\\']) >= 0
            || Path.GetFileName(fileName) != fileName)
        {
            return Results.NotFound();
        }

        var directory = Path.GetFullPath(releaseOptions.Value.Directory);
        var absolutePath = Path.GetFullPath(Path.Combine(directory, fileName));

        // defesa em profundidade: o caminho resolvido tem de continuar dentro do diretório
        if (!absolutePath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(absolutePath))
        {
            return Results.NotFound();
        }

        // streaming (PhysicalFile abre FileStream sob demanda — nunca carrega o MSI em memória)
        return Results.File(absolutePath, "application/octet-stream", fileName);
    }

    private sealed record ManifestRow(string Version, string Url, string Sha256, string MinVersion);
}
