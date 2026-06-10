using System.Net;
using System.Text;
using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Fingerprint;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Storage;

namespace M351.Agent.Core.Net;

/// <summary>
/// POST /api/v1/agent/enroll (Seção 5.7): registra o device com a enrollment key (ek_…) e o
/// machine_fingerprint; persiste device_id + device_token (DPAPI) + config. Re-enroll é
/// idempotente no servidor (mesmo fingerprint preserva o device e o histórico).
/// </summary>
public sealed class EnrollmentClient
{
    private readonly HttpClient _http;
    private readonly AgentStateStore _state;
    private readonly IFingerprintSource _fingerprintSource;
    private readonly ILogSink _log;
    private readonly Func<string> _osVersion;

    public EnrollmentClient(HttpClient http, AgentStateStore state, IFingerprintSource fingerprintSource,
        ILogSink log, Func<string>? osVersion = null)
    {
        _http = http;
        _state = state;
        _fingerprintSource = fingerprintSource;
        _log = log;
        _osVersion = osVersion ?? (() => Environment.OSVersion.VersionString);
    }

    public async Task<bool> EnrollAsync(string serverUrl, string enrollmentKey, CancellationToken ct)
    {
        serverUrl = serverUrl.TrimEnd('/');
        var request = new EnrollRequest
        {
            EnrollmentKey = enrollmentKey,
            Hostname = Environment.MachineName,
            MachineFingerprint = MachineFingerprint.Compute(_fingerprintSource),
            OsVersion = _osVersion(),
            AgentVersion = AgentVersionInfo.Current
        };

        HttpResponseMessage response;
        try
        {
            var json = JsonSerializer.Serialize(request, AgentJsonContext.Default.EnrollRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await _http.PostAsync($"{serverUrl}/api/v1/agent/enroll", content, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.Warn($"Enroll falhou (servidor inacessível): {ex.Message}");
            return false;
        }

        using (response)
        {
            if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
            {
                _log.Warn($"Enroll rejeitado pelo servidor: HTTP {(int)response.StatusCode}.");
                return false;
            }

            EnrollResponse? body;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                body = await JsonSerializer.DeserializeAsync(stream, AgentJsonContext.Default.EnrollResponse, ct);
            }
            catch (JsonException ex)
            {
                _log.Error("Resposta de enroll ilegível.", ex);
                return false;
            }

            if (body is null || body.DeviceId.Length == 0 || body.DeviceToken.Length == 0)
            {
                _log.Error("Resposta de enroll incompleta.");
                return false;
            }

            _state.ServerUrl = serverUrl;
            _state.EnrollmentKey = enrollmentKey;   // cifrada DPAPI — usada no re-enroll N15
            _state.DeviceId = body.DeviceId;
            _state.DeviceToken = body.DeviceToken;  // cifrado DPAPI escopo máquina
            _state.Unenrolled = false;
            _state.SaveConfig(body.Config, body.ConfigVersion);

            _log.Info($"Device registrado: device_id={body.DeviceId} (config v{body.ConfigVersion}).");
            return true;
        }
    }
}
