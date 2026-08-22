namespace M351.Infrastructure.Email;

/// <summary>
/// Anexo de e-mail com o conteúdo JÁ EM MEMÓRIA. Existe para os relatórios pequenos gerados
/// pelo próprio produto (o CSV de saúde de conta do CS, alguns poucos KB); arquivo de CLIENTE
/// continua saindo por LINK autenticado no portal, nunca por anexo, porque e-mail não é canal
/// de distribuição de dado pessoal (mesma regra já aplicada à jornada semanal).
/// </summary>
public record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>IsHtml: corpo HTML simples (digest semanal); default texto puro (convites, links).</summary>
public record EmailMessage(
    string To,
    string Subject,
    string Body,
    bool IsHtml = false,
    IReadOnlyList<EmailAttachment>? Attachments = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
