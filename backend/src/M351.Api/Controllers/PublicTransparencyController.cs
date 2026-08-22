using Dapper;
using M351.Api.Agent;
using M351.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using M351.Api.RateLimiting;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// Páginas PÚBLICAS de transparência (F4.8, Seção 8.8) em duas rotas, mesmo payload:
///  - GET /api/v1/public/transparencia/{slug} — a página da ORGANIZAÇÃO, link divulgável;
///  - GET /api/v1/public/t/{token} (F5) — a página DO FUNCIONÁRIO, aberta pelo tray da própria
///    máquina (devices.transparency_token), que soma o bloco "Este dispositivo": estado da
///    INSTALAÇÃO, jamais dado pessoal do dia (ver GetByToken).
///
/// [AllowAnonymous]: SEM login, SEM cookie. É o link que o tray do agente abre para o funcionário
/// (transparency_url = Portal:BaseUrl + /transparencia/{slug}). Renderiza o estado REAL das configs
/// do tenant (window_title_policy, collection_window) + as retenções fixas (Seção 9.6) + os campos
/// editáveis (finalidade/DPO/vigência) + a data da última purga (maintenance_runs).
///
/// PRIVACIDADE (Seções 9.1/9.7): expõe APENAS a política de coleta vigente. JAMAIS dado pessoal,
/// window_title cru ou masked_patterns crus (os regex internos). A descrição da política de títulos
/// é amigável em pt-BR — nunca o conteúdo dos padrões.
///
/// O slug vem da URL (é a chave pública, não há tenant no contexto): a leitura é por SQL cru
/// (Dapper) — o filtro global por tenant do EF retornaria vazio numa requisição anônima. O slug é
/// único (índice). Slug inexistente → 404. Cache-Control público curto (a política muda raramente,
/// mas a última purga avança diariamente): max-age=300.
///
/// Rate limit: reusa a policy de IP já existente (enroll-per-ip) — rota anônima por IP, mesma régua.
/// Em testes o rate limiting fica desligado (RateLimiting:Enabled=false), então a policy é no-op.
/// </summary>
[ApiController]
[Route("api/v1/public/transparencia")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingPolicies.Enroll)]
public class PublicTransparencyController(NpgsqlDataSource dataSource) : ControllerBase
{
    /// <summary>Cache público curto: política estável, última purga avança diariamente (Seção 8.8).</summary>
    public const int CacheMaxAgeSeconds = 300;

    /// <summary>
    /// Cache da rota por token: PRIVADO (a URL carrega um segredo — cache compartilhado não pode
    /// guardá-la) e mais curto, porque o último contato do device avança a cada minuto.
    /// </summary>
    public const int DeviceCacheMaxAgeSeconds = 60;

    /// <summary>
    /// Projeção da leitura por slug. Classe mutável (não record posicional) porque o Dapper mapeia
    /// coluna→propriedade individualmente — necessário para o date NULLABLE (data_vigencia) chegar
    /// como DateOnly? sem o Dapper exigir um construtor com assinatura exata.
    /// </summary>
    private sealed class OrgConfigRow
    {
        public string Name { get; init; } = string.Empty;
        public string? WindowTitlePolicy { get; init; }
        public string? CollectionWindow { get; init; }
        public string? FinalidadeDeclarada { get; init; }
        public string? ContatoDpo { get; init; }
        public DateOnly? DataVigencia { get; init; }
    }

