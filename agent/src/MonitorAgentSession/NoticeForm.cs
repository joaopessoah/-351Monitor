using M351.Agent.Core.Privacy;

namespace MonitorAgentSession;

/// <summary>
/// Aviso de primeiro logon (Seção 6.5 / 9.4): "Esta máquina é monitorada — veja o que é
/// coletado", com botão "Entendi" → NOTICE_ACK. É evidência de CIÊNCIA, não consentimento.
///
/// O corpo pode vir da config do tenant (notice_text); o enquadramento jurídico é fixo e
/// concatenado pelo NoticeTextComposer — o tenant não edita essa parte.
/// </summary>
public sealed class NoticeForm : Form
{
    public bool Acknowledged { get; private set; }

    public NoticeForm(Action showCollectedWindow, string? tenantNoticeText = null)
    {
        Text = "Monitoramento corporativo";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 300);
        TopMost = true;

        var text = new Label
        {
            Dock = DockStyle.Top,
            Height = 200,
            Padding = new Padding(16),
            AutoEllipsis = true,
            Text = NoticeTextComposer.Compose(tenantNoticeText)
        };

        var seeButton = new Button
        {
            Text = "Ver o que é coletado",
            Width = 170,
            Height = 32,
            Left = 60,
            Top = 230
        };
        seeButton.Click += (_, _) => showCollectedWindow();

        var okButton = new Button
        {
            Text = "Entendi",
            Width = 120,
            Height = 32,
            Left = 280,
            Top = 230
        };
        okButton.Click += (_, _) =>
        {
            Acknowledged = true;
            Close();
        };

        Controls.Add(text);
        Controls.Add(seeButton);
        Controls.Add(okButton);
        AcceptButton = okButton;
    }
}
