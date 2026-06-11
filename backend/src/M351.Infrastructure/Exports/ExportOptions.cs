namespace M351.Infrastructure.Exports;

/// <summary>
/// Config da seção Exports: diretório onde o worker grava os CSVs e de onde a API serve o
/// download. Em staging/produção é um VOLUME COMPARTILHADO entre api e worker
/// (infra/docker-compose.staging.yml); em dev local o default relativo resolve contra o
/// cwd do processo — rode API e worker a partir do MESMO diretório (raiz do repo) ou
/// aponte Exports:Directory para um caminho absoluto comum.
/// </summary>
public sealed class ExportOptions
{
    public const string SectionName = "Exports";

    public string Directory { get; set; } = ".exports";
}
