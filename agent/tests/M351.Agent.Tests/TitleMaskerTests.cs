using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Privacy;
using Xunit;

namespace M351.Agent.Tests;

public class TitleMaskerTests
{
    private static AgentConfig Config(string policy, params string[] patterns)
    {
        var config = AgentConfig.FactoryDefault();
        config.WindowTitlePolicy = policy;
        if (patterns.Length > 0) config.MaskedPatterns = patterns.ToList();
        return config;
    }

    private static ForegroundSample Sample(string process, string? title) =>
        new(process, $@"C:\apps\{process}", null, title);

    [Fact]
    public void FULL_mantem_titulo_completo()
    {
        var data = new TitleMasker().Apply(Sample("excel.exe", "Orcamento_2026.xlsx - Excel"), Config(TitlePolicies.Full));
        Assert.Equal("excel.exe", data.ProcessName);
        Assert.Equal("Orcamento_2026.xlsx - Excel", data.WindowTitle);
        Assert.False(data.TitleMasked);
    }

    [Fact]
    public void FULL_trunca_em_256_chars()
    {
        var longTitle = new string('a', 400);
        var data = new TitleMasker().Apply(Sample("notepad.exe", longTitle), Config(TitlePolicies.Full));
        Assert.Equal(256, data.WindowTitle!.Length);
    }

    [Fact]
    public void MASKED_PATTERNS_substitui_trecho_por_asteriscos_e_marca_title_masked()
    {
        var config = Config(TitlePolicies.MaskedPatterns, "(?i)senha", @"(?i)\bbanco\b");
        var data = new TitleMasker().Apply(Sample("chrome.exe", "Minha Senha do Banco - Chrome"), config);

        Assert.Equal("Minha *** do *** - Chrome", data.WindowTitle);
        Assert.True(data.TitleMasked);
    }

    [Fact]
    public void MASKED_PATTERNS_mascara_CPF()
    {
        var config = Config(TitlePolicies.MaskedPatterns, @"\d{3}\.\d{3}\.\d{3}-\d{2}");
        var data = new TitleMasker().Apply(Sample("chrome.exe", "Cadastro 123.456.789-09 - Chrome"), config);

        Assert.Equal("Cadastro *** - Chrome", data.WindowTitle);
        Assert.True(data.TitleMasked);
    }

    [Fact]
    public void MASKED_PATTERNS_sem_match_nao_marca_title_masked()
    {
        var config = Config(TitlePolicies.MaskedPatterns, "(?i)senha");
        var data = new TitleMasker().Apply(Sample("excel.exe", "Planilha de custos"), config);

        Assert.Equal("Planilha de custos", data.WindowTitle);
        Assert.False(data.TitleMasked);
    }

    [Fact]
    public void APP_ONLY_zera_titulo_e_mantem_processo()
    {
        var data = new TitleMasker().Apply(Sample("chrome.exe", "Qualquer coisa"), Config(TitlePolicies.AppOnly));
        Assert.Equal("chrome.exe", data.ProcessName);
        Assert.Null(data.WindowTitle);
        Assert.False(data.TitleMasked);
    }

    [Theory]
    [InlineData("Página secreta - Google Chrome (navegação anônima)")] // Chrome pt-BR
    [InlineData("Pesquisa - Google Chrome (Navegação anónima)")]        // Chrome pt-PT (acento agudo)
    [InlineData("Secret page - Google Chrome (Incognito)")]             // Chrome en-US
    [InlineData("Search results - Google Chrome (INCOGNITO)")]          // en-US case-insensitive
    [InlineData("Pesquisa - Microsoft Edge InPrivate")]                 // Edge
    [InlineData("Algo - Mozilla Firefox (Navegação privativa)")]        // Firefox (case-insensitive)
    [InlineData("Algo - Mozilla Firefox (Navegação privada)")]          // Firefox pt-PT
    [InlineData("Some page - Mozilla Firefox (Private Browsing)")]      // Firefox en-US
    [InlineData("Some page - Mozilla Firefox (private browsing)")]      // en-US case-insensitive
    [InlineData("Algo - Edge INPRIVATE")]                               // case-insensitive
    public void Navegacao_anonima_rebaixa_para_APP_ONLY_mesmo_em_FULL(string title)
    {
        var data = new TitleMasker().Apply(Sample("chrome.exe", title), Config(TitlePolicies.Full));
        Assert.Null(data.WindowTitle);
        Assert.Equal("chrome.exe", data.ProcessName);
    }

