using System.Text;
using System.Text.Json;
using M351.Api;
using M351.Api.Auth;
using M351.Api.Backoffice;
using M351.Api.Middleware;
using M351.Api.Services;
using M351.Infrastructure;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddM351Infrastructure(builder.Configuration);
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<AuthFlowService>();
builder.Services.AddScoped<AuditWriter>();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddProblemDetails();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AuthConstants.ClaimSub,
            RoleClaimType = AuthConstants.ClaimRole,
        };
    });

builder.Services.AddAuthorization(options =>
{
    // tokens temporários de MFA (token_use=mfa) NÃO passam na policy default
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.ClaimTokenUse, AuthConstants.TokenUseAccess)
        .Build();

    options.AddPolicy(AuthConstants.PolicyAccess, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.ClaimTokenUse, AuthConstants.TokenUseAccess));

    options.AddPolicy(AuthConstants.PolicyAdminPlus, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.ClaimTokenUse, AuthConstants.TokenUseAccess)
        .RequireClaim(AuthConstants.ClaimRole, "admin", "owner"));

    options.AddPolicy(AuthConstants.PolicyOwnerOnly, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.ClaimTokenUse, AuthConstants.TokenUseAccess)
        .RequireClaim(AuthConstants.ClaimRole, "owner"));

    // fluxo de MFA: aceita token temporário (mfa) ou token pleno (access)
    options.AddPolicy(AuthConstants.PolicyMfaToken, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.ClaimTokenUse, AuthConstants.TokenUseMfa, AuthConstants.TokenUseAccess));
});

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy("portal", policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
}

var app = builder.Build();

// Backoffice CLI (sem signup self-service): cria tenant + Owner + convite
if (args.Length > 0 && string.Equals(args[0], "create-org", StringComparison.OrdinalIgnoreCase))
{
    return await CreateOrgCommand.RunAsync(app.Services, args[1..]);
}

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    await DatabaseInitializer.MigrateAsync(scope.ServiceProvider.GetRequiredService<M351DbContext>());
}

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

// SPA do portal: em produção a imagem Docker copia o dist do Vite para wwwroot
// (infra/docker/api.Dockerfile). Em dev/testes não há wwwroot — middlewares inertes.
app.UseDefaultFiles();
app.UseStaticFiles();

if (corsOrigins.Length > 0)
{
    app.UseCors("portal");
}

app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/healthz", async (M351DbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.Problem(title: "Banco de dados indisponível.", statusCode: StatusCodes.Status503ServiceUnavailable));

// Fallback do SPA (rotas client-side como /visao-geral): serve wwwroot/index.html,
// mas NUNCA para /api/* — rota de API desconhecida segue respondendo 404.
app.MapFallback(context =>
{
    var indexPath = app.Environment.WebRootPath is { Length: > 0 } webRoot
        ? Path.Combine(webRoot, "index.html")
        : null;

    if (context.Request.Path.StartsWithSegments("/api") || indexPath is null || !File.Exists(indexPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    return context.Response.SendFileAsync(indexPath);
});

await app.RunAsync();
return 0;

/// <summary>Exposto para a WebApplicationFactory dos testes de integração.</summary>
public partial class Program;
