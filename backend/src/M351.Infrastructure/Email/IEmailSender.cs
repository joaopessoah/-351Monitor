namespace M351.Infrastructure.Email;

/// <summary>IsHtml: corpo HTML simples (digest semanal); default texto puro (convites, links).</summary>
public record EmailMessage(string To, string Subject, string Body, bool IsHtml = false);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
