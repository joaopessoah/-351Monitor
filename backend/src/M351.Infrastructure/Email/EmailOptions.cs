namespace M351.Infrastructure.Email;

public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>"Dev" (grava .txt em disco) ou "Smtp".</summary>
    public string Provider { get; set; } = "Dev";

    /// <summary>Diretório dos e-mails do provider Dev (relativo ao diretório de trabalho por padrão — seguro em container).</summary>
    public string DevMailDirectory { get; set; } = ".dev-mail";

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromAddress { get; set; } = "nao-responda@351monitor.com.br";
    public string FromName { get; set; } = "+351 Monitor";
}
