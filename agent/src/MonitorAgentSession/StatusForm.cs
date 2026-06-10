using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;

namespace MonitorAgentSession;

/// <summary>
/// "O que está sendo coletado agora" (Seção 6.5): app ativo, título capturado (já mascarado/null),
/// estado ativo/ocioso/bloqueado, último envio, config aplicada e device_id — em tempo real.
/// </summary>
public sealed class StatusForm : Form
{
    private readonly Func<CollectorStatus?> _getStatus;
    private readonly Func<(string? DeviceId, int ConfigVersion, AgentConfig Config, bool PipeConnected)> _getInfo;
    private readonly Label _appLabel = NewValueLabel();
    private readonly Label _titleLabel = NewValueLabel();
    private readonly Label _stateLabel = NewValueLabel();
    private readonly Label _lastSentLabel = NewValueLabel();
    private readonly Label _policyLabel = NewValueLabel();
    private readonly Label _deviceLabel = NewValueLabel();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public StatusForm(Func<CollectorStatus?> getStatus,
        Func<(string?, int, AgentConfig, bool)> getInfo)
    {
        _getStatus = getStatus;
        _getInfo = getInfo;

        Text = "O que está sendo coletado agora";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 280);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
            AutoSize = true
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(table, "Aplicativo em foco:", _appLabel);
        AddRow(table, "Título capturado:", _titleLabel);
        AddRow(table, "Estado:", _stateLabel);
        AddRow(table, "Último envio:", _lastSentLabel);
        AddRow(table, "Política de títulos:", _policyLabel);
        AddRow(table, "Dispositivo:", _deviceLabel);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(16, 4, 16, 4),
            Text = "Coleta limitada a: aplicativo/título em foco, sessão, ociosidade e saúde do agente.\n" +
                   "Nunca: teclas digitadas, capturas de tela, arquivos, e-mails ou mensagens.",
            ForeColor = SystemColors.GrayText
        };

        Controls.Add(table);
        Controls.Add(hint);

        _timer.Tick += (_, _) => Refresh_();
        _timer.Start();
        Refresh_();
    }

    private static Label NewValueLabel() => new()
    {
        AutoSize = true,
        Font = new Font(FontFamily.GenericSansSerif, 9.5f, FontStyle.Bold),
        Text = "—"
    };

    private static void AddRow(TableLayoutPanel table, string caption, Label value)
    {
        table.Controls.Add(new Label { Text = caption, AutoSize = true, Padding = new Padding(0, 4, 0, 4) });
        value.Padding = new Padding(0, 4, 0, 4);
        table.Controls.Add(value);
    }

    private void Refresh_()
    {
        var status = _getStatus();
        var (deviceId, configVersion, config, pipeConnected) = _getInfo();

        _appLabel.Text = status?.ForegroundProcess ?? "—";
        _titleLabel.Text = status?.ForegroundTitle ?? "(não coletado)";
        _stateLabel.Text = status?.State switch
        {
            "active" => "Ativo",
            "idle" => "Ocioso",
            "locked" => "Bloqueado",
            _ => "—"
        };
        _lastSentLabel.Text = status?.LastSentAt is { } t
            ? t.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
            : pipeConnected ? "aguardando primeiro envio" : "serviço indisponível";
        _policyLabel.Text = config.WindowTitlePolicy switch
        {
            TitlePolicies.Full => $"Título completo (config v{configVersion})",
            TitlePolicies.AppOnly => $"Somente aplicativo (config v{configVersion})",
            _ => $"Título com mascaramento (config v{configVersion})"
        };
        _deviceLabel.Text = deviceId ?? "(não registrado)";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
