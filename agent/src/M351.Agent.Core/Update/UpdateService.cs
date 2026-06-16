using M351.Agent.Core.Logging;
using M351.Agent.Core.Storage;

namespace M351.Agent.Core.Update;

/// <summary>
/// Loop de auto-update do servico (Secao 6.7): checa o manifesto a cada 6 h com jitter ate 30 min;
/// a primeira checagem ocorre apos um pequeno atraso pos-boot (nao competir com o start). Decide
/// update normal vs forcado (current &lt; min_version), baixa+verifica+aplica via UpdateInstaller.
/// 204/erros nao derrubam o agente: loga e reagenda (sem backoff N14 — a cadencia ja e longa).
/// </summary>
public sealed class UpdateService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly UpdateClient _client;
    private readonly UpdateInstaller _installer;
    private readonly AgentStateStore _state;
    private readonly ILogSink _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan> _nextJitter;

    public UpdateService(
        UpdateClient client,
        UpdateInstaller installer,
        AgentStateStore state,
        ILogSink log,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan>? jitter = null)
    {
        _client = client;
        _installer = installer;
        _state = state;
        _log = log;
        _delay = delay ?? Task.Delay;
        _nextJitter = jitter ?? (() => TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * MaxJitter.TotalMilliseconds));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try { await _delay(InitialDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Se o servico estiver descendo por causa do proprio update, o ct ja foi cancelado.
                if (await CheckOnceAsync(ct)) return; // msiexec disparado: o MSI conduz o stop daqui
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("Auto-update: falha inesperada no ciclo (continua no proximo).", ex);
            }

            var wait = CheckInterval + _nextJitter();
            try { await _delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Uma checagem: manifesto -> decisao -> (se for o caso) aplicar. Retorna true se o instalador
    /// foi disparado (encerra o loop; o MSI assume daqui). Publica para teste.
    /// </summary>
    public async Task<bool> CheckOnceAsync(CancellationToken ct)
    {
        if (!_state.IsEnrolled)
        {
            _log.Info("Auto-update: agente nao enrolado — checagem pulada.");
            return false;
        }

        var current = AgentVersionInfo.Current;
        var manifest = await _client.FetchManifestAsync(current, ct);
        var decision = UpdatePlanner.Decide(manifest, current);

        if (!decision.ShouldUpdate)
        {
            _log.Info($"Auto-update: nada a fazer ({decision.Reason}).");
            return false;
        }

        _log.Info(decision.Action == UpdateAction.ForcedUpdate
            ? $"Auto-update FORCADO: {decision.Reason}."
            : $"Auto-update disponivel: {decision.Reason}.");

        return await _installer.ApplyAsync(manifest!, ct);
    }
}
