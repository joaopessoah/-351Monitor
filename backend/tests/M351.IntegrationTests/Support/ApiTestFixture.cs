using M351.Domain;
using M351.Domain.Entities;
using M351.Infrastructure.Data;
using M351.Infrastructure.Email;
using M351.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace M351.IntegrationTests.Support;

public record TestUser(Guid Id, Guid TenantId, string Email, string Password, string? MfaSecretBase32);

/// <summary>
/// WebApplicationFactory compartilhada pela coleção de testes: um banco descartável por execução,
/// migrations aplicadas no boot (Database:AutoMigrate) e IEmailSender substituído por captura.
/// </summary>
public class ApiTestFixture : WebApplicationFactory<Program>
{
    public PostgresTestDatabase Database { get; } = new();
    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", Database.ConnectionString);
        builder.UseSetting("Database:AutoMigrate", "true");
        builder.UseSetting("Jwt:SigningKey", "chave-de-testes-integracao-0123456789abcdef");
        builder.UseSetting("Mfa:EncryptionKey", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        builder.UseSetting("Portal:BaseUrl", "http://localhost:5173");
        builder.UseSetting("Email:Provider", "Dev");

        // Argon2id reduzido APENAS para acelerar a suíte de integração; os parâmetros canônicos
        // (64 MB / 3 / 4) são cobertos pelos testes unitários do hasher.
        builder.UseSetting("PasswordHashing:MemoryKb", "8192");
        builder.UseSetting("PasswordHashing:Iterations", "1");
        builder.UseSetting("PasswordHashing:Parallelism", "2");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    /// <summary>
    /// Client SEM gerenciamento automático de cookies: os testes de refresh manipulam o cookie
    /// m351_refresh manualmente (rotação/reuso) e o CookieContainer padrão mascararia o reuso.
    /// </summary>
    public HttpClient CreateApiClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public async Task<Organization> CreateOrganizationAsync(string name)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();

        var org = new Organization
        {
            Id = Uuid7.NewUuid7(),
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}"[..30],
            Plan = "trial",
            DeviceLimit = 25,
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    public async Task<TestUser> CreateUserAsync(
        Guid tenantId, UserRole role, string? password = null, bool mfaEnabled = false, string status = UserStatus.Active)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();

        password ??= $"Senha-Forte-{Guid.NewGuid():N}"[..20];
        var email = $"{role.ToDbValue()}-{Guid.NewGuid():N}@teste.com.br";

        string? secretBase32 = null;
        byte[]? secretEnc = null;
        if (mfaEnabled)
        {
            (secretBase32, secretEnc) = mfa.GenerateSecret();
        }

        var user = new User
        {
            Id = Uuid7.NewUuid7(),
            TenantId = tenantId,
            Email = email,
            DisplayName = $"Usuário {role.ToDbValue()}",
            Role = role,
            PasswordHash = status == UserStatus.Invited ? null : hasher.Hash(password),
            MfaSecretEnc = secretEnc,
            MfaEnabled = mfaEnabled,
            Status = status,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new TestUser(user.Id, tenantId, email, password, secretBase32);
    }

    public async Task<Device> CreateDeviceAsync(Guid tenantId, string hostname)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();

        var device = new Device
        {
            Id = Uuid7.NewUuid7(),
            TenantId = tenantId,
            Hostname = hostname,
            MachineFingerprint = Guid.NewGuid().ToString("N"),
            TokenHash = TokenGenerator.Sha256(TokenGenerator.NewOpaqueToken()),
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    public async Task<EnrollmentKey> CreateEnrollmentKeyAsync(Guid tenantId, string? label = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();

        var fullKey = EnrollmentKeyGenerator.NewKey();
        var key = new EnrollmentKey
        {
            Id = Uuid7.NewUuid7(),
            TenantId = tenantId,
            KeyPrefix = EnrollmentKeyGenerator.VisiblePrefix(fullKey),
            KeyHash = TokenGenerator.Sha256(fullKey),
            Label = label,
        };
        db.EnrollmentKeys.Add(key);
        await db.SaveChangesAsync();
        return key;
    }

    public async Task<(Invitation Invitation, string Token, TestUser User)> CreateInvitationAsync(
        Guid tenantId, UserRole role, DateTimeOffset expiresAt)
    {
        var user = await CreateUserAsync(tenantId, role, status: UserStatus.Invited);

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<M351DbContext>();

        var token = TokenGenerator.NewOpaqueToken();
        var invitation = new Invitation
        {
            Id = Uuid7.NewUuid7(),
            TenantId = tenantId,
            Email = user.Email,
            Role = role,
            TokenHash = TokenGenerator.Sha256(token),
            ExpiresAt = expiresAt,
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync();
        return (invitation, token, user);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Database.Dispose();
        }
    }
}

[CollectionDefinition(ApiCollection.Name)]
public class ApiCollection : ICollectionFixture<ApiTestFixture>
{
    public const string Name = "api";
}
