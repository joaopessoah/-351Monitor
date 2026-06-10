namespace M351.Api.Agent;

/// <summary>Linha de device_current_state em construção (projeção "agora" — Seção 7.1).</summary>
public sealed class CurrentStateRow
{
    public string State { get; set; } = "no_data";
    public string? WindowsSid { get; set; }
    public string? WindowsUsername { get; set; }
    public string? ForegroundProcess { get; set; }
    public string? ForegroundTitle { get; set; }
    public DateTimeOffset? StateSince { get; set; }
    public DateTimeOffset? AppSince { get; set; }
}

/// <summary>
/// Projeção "agora" atualizada NO CAMINHO DA INGESTÃO (Seção 7.1): aplica os eventos relevantes
/// em ordem de seq — ACTIVE_WINDOW_CHANGED (active + app), IDLE_START/IDLE_END, LOCK/UNLOCK,
/// SESSION_END/AGENT_STOP/SYSTEM_SUSPEND (off_clean), HEARTBEAT (refresh). Não é a máquina de
/// estados do pipeline (Seção 7.3, F2) — é apenas o snapshot para o dashboard de presença.
/// </summary>
public static class CurrentStateProjector
{
    /// <summary>Aplica um evento à linha corrente. Eventos não-relevantes só atualizam usuário.</summary>
    public static void Apply(CurrentStateRow row, ParsedEvent e)
    {
        if (e.WindowsSid is not null)
        {
            row.WindowsSid = e.WindowsSid;
        }

        if (e.WindowsUser is not null)
        {
            row.WindowsUsername = e.WindowsUser;
        }

        switch (e.Type)
        {
            case EventTypes.ActiveWindowChanged:
                if (row.State != "active")
                {
                    row.State = "active";
                    row.StateSince = e.OccurredAt;
                }

                if (!string.Equals(row.ForegroundProcess, e.ProcessName, StringComparison.Ordinal))
                {
                    row.AppSince = e.OccurredAt;
                }

                row.ForegroundProcess = e.ProcessName;
                row.ForegroundTitle = e.WindowTitle; // já vem mascarado/null do agente (Seção 6.3)
                break;

            case EventTypes.IdleStart:
                row.State = "idle";
                // fechamento retroativo (N5): o estado ocioso começa no último input real
                row.StateSince = e.LastInputAt ?? e.OccurredAt;
                break;

            case EventTypes.IdleEnd:
                row.State = "active";
                row.StateSince = e.OccurredAt;
                break;

            case EventTypes.Lock:
                row.State = "locked";
                row.StateSince = e.OccurredAt;
                break;

            case EventTypes.Unlock:
                row.State = "active";
                row.StateSince = e.OccurredAt;
                break;

            case EventTypes.SessionEnd:
                row.State = "off_clean";
                row.StateSince = e.OccurredAt;
                row.WindowsSid = null;
                row.WindowsUsername = null;
                row.ForegroundProcess = null;
                row.ForegroundTitle = null;
                break;

            case EventTypes.AgentStop:
            case EventTypes.SystemSuspend:
                row.State = "off_clean";
                row.StateSince = e.OccurredAt;
                row.ForegroundProcess = null;
                row.ForegroundTitle = null;
                break;

            case EventTypes.Heartbeat:
                // refresh: o heartbeat carrega o estado corrente (active|idle|locked|no_session)
                if (e.HeartbeatState is { Length: > 0 } hbState)
                {
                    if (row.State != hbState)
                    {
                        row.State = hbState;
                        row.StateSince = e.OccurredAt;
                    }

                    if (e.ProcessName is not null)
                    {
                        row.ForegroundProcess = e.ProcessName;
                    }
                }

                break;
        }
    }
}
