using M351.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Backoffice;

/// <summary>
/// Backoffice: troca o PLANO de uma organização. O plano é a flag por tenant que liga as
/// features pagas (alertas de saúde de frota e relatório de jornada semanal por e-mail são
/// exclusivos do Pro, docs/design/05-produto-mvp.md); sem este comando o gate existia no código
/// mas não havia como abrir a feature para um cliente que assinou.
///
/// Não mexe em device_limit: o limite de dispositivos é a régua comercial do contrato e continua
/// sendo decidido caso a caso, não como efeito colateral da troca de plano.
///
/// Uso: dotnet run --project src/M351.Api -- set-org-plan --org-slug empresa-x --plan pro
/// </summary>
public static class SetOrgPlanCommand
{
    /// <summary>Planos aceitos: o trial da criação e os dois planos comerciais.</summary>
    private static readonly string[] ValidPlans = ["trial", "essencial", "pro"];

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? orgSlug = null, plan = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--org-slug": orgSlug = args[++i]; break;
                case "--plan": plan = args[++i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(plan)
            || !ValidPlans.Contains(plan.Trim().ToLowerInvariant()))
        {
            Console.Error.WriteLine(
                $"Uso: set-org-plan --org-slug <slug> --plan <{string.Join('|', ValidPlans)}>");
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
        await DatabaseInitializer.MigrateAsync(db);

        var slug = orgSlug.Trim();
        var org = await db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Slug == slug);
        if (org is null)
        {
            Console.Error.WriteLine($"ERRO: organização com slug '{slug}' não encontrada.");
            return 1;
        }

        var previousPlan = org.Plan;
        var newPlan = plan.Trim().ToLowerInvariant();
        if (previousPlan == newPlan)
        {
            Console.WriteLine($"Nada a fazer: {org.Name} ({org.Slug}) já está no plano {newPlan}.");
            return 0;
        }

        org.Plan = newPlan;
        await db.SaveChangesAsync();

        Console.WriteLine("Plano atualizado com sucesso.");
        Console.WriteLine($"  Organização : {org.Name} ({org.Slug})");
        Console.WriteLine($"  Tenant ID   : {org.Id}");
        Console.WriteLine($"  Plano       : {previousPlan} -> {newPlan}");
        if (newPlan != "pro")
        {
            Console.WriteLine(
                "  Atenção     : fora do Pro, os alertas de saúde de frota e o relatório de");
            Console.WriteLine(
                "                jornada semanal por e-mail param de sair. As assinaturas das");
            Console.WriteLine(
                "                pessoas ficam gravadas e voltam a valer se o plano subir de novo.");
        }

        return 0;
    }
}
