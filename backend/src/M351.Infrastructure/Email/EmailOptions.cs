namespace M351.Infrastructure.Email;

public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>"Dev" (grava .txt em disco) ou "Smtp".</summary>
    public string Provider { get; set; } = "Dev";

    /// <summary>Diretório dos e-mails do provider Dev (relativo ao diretório de trabalho por padrão — seguro em container).</summary>
    public string DevMailDirectory { get; set; } = ".dev-mail";

    /// <summary>Caixa real da Hostinger. O Email__SmtpPassword vem do .env, nunca do repo.</summary>
    public string SmtpHost { get; set; } = "smtp.hostinger.com";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string SmtpUsername { get; set; } = "contato@mais351monitor.com.br";
    public string SmtpPassword { get; set; } = string.Empty;
    /// <summary>
    /// Precisa ser a MESMA caixa autenticada em SmtpUsername: a Hostinger recusa
    /// MAIL FROM de endereco diferente, e o SPF do dominio so cobre o que sai por ela.
    /// </summary>
    public string FromAddress { get; set; } = "contato@mais351monitor.com.br";
    public string FromName { get; set; } = "+351 Monitor";
}
