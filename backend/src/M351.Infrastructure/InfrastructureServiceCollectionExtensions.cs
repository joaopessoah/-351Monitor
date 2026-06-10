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
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        services.AddScoped<TenantContext>();
        services.AddScoped<TenantSaveChangesInterceptor>();

        services.AddDbContext<M351DbContext>((provider, options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString("Default"));
            options.AddInterceptors(provider.GetRequiredService<TenantSaveChangesInterceptor>());
        });

        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IMfaService, MfaService>();

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
