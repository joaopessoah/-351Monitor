using System.Reflection;
using System.Text;
using M351.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace M351.Infrastructure.Data.AppDictionary;

/// <summary>Uma linha do dicionário de sites: domínio (chave), nome amigável e categoria sugerida.</summary>
public sealed record SiteDictionaryEntry(string Domain, string DisplayName, string DefaultCategory);

/// <summary>
/// Curadoria assistida do catálogo global de SITES: aplica o dicionário brasileiro (sites-br.csv)
/// em site_catalog, populando display_name amigável, default_category (SUGESTÃO) e curated=true.
/// É o irmão gêmeo do <see cref="AppDictionarySeeder"/> — mesmas decisões, mesmas garantias:
///
///  - RECURSO EMBUTIDO, não arquivo por caminho relativo: o dicionário viaja dentro do assembly e
///    funciona igual na API em container, no worker e nos testes de integração;
///  - SEEDER IDEMPOTENTE NO STARTUP, não migration: dicionário é DADO DE PRODUTO que evolui a
///    cada release, e o seeder reaplica a versão vigente em todo deploy;
///  - A DECISÃO DO CLIENTE SEMPRE VENCE: o upsert toca EXCLUSIVAMENTE site_catalog (catálogo
///    GLOBAL); jamais tenant_site_categories (a regra do tenant) nem custom_display_name.
///
/// Por que o catálogo de sites pode ser global sem virar problema de privacidade: a linha é o
/// DOMÍNIO ("mercadolivre.com.br"), que é público e igual para todo mundo. Quem visitou, quando e
/// por quanto tempo vive em activity_intervals/daily_site_usage, sempre com tenant_id.
/// </summary>
public sealed class SiteDictionarySeeder(NpgsqlDataSource dataSource, ILogger<SiteDictionarySeeder>? logger = null)
{
    /// <summary>Nome do recurso embutido (namespace do projeto + caminho da pasta).</summary>
    public const string ResourceName = "M351.Infrastructure.Data.AppDictionary.sites-br.csv";

    /// <summary>
    /// Mesmo conjunto canônico do dicionário de apps: app e site compartilham a tabela
    /// `categories` do tenant, então compartilham o vocabulário. Categoria fora daqui é sugestão
    /// órfã e a linha é DESCARTADA com log de erro.
    /// </summary>
    public static IReadOnlyList<string> CanonicalCategories => AppDictionarySeeder.CanonicalCategories;

    /// <summary>
    /// Lê e valida o dicionário embutido. Linhas vazias e iniciadas por '#' são comentário.
    /// Linha malformada entra em <paramref name="rejected"/> em vez de derrubar o processo: uma
    /// linha ruim no CSV jamais deve impedir a API de subir.
    /// </summary>
    public static IReadOnlyList<SiteDictionaryEntry> Load(out IReadOnlyList<string> rejected)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Recurso embutido {ResourceName} não encontrado no assembly {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var entries = new List<SiteDictionaryEntry>();
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

            // domínio minúsculo, sem "www." e sem bordas: é a forma EXATA que o agente envia
            // (Privacy.SiteDomain). Qualquer outra nunca casaria com o uso real.
            var domain = parts[0].Trim().ToLowerInvariant();
            if (domain.StartsWith("www.", StringComparison.Ordinal)) domain = domain[4..];
            var displayName = parts[1].Trim();
            var category = parts[2].Trim();

            if (domain.Length == 0 || displayName.Length == 0)
            {
                problems.Add($"{text} (domain e display_name são obrigatórios)");
                continue;
            }

            if (!domain.Contains('.'))
            {
                problems.Add($"{text} (domain precisa ser um domínio, com ponto)");
                continue;
            }

            if (!CanonicalCategories.Contains(category))
            {
                problems.Add($"{text} (categoria \"{category}\" fora da lista canônica)");
                continue;
            }

            if (!seen.Add(domain))
            {
                // duplicata quebraria o ON CONFLICT DO UPDATE (a mesma linha não pode ser
                // afetada duas vezes no mesmo comando); a primeira ocorrência vence
                problems.Add($"{text} (domain duplicado no dicionário)");
                continue;
            }

            entries.Add(new SiteDictionaryEntry(domain, displayName, category));
        }

        rejected = problems;
        return entries;
    }

    /// <summary>
    /// Aplica o dicionário: um único INSERT ... ON CONFLICT (domain) DO UPDATE com as linhas em
    /// arrays paralelos (unnest). Idempotente por construção. Retorna quantas linhas do catálogo
    /// foram criadas/atualizadas.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var entries = Load(out var rejected);
        foreach (var problem in rejected)
            logger?.LogError("Dicionário de sites: linha descartada, {Linha}", problem);

        if (entries.Count == 0)
        {
            logger?.LogWarning("Dicionário de sites vazio: nada a aplicar.");
            return 0;
        }

        var ids = new Guid[entries.Count];
        var domains = new string[entries.Count];
        var displays = new string[entries.Count];
        var categories = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            // id só é usado quando a linha é NOVA (o conflito preserva o id existente e, com
            // ele, todas as regras de tenant que já apontam para o site)
            ids[i] = Uuid7.NewUuid7();
            domains[i] = entries[i].Domain;
            displays[i] = entries[i].DisplayName;
            categories[i] = entries[i].DefaultCategory;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO site_catalog (id, domain, display_name, default_category, curated)
            SELECT d.id, d.domain, d.display_name, d.default_category, true
            FROM unnest(@ids, @domains, @displays, @categories)
                 AS d(id, domain, display_name, default_category)
            ON CONFLICT (domain) DO UPDATE
            SET display_name = EXCLUDED.display_name,
                default_category = EXCLUDED.default_category,
                curated = true
            """, connection);
        command.Parameters.AddWithValue("ids", ids);
        command.Parameters.AddWithValue("domains", domains);
        command.Parameters.AddWithValue("displays", displays);
        command.Parameters.AddWithValue("categories", categories);

        var affected = await command.ExecuteNonQueryAsync(ct);
        logger?.LogInformation(
            "Dicionário de sites aplicado: {Linhas} site(s) curado(s) no catálogo global{Descartes}.",
            affected, rejected.Count > 0 ? $" ({rejected.Count} linha(s) descartada(s))" : "");
        return affected;
    }
}