    /// <summary>Projeção do bloco "Este dispositivo" (rota por token). Só estado de instalação.</summary>
    private sealed class DeviceRow
    {
        public string Hostname { get; init; } = string.Empty;
        public DateTimeOffset? NoticeAckedAt { get; init; }
        public DateTimeOffset? LastSeenAt { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Get(string slug, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // org + config do agente do tenant em uma só leitura (LEFT JOIN: a config pode não existir
        // ainda — tenant sem nenhum enroll). Sem filtro de tenant: o slug é a chave pública única.
        var row = await connection.QueryFirstOrDefaultAsync<OrgConfigRow>(new CommandDefinition(
            $"{OrgConfigSelect}\nWHERE o.slug = @Slug",
            new { Slug = slug }, cancellationToken: ct));

        if (row is null)
        {
            // slug inexistente → 404 (ProblemDetails simples; rota anônima não vaza nada)
            return Problem(title: "Organização não encontrada.", statusCode: StatusCodes.Status404NotFound);
        }

        var response = await BuildResponseAsync(connection, row, device: null, ct);
        Response.Headers.CacheControl = $"public, max-age={CacheMaxAgeSeconds}";
        return Ok(response);
    }

    /// <summary>
    /// GET /api/v1/public/t/{token} (AllowAnonymous) — a MESMA página, alcançada pelo link que o
    /// tray do agente abre na própria máquina do funcionário (devices.transparency_token). Mesmo
    /// rate limit por IP e mesmo 404 opaco da rota por slug.
    ///
    /// Devolve o payload da transparência do tenant do device MAIS o bloco device com o estado da
    /// INSTALAÇÃO (hostname, ciência registrada, último contato, status). NADA de dado pessoal do
    /// dia: horas ativas/ociosas e aplicativo em foco ficam de FORA por decisão — o token é uma
    /// capability numa URL sem autenticação, e quem obtivesse o link (histórico de navegador de
    /// máquina compartilhada, print, encaminhamento) leria o comportamento de quem usa o
    /// equipamento. Para os próprios dados, o caminho é o pedido de acesso ao DPO da organização,
    /// que responde com o pacote DSR.
    ///
    /// Cache PRIVADO e curto (não "public" como a rota por slug): a URL contém um segredo, então
    /// cache compartilhado não pode guardá-la, e last_seen_at avança a cada minuto.
    /// </summary>
    [HttpGet("/api/v1/public/t/{token:guid}")]
    public async Task<IActionResult> GetByToken(Guid token, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // o token é a chave pública ÚNICA (índice único em devices.transparency_token): resolve
        // device → tenant → org/config numa leitura. Sem filtro de tenant (requisição anônima).
        var row = await connection.QueryFirstOrDefaultAsync<OrgConfigRow>(new CommandDefinition(
            $"{OrgConfigSelect}\nJOIN devices d ON d.tenant_id = o.id\nWHERE d.transparency_token = @Token",
            new { Token = token }, cancellationToken: ct));

        if (row is null)
        {
            // token inexistente → MESMO 404 opaco da rota por slug (nada distingue token
            // inválido de token de outro ambiente — a resposta não confirma existência)
            return Problem(title: "Organização não encontrada.", statusCode: StatusCodes.Status404NotFound);
        }

        var device = await connection.QueryFirstOrDefaultAsync<DeviceRow>(new CommandDefinition(
            """
            SELECT hostname AS Hostname, notice_acked_at AS NoticeAckedAt,
                   last_seen_at AS LastSeenAt, status AS Status
            FROM devices WHERE transparency_token = @Token
            """,
            new { Token = token }, cancellationToken: ct));

        var response = await BuildResponseAsync(connection, row, device, ct);
        Response.Headers.CacheControl = $"private, max-age={DeviceCacheMaxAgeSeconds}";
        return Ok(response);
    }

    /// <summary>
    /// Leitura compartilhada pelas duas rotas: org + config do agente (LEFT JOIN — a config pode
    /// não existir num tenant sem nenhum enroll). O WHERE fica com cada rota (slug ou token).
    /// </summary>
    private const string OrgConfigSelect = """
        SELECT o.name                  AS Name,
               c.window_title_policy    AS WindowTitlePolicy,
               c.collection_window::text AS CollectionWindow,
               o.finalidade_declarada   AS FinalidadeDeclarada,
               o.contato_dpo            AS ContatoDpo,
               o.data_vigencia          AS DataVigencia
        FROM organizations o
        LEFT JOIN tenant_agent_configs c ON c.tenant_id = o.id
        """;

    /// <summary>Monta o payload público (idêntico nas duas rotas; device só na rota por token).</summary>
    private static async Task<PublicTransparencyResponse> BuildResponseAsync(
        NpgsqlConnection connection, OrgConfigRow row, DeviceRow? device, CancellationToken ct)
    {
        // config ausente (tenant ainda sem enroll): cai nos defaults de fábrica (MASKED_PATTERNS,
        // janela ALWAYS) — é o que um device receberia no enroll.
        var policyMode = string.IsNullOrWhiteSpace(row.WindowTitlePolicy)
            ? "MASKED_PATTERNS"
            : row.WindowTitlePolicy;
        var window = AgentConfigService.ParseCollectionWindow(row.CollectionWindow);

        var ultimaPurga = await connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            """
            SELECT finished_at FROM maintenance_runs
            WHERE job_name = 'RetentionPurge' AND status = 'ok'
            ORDER BY finished_at DESC
            LIMIT 1
            """,
            cancellationToken: ct));

