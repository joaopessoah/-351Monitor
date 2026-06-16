using System.Net;
using System.Reflection;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Net;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Escolha de proxy (Secao 6.4 l.445): PROXYURL explicito vs proxy de sistema (WinHTTP) vs direta.
/// A validacao de TLS JAMAIS e desabilitada — confirmamos que o callback de validacao continua nulo.
/// </summary>
public class AgentHttpClientFactoryTests
{
    private static HttpClientHandler ExtractHandler(HttpClient client)
    {
        // O handler interno do HttpClient nao e publico; lemos via reflection apenas no teste.
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = field!.GetValue(client);
        // .NET embrulha o handler em um HttpMessageHandler delegante quando ha DiagnosticsHandler;
        // desce ate achar o HttpClientHandler.
        while (handler is DelegatingHandler dh) handler = dh.InnerHandler;
        return Assert.IsType<HttpClientHandler>(handler);
    }

    [Fact]
    public void Com_PROXYURL_usa_WebProxy_explicito()
    {
        using var client = AgentHttpClientFactory.Create("http://proxy.acme.local:8080", new NullLogSink());
        var handler = ExtractHandler(client);

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("proxy.acme.local", proxy.Address!.Host);
        Assert.Equal(8080, proxy.Address.Port);
    }

    [Fact]
    public void Sem_PROXYURL_usa_proxy_de_sistema_sem_WebProxy_explicito()
    {
        using var client = AgentHttpClientFactory.Create(null, new NullLogSink());
        var handler = ExtractHandler(client);

        Assert.True(handler.UseProxy);          // habilitado => usa o proxy de sistema (WinHTTP)
        Assert.Null(handler.Proxy);             // sem WebProxy explicito => default do sistema/direta
    }

    [Fact]
    public void PROXYURL_invalido_cai_para_proxy_de_sistema()
    {
        using var client = AgentHttpClientFactory.Create("nao-e-uma-url", new NullLogSink());
        var handler = ExtractHandler(client);

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void Nunca_desabilita_a_validacao_de_certificado()
    {
        using var direct = AgentHttpClientFactory.Create(null, new NullLogSink());
        using var proxied = AgentHttpClientFactory.Create("http://proxy:3128", new NullLogSink());

        // Callback de validacao customizado == null => validacao padrao do .NET permanece ativa.
        Assert.Null(ExtractHandler(direct).ServerCertificateCustomValidationCallback);
        Assert.Null(ExtractHandler(proxied).ServerCertificateCustomValidationCallback);
    }
}
