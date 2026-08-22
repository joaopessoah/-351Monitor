using M351.Infrastructure.Data;
using M351.Infrastructure.Email;
using M351.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace M351.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddM351Infrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PasswordHashingOptions>(configuration.GetSection(PasswordHashingOptions.SectionName));
        services.Configure<MfaOptions>(configuration.GetSection(MfaOptions.SectionName));

        services.AddScoped<TenantContext>();
        services.AddScoped<TenantSaveChangesInterceptor>();

        services.AddDbContext<M351DbContext>((provider, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddInterceptors(provider.GetRequiredService<TenantSaveChangesInterceptor>());
        });

        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IMfaService, MfaService>();

        services.AddM351Email(configuration);

        return services;
    }

    /// <summary>
    /// Só o envio de e-mail (Dev grava .txt em disco; Smtp real via env): usado pela API
    /// dentro do AddM351Infrastructure e pelo WORKER isoladamente (digest semanal e alertas,
    /// F5) — o worker não usa EF e não precisa do resto.
    /// </summary>
    public static IServiceCollection AddM351Email(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<DevFileEmailSender>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<IEmailSender>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
            return string.Equals(options.Provider, "Smtp", StringComparison.OrdinalIgnoreCase)
                ? provider.GetRequiredService<SmtpEmailSender>()
                : provider.GetRequiredService<DevFileEmailSender>();
        });

        return services;
    }
}
