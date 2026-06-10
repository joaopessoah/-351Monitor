using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace M351.Api.Backoffice;

/// <summary>
/// Backoffice: gera uma enrollment key (`ek_` + 12 chars base62) para uma organização.
/// A chave completa é impressa UMA única vez — só o SHA-256 + prefixo ficam no banco.
/// Uso: dotnet run --project src/M351.Api -- create-enrollment-key --org-slug empresa-x [--label "Onboarding"]
/// </summary>
public static class CreateEnrollmentKeyCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? orgSlug = null, label = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--org-slug": orgSlug = args[++i]; break;
                case "--label": label = args[++i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(orgSlug))
        {
            Console.Error.WriteLine("Uso: create-enrollment-key --org-slug <slug> [--label \"texto\"]");
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

        var fullKey = EnrollmentKeyGenerator.NewKey();
        db.EnrollmentKeys.Add(new EnrollmentKey
        {
            Id = Uuid7.NewUuid7(),
            TenantId = org.Id,
            KeyPrefix = EnrollmentKeyGenerator.VisiblePrefix(fullKey),
            KeyHash = TokenGenerator.Sha256(fullKey),
            Label = string.IsNullOrWhiteSpace(label) ? "backoffice" : label.Trim(),
        });
        await db.SaveChangesAsync();

        Console.WriteLine("Enrollment key criada com sucesso.");
        Console.WriteLine($"  Organização : {org.Name} ({org.Slug})");
        Console.WriteLine($"  Tenant ID   : {org.Id}");
        Console.WriteLine($"  Key         : {fullKey}");
        Console.WriteLine("  ATENÇÃO: a chave acima é exibida UMA única vez (apenas o hash fica no banco).");

        return 0;
    }
}
