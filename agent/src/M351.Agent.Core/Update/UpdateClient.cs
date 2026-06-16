using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Storage;

namespace M351.Agent.Core.Update;

/// <summary>
/// Cliente do auto-update (Secao 6.7). Reusa o HttpClient e o device token do agente (mesma
/// autenticacao PolicyDevice do /ingest — esta e a UNICA excecao GET do device, Secao 5.1).
///
///   GET {SERVERURL}/api/v1/agent/update-manifest?current={current}   -> 200 manifesto | 204 nada
///   GET {url do manifesto} (releases/{fileName})                     -> stream do MSI | 404
///
/// 204 e erro de rede NAO derrubam o agente: retornam null/false; o UpdateService apenas reagenda
/// (padrao N14 de backoff nao se aplica aqui — a checagem e periodica de 6 h).
/// </summary>
public sealed class UpdateClient
{
    private readonly HttpClient _http;
    private readonly AgentStateStore _state;
    private readonly ILogSink _log;

    public UpdateClient(HttpClient http, AgentStateStore state, ILogSink log)
    {
        _http = http;
        _state = state;
        _log = log;
    }

    /// <summary>
    /// Busca o manifesto do canal. Retorna null em 204 (sem release), 401/erro de rede ou resposta
    /// ilegivel — em todos os casos o agente nao faz nada e tenta no proximo ciclo.
    /// </summary>
    public async Task<UpdateManifest?> FetchManifestAsync(string currentVersion, CancellationToken ct)
    {
        var serverUrl = _state.ServerUrl;
        var token = _state.DeviceToken;
        if (serverUrl is null || token is null)
        {
            _log.Info("Auto-update: sem servidor/token (nao enrolado) — checagem pulada.");
            return null;
        }

        var url = $"{serverUrl}/api/v1/agent/update-manifest?current={Uri.EscapeDataString(currentVersion)}";

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.Warn($"Auto-update: servidor inacessivel na checagem de manifesto ({ex.Message}). Nova tentativa no proximo ciclo.");
            return null;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                _log.Info("Auto-update: 204 (sem release publicado para o canal) — nada a fazer.");
                return null;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log.Warn($"Auto-update: manifesto retornou HTTP {(int)response.StatusCode} — ignorado neste ciclo.");
                return null;
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var manifest = await JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.UpdateManifest, ct);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url))
                {
                    _log.Warn("Auto-update: manifesto incompleto (version/url ausentes) — ignorado.");
                    return null;
                }
                return manifest;
            }
            catch (JsonException ex)
            {
                _log.Error("Auto-update: manifesto ilegivel.", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Baixa o MSI da url do manifesto (device token) direto para destPath, em streaming
    /// (nunca em memoria). Retorna false em erro de rede / HTTP != 200 (descarta arquivo parcial).
    /// </summary>
    public async Task<bool> DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var token = _state.DeviceToken;
        if (token is null) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log.Warn($"Auto-update: download do MSI retornou HTTP {(int)response.StatusCode}.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst, ct);
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            _log.Warn($"Auto-update: falha no download do MSI ({ex.Message}).");
            TryDelete(destPath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* best-effort */ }
    }
}
