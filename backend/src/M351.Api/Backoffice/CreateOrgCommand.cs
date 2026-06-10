using System.Text;
using M351.Api.Controllers;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Email;
using M351.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M351.Api.Backoffice;

/// <summary>
/// Backoffice (SEM signup self-service): cria tenant + Owner pendente + convite por e-mail.
/// Uso: dotnet run --project src/M351.Api -- create-org --name "Empresa X" --owner-email dono@empresa.com.br [--slug empresa-x]
/// </summary>
public static class CreateOrgCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? name = null, ownerEmail = null, slug = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--name": name = args[++i]; break;
                case "--owner-email": ownerEmail = args[++i]; break;
                case "--slug": slug = args[++i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ownerEmail) || !ownerEmail.Contains('@'))
        {
            Console.Error.WriteLine("Uso: create-org --name \"Empresa X\" --owner-email dono@empresa.com.br [--slug empresa-x]");
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var portal = scope.ServiceProvider.GetRequiredService<IOptions<PortalOptions>>().Value;

        await DatabaseInitializer.MigrateAsync(db);

        var email = ownerEmail.Trim();
        var existingUser = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email);
        if (existingUser)
        {
            Console.Error.WriteLine($"ERRO: já existe um usuário com o e-mail {email}.");
            return 1;
        }

        slug = string.IsNullOrWhiteSpace(slug) ? Slugify(name) : Slugify(slug);
        var baseSlug = slug;
        for (var n = 2; await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == slug); n++)
        {
            slug = $"{baseSlug}-{n}";
        }

        var org = new Organization
        {
            Id = Uuid7.NewUuid7(),
            Name = name.Trim(),
            Slug = slug,
            Plan = "trial",
            DeviceLimit = 25, // N24 — limite do trial
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var owner = new User
        {
            Id = Uuid7.NewUuid7(),
            TenantId = org.Id,
            Email = email,
            DisplayName = email.Split('@')[0],
            Role = UserRole.Owner,
            Status = UserStatus.Invited,
        };

        var token = TokenGenerator.NewOpaqueToken();
        var invitation = new Invitation
        {
            Id = Uuid7.NewUuid7(),
            TenantId = org.Id,
            Email = email,
            Role = UserRole.Owner,
            TokenHash = TokenGenerator.Sha256(token),
            ExpiresAt = DateTimeOffset.UtcNow.Add(UsersController.InvitationLifetime),
        };

        db.Organizations.Add(org);
        db.Users.Add(owner);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync();
        await SeedCategoriesAsync(db, org.Id);

        var link = $"{portal.BaseUrl.TrimEnd('/')}/convite/{token}";
        await emailSender.SendAsync(new EmailMessage(
            email,
            $"Sua organização {org.Name} foi criada no +351 Monitor",
            $"""
            Olá,

            A organização {org.Name} foi provisionada no +351 Monitor e você é o Owner.

            Para definir sua senha e configurar a verificação em duas etapas (obrigatória para Owner),
            abra o link abaixo (válido por 7 dias):

            {link}
            """));

        Console.WriteLine("Organização criada com sucesso.");
        Console.WriteLine($"  Tenant ID : {org.Id}");
        Console.WriteLine($"  Nome      : {org.Name}");
        Console.WriteLine($"  Slug      : {org.Slug}");
        Console.WriteLine($"  Owner     : {email}");
        Console.WriteLine($"  Convite   : {link}");
        return 0;
    }

    /// <summary>Seed das categorias padrão (Seção 7.1) — classificação: 1=trabalho, 0=neutro, -1=não relacionado.</summary>
    private static async Task SeedCategoriesAsync(M351DbContext db, Guid tenantId)
    {
        (string Name, int Classification, string Color)[] seed =
        [
            ("Desenvolvimento", 1, "#2563eb"),
            ("Escritório/Documentos", 1, "#0891b2"),
            ("Comunicação", 1, "#7c3aed"),
            ("Reuniões", 1, "#9333ea"),
            ("Navegação", 1, "#0d9488"),
            ("Design", 1, "#db2777"),
            ("ERP/Sistemas internos", 1, "#4f46e5"),
            ("Sistema/Utilitários", 1, "#64748b"),
            ("Música/Streaming de áudio", 0, "#a3a3a3"),
            ("Não categorizado", 0, "#9ca3af"),
            ("Jogos", -1, "#dc2626"),
            ("Redes sociais", -1, "#ea580c"),
            ("Vídeo/Streaming", -1, "#e11d48"),
        ];

        foreach (var (categoryName, classification, color) in seed)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO categories (id, tenant_id, name, classification, color)
                VALUES ({Uuid7.NewUuid7()}, {tenantId}, {categoryName}, {(short)classification}, {color})
                ON CONFLICT (tenant_id, name) DO NOTHING
                """);
        }
    }

    private static string Slugify(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (c is ' ' or '-' or '_' or '.')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString();
        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        return slug.Trim('-') is { Length: > 0 } s ? s : "org";
    }
}
