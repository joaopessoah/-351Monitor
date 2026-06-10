using System.Text.Json;
using M351.Api.Auth;
using M351.Api.Contracts;

namespace M351.Api.Agent;

/// <summary>
/// Endpoints do agente (Minimal APIs — Seção 4): POST /api/v1/agent/enroll (anônimo, key no
/// body) e POST /api/v1/ingest/batch (device token). Únicas rejeições do lote inteiro
/// (Seção 5.5): 422 para JSON malformado ou &gt; 500 eventos (batch_too_large); 413 para
/// payload grande demais.
/// </summary>
public static class AgentEndpoints
{
    /// <summary>N3/Seção 5.6 — máx. 500 eventos por lote.</summary>
    public const int MaxBatchEvents = 500;

    /// <summary>Seção 5.6 — body descomprimido máx. 5 MB (proteção contra zip bomb).</summary>
    public const long MaxDecompressedBytes = 5 * 1024 * 1024;

    /// <summary>Seção 5.6 — body comprimido máx. 1 MB.</summary>
    public const long MaxCompressedBytes = 1 * 1024 * 1024;

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/agent/enroll",
                async (EnrollRequest? request, EnrollmentService service, CancellationToken ct) =>
                    await service.EnrollAsync(request, ct))
            .AllowAnonymous();

        app.MapPost("/api/v1/ingest/batch", IngestBatchAsync)
            .RequireAuthorization(AuthConstants.PolicyDevice);

        return app;
    }

    private static async Task<IResult> IngestBatchAsync(
        HttpContext context, IngestService service, CancellationToken ct)
    {
        JsonDocument? doc;
        try
        {
            doc = await ReadBodyAsync(context.Request.Body, ct);
        }
        catch (BodyTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (JsonException)
        {
            return Problem422("Body malformado (JSON inválido).", "malformed_json");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Problem422("Body malformado: objeto JSON esperado.", "malformed_json");
            }

            var events = new List<JsonElement>();
            if (root.TryGetProperty("events", out var eventsElement))
            {
                if (eventsElement.ValueKind != JsonValueKind.Array)
                {
                    return Problem422("Campo events deve ser um array.", "malformed_json");
                }

                events.AddRange(eventsElement.EnumerateArray());
            }

            if (events.Count > MaxBatchEvents)
            {
                return Problem422($"Lote com mais de {MaxBatchEvents} eventos.", RejectReasons.BatchTooLarge);
            }

            var batch = new IngestBatch(
                BatchId: GetString(root, "batch_id"),
                AgentVersion: GetString(root, "agent_version"),
                SentAt: GetTimestamp(root, "sent_at"),
                ConfigVersion: GetInt32(root, "config_version"),
                Events: events);

            var ack = await service.ProcessAsync(
                CurrentDevice.TenantId(context.User), CurrentDevice.DeviceId(context.User), batch, ct);

            return Results.Ok(ack);
        }
    }

    private static async Task<JsonDocument> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffered = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxDecompressedBytes)
            {
                throw new BodyTooLargeException();
            }

            buffered.Write(buffer, 0, read);
        }

        buffered.Position = 0;
        return await JsonDocument.ParseAsync(buffered, cancellationToken: ct);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt32(out var value)
            ? value
            : null;

    private static DateTimeOffset? GetTimestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(prop.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value.ToUniversalTime()
            : null;

    private static IResult Problem422(string title, string reason) =>
        Results.Problem(title: title, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });

    private sealed class BodyTooLargeException : Exception;
}
