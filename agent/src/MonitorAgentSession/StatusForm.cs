using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Privacy;

namespace MonitorAgentSession;

/// <summary>
/// "O que está sendo coletado agora" (Seção 6.5): app ativo, título capturado (já mascarado/null),
/// estado ativo/ocioso/bloqueado, último envio, config aplicada e device_id - em tempo real.
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
    private readonly TextBox _maskTestInput = new()
    {
        PlaceholderText = "Digite um título para ver como ele chegaria ao servidor"
    };
    private readonly Label _maskTestResult = NewValueLabel();
    private readonly TitleMasker _masker = new();
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
        ClientSize = new Size(520, 344);

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

        // Teste de mascaramento (transparência verificável, Seção 6.5): o funcionário digita um
        // título hipotético e vê, pela MESMA config vigente do provedor de estado acima, o que o
        // TitleMasker deixaria chegar ao servidor — mesmo caminho de código da coleta real.
        table.Controls.Add(new Label
        {
            Text = "Teste de mascaramento:",
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 4)
        });
        _maskTestInput.Dock = DockStyle.Fill;
        _maskTestInput.Margin = new Padding(3, 12, 3, 4);
        table.Controls.Add(_maskTestInput);
        AddRow(table, "Como chegaria ao servidor:", _maskTestResult);
        _maskTestInput.TextChanged += (_, _) => RefreshMaskTest();

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
        Text = "-"
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

        _appLabel.Text = status?.ForegroundProcess ?? "-";
        _titleLabel.Text = status?.ForegroundTitle ?? "(não coletado)";
        _stateLabel.Text = status?.State switch
        {
            "active" => "Ativo",
            "idle" => "Ocioso",
            "locked" => "Bloqueado",
            _ => "-"
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
        RefreshMaskTest(); // config pode ter mudado desde a última digitação
    }

    private void RefreshMaskTest()
    {
        if (_maskTestInput.Text.Length == 0)
        {
            _maskTestResult.Text = "-";
            return;
        }
        // Processo fictício "exemplo.exe": o teste demonstra a política de títulos
        // (FULL/MASKED_PATTERNS/APP_ONLY e o rebaixamento em navegação anônima).
        var (_, _, config, _) = _getInfo();
        var data = _masker.Apply(new ForegroundSample("exemplo.exe", null, null, _maskTestInput.Text), config);
        _maskTestResult.Text = data.WindowTitle is null
            ? "(somente o aplicativo, o título não é enviado)"
            : data.TitleMasked ? $"{data.WindowTitle} (trechos mascarados)" : data.WindowTitle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
