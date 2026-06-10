namespace M351.Infrastructure.Email;

public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>"Dev" (grava .txt em disco) ou "Smtp".</summary>
    public string Provider { get; set; } = "Dev";

    /// <summary>Diretório dos e-mails do provider Dev.</summary>
    public string DevMailDirectory { get; set; } = @"C:\dev\351-monitor\.dev-mail";

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromAddress { get; set; } = "nao-responda@351monitor.com.br";
    public string FromName { get; set; } = "+351 Monitor";
}
