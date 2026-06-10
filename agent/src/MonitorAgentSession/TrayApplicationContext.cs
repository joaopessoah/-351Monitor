using System.Diagnostics;
using System.Security.Principal;
using M351.Agent.Core;
using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Win32;
using Microsoft.Win32;

namespace MonitorAgentSession;

/// <summary>
/// Transparência por arquitetura (Princípio 1): NotifyIcon SEMPRE visível, sem opção "Sair",
/// sem flag de ocultação (a opção não existe no código). Menu: "O que está sendo coletado
/// agora", "Política de monitoramento", "Status da conexão", "Sobre". pt-BR.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    public const int NoticeVersion = 1; // versão do aviso NOTICE_ACK (Seção 6.5)

    private readonly int _sessionId;
    private readonly ILogSink _log;
    private readonly PipeClient _pipe;
    private readonly PipeEventSink _sink;
    private readonly NotifyIcon _trayIcon;
    private readonly CancellationTokenSource _cts = new();
    private readonly SessionIdentity _identity;

    private volatile AgentConfig _config = AgentConfig.FactoryDefault();
    private int _configVersion;
    private string? _deviceId;
    private SessionCollectorEngine? _engine;
    private EventFactory? _factory;
    private StatusForm? _statusForm;
    private bool _locked;
    private bool _collectorsStarted;

    public TrayApplicationContext(int sessionId)
    {
        _sessionId = sessionId;
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M351", "MonitorAgent");
        _log = new FileLogSink(Path.Combine(dataDir, "logs"), $"session-{sessionId}");

        _identity = new SessionIdentity(
            sessionId,
            WindowsIdentity.GetCurrent().User?.Value,
            $"{Environment.UserDomainName}\\{Environment.UserName}");

        _pipe = new PipeClient(sessionId, _log);
        _sink = new PipeEventSink(_pipe);
        _pipe.ConfigReceived += OnConfigReceived;

        SystemEvents.SessionSwitch += OnSessionSwitch;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Monitoramento corporativo ativo",
            Visible = true, // sempre visível — modo stealth é inexistente por design
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ShowStatusWindow();

        _log.Info($"Helper iniciado na sessão {sessionId} (aguardando config do serviço pelo pipe).");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("O que está sendo coletado agora", null, (_, _) => ShowStatusWindow());
        menu.Items.Add("Política de monitoramento", null, (_, _) => OpenTransparencyUrl());
        menu.Items.Add("Status da conexão", null, (_, _) => ShowConnectionStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sobre", null, (_, _) => ShowAbout());
        // SEM item "Sair" — por arquitetura (Seção 6.5)
        return menu;
    }

    /// <summary>A coleta só começa quando o serviço entrega config + boot_id pelo pipe.</summary>
    private void OnConfigReceived(PipeMessage message)
    {
        if (message.Config is not null) _config = message.Config;
        if (message.ConfigVersion is not null) _configVersion = message.ConfigVersion.Value;
        _deviceId = message.DeviceId ?? _deviceId;

        if (!_collectorsStarted && message.BootId is not null)
        {
            _collectorsStarted = true;
            _factory = new EventFactory(message.BootId);
            _engine = new SessionCollectorEngine(
                new Win32ForegroundWindowQuery(),
                new Win32IdleTimeQuery(),
                _sink,
                _factory,
                _identity,
                () => _config,
                () => _locked,
                queueDepth: null, // o serviço injeta o queue_depth real no HEARTBEAT
                _log);
            _ = _engine.RunAsync(_cts.Token);
            _log.Info("Coletores de sessão iniciados (janela ativa, ociosidade, heartbeat).");

            // Task.Run: o NoticeForm e modal (ShowDialog) e este handler roda no read-loop
            // do pipe — bloquear aqui deixaria o helper sem read pendente e configs novas
            // do servico nunca seriam aplicadas (nem entregues, com buffer de pipe cheio).
            _ = Task.Run(MaybeShowNotice);
        }
        else
        {
            _engine?.ApplyConfig(_config);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _locked = true;
                _engine?.IdleTracker.ResetOnLock();
                break;
            case SessionSwitchReason.SessionUnlock:
                _locked = false;
                break;
        }
    }

    // ------------------------------------------------------------ NOTICE_ACK (gate LGPD, Seção 6.5)

    private string NoticeFlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "M351", "MonitorAgent", "notice_ack.txt");

    private void MaybeShowNotice()
    {
        try
        {
            if (File.Exists(NoticeFlagPath) &&
                int.TryParse(File.ReadAllText(NoticeFlagPath).Trim(), out var acked) &&
                acked >= NoticeVersion)
            {
                return; // já confirmou esta versão do aviso — não reexibir a cada logon
            }
        }
        catch (Exception) { /* sem flag legível: exibe */ }

        var shownAt = DateTimeOffset.UtcNow;
        using var notice = new NoticeForm(() => ShowStatusWindow());
        notice.ShowDialog();

        if (notice.Acknowledged && _factory is not null)
        {
            _sink.Emit(_factory.Create(EventTypes.NoticeAck,
                new NoticeAckData { NoticeVersion = NoticeVersion, ShownAt = Iso.Format(shownAt) },
                _identity.SessionId, _identity.WindowsSid, _identity.WindowsUser));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(NoticeFlagPath)!);
                File.WriteAllText(NoticeFlagPath, NoticeVersion.ToString());
            }
            catch (Exception) { /* reexibirá no próximo logon */ }
            _log.Info("NOTICE_ACK emitido (evidência de ciência — não é consentimento).");
        }
    }

    // ------------------------------------------------------------ janelas do tray

    private void ShowStatusWindow()
    {
        if (_statusForm is { IsDisposed: false })
        {
            _statusForm.Activate();
            return;
        }
        _statusForm = new StatusForm(
            () => _engine?.Status,
            () => (_deviceId, _configVersion, _config, _pipe.IsConnected));
        _statusForm.Show();
    }

    private void OpenTransparencyUrl()
    {
        var url = _config.TransparencyUrl;
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show(
                "A política de monitoramento ainda não foi configurada pela sua empresa.\n" +
                "Use \"O que está sendo coletado agora\" para ver a coleta em tempo real.",
                "Política de monitoramento", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show($"Não foi possível abrir o navegador. Endereço: {url}",
                "Política de monitoramento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowConnectionStatus()
    {
        var pipeState = _pipe.IsConnected ? "conectado ao serviço" : "reconectando ao serviço…";
        var lastSent = _pipe.LastSentAt is { } t
            ? t.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
            : "ainda não enviado";
        MessageBox.Show(
            $"Canal local: {pipeState}\nÚltimo envio ao servidor: {lastSent}",
            "Status da conexão", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"+351 Monitor — Agente de monitoramento corporativo\n\n" +
            $"Versão: {AgentVersionInfo.Current}\n" +
            $"Dispositivo: {_deviceId ?? "(não registrado)"}\n\n" +
            "Este agente é sempre visível e coleta apenas: aplicativo/título em foco\n" +
            "(conforme a política da empresa), sessão (logon/bloqueio), ociosidade e\n" +
            "saúde do agente. Nunca: teclas digitadas, tela, arquivos ou mensagens.",
            "Sobre o monitoramento", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _cts.Cancel();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _pipe.Dispose();
        }
        base.Dispose(disposing);
    }
}
