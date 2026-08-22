using System.Globalization;
using M351.Api.Auth;
using M351.Infrastructure.Exports;
using Microsoft.Extensions.Options;

namespace M351.Api.Agent;

/// <summary>
/// Recebimento do pacote de diagnóstico do agente (F5): POST /api/v1/agent/diagnostics,
/// autenticado por DEVICE TOKEN (policy PolicyDevice, igual aos endpoints de auto-update).
///
/// É o caminho de suporte do item "Enviar diagnóstico ao suporte" do tray: o usuário confirma no
/// helper, o SERVIÇO empacota os logs já redigidos pelo LogScrubber (mesmo ZIP do `--diag`) e faz
/// o upload. O corpo é o ZIP (application/zip), direto ou dentro de um multipart, com cap de 10 MB.
///
/// O arquivo vai para {Exports:Directory}/diagnostics/{deviceId}-{timestamp}.zip — o diretório de
/// exports já é volume compartilhado entre api e worker (infra/docker-compose.staging.yml); o
/// subdiretório é criado na primeira chamada. Nada é aberto nem indexado pela API: o pacote existe
/// para o suporte humano baixar da máquina, e todo recebimento fica registrado no log.
/// </summary>
public static class AgentDiagnosticsEndpoints
{
    /// <summary>Cap do pacote: 10 MB (o agente também trunca antes de enviar).</summary>
    public const long MaxZipBytes = 10 * 1024 * 1024;

    public const string ZipContentType = "application/zip";

    /// <summary>Subdiretório do volume de exports onde os pacotes são gravados.</summary>
    public const string SubDirectory = "diagnostics";

    public static IEndpointRouteBuilder MapAgentDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/agent/diagnostics", ReceiveDiagnosticsAsync)
            .RequireAuthorization(AuthConstants.PolicyDevice);

        return app;
    }

    private static async Task<IResult> ReceiveDiagnosticsAsync(
        HttpContext context, IOptions<ExportOptions> exportOptions,
        ILogger<DiagnosticsUploadLog> logger, CancellationToken ct)
    {
        var tenantId = CurrentDevice.TenantId(context.User);
        var deviceId = CurrentDevice.DeviceId(context.User);

        // Content-Length é dica, não garantia: barra o obviamente grande antes de ler um byte,
        // mas o cap real é aplicado durante a leitura (corpo com chunked encoding não tem length).
        if (context.Request.ContentLength is { } declared && declared > MaxZipBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[] zip;
        try
        {
            zip = await ReadZipAsync(context.Request, ct);
        }
        catch (BodyTooLargeException)
        {
            logger.LogWarning("Diagnóstico do device {DeviceId} recusado: pacote acima de {Max} bytes",
                deviceId, MaxZipBytes);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (UnsupportedMediaTypeException)
        {
            return Results.Problem(
                title: $"Content-Type não suportado (esperado {ZipContentType} ou multipart com um arquivo).",
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                extensions: new Dictionary<string, object?> { ["reason"] = "unsupported_media_type" });
        }

        // ZIP vazio ou que não é ZIP: recusa sem gravar (assinatura local "PK")
        if (zip.Length < 4 || zip[0] != 'P' || zip[1] != 'K')
        {
            return Results.Problem(
                title: "Corpo não é um arquivo ZIP.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["reason"] = "invalid_zip" });
        }

        var directory = Path.Combine(Path.GetFullPath(exportOptions.Value.Directory), SubDirectory);
        Directory.CreateDirectory(directory);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{deviceId}-{timestamp}.zip";
        var absolutePath = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(absolutePath, zip, ct);

        logger.LogInformation(
            "Diagnóstico recebido do device {DeviceId} (tenant {TenantId}): {Bytes} bytes em {File}",
            deviceId, tenantId, zip.Length, fileName);

        return Results.Ok(new DiagnosticsReceipt(zip.Length, fileName));
    }

    /// <summary>
    /// Lê o ZIP do corpo: multipart/form-data (primeiro arquivo) ou corpo binário application/zip.
    /// O cap de 10 MB é aplicado byte a byte na leitura do corpo binário.
    /// </summary>
    private static async Task<byte[]> ReadZipAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null) throw new UnsupportedMediaTypeException();
            if (file.Length > MaxZipBytes) throw new BodyTooLargeException();

            using var buffer = new MemoryStream();
            await using var stream = file.OpenReadStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }

        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.StartsWith(ZipContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedMediaTypeException();
        }

        using var buffered = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > MaxZipBytes) throw new BodyTooLargeException();
            buffered.Write(chunk, 0, read);
        }

        return buffered.ToArray();
    }

    /// <summary>Recibo do recebimento (serializado em snake_case como o resto da API).</summary>
    public sealed record DiagnosticsReceipt(int ReceivedBytes, string FileName);

    /// <summary>Categoria de log do recebimento (ILogger&lt;T&gt; precisa de um tipo).</summary>
    public sealed class DiagnosticsUploadLog;

    private sealed class BodyTooLargeException : Exception;

    private sealed class UnsupportedMediaTypeException : Exception;
}
