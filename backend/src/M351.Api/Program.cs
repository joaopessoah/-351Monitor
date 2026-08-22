using System.Text;
using System.Text.Json;
using M351.Api;
using M351.Api.Agent;
using M351.Api.Auth;
using M351.Api.Backoffice;
using M351.Api.Middleware;
using M351.Api.RateLimiting;
using M351.Api.Services;
using M351.Infrastructure;
using M351.Infrastructure.Data;
using M351.Infrastructure.Data.AppDictionary;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Serilog;

DapperConfig.Apply();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

// Sentry (quem monitora o monitor): captura exceções não tratadas com contexto de request.
// Ativo SOMENTE quando Sentry:Dsn está preenchida (env Sentry__Dsn, plumbada no
// docker-compose.staging.yml a partir de SENTRY_DSN do infra/.env); vazia = desativado.
var sentryDsn = builder.Configuration["Sentry:Dsn"];
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(options =>
    {
        options.Dsn = sentryDsn;
        options.Environment = builder.Environment.EnvironmentName;
    });
}

// Não anunciar o servidor: suprime o header "Server: Kestrel" das respostas (a borda
// também remove Server/Via, ver o bloco header do infra/caddy/Caddyfile)
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddM351Infrastructure(builder.Configuration);
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.SectionName));
// F3.5 — diretório dos CSVs exportados (compartilhado com o worker; a API serve o download)
builder.Services.Configure<M351.Infrastructure.Exports.ExportOptions>(
    builder.Configuration.GetSection(M351.Infrastructure.Exports.ExportOptions.SectionName));
// F4.2 — diretório dos MSIs do agente (auto-update; a API serve o download por streaming)
builder.Services.Configure<M351.Infrastructure.Exports.ReleaseOptions>(
    builder.Configuration.GetSection(M351.Infrastructure.Exports.ReleaseOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<AuthFlowService>();
builder.Services.AddScoped<AuditWriter>();

// F4.7 — auditoria de leitura: contexto por-request (o controller descreve o que foi acessado)
// + filter que grava a linha em audit_log APÓS a resposta 2xx (consolida view_timeline/view_report
// incondicionais; actor_ip sempre preenchido). Filter scoped pois usa AuditWriter/DbContext scoped.
builder.Services.AddScoped<M351.Api.Auditing.AuditReadContext>();
builder.Services.AddScoped<M351.Api.Auditing.AuditReadFilter>();

// F1 — ingestão (hot path em Dapper/Npgsql — Seção 7) e enrollment
builder.Services.AddSingleton(provider =>
    NpgsqlDataSource.Create(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default ausente.")));
builder.Services.AddSingleton<AgentConfigService>();
builder.Services.AddSingleton<RawEventPartitionManager>();
builder.Services.AddSingleton<M351.Infrastructure.Privacy.DsrService>(); // F4.5 — exclusão DSR (sem estado por request)
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<IngestService>();
builder.Services.AddRequestDecompression(); // Content-Encoding: gzip dos lotes (Seção 5.4)

// Rate limiting nativo .NET 8 (Seções 5.6/5.7): enroll por IP, ingestão por device, cota diária
builder.Services.AddM351RateLimiting(builder.Configuration);

// Atrás do Caddy (infra/caddy) o IP real do agente chega em X-Forwarded-For. Proxies CONFIÁVEIS:
// loopback (default do middleware) + 172.16.0.0/12 (faixa default das redes bridge do Docker —
// docker-compose.*.yml não fixa subnet, então o Caddy recebe IP desse pool). XFF de origem não
// confiável é ignorado e vale o RemoteIpAddress da conexão (fallback). ForwardedLimit default = 1:
// honra apenas o último salto (o Caddy), impedindo spoofing de cadeia de XFF.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
        System.Net.IPAddress.Parse("172.16.0.0"), 12));
});

builder.Services.AddControllers(options =>
{
    // F4.7 — filter global de auditoria de leitura: inerte exceto nas ações que preenchem o
    // AuditReadContext (anotadas [AuditRead]); grava a trilha só em 2xx (ver AuditReadFilter).
    options.Filters.AddService<M351.Api.Auditing.AuditReadFilter>();
}).AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
});
builder.Services.AddProblemDetails();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    // scheme do device token (Bearer dt_...), SEPARADO do JWT do portal (Seção 7.5)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
        AuthConstants.SchemeDevice, displayName: null, configureOptions: null)
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

    // device token: escopo EXCLUSIVO das rotas /api/v1/agent/* e /api/v1/ingest/* —
    // JWT do portal não passa aqui, e o device token não passa nas policies acima
    options.AddPolicy(AuthConstants.PolicyDevice, policy => policy
        .AddAuthenticationSchemes(AuthConstants.SchemeDevice)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.ClaimTokenUse, AuthConstants.TokenUseDevice));
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

