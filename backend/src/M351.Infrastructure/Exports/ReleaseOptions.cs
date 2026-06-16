namespace M351.Infrastructure.Exports;

/// <summary>
/// Config da hospedagem dos MSIs do agente (F4.2 — auto-update, Seção 6.7). Diretório onde a
/// CLI publish-agent-release copia o binário e de onde a API serve GET /agent/releases/{file}
/// por streaming. No MVP é a hospedagem do MSI (a url do manifesto aponta para cá); em produção
/// pode trocar por CDN. Default relativo (.releases) resolve contra o cwd do processo — em
/// staging/produção é um volume; mantido fora do git (.gitignore).
/// </summary>
public sealed class ReleaseOptions
{
    public const string SectionName = "Releases";

    public string Directory { get; set; } = ".releases";
}