        return new PublicTransparencyResponse(
            OrganizationName: row.Name,
            WindowTitlePolicy: new WindowTitlePolicyPublic(policyMode, DescribeTitlePolicy(policyMode)),
            CollectionWindow: new CollectionWindowPublic(
                window.Mode, window.Days, window.Start, window.End, DescribeWindow(window)),
            Retencoes: new RetencoesPublic(
                EventosDias: 90, IntervalosMeses: 12, AgregadosMeses: 24, AuditoriaMeses: 24),
            FinalidadeDeclarada: row.FinalidadeDeclarada,
            ContatoDpo: row.ContatoDpo,
            Vigencia: row.DataVigencia,
            UltimaPurga: ultimaPurga,
            Coletado: BuildColetado(policyMode),
            NuncaColetado: NuncaColetado,
            Device: device is null
                ? null
                : new PublicDeviceBlock(device.Hostname, device.NoticeAckedAt, device.LastSeenAt, device.Status));
    }

    // ---------------------------------------------------------------- descrições pt-BR amigáveis

    /// <summary>Descrição amigável da política de títulos — NUNCA o conteúdo dos masked_patterns.</summary>
    private static string DescribeTitlePolicy(string mode) => mode switch
    {
        "FULL" => "titulos de janela completos",
        "APP_ONLY" => "apenas o nome do aplicativo, sem titulos",
        _ => "titulos com mascaramento de termos sensiveis", // MASKED_PATTERNS (default)
    };

    private static string DescribeWindow(CollectionWindowDto window)
    {
        if (!string.Equals(window.Mode, "BUSINESS_HOURS", StringComparison.OrdinalIgnoreCase))
        {
            return "coleta o tempo todo enquanto o agente esta em execucao";
        }

        var horario = window.Start is { Length: > 0 } && window.End is { Length: > 0 }
            ? $" das {window.Start} as {window.End}"
            : string.Empty;
        var dias = DescribeDays(window.Days);
        return $"coleta apenas em horario comercial{horario}{dias}";
    }

    private static string DescribeDays(int[]? days)
    {
        if (days is null || days.Length == 0)
        {
            return string.Empty;
        }

        string Nome(int d) => d switch
        {
            1 => "segunda", 2 => "terca", 3 => "quarta", 4 => "quinta",
            5 => "sexta", 6 => "sabado", 0 or 7 => "domingo", _ => d.ToString(),
        };

        return ", nos dias: " + string.Join(", ", days.Select(Nome));
    }

    // ---------------------------------------------------------------- listas pt-BR (Seções 9.1/9.7)

    /// <summary>
    /// "O que é coletado" derivado da política (Seção 9.1, lista FECHADA). O item de título em foco
    /// reflete a window_title_policy vigente. JAMAIS expõe títulos crus.
    /// </summary>
    private static IReadOnlyList<string> BuildColetado(string policyMode)
    {
        var foco = policyMode switch
        {
            "FULL" => "Aplicativo e titulo da janela em foco",
            "APP_ONLY" => "Apenas o aplicativo em foco (sem o titulo da janela)",
            _ => "Aplicativo em foco e o titulo da janela com mascaramento de termos sensiveis",
        };

        return
        [
            foco,
            "Identificacao da maquina e do usuario do Windows",
            "Eventos de sessao (logon, logoff, bloqueio e desbloqueio)",
            "Eventos de energia (ligar, desligar, suspender e retomar)",
            "O fato da ociosidade (jamais o que foi digitado ou clicado)",
            "Saude do agente (versao, ultimo contato, integridade)",
        ];
    }

    /// <summary>"O que NUNCA é coletado" — lista FIXA da Seção 9.7 (linhas vermelhas inegociáveis).</summary>
    private static readonly IReadOnlyList<string> NuncaColetado =
    [
        "Teclas digitadas ou qualquer captura de entrada (teclado, mouse)",
        "Capturas ou gravacao de tela",
        "Conteudo da area de transferencia (clipboard)",
        "Conteudo de arquivos, e-mails, mensagens ou paginas (DOM)",
        "Webcam ou microfone",
        "Localizacao geografica",
    ];
}
