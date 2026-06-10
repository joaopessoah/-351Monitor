using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>
/// Lança e supervisiona 1 MonitorAgentSession.exe por sessão interativa (Seção 6.1):
/// WTSQueryUserToken → DuplicateTokenEx → CreateEnvironmentBlock → CreateProcessAsUser,
/// com watchdog N19 (relança em 5 s; máx. 5 relançamentos/10 min → AGENT_TAMPER; depois 15 min).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionManager
{
    private readonly AgentRuntime _runtime;
    private readonly ILogSink _log;
    private readonly ConcurrentDictionary<int, SessionHelperHost> _helpers = new();

    public SessionManager(AgentRuntime runtime, ILogSink log)
    {
        _runtime = runtime;
        _log = log;
    }

    public void Start()
    {
        foreach (var (sessionId, state) in ServiceNativeMethods.EnumerateSessions())
        {
            if (state == ServiceNativeMethods.WTSActive && sessionId != 0)
                EnsureHelper(sessionId);
        }
    }

    public void EnsureHelper(int sessionId)
    {
        if (_runtime.State.Unenrolled) return;
        var host = _helpers.GetOrAdd(sessionId, id => new SessionHelperHost(id, _runtime, _log, this));
        host.Start();
    }

    public void Remove(int sessionId)
    {
        if (_helpers.TryRemove(sessionId, out var host)) host.Stop();
    }

    public void StopAll()
    {
        foreach (var sessionId in _helpers.Keys.ToArray()) Remove(sessionId);
    }

    public void BroadcastConfig()
    {
        foreach (var host in _helpers.Values) host.Pipe?.SendConfig();
    }

    public string? GetSessionSid(int sessionId) =>
        _helpers.TryGetValue(sessionId, out var host) ? host.UserSid?.Value : null;

    internal void EmitTamper(string reason, int sessionId)
    {
        if (_runtime.State.Unenrolled) return;
        _runtime.Queue.Enqueue(_runtime.Factory.Create(EventTypes.AgentTamper,
            new AgentTamperData { Reason = reason }, sessionId));
        _log.Warn($"AGENT_TAMPER emitido (reason={reason}, sessão {sessionId}).");
    }
}

/// <summary>Helper de uma sessão: pipe + processo + watchdog (N19).</summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionHelperHost
{
    private readonly int _sessionId;
    private readonly AgentRuntime _runtime;
    private readonly ILogSink _log;
    private readonly SessionManager _manager;
    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _relaunches = [];
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _stopping;

    public PipeServer? Pipe { get; private set; }
    public SecurityIdentifier? UserSid { get; private set; }

    public SessionHelperHost(int sessionId, AgentRuntime runtime, ILogSink log, SessionManager manager)
    {
        _sessionId = sessionId;
        _runtime = runtime;
        _log = log;
        _manager = manager;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();
        }
        _ = SuperviseAsync(_cts!.Token);
    }

    public void Stop()
    {
        lock (_gate) { _stopping = true; }
        _cts?.Cancel();
        Pipe?.Dispose();
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Process? process = null;
                try
                {
                    process = Launch();
                }
                catch (Exception ex)
                {
                    _log.Error($"Falha ao lançar helper na sessão {_sessionId}.", ex);
                }

                if (process is not null)
                {
                    try { await process.WaitForExitAsync(ct); }
                    catch (OperationCanceledException) { return; }
                    finally { process.Dispose(); }
                }

                if (ct.IsCancellationRequested || _stopping) return;

                // Watchdog N19
                var now = DateTimeOffset.UtcNow;
                _relaunches.Add(now);
                _relaunches.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));

                if (_relaunches.Count > 5)
                {
                    _manager.EmitTamper("helper_killed_repeatedly", _sessionId);
                    _log.Warn($"Helper da sessão {_sessionId} excedeu 5 relançamentos/10 min — retry a cada 15 min.");
                    try { await Task.Delay(TimeSpan.FromMinutes(15), ct); }
                    catch (OperationCanceledException) { return; }
                }
                else
                {
                    _manager.EmitTamper("helper_killed", _sessionId);
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Watchdog do helper (sessão {_sessionId}) falhou.", ex);
        }
    }

    private Process? Launch()
    {
        if (!ServiceNativeMethods.WTSQueryUserToken(_sessionId, out var userToken))
        {
            _log.Warn($"WTSQueryUserToken falhou para a sessão {_sessionId} (sessão sem usuário?).");
            return null;
        }

        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            if (!ServiceNativeMethods.DuplicateTokenEx(userToken, ServiceNativeMethods.TokenAllAccess,
                    IntPtr.Zero, ServiceNativeMethods.SecurityImpersonation, ServiceNativeMethods.TokenPrimary,
                    out primaryToken))
            {
                _log.Warn($"DuplicateTokenEx falhou para a sessão {_sessionId}.");
                return null;
            }

            using (var identity = new WindowsIdentity(primaryToken))
            {
                UserSid = identity.User;
            }

            // pipe primeiro (DACL restrita ao SID do usuário + SYSTEM)
            Pipe ??= new PipeServer(_sessionId, UserSid, _runtime, _log,
                () => _manager.EmitTamper("pipe_denied", _sessionId));
            Pipe.Start();

            if (!ServiceNativeMethods.CreateEnvironmentBlock(out environment, primaryToken, false))
                environment = IntPtr.Zero;

            var exe = Path.Combine(AppContext.BaseDirectory, "MonitorAgentSession.exe");
            var commandLine = new StringBuilder($"\"{exe}\" --session {_sessionId}");
            var si = new ServiceNativeMethods.STARTUPINFO
            {
                lpDesktop = @"winsta0\default"
            };
            si.cb = System.Runtime.InteropServices.Marshal.SizeOf<ServiceNativeMethods.STARTUPINFO>();

            if (!ServiceNativeMethods.CreateProcessAsUser(primaryToken, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    ServiceNativeMethods.CreateUnicodeEnvironment | ServiceNativeMethods.CreateNoWindow,
                    environment, null, ref si, out var pi))
            {
                _log.Warn($"CreateProcessAsUser falhou para a sessão {_sessionId} " +
                          $"(erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                return null;
            }

            ServiceNativeMethods.CloseHandle(pi.hThread);
            ServiceNativeMethods.CloseHandle(pi.hProcess);
            _log.Info($"Helper lançado na sessão {_sessionId} (pid {pi.dwProcessId}).");
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            if (environment != IntPtr.Zero) ServiceNativeMethods.DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) ServiceNativeMethods.CloseHandle(primaryToken);
            ServiceNativeMethods.CloseHandle(userToken);
        }
    }
}
