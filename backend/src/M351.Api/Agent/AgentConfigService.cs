using System.Text.Json;
using M351.Api.Contracts;
using M351.Domain.Entities;
using Microsoft.Extensions.Options;

namespace M351.Api.Agent;

/// <summary>
/// Monta o objeto canônico de config do agente (Seção 5.5 — sempre os 8 campos).
/// `transparency_url` é derivada de Portal:BaseUrl + slug da org (página pública
/// /transparencia/:slug — Seção 8.8).
/// </summary>
public class AgentConfigService(IOptions<PortalOptions> portalOptions)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public AgentConfigDto Build(TenantAgentConfig config, string orgSlug) => new(
        config.HeartbeatSec,
        config.ActiveWindowPollSec,
        config.IdleThresholdSec,
        config.WindowTitlePolicy,
        config.MaskedPatterns,
        config.IgnoredProcesses,
        ParseCollectionWindow(config.CollectionWindow),
        TransparencyUrl(orgSlug));

    public string TransparencyUrl(string orgSlug) =>
        $"{portalOptions.Value.BaseUrl.TrimEnd('/')}/transparencia/{orgSlug}";

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
