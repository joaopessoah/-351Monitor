using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Privacy;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Aviso de ciência gerenciado pelo tenant (Seções 5.5/6.5/9.4): o tenant escreve o CORPO
/// (notice_text da config), mas o enquadramento que sustenta a base legal é fixo no agente e
/// sempre concatenado. A versão (notice_version) decide a reexibição.
/// </summary>
public class NoticeTests
{
    [Fact]
    public void Sem_texto_do_tenant_usa_o_padrao_do_agente()
    {
        foreach (var vazio in new[] { (string?)null, "", "   ", "\n\t " })
        {
            var text = NoticeTextComposer.Compose(vazio);
            Assert.Contains(NoticeTextComposer.DefaultBody, text);
            Assert.Contains(NoticeTextComposer.FixedFraming, text);
        }
    }

    [Fact]
    public void Com_texto_do_tenant_o_corpo_padrao_da_lugar_ao_do_tenant()
    {
        const string doTenant = "A ACME monitora os notebooks corporativos durante o horário de trabalho.";

        var text = NoticeTextComposer.Compose(doTenant);

        Assert.Contains(doTenant, text);
        Assert.DoesNotContain(NoticeTextComposer.DefaultBody, text);
        Assert.StartsWith(doTenant, text);
    }

    [Fact]
    public void O_enquadramento_legal_e_SEMPRE_concatenado_e_nao_e_editavel_pelo_tenant()
    {
        // tentativa do tenant de transformar ciência em consentimento e de esconder a transparência
        const string malicioso = "Ao clicar em Entendi você CONSENTE com o monitoramento total.";

        var text = NoticeTextComposer.Compose(malicioso);

        Assert.Contains(malicioso, text); // o corpo do tenant é exibido como ele escreveu
        // mas o enquadramento correto vem depois, íntegro, e desmente o consentimento
        Assert.Contains("Este aviso registra a sua ciência. Não é um pedido de consentimento.", text);
        Assert.Contains("ícone de monitoramento na área de notificação", text);
        Assert.EndsWith(NoticeTextComposer.FixedFraming, text);
    }

    [Fact]
    public void Corpo_gigante_do_tenant_e_truncado_mas_o_enquadramento_sobrevive()
    {
        var gigante = new string('x', NoticeTextComposer.MaxBodyLength + 500);

        var text = NoticeTextComposer.Compose(gigante);

        Assert.Contains(NoticeTextComposer.FixedFraming, text);
        Assert.Equal(NoticeTextComposer.MaxBodyLength + 2 + NoticeTextComposer.FixedFraming.Length, text.Length);
    }

    [Fact]
    public void Versao_maior_que_a_confirmada_reexibe_o_aviso()
    {
        Assert.True(NoticeGate.ShouldShow(acknowledgedVersion: null, currentVersion: 1)); // primeiro logon
        Assert.False(NoticeGate.ShouldShow(1, 1));                                        // já confirmou
        Assert.True(NoticeGate.ShouldShow(1, 2));                                         // bump no portal
        Assert.False(NoticeGate.ShouldShow(3, 2));                                        // config antiga não reexibe
    }

    [Fact]
    public void Config_do_agente_carrega_notice_text_e_notice_version_da_secao_5_5()
    {
        var config = AgentConfig.FactoryDefault();
        Assert.Null(config.NoticeText);   // fábrica: texto padrão do agente
        Assert.Equal(1, config.NoticeVersion);

        // shape do objeto config: os 10 campos da Seção 5.5, nomes exatos
        var json = JsonSerializer.Serialize(config, AgentJsonContext.Default.AgentConfig);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(
            new[]
            {
                "active_window_poll_sec", "collection_window", "heartbeat_sec", "idle_threshold_sec",
                "ignored_processes", "masked_patterns", "notice_text", "notice_version",
                "transparency_url", "window_title_policy"
            },
            keys);
    }

    [Fact]
    public void Config_do_servidor_com_aviso_do_tenant_e_desserializada_pelo_agente()
    {
        const string json = """
            {
              "heartbeat_sec": 60, "active_window_poll_sec": 5, "idle_threshold_sec": 300,
              "window_title_policy": "MASKED_PATTERNS", "masked_patterns": [], "ignored_processes": [],
              "collection_window": {"mode":"ALWAYS","days":null,"start":null,"end":null},
              "transparency_url": "https://app.exemplo.com.br/transparencia/acme",
              "notice_text": "Aviso da ACME.", "notice_version": 4
            }
            """;

        var config = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentConfig)!;

        Assert.Equal("Aviso da ACME.", config.NoticeText);
        Assert.Equal(4, config.NoticeVersion);
        Assert.StartsWith("Aviso da ACME.", NoticeTextComposer.Compose(config.NoticeText));
    }

    /// <summary>Config de servidor ANTIGO (sem os campos novos): notice_version cai no default 1.</summary>
    [Fact]
    public void Config_sem_os_campos_novos_mantem_versao_1_e_texto_padrao()
    {
        const string json = """
            {
              "heartbeat_sec": 60, "active_window_poll_sec": 5, "idle_threshold_sec": 300,
              "window_title_policy": "APP_ONLY", "masked_patterns": [], "ignored_processes": [],
              "collection_window": {"mode":"ALWAYS","days":null,"start":null,"end":null},
              "transparency_url": null
            }
            """;

        var config = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentConfig)!;

        Assert.Null(config.NoticeText);
        Assert.Equal(1, config.NoticeVersion);
        Assert.Contains(NoticeTextComposer.DefaultBody, NoticeTextComposer.Compose(config.NoticeText));
    }
}
