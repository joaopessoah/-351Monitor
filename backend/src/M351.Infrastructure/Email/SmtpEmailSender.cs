using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace M351.Infrastructure.Email;

/// <summary>
/// Implementação SMTP configurável por env (Email__SmtpHost etc.). Não exercitada na F0
/// (sem servidor SMTP no ambiente) — selecionada via Email:Provider = "Smtp".
/// </summary>
public class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.SmtpUseTls,
            Credentials = string.IsNullOrEmpty(_options.SmtpUsername)
                ? null
                : new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword),
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = message.IsHtml,
        };
        mail.To.Add(message.To);

        await client.SendMailAsync(mail, cancellationToken);
        logger.LogInformation("E-mail SMTP enviado para {To}", message.To);
    }
}
