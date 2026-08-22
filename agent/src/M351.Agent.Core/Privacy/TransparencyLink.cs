using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Privacy;

/// <summary>
/// Qual link o item "Política de monitoramento" do tray abre.
///
/// Preferência pela página DESTE dispositivo (device_transparency_url, /t/{token}): ela mostra a
/// mesma política da organização MAIS o bloco "Este dispositivo" — se a ciência já foi registrada,
/// quando foi o último contato, se a coleta está ativa ou pausada. É a resposta que o funcionário
/// procura quando abre o menu da PRÓPRIA máquina, e sem ela a página por token não tinha como
/// alcançar ninguém: o token só existia no banco.
///
/// Fallback para a página por slug (transparency_url) quando não há url por token: servidor
/// anterior ao campo, device sem token, ou config de fábrica antes do primeiro enroll. Agente novo
/// contra backend velho continua funcionando exatamente como antes.
///
/// PRIVACIDADE: a url por token é um segredo de baixo valor, mas é um segredo. Este resolvedor só
/// devolve a string para quem vai abrir o navegador; NADA aqui loga a url, e ela nunca entra em
/// telemetria (nem como query string, nem em evento).
/// </summary>
public static class TransparencyLink
{
    /// <summary>
    /// A url a abrir, ou null quando a empresa ainda não tem página de transparência publicada
    /// (aí o tray explica isso em vez de abrir o navegador).
    /// </summary>
    public static string? Resolve(AgentConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.DeviceTransparencyUrl))
        {
            return config.DeviceTransparencyUrl.Trim();
        }

        return string.IsNullOrWhiteSpace(config.TransparencyUrl) ? null : config.TransparencyUrl.Trim();
    }

    /// <summary>
    /// Descrição do link SEGURA PARA LOG: diz QUAL das duas páginas foi aberta, sem a url (que
    /// carrega o token). É isto que vai para o arquivo de log do helper, nunca Resolve().
    /// </summary>
    public static string DescribeForLog(AgentConfig config) =>
        !string.IsNullOrWhiteSpace(config.DeviceTransparencyUrl)
            ? "pagina deste dispositivo (link por token)"
            : "pagina da organizacao (link por slug)";
}
