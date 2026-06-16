using System.Net;
using M351.Agent.Core.Logging;

namespace M351.Agent.Core.Net;

/// <summary>
/// Ponto unico de construcao do HttpClient compartilhado por enroll, batch e auto-update
/// (Secao 6.4 l.445). Politica de proxy:
///   - PROXYURL presente (install.json)  -> WebProxy explicito (proxy corporativo declarado no MSI);
///   - sem PROXYURL                       -> proxy de SISTEMA (WinHTTP), o default do .NET no Windows;
///   - sem PROXYURL e sem proxy de sistema -> conexao direta (o proprio WinHTTP resolve isso).
///
/// A validacao de TLS JAMAIS e desabilitada: nao mexemos em ServerCertificateCustomValidationCallback.
/// Uma inspecao MITM que apresente certificado nao confiavel falha o handshake e e classificada
/// por TlsErrorDetector como ErroCertificado (apenas reportada, nunca contornada).
/// </summary>
public static class AgentHttpClientFactory
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cria o HttpClient compartilhado. proxyUrl nulo/vazio = proxy de sistema (UseProxy=true sem
    /// Proxy explicito faz o handler usar o default do sistema/WinHTTP). Loga a escolha (sem segredo).
    /// </summary>
    public static HttpClient Create(string? proxyUrl, ILogSink log)
    {
        var handler = new HttpClientHandler();

        if (!string.IsNullOrWhiteSpace(proxyUrl) && Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxyUri) { UseDefaultCredentials = true };
            log.Info($"Proxy: usando PROXYURL do instalador ({proxyUri.Scheme}://{proxyUri.Authority}).");
        }
        else
        {
            // Default do .NET no Windows: usa o proxy de sistema (WinHTTP/WinINET). Sem proxy
            // configurado no sistema isso vira conexao direta automaticamente.
            handler.UseProxy = true;
            if (!string.IsNullOrWhiteSpace(proxyUrl))
                log.Warn($"PROXYURL invalido ('{proxyUrl}') — ignorado; usando proxy de sistema.");
            else
                log.Info("Proxy: usando proxy de sistema (WinHTTP) ou conexao direta.");
        }

        return new HttpClient(handler) { Timeout = DefaultTimeout };
    }
}
