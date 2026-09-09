using System.Text;
using M351.Api.Controllers;
using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Capacity;
using M351.Infrastructure.Data;
using M351.Infrastructure.Email;
using M351.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace M351.Api.Backoffice;

/// <summary>
/// Backoffice (SEM signup self-service): cria tenant + Owner pendente + convite por e-mail.
/// Uso: dotnet run --project src/M351.Api -- create-org --name "Empresa X" --owner-email dono@empresa.com.br [--slug empresa-x] [--device-limit 10]
/// </summary>
public static class CreateOrgCommand
{
    /// <summary>
    /// N24 — teto de dispositivos com que todo trial nasce. Era 25; em 09/09/2026 passou a 10:
    /// "qualquer trial pode ter 10 licenças, e fica a critério do cliente usar todas ou não".
    /// Acima disso o enroll responde com a frase comercial do trial (EnrollmentService). Para
    /// mudar o teto de uma org já criada: set-org-limit.
    /// </summary>
    public const int TrialDeviceLimit = 10;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? name = null, ownerEmail = null, slug = null, deviceLimitArg = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--name": name = args[++i]; break;
                case "--owner-email": ownerEmail = args[++i]; break;
                case "--slug": slug = args[++i]; break;
                case "--device-limit": deviceLimitArg = args[++i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ownerEmail) || !ownerEmail.Contains('@'))
        {
            Console.Error.WriteLine("Uso: create-org --name \"Empresa X\" --owner-email dono@empresa.com.br [--slug empresa-x] [--device-limit 10]");
            return 1;
        }

        // Teto do trial: 10 por padrão (N24); --device-limit cobre o piloto negociado fora do padrão.
        var deviceLimit = TrialDeviceLimit;
        if (deviceLimitArg is not null && (!int.TryParse(deviceLimitArg, out deviceLimit) || deviceLimit < 1))
        {
            Console.Error.WriteLine("ERRO: --device-limit precisa ser um inteiro maior que zero.");
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
            DeviceLimit = deviceLimit, // N24 — teto do trial (TrialDeviceLimit) ou o --device-limit informado
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

        // F7 — calendário nacional do ano corrente e do próximo. Sem ele a "capacidade
        // utilizada" conta feriado como dia trabalhado e sai subestimada desde o primeiro dia
        // do cliente. Idempotente; Configurações › Equipes tem o botão para semear de novo.
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            await BrazilianHolidays.SeedCurrentAndNextYearAsync(
                connection, null, org.Id, DateOnly.FromDateTime(DateTime.UtcNow));
        }

        var link = $"{portal.BaseUrl.TrimEnd('/')}/convite/{token}";

        // O link no console é a fonte de verdade do backoffice: imprimir ANTES de
        // tentar o e-mail, para que uma falha de envio não perca o convite
        // (o token não é recuperável depois — só o hash fica no banco).
        Console.WriteLine("Organização criada com sucesso.");
        Console.WriteLine($"  Tenant ID : {org.Id}");
        Console.WriteLine($"  Nome      : {org.Name}");
        Console.WriteLine($"  Slug      : {org.Slug}");
        Console.WriteLine($"  Owner     : {email}");
        Console.WriteLine($"  Licenças  : {deviceLimit} dispositivos (plano trial)");
        Console.WriteLine($"  Convite   : {link}");

        try
        {
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  AVISO: o e-mail de convite não pôde ser enviado ({ex.Message}). Use o link acima.");
        }

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
