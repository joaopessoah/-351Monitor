using System.Net.Http.Headers;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Storage;

namespace M351.Agent.Core.Net;

/// <summary>
/// Envio do pacote de diagnóstico ao suporte (F5): POST {SERVERURL}/api/v1/agent/diagnostics com
/// o DEVICE TOKEN, corpo application/zip. Roda no SERVIÇO, nunca no helper: o token e os logs do
/// serviço estão do lado dele; o helper só pede pelo pipe e mostra o resultado no tray.
///
/// O ZIP é o mesmo do `--diag` (DiagnosticsLogPackager.CreateSupportZip): logs redigidos linha a
/// linha pelo LogScrubber, sem título de janela, usuário ou caminho de arquivo do usuário.
/// Reusa o HttpClient único do agente (AgentHttpClientFactory) — portanto respeita o PROXYURL e
/// jamais relaxa a validação de TLS.
/// </summary>
public sealed class DiagnosticsUploader
{
    /// <summary>Cap do lado do agente, espelhando o cap de 10 MB do endpoint.</summary>
    public const long MaxZipBytes = 10 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly AgentStateStore _state;
    private readonly ILogSink _log;
    private readonly string _dataDirectory;

    public DiagnosticsUploader(HttpClient http, AgentStateStore state, ILogSink log, string dataDirectory)
    {
        _http = http;
        _state = state;
        _log = log;
        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// Empacota os logs e envia. Retorna true só quando o servidor confirmou o recebimento.
    /// Best-effort: qualquer falha é logada e reportada ao tray, nunca derruba o serviço.
    /// </summary>
    public async Task<bool> UploadAsync(CancellationToken ct)
    {
        var serverUrl = _state.ServerUrl;
        var token = _state.DeviceToken;
        if (serverUrl is null || token is null)
        {
            _log.Warn("Diagnóstico: dispositivo ainda não registrado — nada a enviar ao suporte.");
            return false;
        }

        var zipPath = Path.Combine(Path.GetTempPath(),
            $"monitoragent-diag-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.zip");
        try
        {
            DiagnosticsLogPackager.CreateSupportZip(
                Path.Combine(_dataDirectory, "logs"), zipPath, AgentVersionInfo.Current);

            var bytes = await File.ReadAllBytesAsync(zipPath, ct);
            if (bytes.LongLength > MaxZipBytes)
            {
                _log.Error($"Diagnóstico: pacote de {bytes.LongLength / (1024 * 1024)} MB acima do limite de " +
                           $"{MaxZipBytes / (1024 * 1024)} MB — envio abortado.", new InvalidOperationException("pacote grande demais"));
                return false;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/v1/agent/diagnostics");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            request.Content = content;

            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                _log.Info($"Diagnóstico enviado ao suporte ({bytes.Length} bytes, HTTP {(int)response.StatusCode}).");
                return true;
            }

            _log.Warn($"Diagnóstico: servidor respondeu HTTP {(int)response.StatusCode} — pacote NÃO recebido.");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("Diagnóstico: falha ao empacotar ou enviar o pacote de suporte.", ex);
            return false;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch (Exception) { /* temporário: o SO limpa depois */ }
        }
    }
}