// Backoffice CLI: enrollment key para onboarding manual (a key completa é impressa UMA vez)
if (args.Length > 0 && string.Equals(args[0], "create-enrollment-key", StringComparison.OrdinalIgnoreCase))
{
    return await CreateEnrollmentKeyCommand.RunAsync(app.Services, args[1..]);
}

// Backoffice CLI (F3.6): tenant demo sintético injetado pelo pipeline REAL de intervalização
if (args.Length > 0 && string.Equals(args[0], "seed-demo-tenant", StringComparison.OrdinalIgnoreCase))
{
    return await SeedDemoTenantCommand.RunAsync(app.Services, args[1..]);
}

// Backoffice CLI (F4.2): publica um release do agente no canal de auto-update (Seção 6.7)
if (args.Length > 0 && string.Equals(args[0], "publish-agent-release", StringComparison.OrdinalIgnoreCase))
{
    return await PublishAgentReleaseCommand.RunAsync(app.Services, args[1..]);
}

// Backoffice CLI (F4.2): rollback do canal de auto-update para uma versão já publicada
if (args.Length > 0 && string.Equals(args[0], "rollback-agent-release", StringComparison.OrdinalIgnoreCase))
{
    return await RollbackAgentReleaseCommand.RunAsync(app.Services, args[1..]);
}

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    await DatabaseInitializer.MigrateAsync(scope.ServiceProvider.GetRequiredService<M351DbContext>());

    // F1.1, dicionário de apps BR: seeder IDEMPOTENTE do catálogo global (display_name
    // amigável + default_category sugerida + curated=true). Roda junto com as migrations
    // porque é dado de produto que evolui a cada release (uma migration aplicaria a versão
    // daquele commit uma única vez). Jamais toca o mapeamento do tenant.
    await new AppDictionarySeeder(
        scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>(),
        scope.ServiceProvider.GetRequiredService<ILogger<AppDictionarySeeder>>()).RunOnceAsync();
}

// antes de QUALQUER middleware que use o IP da conexão (rate limit por IP, logs)
app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

// Seção 5.6: body comprimido máx. 1 MB nas rotas de ingestão (checado ANTES de descomprimir)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/ingest")
        && context.Request.Headers.ContentEncoding.Count > 0
        && context.Request.ContentLength > AgentEndpoints.MaxCompressedBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    await next(context);
});
app.UseRequestDecompression();

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

// DEPOIS de UseAuthorization: o token bucket por device precisa dos claims, e requisição sem
// token leva 401 sem consumir limite; com RateLimiting:Enabled=false as policies viram no-op
app.UseRateLimiter();

app.MapControllers();
app.MapAgentEndpoints();
app.MapAgentUpdateEndpoints(); // F4.2 — manifesto de auto-update + hospedagem do MSI (device token)
app.MapAgentDiagnosticsEndpoints(); // F5 — upload do ZIP de diagnóstico do agente (device token)

app.MapGet("/healthz", async (M351DbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.Problem(title: "Banco de dados indisponível.", statusCode: StatusCodes.Status503ServiceUnavailable));

// /readyz (prontidão operacional, além do /healthz): exige conexão ao Postgres E que a
// última execução com SUCESSO registrada em maintenance_runs (qualquer job) tenha menos
// de 26 horas. Os jobs de manutenção são diários (02:00 a 03:00 BRT); 26 h dá folga de
// fuso e de atraso sem mascarar um worker parado há mais de um ciclo. A consulta usa o
// mesmo NpgsqlDataSource singleton da ingestão (mesma infraestrutura de acesso do banco
// que o healthz valida via EF); a tabela maintenance_runs é gravada pelo
// MaintenanceRunRecorder (M351.Infrastructure/Maintenance).
app.MapGet("/readyz", async (NpgsqlDataSource dataSource, TimeProvider clock, CancellationToken ct) =>
{
    DateTimeOffset? lastSuccess;
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT max(finished_at) FROM maintenance_runs WHERE status = 'ok'", connection);
        var scalar = await command.ExecuteScalarAsync(ct);
        lastSuccess = scalar switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => null,
        };
    }
    catch (Exception)
    {
        return Results.Problem(
            title: "Banco de dados indisponível.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (lastSuccess is null)
    {
        return Results.Problem(
            title: "Nenhuma execução de manutenção com sucesso registrada em maintenance_runs (worker ainda não rodou os jobs noturnos).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var age = clock.GetUtcNow() - lastSuccess.Value;
    if (age > TimeSpan.FromHours(26))
    {
        return Results.Problem(
            title: $"Última manutenção com sucesso há {age.TotalHours:F1} horas (limite: 26 horas). Verifique o worker.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new { status = "ready" });
});

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
