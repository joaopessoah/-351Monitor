using M351.Infrastructure.Data;
using M351.Infrastructure.DemoSeed;
using M351.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Npgsql;

namespace M351.Api.Backoffice;

/// <summary>
/// Backoffice (F3.6): semeia o tenant demo sintético injetando eventos pelo pipeline REAL
/// de intervalização (requisito de vendas — nenhum prospect vê dados de outro cliente).
/// Uso: dotnet run --project src/M351.Api -- seed-demo-tenant
///        [--devices 30] [--days 60] [--slug empresa-demo] [--reset] [--keep-alive]
///        [--owner-email dono@empresademo.com.br] [--owner-password ...] [--viewer-password ...]
///
/// --keep-alive: após semear, re-toca a presença dos devices vivos a cada 60 s até Ctrl+C —
/// a janela N6 do "online agora" é de 180 s e ninguém faz ingest no tenant demo; sem isso o
/// painel "Equipe agora" viraria "Sem comunicação" minutos depois do seed.
/// </summary>
public static class SeedDemoTenantCommand
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var options = new DemoSeedOptions();
        var keepAlive = false;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--devices": options = options with { DeviceCount = int.Parse(args[++i]) }; break;
                    case "--days": options = options with { Days = int.Parse(args[++i]) }; break;
                    case "--slug": options = options with { Slug = args[++i] }; break;
                    case "--reset": options = options with { Reset = true }; break;
                    case "--keep-alive": keepAlive = true; break;
                    case "--owner-email": options = options with { OwnerEmail = args[++i] }; break;
                    case "--owner-password": options = options with { OwnerPassword = args[++i] }; break;
                    case "--viewer-password": options = options with { ViewerPassword = args[++i] }; break;
                    default:
                        Console.Error.WriteLine($"Argumento desconhecido: {args[i]}");
                        Console.Error.WriteLine("Uso: seed-demo-tenant [--devices 30] [--days 60] [--slug empresa-demo] [--reset] [--keep-alive] [--owner-email e] [--owner-password p] [--viewer-password p]");
                        return 1;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine("ERRO: argumento inválido ou sem valor. Uso: seed-demo-tenant [--devices N] [--days N] [--slug s] [--reset] [--keep-alive] [--owner-email e] [--owner-password p] [--viewer-password p]");
            return 1;
        }

        using var scope = services.CreateScope();
        await DatabaseInitializer.MigrateAsync(scope.ServiceProvider.GetRequiredService<M351DbContext>());

        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var portal = scope.ServiceProvider.GetRequiredService<IOptions<PortalOptions>>().Value;

        var seeder = new DemoSeeder(dataSource, hasher, Console.WriteLine);
        DemoSeedResult result;
        try
        {
            result = await seeder.RunAsync(options);
        }
        catch (DemoSeedException ex)
        {
            Console.Error.WriteLine($"ERRO: {ex.Message}");
            return 1;
        }

        // credenciais impressas UMA vez (só o hash fica no banco — não recuperáveis depois)
        Console.WriteLine();
        Console.WriteLine("Tenant demo semeado com sucesso.");
        Console.WriteLine($"  Tenant ID            : {result.TenantId}");
        Console.WriteLine($"  Slug                 : {options.Slug}");
        Console.WriteLine($"  Devices              : {result.DeviceCount} (1 arquivado, {result.StaleDeviceIds.Count} sem comunicação)");
        Console.WriteLine($"  Eventos brutos       : {result.EventCount}");
        Console.WriteLine($"  Intervalos (pipeline): {result.IntervalCount}");
        Console.WriteLine($"  Device-dias agregados: {result.AggregatedDeviceDays}");
        Console.WriteLine($"  Owner                : {result.OwnerEmail} / {result.OwnerPassword} (define MFA no 1º login)");
        Console.WriteLine($"  Viewer (demo)        : {result.ViewerEmail} / {result.ViewerPassword}");
        Console.WriteLine($"  Dia com lacuna de seq: {result.SeqGapDay:yyyy-MM-dd} (badge de dados incompletos)");
        Console.WriteLine($"  URL sugerida         : {portal.BaseUrl}");

        if (!keepAlive)
        {
            Console.WriteLine();
            Console.WriteLine("Dica: rode com --keep-alive durante a apresentação para a presença \"agora\" não expirar (janela de 180 s).");
            return 0;
        }

        // sustenta a demo: mesmo efeito do lote VAZIO de keep-alive da ingestão real, a cada
        // 60 s, nos devices vivos (archived e "sem comunicação" ficam de fora — são cenário)
        Console.WriteLine();
        Console.WriteLine("--keep-alive: renovando a presença dos devices vivos a cada 60 s. Ctrl+C encerra.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var touched = await DemoSeeder.TouchPresenceAsync(dataSource, result.TenantId, result.StaleDeviceIds, cts.Token);
                Console.WriteLine($"  [{DateTimeOffset.Now:HH:mm:ss}] presença renovada em {touched} devices");
                await Task.Delay(KeepAliveInterval, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("keep-alive encerrado.");
        }

        return 0;
    }
}
