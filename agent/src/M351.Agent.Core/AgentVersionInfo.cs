namespace M351.Agent.Core;

/// <summary>
/// Versão do agente — FONTE ÚNICA. Lida pelo build do MSI (build-agent-msi.ps1 extrai daqui por
/// regex), carimbada no AGENT_START, exibida no tray e comparada com o manifesto de auto-update.
/// Subir esta constante é o primeiro passo de todo release (docs/runbooks/publicar-release-agente.md).
/// </summary>
public static class AgentVersionInfo
{
    /// <summary>
    /// 1.1.0 (16/09/2026): coleta do domínio do site em foco e do nome do arquivo aberto, mais a
    /// correção do rebaixamento em navegação anônima (Chrome deixou de marcar o título Win32).
    /// MINOR e não PATCH porque a superfície de coleta mudou — e é isso que o funcionário vê
    /// reaparecer no aviso de ciência.
    /// </summary>
    public const string Current = "1.1.0";
}
