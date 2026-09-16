using M351.Agent.Core.Privacy;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Redução da barra de endereço a DOMÍNIO REGISTRÁVEL. As entradas destes testes são textos
/// REAIS de omnibox, medidos no Chrome e no Edge do Windows 11 pt-BR: o Chromium mostra ora a
/// url completa, ora só host+caminho (esconde https:// e www.), e o Firefox sempre a url completa.
/// </summary>
public class SiteDomainTests
{
    [Theory]
    // formas reais medidas na omnibox
    [InlineData("https://www.gov.br/pt-br/servicos", "gov.br")]
    [InlineData("lista.mercadolivre.com.br/notebook", "mercadolivre.com.br")]
    [InlineData("https://bennerind.portalbenner.com.br/Financeiro/ProjecaoFaturamento", "portalbenner.com.br")]
    [InlineData("globo.com", "globo.com")]
    // esquema, porta, âncora e query somem
    [InlineData("http://exemplo.com.br:8080/rel?cpf=123.456.789-09#topo", "exemplo.com.br")]
    [InlineData("https://docs.google.com/document/d/1a2b3c/edit", "google.com")]
    // sufixo público de dois rótulos: o registrável tem três
    [InlineData("https://receita.fazenda.gov.br/consulta", "fazenda.gov.br")]
    [InlineData("https://empresa.eco.br", "empresa.eco.br")]
    [InlineData("https://www.bbc.co.uk/news", "bbc.co.uk")]
    // sistema interno por IP continua identificável
    [InlineData("http://192.168.0.10/erp/pedidos", "192.168.0.10")]
    public void Reduz_url_ao_dominio_registravel(string barra, string esperado) =>
        Assert.Equal(esperado, SiteDomain.FromAddressBar(barra));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // busca digitada na omnibox NUNCA vira dado coletado
    [InlineData("como fazer bolo de cenoura")]
    [InlineData("mercadoliv")]
    [InlineData("nota fiscal 2026")]
    // páginas internas do navegador e arquivo local não são site visitado
    [InlineData("chrome://settings/passwords")]
    [InlineData("edge://favorites")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/Users/joao/Documentos/rescisao.pdf")]
    [InlineData("view-source:https://exemplo.com.br")]
    [InlineData("javascript:alert(1)")]
    public void Entrada_que_nao_e_endereco_nao_vira_dominio(string? barra) =>
        Assert.Null(SiteDomain.FromAddressBar(barra));

    [Fact]
    public void Credencial_embutida_na_url_nao_sai_da_maquina()
    {
        var dominio = SiteDomain.FromAddressBar("https://joao:senha123@intranet.empresa.com.br/rh");
        Assert.Equal("empresa.com.br", dominio);
    }

    [Fact]
    public void Www_e_ponto_final_somem_na_normalizacao()
    {
        Assert.Equal("uol.com.br", SiteDomain.FromAddressBar("https://WWW.UOL.COM.BR./noticias"));
    }

    [Fact]
    public void Reducao_e_idempotente()
    {
        var uma = SiteDomain.FromAddressBar("https://loja.americanas.com.br/produto/123");
        var duas = SiteDomain.FromAddressBar(uma);
        Assert.Equal("americanas.com.br", uma);
        Assert.Equal(uma, duas);
    }

    [Fact]
    public void Dominio_nunca_passa_do_teto_de_tamanho()
    {
        var gigante = "https://" + new string('a', 300) + ".com.br/x";
        Assert.Null(SiteDomain.FromAddressBar(gigante)); // rótulo acima de 63 chars não é host válido
    }
}
