using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Privacy;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Enforcement de privacidade dos DOIS campos novos do ACTIVE_WINDOW_CHANGED (site_domain e
/// document_name). A regra que amarra tudo: eles seguem a MESMA política do título — o que a
/// política proíbe no título, proíbe neles.
/// </summary>
public class TitleMaskerSiteDocumentTests
{
    private static AgentConfig Config(
        string policy = TitlePolicies.MaskedPatterns,
        bool site = true,
        bool document = true,
        params string[] patterns)
    {
        var config = AgentConfig.FactoryDefault();
        config.WindowTitlePolicy = policy;
        config.SiteCapture = site;
        config.DocumentCapture = document;
        config.MaskedPatterns = patterns.Length > 0 ? patterns.ToList() : [];
        return config;
    }

    private static ForegroundSample Navegador(
        string? url, string? titulo = "Notebook | Mercado Livre - Google Chrome",
        string processo = "chrome.exe", params string[] nomesDaJanela) =>
        new(processo, $@"C:\apps\{processo}", null, titulo, new IntPtr(42), url, nomesDaJanela);

    [Fact]
    public void Navegador_comum_rende_dominio_registravel()
    {
        var dados = new TitleMasker().Apply(
            Navegador("https://lista.mercadolivre.com.br/notebook"), Config());

        Assert.Equal("mercadolivre.com.br", dados.SiteDomain);
        Assert.Equal("Notebook | Mercado Livre - Google Chrome", dados.WindowTitle);
    }

    [Fact]
    public void APP_ONLY_nao_coleta_site_nem_documento()
    {
        var dados = new TitleMasker().Apply(
            Navegador("https://www.gov.br/servicos", "Contrato.docx - Word"), Config(TitlePolicies.AppOnly));

        Assert.Null(dados.WindowTitle);
        Assert.Null(dados.SiteDomain);
        Assert.Null(dados.DocumentName);
    }

    /// <summary>
    /// Regressão do vazamento medido em 16/09/2026: o Edge escreve "[InPrivate]" NO MEIO do
    /// título, e o teste por sufixo que existia aqui nunca casava — o título da janela anônima
    /// chegava ao servidor.
    /// </summary>
    [Fact]
    public void Janela_InPrivate_do_Edge_e_reconhecida_mesmo_com_a_marca_no_meio()
    {
        var titulo = "UOL - Seu universo online — [InPrivate] — Microsoft Edge";
        var dados = new TitleMasker().Apply(
            Navegador("https://www.uol.com.br", titulo, "msedge.exe"), Config(TitlePolicies.Full));

        Assert.Null(dados.WindowTitle);
        Assert.Null(dados.SiteDomain);
    }

    /// <summary>
    /// Regressão do mesmo dia: o Chrome atual NÃO marca a janela anônima no título Win32 — a
    /// marca só existe no nome acessível da janela. Sem olhar para lá, navegação anônima do
    /// Chrome era coletada como navegação comum.
    /// </summary>
    [Fact]
    public void Janela_anonima_do_Chrome_e_reconhecida_pelo_nome_acessivel()
    {
        var titulo = "Terra - Seu Portal de Notícias - Google Chrome";
        var dados = new TitleMasker().Apply(
            Navegador("terra.com.br", titulo, "chrome.exe",
                titulo, titulo + " (Modo anônimo)"),
            Config(TitlePolicies.Full));

        Assert.Null(dados.WindowTitle);
        Assert.Null(dados.SiteDomain);
    }

    [Fact]
    public void Janela_normal_do_Chrome_nao_e_confundida_com_anonima()
    {
        var titulo = "Notebook | Mercado Livre - Google Chrome";
        var dados = new TitleMasker().Apply(
            Navegador("lista.mercadolivre.com.br/notebook", titulo, "chrome.exe", titulo, titulo),
            Config(TitlePolicies.Full));

        Assert.Equal(titulo, dados.WindowTitle);
        Assert.Equal("mercadolivre.com.br", dados.SiteDomain);
    }

    [Fact]
    public void Coleta_de_sites_desligada_zera_o_dominio_e_preserva_o_resto()
    {
        var dados = new TitleMasker().Apply(
            Navegador("https://www.gov.br/servicos", "Relatório.pdf - Chrome"),
            Config(site: false));

        Assert.Null(dados.SiteDomain);
        Assert.Equal("Relatório.pdf", dados.DocumentName);
        Assert.NotNull(dados.WindowTitle);
    }

    [Fact]
    public void Coleta_de_arquivos_desligada_zera_o_nome_e_preserva_o_resto()
    {
        var dados = new TitleMasker().Apply(
            Navegador("https://www.gov.br/servicos", "Relatório.pdf - Chrome"),
            Config(document: false));

        Assert.Null(dados.DocumentName);
        Assert.Equal("gov.br", dados.SiteDomain);
    }

    [Fact]
    public void Dominio_que_casa_com_padrao_sigiloso_e_descartado_inteiro()
    {
        // Mascarar "bancobrasil.com.br" para "***brasil.com.br" inventaria uma chave de catálogo
        // falsa; o domínio sai fora, e o título segue a regra normal de mascaramento.
        var dados = new TitleMasker().Apply(
            Navegador("https://www.bancobrasil.com.br/conta", "Banco do Brasil - Chrome"),
            Config(patterns: "(?i)banco"));

        Assert.Null(dados.SiteDomain);
        Assert.Equal("*** do Brasil - Chrome", dados.WindowTitle);
        Assert.True(dados.TitleMasked);
    }

    [Fact]
    public void Politica_FULL_nao_aplica_padroes_ao_dominio()
    {
        // Sob FULL o tenant declarou que quer tudo: os padrões não valem para o título, e não
        // podem valer só para o domínio.
        var dados = new TitleMasker().Apply(
            Navegador("https://www.bancobrasil.com.br/conta", "Banco do Brasil - Chrome"),
            Config(TitlePolicies.Full, patterns: "(?i)banco"));

        Assert.Equal("bancobrasil.com.br", dados.SiteDomain);
    }

    [Fact]
    public void Processo_que_nao_e_navegador_nunca_rende_site()
    {
        var dados = new TitleMasker().Apply(
            Navegador("https://www.gov.br", "Contrato.docx - Word", "winword.exe"), Config());

        Assert.Null(dados.SiteDomain);
        Assert.Equal("Contrato.docx", dados.DocumentName);
    }

    [Fact]
    public void Processo_ignorado_nao_rende_site_nem_documento()
    {
        var config = Config();
        config.IgnoredProcesses = ["chrome.exe"];
        var dados = new TitleMasker().Apply(Navegador("https://www.gov.br"), config);

        Assert.Equal(TitleMasker.PrivateProcessName, dados.ProcessName);
        Assert.Null(dados.SiteDomain);
        Assert.Null(dados.DocumentName);
    }

    [Fact]
    public void Nome_do_arquivo_sai_do_titulo_MASCARADO_e_nao_do_cru()
    {
        // "Senha do cliente.docx" mascarado vira "*** do cliente.docx": o nome não pode
        // reaparecer inteiro por outro campo.
        var amostra = new ForegroundSample(
            "winword.exe", @"C:\apps\winword.exe", null, "Senha do cliente.docx - Word");
        var dados = new TitleMasker().Apply(amostra, Config(patterns: "(?i)senha"));

        Assert.Equal("*** do cliente.docx - Word", dados.WindowTitle);
        Assert.Null(dados.DocumentName); // candidato com *** é descartado
    }
}
