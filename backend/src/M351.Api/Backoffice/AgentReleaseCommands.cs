using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using M351.Api.Agent;
using M351.Api.Services;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Exports;
using Microsoft.Extensions.Options;
using Npgsql;

namespace M351.Api.Backoffice;

/// <summary>
/// Backoffice (F4.2 — auto-update, Seção 6.7): publica um release do agente no canal de
/// auto-update. Calcula o SHA-256 do arquivo, copia-o para Releases:Directory, INSERE a linha
/// em agent_releases e marca-a como is_current (desmarcando as demais do canal) — tudo numa
/// transação, auditado. A url aponta para a hospedagem do MVP (GET /agent/releases/{file}).
/// Uso: dotnet run --project src/M351.Api -- publish-agent-release
///        --version 1.1.0 --file ./MonitorAgent-1.1.0.msi --min-version 1.0.0
///        [--channel stable] [--server-url https://api.exemplo.com.br]
/// </summary>
public static class PublishAgentReleaseCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? version = null, file = null, minVersion = null, serverUrl = null;
        var channel = AgentUpdateEndpoints.DefaultChannel;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--version": version = args[++i]; break;
                    case "--file": file = args[++i]; break;
                    case "--min-version": minVersion = args[++i]; break;
                    case "--channel": channel = args[++i]; break;
                    case "--server-url": serverUrl = args[++i]; break;
                    default:
                        Console.Error.WriteLine($"Argumento desconhecido: {args[i]}");
                        PrintUsage();
                        return 1;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            Console.Error.WriteLine("ERRO: argumento sem valor.");
            PrintUsage();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(file)
            || string.IsNullOrWhiteSpace(minVersion))
        {
            PrintUsage();
            return 1;
        }

        var sourcePath = Path.GetFullPath(file);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"ERRO: arquivo não encontrado: {sourcePath}");
            return 1;
        }

        using var scope = services.CreateScope();
        await DatabaseInitializer.MigrateAsync(scope.ServiceProvider.GetRequiredService<M351DbContext>());

        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var releaseOptions = scope.ServiceProvider.GetRequiredService<IOptions<ReleaseOptions>>().Value;

        // SHA-256 hex64 (minúsculo) — o agente confere o download contra este valor
        var sha256 = await ComputeSha256Async(sourcePath);

        // o file_name é só o nome (o GET /agent/releases/{file} barra separadores); copia para
        // o diretório de releases (idempotente — sobrescreve se já existir o mesmo nome)
        var fileName = Path.GetFileName(sourcePath);
        var directory = Path.GetFullPath(releaseOptions.Directory);
        Directory.CreateDirectory(directory);
        var destPath = Path.Combine(directory, fileName);
        if (!string.Equals(Path.GetFullPath(destPath), sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, destPath, overwrite: true);
        }

        // url do manifesto: aponta para a hospedagem do MVP. Sem --server-url fica relativa
        // (o agente já conhece o SERVERURL); com --server-url vira absoluta (recomendado).
        var url = string.IsNullOrWhiteSpace(serverUrl)
            ? $"/api/v1/agent/releases/{fileName}"
            : $"{serverUrl.TrimEnd('/')}/api/v1/agent/releases/{fileName}";

        var id = Uuid7.NewUuid7();

        await using var connection = await dataSource.OpenConnectionAsync();

        // REPUBLICAR a mesma versão é operação normal, não erro: o workflow de release é disparado
        // à mão e pode ser repetido depois de uma falha TARDIA (limpeza de temporário, rede caindo
        // no final) que não desfez a gravação. Sem este trecho, a repetição batia no índice único
        // ux_agent_releases_channel_version e devolvia um erro de Postgres que não dizia a quem
        // operava nem o que aconteceu nem o que fazer.
        var existing = await connection.QuerySingleOrDefaultAsync<ExistingRelease>(
            new CommandDefinition(
                """
                SELECT id AS "Id", sha256 AS "Sha256", is_current AS "IsCurrent"
                FROM agent_releases WHERE channel = @Channel AND version = @Version
                """,
                new { Channel = channel, Version = version }));

        if (existing is not null)
        {
            // Conteúdo diferente com a MESMA versão é o caso que nunca pode passar batido: dois
            // binários distintos com o mesmo número deixam a frota impossível de diagnosticar.
            if (!string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"ERRO: a versão {version} já está publicada no canal '{channel}' com OUTRO conteúdo.");
                Console.Error.WriteLine($"  SHA-256 publicado : {existing.Sha256}");
                Console.Error.WriteLine($"  SHA-256 do arquivo: {sha256}");
                Console.Error.WriteLine(
                    "  Suba AgentVersionInfo.Current e publique uma versão nova — nunca troque o "
                    + "binário de uma versão já publicada.");
                return 1;
            }

            if (existing.IsCurrent)
            {
                Console.WriteLine($"Nada a fazer: {version} já está publicada e é a current do canal '{channel}'.");
                Console.WriteLine($"  SHA-256 : {sha256}");
                return 0;
            }

            // Mesma versão, mesmo conteúdo, mas não é a current (cenário de rollback anterior):
            // publicar de novo significa exatamente voltar o is_current para ela. Dois UPDATEs
            // numa transação, como no rollback — o índice parcial único não tolera os dois
            // estados num statement só.
            await using (var restore = await connection.BeginTransactionAsync())
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE agent_releases SET is_current = false WHERE channel = @Channel AND is_current",
                    new { Channel = channel }, transaction: restore));

                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE agent_releases SET is_current = true WHERE id = @Id",
                    new { Id = existing.Id }, transaction: restore));

                await AuditWriter.AddInTransactionAsync(
                    connection, restore, AgentReleaseAudit.SystemTenantId, AuditActions.PublishAgentRelease,
                    targetType: "agent_release", targetId: existing.Id,
                    detailJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["channel"] = channel,
                        ["version"] = version,
                        ["sha256"] = sha256,
                        ["republicacao"] = true,
                    }));

                await restore.CommitAsync();
            }

            Console.WriteLine($"Versão {version} já existia com o mesmo conteúdo; voltou a ser a current do canal '{channel}'.");
            return 0;
        }

        await using (var tx = await connection.BeginTransactionAsync())
        {
            // desmarca o current vigente do canal e insere o novo já como current — índice
            // parcial único garante que nunca há dois currents simultâneos
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_releases SET is_current = false WHERE channel = @Channel AND is_current",
                new { Channel = channel }, transaction: tx));

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO agent_releases
                    (id, channel, version, url, sha256, min_version, file_name, is_current)
                VALUES (@Id, @Channel, @Version, @Url, @Sha256, @MinVersion, @FileName, true)
                """,
                new
                {
                    Id = id,
                    Channel = channel,
                    Version = version,
                    Url = url,
                    Sha256 = sha256,
                    MinVersion = minVersion,
                    FileName = fileName,
                },
                transaction: tx));

            await AuditWriter.AddInTransactionAsync(
                connection, tx, AgentReleaseAudit.SystemTenantId, AuditActions.PublishAgentRelease,
                targetType: "agent_release", targetId: id,
                detailJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["channel"] = channel,
                    ["version"] = version,
                    ["min_version"] = minVersion,
                    ["sha256"] = sha256,
                    ["file_name"] = fileName,
                }));

            await tx.CommitAsync();
        }

        Console.WriteLine("Release do agente publicado com sucesso.");
        Console.WriteLine($"  Canal       : {channel}");
        Console.WriteLine($"  Versão      : {version} (current)");
        Console.WriteLine($"  Min version : {minVersion}");
        Console.WriteLine($"  Arquivo     : {fileName} -> {destPath}");
        Console.WriteLine($"  SHA-256     : {sha256}");
        Console.WriteLine($"  URL         : {url}");
        return 0;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant(); // hex64 minúsculo (contrato do manifesto)
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Uso: publish-agent-release --version <v> --file <caminho.msi> --min-version <v> "
        + "[--channel stable] [--server-url https://api.exemplo.com.br]");

    /// <summary>Linha já existente do canal+versão — base da republicação idempotente.</summary>
    private sealed record ExistingRelease(Guid Id, string Sha256, bool IsCurrent);
}

/// <summary>
/// Backoffice (F4.2): rollback do canal para uma versão JÁ PUBLICADA — move is_current sem
/// redeploy e sem tocar nas máquinas (cumpre o "pronto quando" da F4). Não recalcula sha256
/// nem recopia arquivo: a linha-alvo já existe.
/// Uso: dotnet run --project src/M351.Api -- rollback-agent-release --version 1.0.3 [--channel stable]
/// </summary>
public static class RollbackAgentReleaseCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? version = null;
        var channel = AgentUpdateEndpoints.DefaultChannel;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--version": version = args[++i]; break;
                    case "--channel": channel = args[++i]; break;
                    default:
                        Console.Error.WriteLine($"Argumento desconhecido: {args[i]}");
                        PrintUsage();
                        return 1;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            Console.Error.WriteLine("ERRO: argumento sem valor.");
            PrintUsage();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            PrintUsage();
            return 1;
        }

        using var scope = services.CreateScope();
        await DatabaseInitializer.MigrateAsync(scope.ServiceProvider.GetRequiredService<M351DbContext>());
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var connection = await dataSource.OpenConnectionAsync();

        // QuerySingleOrDefault devolve Guid.Empty quando não há linha (release real é uuid v7)
        var targetId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM agent_releases WHERE channel = @Channel AND version = @Version",
            new { Channel = channel, Version = version }));
        if (targetId == Guid.Empty)
        {
            Console.Error.WriteLine($"ERRO: versão '{version}' não publicada no canal '{channel}'.");
            return 1;
        }

        var fromVersion = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT version FROM agent_releases WHERE channel = @Channel AND is_current",
            new { Channel = channel }));

        await using (var tx = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_releases SET is_current = false WHERE channel = @Channel AND is_current",
                new { Channel = channel }, transaction: tx));

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_releases SET is_current = true WHERE id = @Id",
                new { Id = targetId }, transaction: tx));

            await AuditWriter.AddInTransactionAsync(
                connection, tx, AgentReleaseAudit.SystemTenantId, AuditActions.RollbackAgentRelease,
                targetType: "agent_release", targetId: targetId,
                detailJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["channel"] = channel,
                    ["from_version"] = fromVersion,
                    ["to_version"] = version,
                }));

            await tx.CommitAsync();
        }

        Console.WriteLine($"Rollback concluído: canal '{channel}' agora aponta para {version} (era {fromVersion ?? "nenhum"}).");
        return 0;
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Uso: rollback-agent-release --version <v> [--channel stable]");
}

/// <summary>
/// agent_releases é GLOBAL (sem tenant), mas audit_log.tenant_id é NOT NULL. As ações de
/// backoffice de release são gravadas sob um tenant-sentinela fixo (Guid.Empty / all-zeros):
/// queryável e jamais colide com um tenant real (uuid v7). Decisão documentada (a spec não
/// especifica auditoria de operação global).
/// </summary>
public static class AgentReleaseAudit
{
    public static readonly Guid SystemTenantId = Guid.Empty;
}
