namespace MonitorAgentSession;

/// <summary>
/// Aviso de primeiro logon (Seção 6.5 / 9.4): "Esta máquina é monitorada — veja o que é
/// coletado", com botão "Entendi" → NOTICE_ACK. É evidência de CIÊNCIA, não consentimento.
/// </summary>
public sealed class NoticeForm : Form
{
    public bool Acknowledged { get; private set; }

    public NoticeForm(Action showCollectedWindow)
    {
        Text = "Monitoramento corporativo";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 220);
        TopMost = true;

        var text = new Label
        {
            Dock = DockStyle.Top,
            Height = 120,
            Padding = new Padding(16),
            Text = "Esta máquina é monitorada pela sua empresa.\n\n" +
                   "São coletados: aplicativo e título da janela em foco (conforme a política da " +
                   "empresa), eventos de sessão (logon/bloqueio), ociosidade e saúde do agente.\n" +
                   "Este aviso registra a sua ciência. Não é um pedido de consentimento."
        };

        var seeButton = new Button
        {
            Text = "Ver o que é coletado",
            Width = 170,
            Height = 32,
            Left = 60,
            Top = 150
        };
        seeButton.Click += (_, _) => showCollectedWindow();

        var okButton = new Button
        {
            Text = "Entendi",
            Width = 120,
            Height = 32,
            Left = 280,
            Top = 150
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
