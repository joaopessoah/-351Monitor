using System.Text.Json;
using M351.Api.Contracts;
using M351.Domain.Entities;
using Microsoft.Extensions.Options;

namespace M351.Api.Agent;

/// <summary>
/// Monta o objeto canônico de config do agente (Seção 5.5 — sempre os 11 campos).
/// `transparency_url` é derivada de Portal:BaseUrl + slug da org (página pública
/// /transparencia/:slug — Seção 8.8).
///
/// F5: `notice_text`/`notice_version` viajam aqui (aviso de ciência gerenciado pelo tenant). O
/// texto pode ser null — nesse caso o agente usa o padrão embutido nele — e os trechos fixos que
/// protegem a base legal são concatenados NO AGENTE, jamais editáveis pelo tenant.
///
/// `device_transparency_url` é a página DAQUELE dispositivo (/t/{token}), montada do
/// devices.transparency_token. Viaja pelo MESMO canal de config (enroll e ack) — não existe canal
/// novo para ela. É opcional de propósito: agente antigo que não conhece o campo continua abrindo
/// a url por slug, e device sem token também.
/// </summary>
public class AgentConfigService(IOptions<PortalOptions> portalOptions)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public AgentConfigDto Build(TenantAgentConfig config, string orgSlug, Guid? transparencyToken = null) => new(
        config.HeartbeatSec,
        config.ActiveWindowPollSec,
        config.IdleThresholdSec,
        config.WindowTitlePolicy,
        config.MaskedPatterns,
        config.IgnoredProcesses,
        ParseCollectionWindow(config.CollectionWindow),
        TransparencyUrl(orgSlug),
        string.IsNullOrWhiteSpace(config.NoticeText) ? null : config.NoticeText,
        config.NoticeVersion,
        transparencyToken is { } token ? DeviceTransparencyUrl(token) : null);

    public string TransparencyUrl(string orgSlug) =>
        $"{portalOptions.Value.BaseUrl.TrimEnd('/')}/transparencia/{orgSlug}";

    /// <summary>
    /// Página pública do funcionário daquele device. O token é um segredo de baixo valor mas É um
    /// segredo: esta url só sai daqui para o agente da própria máquina (config do enroll/ack) e
    /// para Admin+ no portal, e nunca é registrada em log.
    /// </summary>
    public string DeviceTransparencyUrl(Guid transparencyToken) =>
        $"{portalOptions.Value.BaseUrl.TrimEnd('/')}/t/{transparencyToken}";

    public static CollectionWindowDto ParseCollectionWindow(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<CollectionWindowDto>(json, WebJson);
                if (parsed is { Mode.Length: > 0 })
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // collection_window malformada no banco: cai no default seguro ALWAYS
            }
        }

        return new CollectionWindowDto("ALWAYS", null, null, null);
    }
}
