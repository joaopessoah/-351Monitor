using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using M351.Infrastructure.Email;

namespace M351.IntegrationTests.Support;

/// <summary>Substitui o IEmailSender nos testes e captura os e-mails em memória.</summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public EmailMessage? LastFor(string recipient) =>
        Sent.LastOrDefault(m => string.Equals(m.To, recipient, StringComparison.OrdinalIgnoreCase));

    /// <summary>Extrai o token do link /convite/{token} do corpo do e-mail.</summary>
    public string ExtractInviteToken(string recipient)
    {
        var message = LastFor(recipient)
            ?? throw new InvalidOperationException($"Nenhum e-mail capturado para {recipient}.");
        var match = InviteLinkRegex().Match(message.Body);
        if (!match.Success)
        {
            throw new InvalidOperationException("Link de convite não encontrado no corpo do e-mail.");
        }

        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"/convite/([A-Za-z0-9_\-]+)")]
    private static partial Regex InviteLinkRegex();
}
