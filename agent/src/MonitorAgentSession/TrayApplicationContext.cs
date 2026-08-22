using System.Diagnostics;
using System.Security.Principal;
using M351.Agent.Core;
using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Diagnostics;
using M351.Agent.Core.Events;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Privacy;
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
    private readonly int _sessionId;
    private readonly ILogSink _log;
    private readonly IDisposable _logDisposable;
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

    /// <summary>Evita duas janelas de aviso simultâneas quando a config chega enquanto o modal está aberto.</summary>
    private volatile bool _noticeShowing;

    public TrayApplicationContext(int sessionId)
    {
        _sessionId = sessionId;

        // Logs do helper junto aos do servico em %ProgramData% quando acessivel (Secao 6.6 l.461);
        // fallback para %LOCALAPPDATA% se o helper de baixo privilegio nao puder escrever la.
        var logsDir = ResolveLogsDirectory();
        var serilog = SerilogLogSink.CreateFile(logsDir, $"session-{sessionId}");
        _logDisposable = serilog;
        _log = serilog;

        _identity = new SessionIdentity(
            sessionId,
            WindowsIdentity.GetCurrent().User?.Value,
            $"{Environment.UserDomainName}\\{Environment.UserName}");

        _pipe = new PipeClient(sessionId, _log);
        _sink = new PipeEventSink(_pipe);
        _pipe.ConfigReceived += OnConfigReceived;
        _pipe.DiagnosticsResult += OnDiagnosticsResult;

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
        menu.Items.Add("Enviar diagnóstico ao suporte", null, (_, _) => SendDiagnosticsToSupport());
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
            // AGENT_ERROR do helper sai pelo pipe (o helper não toca a fila) — máx. 1/hora por tipo
            var errors = new AgentErrorReporter(_factory, ev => _sink.Emit(ev));
            _engine = new SessionCollectorEngine(
                new Win32ForegroundWindowQuery(),
                new Win32IdleTimeQuery(),
                _sink,
                _factory,
                _identity,
                () => _config,
                () => _locked,
                queueDepth: null, // o serviço injeta a saúde operacional no HEARTBEAT
                _log,
                errors);
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

            // Config nova pode trazer notice_version maior (o tenant reescreveu o aviso no portal):
            // o MaybeShowNotice compara com a versão confirmada localmente e reexibe sozinho.
            // Mesmo Task.Run do primeiro caso: o modal NUNCA pode bloquear o read-loop do pipe.
            _ = Task.Run(MaybeShowNotice);
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

    /// <summary>Versão confirmada localmente (null se nunca confirmada ou flag ilegível).</summary>
    private int? ReadAcknowledgedNoticeVersion()
    {
        try
        {
            if (File.Exists(NoticeFlagPath) &&
                int.TryParse(File.ReadAllText(NoticeFlagPath).Trim(), out var acked))
            {
                return acked;
            }
        }
        catch (Exception) { /* sem flag legível: trata como nunca confirmado (exibe) */ }
        return null;
    }

    /// <summary>
    /// Exibe o aviso quando a versão vigente da CONFIG DO TENANT (notice_version) é maior que a
    /// confirmada localmente: bump no portal reexibe o aviso na frota e gera novo NOTICE_ACK.
    /// O texto do corpo vem da config (notice_text); o enquadramento legal é fixo no agente.
    /// </summary>
    private void MaybeShowNotice()
    {
        var currentVersion = _config.NoticeVersion;
        if (!NoticeGate.ShouldShow(ReadAcknowledgedNoticeVersion(), currentVersion)) return;
        if (_noticeShowing) return; // já há um aviso na tela (config nova chegou no meio)

        _noticeShowing = true;
        try
        {
            var shownAt = DateTimeOffset.UtcNow;
            using var notice = new NoticeForm(() => ShowStatusWindow(), _config.NoticeText);
            notice.ShowDialog();

            if (!notice.Acknowledged || _factory is null) return;

            _sink.Emit(_factory.Create(EventTypes.NoticeAck,
                new NoticeAckData { NoticeVersion = currentVersion, ShownAt = Iso.Format(shownAt) },
                _identity.SessionId, _identity.WindowsSid, _identity.WindowsUser));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(NoticeFlagPath)!);
                File.WriteAllText(NoticeFlagPath, currentVersion.ToString());
            }
            catch (Exception) { /* reexibirá no próximo logon */ }
            _log.Info($"NOTICE_ACK v{currentVersion} emitido (evidência de ciência — não é consentimento).");
        }
        finally
        {
            _noticeShowing = false;
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

    /// <summary>
    /// Abre a página de transparência: a DESTE dispositivo (link por token) quando o servidor a
    /// entregou na config, caindo na página da organização (link por slug) quando não.
    ///
    /// O log registra apenas QUAL das duas foi aberta — a url por token carrega um segredo de
    /// baixo valor e não pode ir para arquivo de log. A caixa de erro mostra o endereço porque ela
    /// aparece na tela do próprio dono da máquina, que é exatamente quem pode vê-lo.
    /// </summary>
    private void OpenTransparencyUrl()
    {
        var config = _config;
        var url = TransparencyLink.Resolve(config);
        if (url is null)
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
            _log.Info($"Política de monitoramento aberta: {TransparencyLink.DescribeForLog(config)}.");
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
        var serverState = M351.Agent.Core.Net.ConnectionStateNames.ToHumanPtBr(_pipe.ConnectionState);

        // Erro de certificado (possível MITM): destaca com ícone de aviso (Seção 6.4 l.445).
        var isCertError = _pipe.ConnectionState == M351.Agent.Core.Net.ConnectionStateNames.ErroCertificado;
        var icon = isCertError ? MessageBoxIcon.Warning : MessageBoxIcon.Information;

        MessageBox.Show(
            $"Canal local: {pipeState}\nServidor: {serverState}\nÚltimo envio ao servidor: {lastSent}",
            "Status da conexão", MessageBoxButtons.OK, icon);
    }

    /// <summary>
    /// Envio do pacote de diagnóstico ao suporte (F5). O usuário precisa saber EXATAMENTE o que
    /// sai da máquina dele antes de confirmar: só logs técnicos do agente, já redigidos (sem
    /// título de janela, sem nome de usuário, sem conteúdo do que ele digita ou vê). Quem empacota
    /// e envia é o SERVIÇO — o helper apenas pede pelo pipe e mostra o resultado.
    /// </summary>
    private void SendDiagnosticsToSupport()
    {
        var confirm = MessageBox.Show(
            "Enviar um pacote de diagnóstico ao suporte da sua empresa?\n\n" +
            "O que É enviado: logs técnicos do agente já redigidos (horários, erros de conexão, " +
            "versão do agente, nome da máquina).\n" +
            "O que NÃO é enviado: títulos de janela, nomes de usuário, endereços de arquivos, " +
            "senhas ou qualquer conteúdo do que você digita, vê ou envia.\n\n" +
            "O envio é manual e só acontece se você confirmar agora.",
            "Enviar diagnóstico ao suporte", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        _pipe.Send(new PipeMessage { Kind = PipeMessage.KindDiagnosticsRequest });
        ShowBalloon("Diagnóstico", "Gerando e enviando o pacote de diagnóstico ao suporte…");
        _log.Info("Envio de diagnóstico solicitado pelo usuário no tray (confirmado).");
    }

    private void OnDiagnosticsResult(bool ok)
    {
        if (ok)
        {
            ShowBalloon("Diagnóstico enviado", "O pacote chegou ao suporte da sua empresa.");
            _log.Info("Diagnóstico enviado ao suporte com sucesso.");
        }
        else
        {
            ShowBalloon("Diagnóstico não enviado",
                "Não foi possível enviar agora. Tente novamente mais tarde ou fale com o suporte.");
            _log.Warn("Envio de diagnóstico ao suporte falhou (reportado no tray).");
        }
    }

    private void ShowBalloon(string title, string text)
    {
        try
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = text;
            _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(5_000);
        }
        catch (Exception ex)
        {
            _log.Warn($"Falha ao exibir o balão do tray: {ex.Message}");
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"+351 Monitor: agente de monitoramento corporativo\n\n" +
            $"Versão: {AgentVersionInfo.Current}\n" +
            $"Dispositivo: {_deviceId ?? "(não registrado)"}\n\n" +
            "Este agente é sempre visível e coleta apenas: aplicativo/título em foco\n" +
            "(conforme a política da empresa), sessão (logon/bloqueio), ociosidade e\n" +
            "saúde do agente. Nunca: teclas digitadas, tela, arquivos ou mensagens.",
            "Sobre o monitoramento", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// %ProgramData%\M351\MonitorAgent\logs quando o helper consegue escrever (proximo aos logs do
    /// servico); fallback %LOCALAPPDATA% para o helper de baixo privilegio sem acesso de escrita la.
    /// </summary>
    private static string ResolveLogsDirectory()
    {
        var programDataLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "M351", "MonitorAgent", "logs");
        try
        {
            Directory.CreateDirectory(programDataLogs);
            var probe = Path.Combine(programDataLogs, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return programDataLogs;
        }
        catch (Exception)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "M351", "MonitorAgent", "logs");
        }
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
            _logDisposable.Dispose();
        }
        base.Dispose(disposing);
    }
}
