using M351.Infrastructure.DemoSeed;
using M351.Infrastructure.Security;
using Npgsql;
using Quartz;

namespace M351.Worker;

/// <summary>
/// Demo pública permanente (F5): o tenant demo (DemoSeeder, 30 devices × 60 dias pelo pipeline
/// real) deixa de depender de um console aberto. Dois jobs, registrados SOMENTE quando
/// Demo:Slug está configurado (env Demo__Slug):
///
/// - DemoKeepAliveJob (60 s): re-toca a presença dos devices vivos do tenant demo, o mesmo
///   efeito do keep-alive da ingestão real — a demo nunca parece abandonada. Os devices-cenário
///   "sem comunicação" (last_seen_at antigo) ficam de fora por derivação, sem depender do
///   resultado do seed em memória.
/// - DemoReseedJob (domingo 04:30 America/Sao_Paulo): re-semeia com Reset=true, o que também
///   apaga o audit_log do tenant demo (acessos públicos da semana não se acumulam). A senha da
///   conta viewer compartilhável vem de Demo:ViewerPassword para permanecer ESTÁVEL entre
///   reseeds (sem ela, cada reseed geraria outra senha e quebraria o link enviado a prospects).
/// </summary>
[DisallowConcurrentExecution]
public sealed class DemoKeepAliveJob(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    ILogger<DemoKeepAliveJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var slug = configuration["Demo:Slug"];
            if (string.IsNullOrWhiteSpace(slug))
            {
                return;
            }

            await using var conn = await dataSource.OpenConnectionAsync(context.CancellationToken);

            Guid tenantId;
            await using (var cmd = new NpgsqlCommand("SELECT id FROM organizations WHERE slug = @slug", conn))
            {
                cmd.Parameters.AddWithValue("slug", slug);
                var result = await cmd.ExecuteScalarAsync(context.CancellationToken);
                if (result is not Guid id)
                {
                    return; // tenant demo ainda não semeado
                }

                tenantId = id;
            }

            // devices-cenário "sem comunicação": last_seen_at velho é o próprio critério
            // (o keep-alive só re-toca quem já está vivo; quem está parado continua parado)
            var stale = new List<Guid>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT id FROM devices WHERE tenant_id = @t AND last_seen_at < now() - interval '2 days'", conn))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                await using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken);
                while (await reader.ReadAsync(context.CancellationToken))
                {
                    stale.Add(reader.GetGuid(0));
                }
            }

            await DemoSeeder.TouchPresenceAsync(dataSource, tenantId, stale, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Keep-alive do tenant demo falhou.");
        }
    }
}

[DisallowConcurrentExecution]
public sealed class DemoReseedJob(
    NpgsqlDataSource dataSource,
    IPasswordHasher passwordHasher,
    IConfiguration configuration,
    ILogger<DemoReseedJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var slug = configuration["Demo:Slug"];
            if (string.IsNullOrWhiteSpace(slug))
            {
                return;
            }

            var seeder = new DemoSeeder(dataSource, passwordHasher, m => logger.LogInformation("reseed demo: {Message}", m));
            var result = await seeder.RunAsync(new DemoSeedOptions
            {
                Slug = slug,
                Reset = true,
                ViewerPassword = string.IsNullOrWhiteSpace(configuration["Demo:ViewerPassword"])
                    ? null
                    : configuration["Demo:ViewerPassword"],
            }, context.CancellationToken);

            logger.LogInformation(
                "Tenant demo re-semeado: {Devices} devices, {Events} eventos ({TenantId})",
                result.DeviceCount, result.EventCount, result.TenantId);
        }
        catch (OperationCanceledException)
        {
            // shutdown do host
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reseed semanal do tenant demo falhou.");
        }
    }
}
