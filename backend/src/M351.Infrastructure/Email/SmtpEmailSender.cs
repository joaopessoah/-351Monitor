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

        // Attachment não copia o stream: ele é lido no SendMailAsync, então os MemoryStream
        // precisam viver ATÉ o envio. O mail.Dispose (using acima) descarta os anexos e,
        // com eles, os streams.
        foreach (var attachment in message.Attachments ?? [])
        {
            mail.Attachments.Add(new Attachment(
                new MemoryStream(attachment.Content), attachment.FileName, attachment.ContentType));
        }

        await client.SendMailAsync(mail, cancellationToken);
        logger.LogInformation(
            "E-mail SMTP enviado para {To} com {Anexos} anexo(s)",
            message.To, message.Attachments?.Count ?? 0);
    }
}
