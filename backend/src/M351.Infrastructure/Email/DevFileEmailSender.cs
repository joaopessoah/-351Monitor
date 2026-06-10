using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace M351.Infrastructure.Email;

/// <summary>Implementação de desenvolvimento: grava cada e-mail como .txt (assunto + corpo + links).</summary>
public class DevFileEmailSender(IOptions<EmailOptions> options, ILogger<DevFileEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DevMailDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
        var safeTo = string.Concat(message.To.Select(c => char.IsLetterOrDigit(c) || c is '.' or '@' or '-' or '_' ? c : '_'));
        var path = Path.Combine(_options.DevMailDirectory, $"{timestamp}_{safeTo}.txt");

        var content = $"""
            Para: {message.To}
            Assunto: {message.Subject}
            Data: {DateTimeOffset.UtcNow:O}

            {message.Body}
            """;

        await File.WriteAllTextAsync(path, content, cancellationToken);
        logger.LogInformation("E-mail (Dev) gravado em {Path} para {To}", path, message.To);
    }
}
