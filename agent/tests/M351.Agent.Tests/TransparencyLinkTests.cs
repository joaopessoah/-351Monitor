using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Privacy;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Entrega do link de transparência POR TOKEN até o agente: a página daquele dispositivo
/// (/t/{token}) chega pelo campo device_transparency_url da config (Seção 5.5) e é a que o tray
/// abre; sem ela, o tray cai na página por slug, para agente novo contra backend velho e para
/// device sem token continuarem funcionando exatamente como antes.
/// </summary>
public class TransparencyLinkTests
{
    private const string PorSlug = "https://app.exemplo.com.br/transparencia/acme";
    private const string PorToken = "https://app.exemplo.com.br/t/01976f2a-0001-7aaa-b111-000000000001";

    [Fact]
    public void Com_url_por_token_o_tray_abre_a_pagina_deste_dispositivo()
    {
        var config = AgentConfig.FactoryDefault();
        config.TransparencyUrl = PorSlug;
        config.DeviceTransparencyUrl = PorToken;

        Assert.Equal(PorToken, TransparencyLink.Resolve(config));
    }

    [Fact]
    public void Sem_url_por_token_cai_na_pagina_por_slug()
    {
        var config = AgentConfig.FactoryDefault();
        config.TransparencyUrl = PorSlug;
        config.DeviceTransparencyUrl = null; // servidor antigo ou device sem token

        Assert.Equal(PorSlug, TransparencyLink.Resolve(config));
    }

    /// <summary>String vazia/em branco do servidor é tratada como ausência, não como url válida.</summary>
    [Fact]
    public void Url_por_token_em_branco_tambem_cai_no_slug()
    {
        var config = AgentConfig.FactoryDefault();
        config.TransparencyUrl = PorSlug;
        config.DeviceTransparencyUrl = "   ";

        Assert.Equal(PorSlug, TransparencyLink.Resolve(config));
    }

    [Fact]
    public void Sem_nenhuma_das_duas_nao_ha_link_a_abrir()
    {
        var config = AgentConfig.FactoryDefault();
        Assert.Null(config.TransparencyUrl);
        Assert.Null(config.DeviceTransparencyUrl);
        Assert.Null(TransparencyLink.Resolve(config));
    }

    /// <summary>
    /// O que vai para o log NUNCA pode conter a url por token (ela carrega um segredo): só a
    /// informação de QUAL das duas páginas foi aberta.
    /// </summary>
    [Fact]
    public void Descricao_de_log_nao_contem_a_url_nem_o_token()
    {
        var config = AgentConfig.FactoryDefault();
        config.TransparencyUrl = PorSlug;
        config.DeviceTransparencyUrl = PorToken;

        var descricao = TransparencyLink.DescribeForLog(config);
        Assert.DoesNotContain("01976f2a", descricao, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", descricao, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token", descricao, StringComparison.OrdinalIgnoreCase);

        config.DeviceTransparencyUrl = null;
        Assert.Contains("slug", TransparencyLink.DescribeForLog(config), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Config_do_agente_tem_os_11_campos_da_secao_5_5()
    {
        var json = JsonSerializer.Serialize(AgentConfig.FactoryDefault(), AgentJsonContext.Default.AgentConfig);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();

        Assert.Equal(
            new[]
            {
                "active_window_poll_sec", "collection_window", "device_transparency_url", "heartbeat_sec",
                "idle_threshold_sec", "ignored_processes", "masked_patterns", "notice_text",
                "notice_version", "transparency_url", "window_title_policy"
            },
            keys);
    }

    /// <summary>Config de servidor ANTIGO (sem o campo novo): device_transparency_url fica null.</summary>
    [Fact]
    public void Config_de_servidor_antigo_desserializa_sem_o_campo_novo()
    {
        const string json = """
            {
              "heartbeat_sec": 60, "active_window_poll_sec": 5, "idle_threshold_sec": 300,
              "window_title_policy": "MASKED_PATTERNS", "masked_patterns": [], "ignored_processes": [],
              "collection_window": {"mode":"ALWAYS","days":null,"start":null,"end":null},
              "transparency_url": "https://app.exemplo.com.br/transparencia/acme",
              "notice_text": null, "notice_version": 1
            }
            """;

        var config = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentConfig)!;

        Assert.Null(config.DeviceTransparencyUrl);
        Assert.Equal(PorSlug, TransparencyLink.Resolve(config));
    }

    [Fact]
    public void Config_de_servidor_novo_entrega_a_url_por_token()
    {
        const string json = """
            {
              "heartbeat_sec": 60, "active_window_poll_sec": 5, "idle_threshold_sec": 300,
              "window_title_policy": "MASKED_PATTERNS", "masked_patterns": [], "ignored_processes": [],
              "collection_window": {"mode":"ALWAYS","days":null,"start":null,"end":null},
              "transparency_url": "https://app.exemplo.com.br/transparencia/acme",
              "notice_text": null, "notice_version": 1,
              "device_transparency_url": "https://app.exemplo.com.br/t/01976f2a-0001-7aaa-b111-000000000001"
            }
            """;

        var config = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentConfig)!;

        Assert.Equal(PorToken, config.DeviceTransparencyUrl);
        Assert.Equal(PorToken, TransparencyLink.Resolve(config));
    }
}
