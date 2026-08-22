using System.Reflection;
using System.Text;
using M351.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Data.AppDictionary;

/// <summary>Uma linha do dicionário: process_name (chave), nome amigável e categoria sugerida.</summary>
public sealed record AppDictionaryEntry(string ProcessName, string DisplayName, string DefaultCategory);

/// <summary>
/// Curadoria assistida do catálogo global de apps (F1.1): aplica o dicionário brasileiro
/// (apps-br.csv) em app_catalog, populando display_name amigável, default_category (SUGESTÃO)
/// e curated=true. Sem isto a curadoria é 100% manual app por app: o auto-insert da
/// intervalização cria linhas com display_name = process_name e curated=false.
///
/// DECISÃO, RECURSO EMBUTIDO (embedded resource), não arquivo lido por caminho relativo:
/// o dicionário precisa existir em TRÊS contextos com working directory diferente (API em
/// container, worker em container, testes de integração pelo WebApplicationFactory). Um
/// caminho relativo exigiria configuração de cópia para a saída de build em cada csproj E
/// acertar o diretório corrente de cada host; embutido no assembly ele viaja junto com o
/// código, sem infra nova e sem risco de "arquivo não encontrado" só em produção.
///
/// DECISÃO, SEEDER IDEMPOTENTE NO STARTUP DA API, não migration com Sql: o dicionário é
/// DADO DE PRODUTO que evolui a cada release (apps novos, nomes melhores), não estrutura.
/// Uma migration aplicaria a versão daquele commit UMA vez e nunca mais; o seeder reaplica a
/// versão vigente em todo deploy, no mesmo lugar em que a API já roda as migrations (Program.cs).
///
/// GARANTIA INEGOCIÁVEL: a decisão do cliente SEMPRE vence. O upsert toca EXCLUSIVAMENTE
/// app_catalog (catálogo GLOBAL); jamais tenant_app_categories (mapeamento do tenant) nem
/// custom_display_name. default_category é só a sugestão que o portal oferece em lote no
/// PUT /app-catalog/categories/batch.
/// </summary>
public sealed class AppDictionarySeeder(NpgsqlDataSource dataSource, ILogger<AppDictionarySeeder>? logger = null)
{
    /// <summary>Nome do recurso embutido (namespace do projeto + caminho da pasta).</summary>
    public const string ResourceName = "M351.Infrastructure.Data.AppDictionary.apps-br.csv";

    /// <summary>
    /// Categorias canônicas semeadas por tenant no backoffice (CreateOrgCommand.SeedCategoriesAsync).
    /// ESPELHO: uma default_category fora desta lista é sugestão órfã (o portal não acharia a
    /// categoria do tenant para aplicar), então a linha é DESCARTADA com log de erro.
    /// </summary>
    public static readonly string[] CanonicalCategories =
    [
        "Desenvolvimento",
        "Escritório/Documentos",
        "Comunicação",
        "Reuniões",
        "Navegação",
        "Design",
        "ERP/Sistemas internos",
        "Sistema/Utilitários",
        "Música/Streaming de áudio",
        "Não categorizado",
        "Jogos",
        "Redes sociais",
        "Vídeo/Streaming",
    ];

    /// <summary>
    /// Lê e valida o dicionário embutido. Linhas vazias e iniciadas por '#' são comentário.
    /// Linhas malformadas ou com categoria fora do canônico entram em <paramref name="rejected"/>
    /// (o chamador loga) em vez de derrubar o processo: uma linha ruim no CSV jamais deve
    /// impedir a API de subir.
    /// </summary>
    public static IReadOnlyList<AppDictionaryEntry> Load(out IReadOnlyList<string> rejected)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Recurso embutido {ResourceName} não encontrado no assembly {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var entries = new List<AppDictionaryEntry>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            var parts = text.Split(';');
            if (parts.Length != 3)
            {
                problems.Add($"{text} (esperadas 3 colunas separadas por ';')");
                continue;
            }

            // process_name minúsculo e sem bordas: o agente e a ingestão normalizam assim
            // (Trim().ToLowerInvariant()), então qualquer outra forma nunca casaria com o uso
            var processName = parts[0].Trim().ToLowerInvariant();
            var displayName = parts[1].Trim();
            var category = parts[2].Trim();

            if (processName.Length == 0 || displayName.Length == 0)
            {
                problems.Add($"{text} (process_name e display_name são obrigatórios)");
                continue;
            }

            if (!CanonicalCategories.Contains(category))
            {
                problems.Add($"{text} (categoria \"{category}\" fora da lista canônica)");
                continue;
            }

            if (!seen.Add(processName))
            {
                // duplicata quebraria o ON CONFLICT DO UPDATE (a mesma linha não pode ser
                // afetada duas vezes no mesmo comando), a primeira ocorrência vence
                problems.Add($"{text} (process_name duplicado no dicionário)");
                continue;
            }

            entries.Add(new AppDictionaryEntry(processName, displayName, category));
        }

        rejected = problems;
        return entries;
    }

    /// <summary>
    /// Aplica o dicionário: um único INSERT ... ON CONFLICT (process_name) DO UPDATE com as
    /// linhas em arrays paralelos (unnest). Idempotente por construção, rodar N vezes deixa
    /// exatamente o mesmo estado. Retorna quantas linhas do catálogo foram criadas/atualizadas.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var entries = Load(out var rejected);
        foreach (var problem in rejected)
            logger?.LogError("Dicionário de apps: linha descartada, {Linha}", problem);

        if (entries.Count == 0)
        {
            logger?.LogWarning("Dicionário de apps vazio: nada a aplicar.");
            return 0;
        }

        var ids = new Guid[entries.Count];
        var processes = new string[entries.Count];
        var displays = new string[entries.Count];
        var categories = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            // id só é usado quando a linha é NOVA (o conflito preserva o id existente e,
            // com ele, todos os mapeamentos de tenant que já apontam para o app)
            ids[i] = Uuid7.NewUuid7();
            processes[i] = entries[i].ProcessName;
            displays[i] = entries[i].DisplayName;
            categories[i] = entries[i].DefaultCategory;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO app_catalog (id, process_name, display_name, default_category, curated)
            SELECT d.id, d.process_name, d.display_name, d.default_category, true
            FROM unnest(@ids, @processes, @displays, @categories)
                 AS d(id, process_name, display_name, default_category)
            ON CONFLICT (process_name) DO UPDATE
            SET display_name = EXCLUDED.display_name,
                default_category = EXCLUDED.default_category,
                curated = true
            """, connection);
        command.Parameters.AddWithValue("ids", ids);
        command.Parameters.AddWithValue("processes", processes);
        command.Parameters.AddWithValue("displays", displays);
        command.Parameters.AddWithValue("categories", categories);

        var affected = await command.ExecuteNonQueryAsync(ct);
        logger?.LogInformation(
            "Dicionário de apps aplicado: {Linhas} app(s) curado(s) no catálogo global{Descartes}.",
            affected, rejected.Count > 0 ? $" ({rejected.Count} linha(s) descartada(s))" : "");
        return affected;
    }
}
