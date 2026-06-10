using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using M351.Api.Auth;
using Microsoft.AspNetCore.RateLimiting;

namespace M351.Api.RateLimiting;

/// <summary>Nomes das policies nativas de rate limiting (.NET 8) aplicadas nos endpoints do agente.</summary>
public static class RateLimitingPolicies
{
    /// <summary>Seção 5.7 — POST /api/v1/agent/enroll: janela fixa 1 min por IP.</summary>
    public const string Enroll = "enroll-per-ip";

    /// <summary>Seção 5.6 — POST /api/v1/ingest/batch: token bucket por device autenticado.</summary>
    public const string Ingest = "ingest-per-device";
}

/// <summary>
/// Rate limiting NATIVO do .NET 8 (Microsoft.AspNetCore.RateLimiting + System.Threading.RateLimiting,
/// PartitionedRateLimiter) — sem dependência externa e sem rate limiting distribuído (corte do MVP,
/// ver RateLimitingOptions). O PartitionedRateLimiter do framework descarta limiters ociosos
/// automaticamente (timer interno), então partições de IPs/devices que pararam de chegar não
/// acumulam memória. Resposta de rejeição: 429 ProblemDetails + header Retry-After em segundos.
/// </summary>
public static class RateLimitingExtensions
{
    public static IServiceCollection AddM351RateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        services.AddSingleton<IngestDailyQuota>();

        // limites lidos UMA vez no boot (igual ao JwtOptions no Program.cs) — config fixa por host
        var options = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
            ?? new RateLimitingOptions();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = static async (context, cancellationToken) =>
            {
                // Retry-After do próprio limiter (janela fixa: reset da janela; token bucket:
                // próximo reabastecimento); fallback conservador de 60 s
                var retryAfterSeconds = 60;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                }

                context.HttpContext.Response.Headers.RetryAfter =
                    retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                // mesmo formato ProblemDetails do resto da API (Results.Problem + extension reason)
                await Results.Problem(
                        title: "Limite de requisições excedido.",
                        statusCode: StatusCodes.Status429TooManyRequests,
                        extensions: new Dictionary<string, object?> { ["reason"] = "rate_limited" })
                    .ExecuteAsync(context.HttpContext);
            };

            // Seção 5.7 — enroll: N req/min por IP, janela fixa. O IP real vem do
            // ForwardedHeaders (Caddy → X-Forwarded-For, só de proxies confiáveis — Program.cs);
            // sem proxy vale o RemoteIpAddress da conexão; null (ex.: TestServer) cai numa
            // partição única "unknown" — melhor limitar de menos que deixar passar sem limite.
            limiter.AddPolicy(RateLimitingPolicies.Enroll, context =>
            {
                if (!options.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.EnrollPerMinutePerIp),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            // Seção 5.6 — ingestão: token bucket POR DEVICE autenticado. Sustentado N lotes/min
            // (1 token a cada 60/N s) com burst = capacidade do bucket. O middleware roda DEPOIS
            // de UseAuthorization: requisição sem device token leva 401 antes e não consome token.
            limiter.AddPolicy(RateLimitingPolicies.Ingest, context =>
            {
                if (!options.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var deviceId = context.User.FindFirstValue(AuthConstants.ClaimDeviceId);
                if (deviceId is null)
                {
                    // defesa em profundidade: sem claim de device (não deveria chegar aqui),
                    // não limita — a autorização já rejeitou/rejeitará com 401
                    return RateLimitPartition.GetNoLimiter("anonymous");
                }

                return RateLimitPartition.GetTokenBucketLimiter(deviceId, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, options.IngestBurstPerDevice),
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(60.0 / Math.Max(1, options.IngestPerMinutePerDevice)),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });

        return services;
    }
}