    [Fact]
    public void Sufixo_anonimo_no_MEIO_do_titulo_nao_rebaixa()
    {
        var data = new TitleMasker().Apply(
            Sample("chrome.exe", "Como usar (navegação anônima) no Chrome - artigo"), Config(TitlePolicies.Full));
        Assert.NotNull(data.WindowTitle); // comparação é no FIM do título
    }

    [Theory]
    [InlineData("keepass.exe")]
    [InlineData("1password.exe")]
    [InlineData("bitwarden.exe")]
    [InlineData("logonui.exe")]
    [InlineData("lockapp.exe")]
    [InlineData("consent.exe")]
    [InlineData("monitoragentservice.exe")] // o próprio agente
    [InlineData("monitoragentsession.exe")]
    public void Processo_ignorado_vira_privado_sem_titulo_nem_caminho(string process)
    {
        var data = new TitleMasker().Apply(Sample(process, "Cofre de senhas"), Config(TitlePolicies.Full));
        Assert.Equal(TitleMasker.PrivateProcessName, data.ProcessName);
        Assert.Null(data.WindowTitle);
        Assert.Null(data.ExePath);
        Assert.Null(data.AppId);
    }

    [Fact]
    public void Processo_ignorado_da_config_do_tenant_tambem_vira_privado()
    {
        var config = Config(TitlePolicies.Full);
        config.IgnoredProcesses.Add("meu-erp.exe");
        var data = new TitleMasker().Apply(Sample("MEU-ERP.exe", "Tela do ERP"), config);
        Assert.Equal(TitleMasker.PrivateProcessName, data.ProcessName);
    }

    [Fact]
    public void Process_name_sempre_lowercase()
    {
        var data = new TitleMasker().Apply(Sample("EXCEL.EXE", "x"), Config(TitlePolicies.Full));
        Assert.Equal("excel.exe", data.ProcessName);
    }

    [Fact]
    public void Regex_invalida_da_config_e_ignorada_sem_crash()
    {
        var config = Config(TitlePolicies.MaskedPatterns, "[invalida", "(?i)senha");
        var data = new TitleMasker().Apply(Sample("chrome.exe", "minha senha"), config);
        Assert.Equal("minha ***", data.WindowTitle);
    }

    // ------------------------------------------------------- normalização do exe_path (Seção 6.3)

    [Fact]
    public void Exe_path_em_pasta_de_usuario_vira_USERPROFILE_no_Apply()
    {
        var sample = new ForegroundSample("app.exe",
            @"C:\Users\joao.pessoa\AppData\Local\Programs\App\app.exe", null, "x");
        var data = new TitleMasker().Apply(sample, Config(TitlePolicies.Full));
        Assert.Equal(@"%USERPROFILE%\AppData\Local\Programs\App\app.exe", data.ExePath);
    }

    [Fact]
    public void Exe_path_prefixo_case_insensitive_preserva_o_resto()
    {
        Assert.Equal(@"%USERPROFILE%\Tools\App.EXE",
            TitleMasker.NormalizeExePath(@"c:\users\Fulana Da Silva\Tools\App.EXE"));
    }

    [Fact]
    public void Exe_path_fora_de_Users_fica_intacto()
    {
        const string path = @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE";
        Assert.Equal(path, TitleMasker.NormalizeExePath(path));
    }

    [Fact]
    public void Exe_path_sem_componente_apos_o_usuario_fica_intacto()
    {
        Assert.Equal(@"C:\Users\fulana", TitleMasker.NormalizeExePath(@"C:\Users\fulana"));
    }

    [Fact]
    public void Exe_path_null_continua_null()
    {
        Assert.Null(TitleMasker.NormalizeExePath(null));
        var data = new TitleMasker().Apply(new ForegroundSample("app.exe", null, null, "x"),
            Config(TitlePolicies.Full));
        Assert.Null(data.ExePath);
    }
}
