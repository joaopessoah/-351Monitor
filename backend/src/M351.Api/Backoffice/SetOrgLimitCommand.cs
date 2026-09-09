using M351.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Backoffice;

/// <summary>
/// Backoffice: define o LIMITE DE DISPOSITIVOS (device_limit) de uma organização — a régua
/// comercial do contrato, decidida caso a caso. Nasceu em 09/09/2026 junto com o trial de 10
/// licenças: até então o único caminho era UPDATE à mão no Postgres, porque set-org-plan não toca
/// no limite de propósito (trocar plano e trocar teto são decisões distintas).
///
/// Uso: dotnet run --project src/M351.Api -- set-org-limit --org-slug empresa-x --device-limit 10
///      dotnet run --project src/M351.Api -- set-org-limit --org-slug empresa-x --unlimited
///
/// O limite vale para enroll de máquina NOVA: baixar o teto abaixo do que já está enrolado não
/// desliga ninguém, só impede entradas até a frota caber de novo — o comando avisa quando isso
/// acontece. Conta como ocupada a máquina não arquivada e não revogada (mesma regra do enroll).
/// </summary>
public static class SetOrgLimitCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? orgSlug = null, limitArg = null;
        var unlimited = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--org-slug" when i + 1 < args.Length: orgSlug = args[++i]; break;
                case "--device-limit" when i + 1 < args.Length: limitArg = args[++i]; break;
                case "--unlimited": unlimited = true; break;
            }
        }

        // exatamente UMA das duas formas: --device-limit N (N >= 1) ou --unlimited
        int? newLimit = null;
        var valid = !string.IsNullOrWhiteSpace(orgSlug) && (unlimited ^ (limitArg is not null));
        if (valid && !unlimited)
        {
            valid = int.TryParse(limitArg, out var parsed) && parsed >= 1;
            newLimit = parsed;
        }

        if (!valid)
        {
            Console.Error.WriteLine(
                "Uso: set-org-limit --org-slug <slug> (--device-limit <inteiro maior que zero> | --unlimited)");
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
        await DatabaseInitializer.MigrateAsync(db);

        var slug = orgSlug!.Trim();
        var org = await db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Slug == slug);
        if (org is null)
        {
            Console.Error.WriteLine($"ERRO: organização com slug '{slug}' não encontrada.");
            return 1;
        }

        // CLI roda sem tenant no contexto: filtro explícito por tenant, fora do filtro global
        var licensed = await db.Devices.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == org.Id && d.Status != "archived" && d.Status != "revoked");

        var previous = org.DeviceLimit;
        if (previous == newLimit)
        {
            Console.WriteLine($"Nada a fazer: {org.Name} ({org.Slug}) já está com limite {Describe(newLimit)}.");
            Console.WriteLine($"  Em uso      : {licensed} dispositivo(s)");
            return 0;
        }

        org.DeviceLimit = newLimit;
        await db.SaveChangesAsync();

        Console.WriteLine("Limite de dispositivos atualizado com sucesso.");
        Console.WriteLine($"  Organização : {org.Name} ({org.Slug})");
        Console.WriteLine($"  Tenant ID   : {org.Id}");
        Console.WriteLine($"  Plano       : {org.Plan}");
        Console.WriteLine($"  Limite      : {Describe(previous)} -> {Describe(newLimit)}");
        Console.WriteLine($"  Em uso      : {licensed} dispositivo(s)");
        if (newLimit is { } limit && licensed >= limit)
        {
            Console.WriteLine(
                "  Atenção     : a frota já ocupa o teto novo. Nenhuma máquina NOVA entra até uma");
            Console.WriteLine(
                "                licença ser liberada (arquivar ou revogar um dispositivo); as que");
            Console.WriteLine(
                "                já estão enroladas continuam funcionando normalmente.");
        }

        return 0;
    }

    private static string Describe(int? limit) => limit is { } l ? $"{l} dispositivo(s)" : "sem limite";
}
