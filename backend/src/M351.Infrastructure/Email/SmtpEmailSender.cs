using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace M351.Infrastructure.Email;

/// <summary>
/// Implementação SMTP configurável por env (Email__SmtpHost etc.), selecionada via
/// Email:Provider = "Smtp". O padrão é a caixa contato@mais351monitor.com.br na
/// Hostinger: porta 587 com EnableSsl = STARTTLS, que é o que o System.Net.Mail fala
/// (a 465, SSL implícito, ele NÃO suporta — não troque a porta sem trocar de cliente).
/// Só a senha fica fora do repo, em Email__SmtpPassword.
/// </summary>
public class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        // Host vazio com Provider=Smtp era o pior dos mundos: o SmtpClient estoura
        // "Failure sending mail." em TODO envio, sem dizer o que falta, e cada job
        // do worker repetia o mesmo erro opaco toda semana. A mensagem abaixo nomeia
        // a variavel que resolve. Falha o envio, nao o processo: derrubar a API por
        // causa do e-mail tiraria o login do ar junto.
        if (string.IsNullOrWhiteSpace(_options.SmtpHost))
        {
            throw new InvalidOperationException(
                "Email:Provider = \"Smtp\" mas Email:SmtpHost esta vazio. Defina Email__SmtpHost " +
                "(ex.: smtp.hostinger.com) ou volte Email__Provider para \"Dev\".");
        }

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
