using System.Globalization;
using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Queue;
using M351.Agent.Core.Security;

namespace M351.Agent.Core.Storage;

/// <summary>
/// Estado persistente do agente na tabela kv (Seção 6.4): device_id, device_token (DPAPI),
/// enrollment key (DPAPI), config em cache + config_version, boot_id, flag de shutdown limpo.
/// </summary>
public sealed class AgentStateStore
{
    private const string KeyDeviceId = "device_id";
    private const string KeyDeviceToken = "device_token_enc";
    private const string KeyEnrollmentKey = "enrollment_key_enc";
    private const string KeyServerUrl = "server_url";
    private const string KeyConfigJson = "config_json";
    private const string KeyConfigVersion = "config_version";
    private const string KeyBootId = "boot_id";
    private const string KeyBootTime = "boot_time_utc";
    private const string KeyCleanShutdown = "clean_shutdown";
    private const string KeyUnenrolled = "unenrolled";
    private const string KeyFirstRunDone = "first_run_done";

    private readonly SqliteEventQueue _kv;
    private readonly ISecretProtector _protector;
    private AgentConfig? _configCache;

    public AgentStateStore(SqliteEventQueue kv, ISecretProtector protector)
    {
        _kv = kv;
        _protector = protector;
    }

    public string? DeviceId
    {
        get => _kv.KvGet(KeyDeviceId);
        set => _kv.KvSet(KeyDeviceId, value);
    }

    public string? DeviceToken
    {
        get
        {
            var enc = _kv.KvGet(KeyDeviceToken);
            return enc is null ? null : _protector.Unprotect(enc);
        }
        set => _kv.KvSet(KeyDeviceToken, value is null ? null : _protector.Protect(value));
    }

    public string? EnrollmentKey
    {
        get
        {
            var enc = _kv.KvGet(KeyEnrollmentKey);
            return enc is null ? null : _protector.Unprotect(enc);
        }
        set => _kv.KvSet(KeyEnrollmentKey, value is null ? null : _protector.Protect(value));
    }

    public string? ServerUrl
    {
        get => _kv.KvGet(KeyServerUrl);
        set => _kv.KvSet(KeyServerUrl, value?.TrimEnd('/'));
    }

    public int ConfigVersion
    {
        get => int.TryParse(_kv.KvGet(KeyConfigVersion), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        set => _kv.KvSet(KeyConfigVersion, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Config em cache (ou default de fábrica antes do primeiro enroll).</summary>
    public AgentConfig Config
    {
        get
        {
            if (_configCache is not null) return _configCache;
            var json = _kv.KvGet(KeyConfigJson);
            if (json is not null)
            {
                try { _configCache = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentConfig); }
                catch (JsonException) { _configCache = null; }
            }
            return _configCache ??= AgentConfig.FactoryDefault();
        }
    }

    public void SaveConfig(AgentConfig config, int version)
    {
        _kv.KvSet(KeyConfigJson, JsonSerializer.Serialize(config, AgentJsonContext.Default.AgentConfig));
        ConfigVersion = version;
        _configCache = config;
    }

    public bool Unenrolled
    {
        get => _kv.KvGet(KeyUnenrolled) == "1";
        set => _kv.KvSet(KeyUnenrolled, value ? "1" : null);
    }

    public bool IsEnrolled =>
        !Unenrolled && DeviceId is not null && DeviceToken is not null && ServerUrl is not null;

    /// <summary>UNENROLL: esquece token e identidade (revogação definitiva — Seção 5.5).</summary>
    public void ForgetIdentity()
    {
        DeviceToken = null;
        DeviceId = null;
        Unenrolled = true;
    }

    public string BootId { get; private set; } = "";

    /// <summary>
    /// Inicializa boot_id (GUID novo por boot) e calcula start_reason. Precedencia (Secao 6.7):
    /// update &gt; install &gt; crash_recovery &gt; boot &gt; service_restart. updateDetected vem da sentinela
    /// .update (gravada pelo agente antes do msiexec; o MSI reinicia o servico): nesse caso o
    /// start e atribuido ao update independentemente do estado de boot/shutdown.
    /// </summary>
    public (string BootId, string StartReason) InitializeBoot(DateTimeOffset nowUtc, long monoMs, bool updateDetected = false)
    {
        var bootTime = nowUtc.AddMilliseconds(-monoMs);
        var prevBootId = _kv.KvGet(KeyBootId);
        var prevBootTimeStr = _kv.KvGet(KeyBootTime);
        var firstRun = _kv.KvGet(KeyFirstRunDone) is null;
        var cleanShutdown = _kv.KvGet(KeyCleanShutdown) == "1";

        var newBoot = true;
        if (prevBootId is not null && prevBootTimeStr is not null)
        {
            try
            {
                var prevBootTime = Iso.Parse(prevBootTimeStr);
                newBoot = Math.Abs((bootTime - prevBootTime).TotalSeconds) > 120;
            }
            catch (FormatException) { newBoot = true; }
        }

        var bootId = newBoot || prevBootId is null ? Guid.NewGuid().ToString() : prevBootId;

        var startReason =
            updateDetected ? "update" :
            firstRun ? "install" :
            !cleanShutdown ? "crash_recovery" :
            newBoot ? "boot" :
            "service_restart";

        _kv.KvSet(KeyBootId, bootId);
        _kv.KvSet(KeyBootTime, Iso.Format(bootTime));
        _kv.KvSet(KeyFirstRunDone, "1");
        _kv.KvSet(KeyCleanShutdown, "0"); // será religada no stop limpo

        BootId = bootId;
        return (bootId, startReason);
    }

    public void MarkCleanShutdown() => _kv.KvSet(KeyCleanShutdown, "1");
}
