using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;

namespace M351.Infrastructure.DemoSeed;

/// <summary>Falha de pré-condição do seed (slug ocupado, e-mail em uso...) — mensagem para o operador.</summary>
public sealed class DemoSeedException(string message) : Exception(message);

/// <summary>Parâmetros do seed de demo (F3.6). Defaults espelham o brief: 30 devices × 60 dias.</summary>
public sealed record DemoSeedOptions
{
    public string Slug { get; init; } = "empresa-demo";
    public string OrgName { get; init; } = "Empresa Demo";
    public int DeviceCount { get; init; } = 30;
    public int Days { get; init; } = 60;

    /// <summary>Apaga TODOS os dados do tenant demo (e somente dele) antes de re-semear.</summary>
    public bool Reset { get; init; }

    /// <summary>null = derivado do slug.</summary>
    public string? OwnerEmail { get; init; }

    /// <summary>null = senha aleatória gerada (impressa no console).</summary>
    public string? OwnerPassword { get; init; }

    /// <summary>null = senha aleatória gerada (impressa no console).</summary>
    public string? ViewerPassword { get; init; }

    /// <summary>Semente fixa: a malha de eventos é determinística por device (rng = seed × índice).</summary>
    public int RngSeed { get; init; } = 351;
}

/// <summary>Saída do seed — contagens + credenciais + devices "especiais" (úteis ao teste e ao console).</summary>
public sealed record DemoSeedResult(
    Guid TenantId,
    int DeviceCount,
    long EventCount,
    long IntervalCount,
    long AggregatedDeviceDays,
    string OwnerEmail,
    string OwnerPassword,
    string ViewerEmail,
    string ViewerPassword,
    Guid SeqGapDeviceId,
    DateOnly SeqGapDay,
    Guid ArchivedDeviceId,
    IReadOnlyList<Guid> StaleDeviceIds,
    Guid? ClockSkewDeviceId);

/// <summary>
/// Seed de demo (F3.6) — gera um tenant sintético navegável de ponta a ponta injetando
/// eventos pelo pipeline REAL: raw_events com o MESMO shape do INSERT da ingestão
/// (IngestService), ingest_cursors.dirty_from marcado, e então IntervalizationService +
/// DailyAggregationService REAIS rodando até convergir. JAMAIS insere activity_intervals
/// ou daily_* na mão — o requisito de vendas é exatamente que a demo saia do pipeline.
///
/// Por que direto no banco e não via POST /ingest/batch: a janela N9 rejeita
/// occurred_at &lt; now − 14 dias, e a demo precisa de 60 dias de histórico.
///
/// Decisões documentadas (silêncios do brief):
///  - fuso fixo −03:00 (America/Sao_Paulo, default da org; sem DST desde 2019) — eventos
///    gerados em hora local e convertidos; tz_offset_min = −180 como no agente real;
///  - org plan='pro' sem device_limit: 30 devices estourariam o teto N24 do trial e a demo
///    mostraria um tenant "em violação";
///  - volume: a densidade de ACTIVE_WINDOW_CHANGED é calibrada para a timeline de EQUIPE
///    caber no cap N21 (3.000 intervalos/resposta para ~30 lanes; lanes excedentes são
///    cortadas inteiras) — o total fica em ~200-250k eventos para 30×60, um pouco abaixo
///    da faixa indicativa do brief (~300-600k), priorizando a demo sem truncamento;
///  - audit_log do tenant demo é apagado no --reset (a tabela é append-only para a role da
///    app, mas o backoffice roda como dono do banco e o tenant é sintético);
///  - presença "agora": ninguém faz ingest no tenant demo, então o seed termina re-tocando
///    last_seen_at/last_contact_at dos devices vivos (mesmo efeito do lote VAZIO de
///    keep-alive da ingestão — Seção 5.5) e o CLI oferece --keep-alive para sustentar a
///    janela N6 (180 s) durante a apresentação inteira.
/// </summary>
public sealed class DemoSeeder(NpgsqlDataSource dataSource, IPasswordHasher passwordHasher, Action<string>? log = null)
{
    /// <summary>Fuso fixo da demo: America/Sao_Paulo é -03:00 o ano todo (sem DST desde 2019).</summary>
    private static readonly TimeSpan Tz = TimeSpan.FromMinutes(-180);

    private const int TzOffsetMin = -180;
    private const string AgentVersion = "1.4.2";
    private const string OsVersion = "Windows 11 Pro 23H2 (22631)";

    /// <summary>
    /// Offset do device "relógio dessincronizado": precisa de |offset| &gt; 120.000 ms para o
    /// badge da Seção 8.7 acender (DispositivosPage.CLOCK_SKEW_LIMIT_MS). Negativo = relógio
    /// do agente ADIANTADO (~3 min): a intervalização aplica corrigido = cru + offset (§7.3).
    /// </summary>
    public const long ClockSkewOffsetMs = -185_000L;

    private void Log(string message) => log?.Invoke(message);

    // ------------------------------------------------------------ personas
    private sealed record Persona(
        string Tag,                      // tag de equipe do device
        int StartHour, int EndHour,      // jornada típica (hora local)
        int FocusMinSec, int FocusMaxSec, // permanência por janela ativa
        bool Meetings,                   // LOCK de reunião à tarde
        bool Bursts,                     // rajadas de alt-tab (comercial)
        (string Process, int Weight)[] Apps);

    private static readonly Persona[] Personas =
    [
        // "(privado)": o agente real JAMAIS reporta gerenciadores de senha pelo nome —
        // keepass.exe está em TitleMasker.FactoryIgnoredProcesses e chega como
        // process_name "(privado)" com window_title null; a demo reproduz esse shape.
        new("dev", 9, 18, 240, 720, Meetings: false, Bursts: false,
            [("code.exe", 45), ("chrome.exe", 30), ("teams.exe", 10), ("spotify.exe", 5), ("explorer.exe", 4), ("(privado)", 6)]),
        new("design", 10, 19, 240, 720, Meetings: true, Bursts: false,
            [("figma.exe", 45), ("photoshop.exe", 15), ("chrome.exe", 20), ("teams.exe", 12), ("spotify.exe", 8)]),
        new("comercial", 8, 17, 150, 420, Meetings: true, Bursts: true,
            [("chrome.exe", 32), ("teams.exe", 25), ("outlook.exe", 25), ("excel.exe", 12), ("vlc.exe", 6)]),
        new("financeiro", 8, 17, 240, 660, Meetings: false, Bursts: false,
            [("excel.exe", 40), ("protheus.exe", 30), ("outlook.exe", 15), ("chrome.exe", 10), ("winword.exe", 5)]),
    ];

    /// <summary>
    /// Apps das personas pré-mapeados em tenant_app_categories: a 1ª agregação já classifica.
    /// "(privado)" fica de fora de propósito — entra no app_catalog pelo auto-insert
    /// não-curado da intervalização e cai em "Não categorizado", como nos dados reais.
    /// </summary>
    private static readonly (string Process, string Display, string Category)[] DemoApps =
    [
        ("chrome.exe", "Google Chrome", "Navegação"),
        ("code.exe", "Visual Studio Code", "Desenvolvimento"),
        ("excel.exe", "Microsoft Excel", "Escritório/Documentos"),
        ("winword.exe", "Microsoft Word", "Escritório/Documentos"),
        ("outlook.exe", "Microsoft Outlook", "Escritório/Documentos"),
        ("teams.exe", "Microsoft Teams", "Comunicação"),
        ("figma.exe", "Figma", "Design"),
        ("photoshop.exe", "Adobe Photoshop", "Design"),
        ("protheus.exe", "ERP Protheus", "ERP/Sistemas internos"),
        ("explorer.exe", "Explorador de Arquivos", "Sistema/Utilitários"),
        ("spotify.exe", "Spotify", "Música/Streaming de áudio"),
        ("vlc.exe", "VLC Media Player", "Vídeo/Streaming"),
    ];

    private static readonly Dictionary<string, string[]> Titles = new(StringComparer.Ordinal)
    {
        ["chrome.exe"] =
        [
            "Painel de Vendas - Sistema interno - Google Chrome",
            "Gmail - Caixa de entrada - Google Chrome",
            "Pedido #4{0:D3} - Loja Virtual - Google Chrome",
            "Documentação da API - Google Chrome",
            "Stack Overflow - intervalos sobrepostos em SQL - Google Chrome",
            "Planilha de metas - Google Planilhas - Google Chrome",
            "Portal do cliente - Google Chrome",
        ],
        ["code.exe"] =
        [
            "IngestService.cs - m351-backend - Visual Studio Code",
            "TimelinePage.tsx - m351-portal - Visual Studio Code",
            "docker-compose.yml - infra - Visual Studio Code",
            "RelatorioService.cs - m351-backend - Visual Studio Code",
            "useTimeline.ts - m351-portal - Visual Studio Code",
        ],
        ["figma.exe"] =
        [
            "Landing v2 - Figma",
            "Design System - Componentes - Figma",
            "Fluxo de onboarding - Figma",
            "Apresentação institucional - Figma",
        ],
        ["photoshop.exe"] = ["banner-campanha.psd @ 66,7% (RGB/8)", "foto-equipe.psd @ 50% (RGB/8)"],
        ["excel.exe"] =
        [
            "Orcamento_2026.xlsx - Excel",
            "Fluxo_de_caixa_semanal.xlsx - Excel",
            "Comissoes_{0:D2}.xlsx - Excel",
            "Conciliacao_bancaria.xlsx - Excel",
        ],
        ["winword.exe"] = ["Contrato_prestacao_servicos.docx - Word", "Politica_interna.docx - Word"],
        ["outlook.exe"] =
        [
            "Caixa de Entrada - comercial@empresademo.com.br - Outlook",
            "RE: Proposta comercial - Mensagem (HTML)",
            "ENC: Nota fiscal {0:D4} - Mensagem (HTML)",
            "Calendário - Outlook",
        ],
        ["teams.exe"] =
        [
            "Equipe Comercial | Microsoft Teams",
            "Chat | Maria Diretoria | Microsoft Teams",
            "Reunião semanal | Microsoft Teams",
            "Geral (Empresa Demo) | Microsoft Teams",
        ],
        ["protheus.exe"] = ["Protheus - Módulo Financeiro", "Protheus - Contas a Pagar", "Protheus - Faturamento"],
        ["explorer.exe"] = ["Downloads", "Documentos", "Este Computador"],
        ["spotify.exe"] = ["Spotify Premium"],
        ["vlc.exe"] = ["treinamento-produto.mp4 - Reprodutor de mídia VLC"],
        // "(privado)": processo ignorado (keepass) como o TitleMasker do agente o reporta —
        // window_title null de propósito (sem entrada aqui)
    };

    private static readonly string[] FirstNames =
    [
        "Ana", "Bruno", "Carla", "Diego", "Elisa", "Felipe", "Gabriela", "Heitor", "Isabela", "Joao",
        "Karina", "Lucas", "Mariana", "Nicolas", "Olivia", "Paulo", "Rafaela", "Samuel", "Tatiana", "Vicente",
        "Beatriz", "Caio", "Daniela", "Eduardo", "Fernanda", "Gustavo", "Helena", "Igor", "Juliana", "Leonardo",
    ];

    private static readonly string[] LastNames =
    [
        "Souza", "Oliveira", "Pereira", "Santos", "Lima", "Carvalho", "Ferreira", "Almeida", "Gomes", "Martins",
        "Rocha", "Ribeiro", "Barbosa", "Castro", "Dias", "Moreira", "Teixeira", "Correia", "Cardoso", "Nunes",
    ];

    // ------------------------------------------------------------ modelo interno
    /// <summary>Evento sintético antes do INSERT (seq atribuída após a ordenação do dia).</summary>
    private sealed class RawRow
    {
        public Guid EventId;
        public long Seq;
        public DateTimeOffset OccurredAt;
        public required string EventType;
        public long MonoMs;
        public Guid BootId;
        public int? SessionId;
        public string? WindowsSid;
        public string? WindowsUser;
        public string? ProcessName;
        public string? WindowTitle;
        public string PayloadJson = "{}";
        public int Order; // desempate estável na ordenação por occurred_at
    }

    private sealed class DevicePlan
    {
        public Guid Id;
        public int Index;
        public required string Hostname;
        public required string DisplayName;
        public required string Sid;
        public required string WindowsUser;
        public required Persona Persona;
        public required Random Rng;
        public bool Archived;
        public bool Stale;
        public bool ClockSkew;
        public bool SeqGap;
        public bool SaturdayPartial;
        public DateOnly LastDay;          // último dia com eventos
        public bool DiesDirty;            // último dia termina SEM evento de fim (sem comunicação)
        public long NextSeq = 1;

        // rastreio para devices/device_users/device_current_state coerentes
        public DateTimeOffset? FirstEventAt;
        public DateTimeOffset? LastEventAt;
        public string LastState = "off_clean";
        public DateTimeOffset? StateSince;
        public string? ForegroundProcess;
        public string? ForegroundTitle;
        public DateTimeOffset? AppSince;
    }

    // ------------------------------------------------------------ entrada principal
    public async Task<DemoSeedResult> RunAsync(DemoSeedOptions options, CancellationToken ct = default)
    {
        Validate(options);

        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.ToOffset(Tz).DateTime);
        var firstDay = today.AddDays(-(options.Days - 1));
        var seqGapDay = MostRecentWeekday(today.AddDays(-2));

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // slug ocupado: aborta com mensagem clara, ou --reset apaga e re-semeia
        var existing = await ScalarAsync<Guid?>(conn,
            "SELECT id FROM organizations WHERE slug = @slug", [("slug", options.Slug)], ct);
        if (existing is { } existingId)
        {
            if (!options.Reset)
            {
                throw new DemoSeedException(
                    $"A organização com slug '{options.Slug}' já existe. Use --reset para apagar o tenant demo e re-semear.");
            }

            Log($"--reset: apagando TODOS os dados do tenant {existingId} (slug '{options.Slug}')...");
            await ResetTenantAsync(conn, existingId, ct);
        }

        var ownerEmail = options.OwnerEmail ?? $"dono@{options.Slug.Replace("-", "")}.com.br";
        var viewerEmail = $"demo@{options.Slug.Replace("-", "")}.com.br";
        var ownerPassword = options.OwnerPassword ?? GeneratePassword();
        var viewerPassword = options.ViewerPassword ?? GeneratePassword();

        // login é por e-mail GLOBAL (mesmo gate do create-org): e-mail de outro tenant aborta
        var emailTaken = await ScalarAsync<long?>(conn,
            "SELECT count(*) FROM users WHERE email = @a OR email = @b",
            [("a", ownerEmail), ("b", viewerEmail)], ct);
        if (emailTaken > 0)
        {
            throw new DemoSeedException(
                $"Já existe usuário com o e-mail {ownerEmail} ou {viewerEmail} em outro tenant. Escolha outro --slug/--owner-email.");
        }

        Log($"Semeando tenant demo '{options.OrgName}' ({options.Slug}): {options.DeviceCount} devices × {options.Days} dias ({firstDay:yyyy-MM-dd} → {today:yyyy-MM-dd}).");

        // partições diárias de raw_events para a janela inteira (a migration só cobre o mês
        // corrente e o próximo; mesmo DDL idempotente do RawEventPartitionManager)
        await EnsureRawPartitionsAsync(conn, firstDay.AddDays(-2), today.AddDays(1), ct);

        var tenantId = Uuid7.NewUuid7();
        await CreateOrgUsersAndCategoriesAsync(
            conn, tenantId, options, ownerEmail, ownerPassword, viewerEmail, viewerPassword, ct);
        await SeedAppCatalogAsync(conn, tenantId, ct);

        // ----- devices + eventos -----
        var plans = BuildDevicePlans(options, today);
        long totalEvents = 0;
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var events = GenerateDeviceEvents(plan, firstDay, today, now, seqGapDay);
            await InsertDeviceAsync(conn, tenantId, plan, now, ct);
            var inserted = await InsertEventsAsync(conn, tenantId, plan.Id, events, now, ct);
            await InsertDeviceUserAsync(conn, tenantId, plan, ct);
            await InsertCurrentStateAndCursorAsync(conn, tenantId, plan, events, now, ct);
            totalEvents += inserted;
            Log($"  [{plan.Index + 1}/{plans.Count}] {plan.Hostname} ({plan.Persona.Tag}): {inserted} eventos");
        }

        Log($"Eventos inseridos: {totalEvents}. Rodando o pipeline real de intervalização...");

        // ----- pipeline REAL: intervalização até nenhum cursor sujo do tenant -----
        var intervalization = new IntervalizationService(dataSource);
        for (var cycle = 1; ; cycle++)
        {
            ct.ThrowIfCancellationRequested();
            var processed = await intervalization.RunOnceAsync(ct);
            var remaining = await ScalarAsync<long?>(conn,
                "SELECT count(*) FROM ingest_cursors WHERE tenant_id = @t AND dirty_from IS NOT NULL",
                [("t", tenantId)], ct) ?? 0;
            Log($"  intervalização ciclo {cycle}: {processed} devices processados, {remaining} cursores sujos restantes");
            if (remaining == 0) break;
            if (processed == 0 || cycle >= 20)
            {
                throw new DemoSeedException("A intervalização não convergiu (cursores sujos restantes). Veja os logs do serviço.");
            }
        }

        var intervalCount = await ScalarAsync<long?>(conn,
            "SELECT count(*) FROM activity_intervals WHERE tenant_id = @t", [("t", tenantId)], ct) ?? 0;
        Log($"Intervalos construídos pelo pipeline: {intervalCount}. Rodando a agregação diária...");

        // ----- pipeline REAL: agregação diária até dirty_days vazio para o tenant -----
        var aggregation = new DailyAggregationService(dataSource);
        for (var cycle = 1; ; cycle++)
        {
            ct.ThrowIfCancellationRequested();
            var processed = await aggregation.RunOnceAsync(ct);
            var remaining = await ScalarAsync<long?>(conn,
                "SELECT count(*) FROM dirty_days WHERE tenant_id = @t", [("t", tenantId)], ct) ?? 0;
            Log($"  agregação ciclo {cycle}: {processed} device-dias processados, {remaining} dias sujos restantes");
            if (remaining == 0) break;
            if (processed == 0 || cycle >= 10)
            {
                throw new DemoSeedException("A agregação diária não convergiu (dirty_days restantes). Veja os logs do serviço.");
            }
        }

        var aggregatedDays = await ScalarAsync<long?>(conn,
            "SELECT count(*) FROM (SELECT DISTINCT device_id, summary_date FROM daily_device_summaries WHERE tenant_id = @t) x",
            [("t", tenantId)], ct) ?? 0;

        // presença "AGORA": `now` foi capturado ANTES dos loops de pipeline (minutos em 30×60)
        // e a janela N6 do "online agora" é de 180 s — sem este refresh o painel "Equipe agora"
        // nasceria inteiro "Sem comunicação". Mesmo efeito do lote VAZIO de keep-alive da
        // ingestão real (atualiza last_seen_at/last_contact_at sem inserir eventos).
        var touched = await TouchPresenceAsync(
            conn, tenantId, plans.Where(p => p.Stale).Select(p => p.Id).ToArray(), ct);
        Log($"Presença re-tocada em {touched} devices vivos (janela N6 de 180 s; use --keep-alive durante a apresentação).");

        Log($"Pronto: {plans.Count} devices, {totalEvents} eventos, {intervalCount} intervalos, {aggregatedDays} device-dias agregados.");

        var stale = plans.Where(p => p.Stale).Select(p => p.Id).ToList();
        return new DemoSeedResult(
            tenantId, plans.Count, totalEvents, intervalCount, aggregatedDays,
            ownerEmail, ownerPassword, viewerEmail, viewerPassword,
            plans.Single(p => p.SeqGap).Id, seqGapDay,
            plans.Single(p => p.Archived).Id,
            stale,
            plans.SingleOrDefault(p => p.ClockSkew)?.Id);
    }

    private static void Validate(DemoSeedOptions options)
    {
        if (options.DeviceCount < 4)
            throw new DemoSeedException("--devices precisa ser >= 4 (papéis especiais: lacuna de seq, relógio, archived).");
        if (options.Days is < 5 or > 90)
            throw new DemoSeedException("--days precisa estar entre 5 e 90.");
        if (string.IsNullOrWhiteSpace(options.Slug) || options.Slug.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
            throw new DemoSeedException("--slug deve conter apenas letras minúsculas, dígitos e hífens.");
    }

    // ------------------------------------------------------------ org + usuários + categorias
    private async Task CreateOrgUsersAndCategoriesAsync(
        NpgsqlConnection conn, Guid tenantId, DemoSeedOptions options,
        string ownerEmail, string ownerPassword, string viewerEmail, string viewerPassword, CancellationToken ct)
    {
        // plan 'pro' sem device_limit: 30 devices estourariam o teto N24 do trial
        await ExecAsync(conn, """
            INSERT INTO organizations (id, name, slug, timezone, plan, device_limit, status, created_at)
            VALUES (@id, @name, @slug, 'America/Sao_Paulo', 'pro', NULL, 'active', now())
            """, [("id", tenantId), ("name", options.OrgName), ("slug", options.Slug)], ct);

        // mesmas 13 categorias do create-org (CreateOrgCommand.SeedCategoriesAsync — Seção 7.1)
        (string Name, int Classification, string Color)[] categories =
        [
            ("Desenvolvimento", 1, "#2563eb"),
            ("Escritório/Documentos", 1, "#0891b2"),
            ("Comunicação", 1, "#7c3aed"),
            ("Reuniões", 1, "#9333ea"),
            ("Navegação", 1, "#0d9488"),
            ("Design", 1, "#db2777"),
            ("ERP/Sistemas internos", 1, "#4f46e5"),
            ("Sistema/Utilitários", 1, "#64748b"),
            ("Música/Streaming de áudio", 0, "#a3a3a3"),
            ("Não categorizado", 0, "#9ca3af"),
            ("Jogos", -1, "#dc2626"),
            ("Redes sociais", -1, "#ea580c"),
            ("Vídeo/Streaming", -1, "#e11d48"),
        ];
        foreach (var (name, classification, color) in categories)
        {
            await ExecAsync(conn, """
                INSERT INTO categories (id, tenant_id, name, classification, color)
                VALUES (@id, @t, @n, @c, @cor) ON CONFLICT (tenant_id, name) DO NOTHING
                """, [("id", Uuid7.NewUuid7()), ("t", tenantId), ("n", name), ("c", (short)classification), ("cor", color)], ct);
        }

        // config canônica do agente (defaults N1/N2/N4) — tenant completo como o de produção
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO tenant_agent_configs (tenant_id, masked_patterns, ignored_processes)
            VALUES (@t, @mp, @ip) ON CONFLICT (tenant_id) DO NOTHING
            """, conn))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("mp", new[] { "senha", "password", "banco" });
            cmd.Parameters.AddWithValue("ip", new[] { "keepass.exe" });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // owner ATIVO com senha (sem fluxo de convite: demo precisa logar na hora; o 1º login
        // do owner ainda passa pelo setup obrigatório de MFA — comportamento normal do portal)
        await ExecAsync(conn, """
            INSERT INTO users (id, tenant_id, email, password_hash, display_name, role, mfa_enabled, failed_login_count, status)
            VALUES (@id, @t, @e, @h, @n, 'owner', false, 0, 'active')
            """,
            [("id", Uuid7.NewUuid7()), ("t", tenantId), ("e", ownerEmail), ("h", passwordHasher.Hash(ownerPassword)), ("n", "Dono Demo")], ct);

        // viewer "demo": a conta usada na demonstração (viewer não exige MFA)
        await ExecAsync(conn, """
            INSERT INTO users (id, tenant_id, email, password_hash, display_name, role, mfa_enabled, failed_login_count, status)
            VALUES (@id, @t, @e, @h, @n, 'viewer', false, 0, 'active')
            """,
            [("id", Uuid7.NewUuid7()), ("t", tenantId), ("e", viewerEmail), ("h", passwordHasher.Hash(viewerPassword)), ("n", "Demonstração")], ct);
    }

    /// <summary>app_catalog (global, ON CONFLICT DO NOTHING) + tenant_app_categories do tenant demo.</summary>
    private async Task<Dictionary<string, Guid>> SeedAppCatalogAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        foreach (var (process, display, _) in DemoApps)
        {
            await ExecAsync(conn, """
                INSERT INTO app_catalog (id, process_name, display_name, curated)
                VALUES (@id, @p, @d, true) ON CONFLICT (process_name) DO NOTHING
                """, [("id", Uuid7.NewUuid7()), ("p", process), ("d", display)], ct);
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT process_name, id FROM app_catalog WHERE process_name = ANY(@names)", conn))
        {
            cmd.Parameters.AddWithValue("names", DemoApps.Select(a => a.Process).ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids[reader.GetString(0)] = reader.GetGuid(1);
        }

        foreach (var (process, _, category) in DemoApps)
        {
            await ExecAsync(conn, """
                INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
                SELECT @t, @app, c.id FROM categories c WHERE c.tenant_id = @t AND c.name = @cat
                ON CONFLICT (tenant_id, app_id) DO NOTHING
                """, [("t", tenantId), ("app", ids[process]), ("cat", category)], ct);
        }

        return ids;
    }

    // ------------------------------------------------------------ planos de device
    private List<DevicePlan> BuildDevicePlans(DemoSeedOptions options, DateOnly today)
    {
        var plans = new List<DevicePlan>(options.DeviceCount);
        for (var i = 0; i < options.DeviceCount; i++)
        {
            var rng = new Random(options.RngSeed * 7919 + i); // determinístico por device
            var first = FirstNames[i % FirstNames.Length];
            var last = LastNames[(i * 7 + 3) % LastNames.Length];
            var persona = Personas[i % Personas.Length];

            var plan = new DevicePlan
            {
                Id = Uuid7.NewUuid7(),
                Index = i,
                Hostname = $"{(i % 3 == 0 ? "DT" : "NB")}-{first.ToUpperInvariant()}{last.ToUpperInvariant()[..Math.Min(3, last.Length)]}{i:D2}",
                DisplayName = $"{first} {last}",
                Sid = $"S-1-5-21-3623811015-3361044348-30300820-{1100 + i}",
                WindowsUser = $"EMPRESADEMO\\{first.ToLowerInvariant()}.{last.ToLowerInvariant()}",
                Persona = persona,
                Rng = rng,
                LastDay = today,
                // papéis especiais por índice (determinístico, escala com deviceCount):
                SeqGap = i == 0,                                    // badge "dados incompletos"
                ClockSkew = i == 1,                                 // badge de relógio (~3 min adiantado, > 120 s — §8.7)
                Stale = options.DeviceCount >= 8 && i is 2 or 3,    // painel "Sem comunicação"
                SaturdayPartial = options.DeviceCount >= 8 && i is 4 or 5,
                Archived = i == options.DeviceCount - 1,            // toggle "incluir arquivados"
            };

            if (plan.Archived)
            {
                plan.LastDay = MostRecentWeekday(today.AddDays(-5));
            }
            else if (plan.Stale)
            {
                plan.LastDay = MostRecentWeekday(today.AddDays(i == 2 ? -2 : -3));
                plan.DiesDirty = true; // morre sem desligamento limpo → "Sem comunicação"
            }

            plans.Add(plan);
        }

        return plans;
    }

    // ------------------------------------------------------------ geração de eventos
    private List<RawRow> GenerateDeviceEvents(
        DevicePlan plan, DateOnly firstDay, DateOnly today, DateTimeOffset now, DateOnly seqGapDay)
    {
        var all = new List<RawRow>();
        for (var day = firstDay; day <= plan.LastDay; day = day.AddDays(1))
        {
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var saturdayShift = plan.SaturdayPartial && day.DayOfWeek == DayOfWeek.Saturday && plan.Rng.Next(100) < 60;
            if (weekend && !saturdayShift) continue;

            var dayRows = GenerateDayEvents(plan, day, now, isToday: day == today, saturdayShift);
            if (dayRows.Count == 0) continue;

            // seq estritamente monotônica por device; UMA lacuna deliberada no device 0
            var gapPending = plan.SeqGap && day == seqGapDay;
            var gapAt = LocalTime(day, 14 * 60); // ~14:00 local
            foreach (var row in dayRows.OrderBy(r => r.OccurredAt).ThenBy(r => r.Order))
            {
                if (gapPending && row.OccurredAt >= gapAt)
                {
                    plan.NextSeq += 47; // lacuna de seq → data_incomplete nos intervalos do trecho
                    gapPending = false;
                }
                row.Seq = plan.NextSeq++;
                all.Add(row);
            }
        }

        return all;
    }

    private List<RawRow> GenerateDayEvents(DevicePlan plan, DateOnly day, DateTimeOffset now, bool isToday, bool saturdayShift)
    {
        var rng = plan.Rng;
        var p = plan.Persona;

        var start = LocalTime(day, p.StartHour * 60 + rng.Next(-25, 41));
        var end = LocalTime(day, p.EndHour * 60 + rng.Next(-20, 51));
        if (saturdayShift)
        {
            start = LocalTime(day, 9 * 60 + rng.Next(0, 40));
            end = start.AddMinutes(rng.Next(150, 240));
        }

        var endsClean = true;
        if (isToday && !plan.Archived && !plan.Stale)
        {
            // hoje parcial: o dia corre até "minutos atrás" → presença "agora" viva no portal
            // (mesmo fora da jornada da persona — requisito da demo vence o realismo do horário)
            var cutoff = now.AddMinutes(-rng.Next(2, 5));
            if (cutoff <= start.AddMinutes(10))
            {
                start = cutoff.AddMinutes(-rng.Next(45, 90));
                // antes das 06:00 locais ninguém está em expediente (LocalTime recebe MINUTOS:
                // 6 * 60 = 06:00 — não confundir com LocalTime(day, 6) = 00:06)
                if (start < LocalTime(day, 6 * 60)) return [];
            }

            end = cutoff;
            endsClean = false; // ainda trabalhando: sem SESSION_END/SYSTEM_SUSPEND
        }

        if (plan.DiesDirty && day == plan.LastDay)
        {
            // agente morto à tarde: eventos simplesmente param (sem desligamento limpo)
            end = LocalTime(day, (12 + rng.Next(0, 5)) * 60 + rng.Next(0, 60));
            endsClean = false;
        }

        if (end <= start.AddMinutes(30)) return [];

        // ----- pausas do dia (idle de café, lock de almoço/reunião) -----
        var breaks = new List<(DateTimeOffset S, DateTimeOffset E, char Kind)>();
        void AddBreak(DateTimeOffset s, int minutes, char kind)
        {
            var e = s.AddMinutes(minutes);
            if (s <= start.AddMinutes(20) || e >= end.AddMinutes(-15)) return;
            if (breaks.Any(b => s < b.E.AddMinutes(10) && e > b.S.AddMinutes(-10))) return; // sem sobreposição
            breaks.Add((s, e, kind));
        }

        AddBreak(start.AddMinutes(90 + rng.Next(0, 60)), 8 + rng.Next(0, 8), 'I');             // café (>= N4 300 s)
        if (!saturdayShift) AddBreak(LocalTime(day, 12 * 60 + rng.Next(0, 35)), 45 + rng.Next(0, 26), 'L'); // almoço
        if (p.Meetings && rng.Next(100) < 60) AddBreak(LocalTime(day, 14 * 60 + rng.Next(0, 90)), 20 + rng.Next(0, 21), 'L');
        if (rng.Next(100) < 50) AddBreak(LocalTime(day, 15 * 60 + rng.Next(0, 80)), 6 + rng.Next(0, 7), 'I');
        breaks.Sort((a, b) => a.S.CompareTo(b.S));

        // ----- emissão -----
        var rows = new List<RawRow>();
        var order = 0;
        var bootId = NewGuid(rng); // boot_id por dia
        string? lastProc = null, lastTitle = null;

        void Emit(string type, DateTimeOffset at, object? data, bool machine = false, string? proc = null)
        {
            rows.Add(new RawRow
            {
                EventId = Uuid7.NewUuid7(at), // UUIDv7 coerente com occurred_at
                OccurredAt = at,
                EventType = type,
                MonoMs = (long)(at - start).TotalMilliseconds + 180_000,
                BootId = bootId,
                SessionId = machine ? null : 1,
                WindowsSid = machine ? null : plan.Sid,
                WindowsUser = machine ? null : plan.WindowsUser,
                ProcessName = proc,
                PayloadJson = data is null ? "{}" : JsonSerializer.Serialize(data),
                Order = order++,
            });
        }

        void EmitAwc(DateTimeOffset at)
        {
            var (proc, title, masked) = PickApp(rng, p, lastProc, lastTitle);
            lastProc = proc;
            lastTitle = title;
            var row = new RawRow
            {
                EventId = Uuid7.NewUuid7(at),
                OccurredAt = at,
                EventType = "ACTIVE_WINDOW_CHANGED",
                MonoMs = (long)(at - start).TotalMilliseconds + 180_000,
                BootId = bootId,
                SessionId = 1,
                WindowsSid = plan.Sid,
                WindowsUser = plan.WindowsUser,
                ProcessName = proc,
                WindowTitle = title,
                PayloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["process_name"] = proc,
                    ["window_title"] = title,
                    ["title_masked"] = masked,
                }),
                Order = order++,
            };
            rows.Add(row);
            plan.ForegroundProcess = proc;
            plan.ForegroundTitle = title;
            plan.AppSince = at;
        }

        Emit("AGENT_START", start, new Dictionary<string, object?>
        {
            ["agent_version"] = AgentVersion,
            ["os_version"] = OsVersion,
            ["hostname"] = plan.Hostname,
            ["boot_id"] = bootId.ToString(),
            ["start_reason"] = "boot",
            ["is_vm"] = false,
            ["join_type"] = "ad",
        }, machine: true);
        Emit("SESSION_START", start.AddSeconds(rng.Next(8, 40)), new Dictionary<string, object?> { ["logon_type"] = "console" });

        // segmentos de trabalho entre as pausas
        var cursor = start.AddSeconds(rng.Next(45, 110));
        foreach (var (bs, be, kind) in breaks)
        {
            EmitWork(cursor, bs);
            if (kind == 'I')
            {
                // N4/N5: último input em bs; IDLE_START disparado 300 s depois, retroativo
                Emit("IDLE_START", bs.AddSeconds(300), new Dictionary<string, object?>
                {
                    ["last_input_at"] = bs.UtcDateTime.ToString("o"),
                });
                Emit("IDLE_END", be, new Dictionary<string, object?>
                {
                    ["idle_duration_ms"] = (long)(be - bs).TotalMilliseconds,
                });
            }
            else
            {
                Emit("LOCK", bs, null);
                Emit("UNLOCK", be, null);
            }

            cursor = be.AddSeconds(rng.Next(20, 90));
        }

        EmitWork(cursor, end);

        void EmitWork(DateTimeOffset from, DateTimeOffset to)
        {
            var t = from;
            while (t < to)
            {
                if (p.Bursts && rng.Next(100) < 10)
                {
                    // rajada de alt-tab (comercial): trocas sub-minuto que o merge N21 compacta
                    var n = rng.Next(4, 8);
                    for (var k = 0; k < n && t < to; k++)
                    {
                        EmitAwc(t);
                        t = t.AddSeconds(rng.Next(28, 52));
                    }
                }
                else
                {
                    EmitAwc(t);
                    t = t.AddSeconds(rng.Next(p.FocusMinSec, p.FocusMaxSec));
                }
            }
        }

        // heartbeats a cada ~7-8 min DURANTE a sessão (sustentam intervalos; nunca 600 s exatos — N7)
        char KindAt(DateTimeOffset t)
        {
            foreach (var (bs, be, kind) in breaks)
            {
                if (t >= bs && t < be) return kind == 'I' && t < bs.AddSeconds(300) ? 'W' : kind;
            }

            return 'W';
        }

        for (var t = start.AddSeconds(rng.Next(420, 505)); t < end; t = t.AddSeconds(rng.Next(420, 505)))
        {
            var state = KindAt(t) switch { 'I' => "idle", 'L' => "locked", _ => "active" };
            // shape idêntico à ingestão real: ParseEvent preenche a COLUNA process_name a
            // partir de "foreground_process" do payload (IngestService) — espelhado aqui
            Emit("HEARTBEAT", t, new Dictionary<string, object?>
            {
                ["state"] = state,
                ["foreground_process"] = state == "active" ? lastProc : null,
                ["idle_ms"] = state == "idle" ? 400_000 : rng.Next(0, 90_000),
                ["queue_depth"] = rng.Next(0, 4),
            }, proc: state == "active" ? lastProc : null);
        }

        if (endsClean)
        {
            var att = end.AddSeconds(rng.Next(20, 80));
            if (rng.Next(100) < 50)
            {
                Emit("SYSTEM_SUSPEND", att, null, machine: true);
            }
            else
            {
                Emit("SESSION_END", att, null);
            }

            plan.LastState = "off_clean";
            plan.StateSince = att;
        }
        else
        {
            plan.LastState = KindAt(end) switch { 'I' => "idle", 'L' => "locked", _ => "active" };
            plan.StateSince = end;
        }

        // ~5% dos dias úteis: agente "morto" 20-60 min no meio do dia → gap N7 vira no_data.
        // Nunca no device da lacuna de seq (efeitos separados ficam legíveis na demo) nem hoje.
        if (!isToday && !plan.SeqGap && rng.Next(100) < 5)
        {
            var gapStart = start.AddHours(2 + rng.Next(0, 4)).AddMinutes(rng.Next(0, 50));
            var gapEnd = gapStart.AddMinutes(rng.Next(20, 61));
            if (gapEnd < end.AddMinutes(-20))
            {
                rows.RemoveAll(r => r.OccurredAt > gapStart && r.OccurredAt < gapEnd);
            }
        }

        if (rows.Count > 0)
        {
            plan.FirstEventAt ??= rows.Min(r => r.OccurredAt);
            plan.LastEventAt = rows.Max(r => r.OccurredAt);
        }

        return rows;
    }

    /// <summary>Escolha ponderada de app + título plausível; ~10% mascarado; "(privado)" sem título.</summary>
    private (string Process, string? Title, bool Masked) PickApp(Random rng, Persona p, string? lastProc, string? lastTitle)
    {
        for (var attempt = 0; ; attempt++)
        {
            var total = p.Apps.Sum(a => a.Weight);
            var pick = rng.Next(total);
            var proc = p.Apps[0].Process;
            foreach (var (process, weight) in p.Apps)
            {
                if (pick < weight)
                {
                    proc = process;
                    break;
                }

                pick -= weight;
            }

            if (!Titles.TryGetValue(proc, out var templates))
            {
                // processo "privado" (política APP_ONLY/ignorado): window_title null
                if (proc == lastProc && attempt < 4) continue; // dedupe plausível
                return (proc, null, false);
            }

            var title = string.Format(CultureInfo.InvariantCulture, templates[rng.Next(templates.Length)], rng.Next(1, 999));
            var masked = rng.Next(100) < 10;
            if (masked) title = MaskTitle(title);
            if (proc == lastProc && title == lastTitle && attempt < 4) continue; // o agente deduplica AWC idênticos
            return (proc, title, masked);
        }
    }

    /// <summary>Substitui o miolo do título por *** (como o agente faz com masked_patterns).</summary>
    private static string MaskTitle(string title)
    {
        var words = title.Split(' ');
        if (words.Length < 3) return "***";
        words[words.Length / 2] = "***";
        return string.Join(' ', words);
    }

    // ------------------------------------------------------------ persistência
    private async Task InsertDeviceAsync(NpgsqlConnection conn, Guid tenantId, DevicePlan plan, DateTimeOffset now, CancellationToken ct)
    {
        // fingerprint determinístico por slug+índice; token aleatório (ninguém faz ingest aqui)
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"demo:{tenantId}:{plan.Index}"))).ToLowerInvariant();
        var tokenHash = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
        var lastSeen = plan.Archived || plan.Stale
            ? plan.LastEventAt?.ToUniversalTime()
            : now.AddSeconds(-plan.Rng.Next(30, 90)); // batch keep-alive recém-recebido

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO devices (
                id, tenant_id, hostname, display_name, machine_fingerprint, os_version, os_type,
                agent_version, token_hash, config_version, tags, status, last_seen_at,
                clock_offset_ms, tz_offset_min, tz_iana, seq_max)
            VALUES (@id, @t, @h, @dn, @fp, @os, 'workstation', @av, @th, 1, @tags, @st, @ls, @co, @tz, 'America/Sao_Paulo', @sq)
            """, conn);
        cmd.Parameters.AddWithValue("id", plan.Id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("h", plan.Hostname);
        cmd.Parameters.AddWithValue("dn", plan.DisplayName);
        cmd.Parameters.AddWithValue("fp", fingerprint);
        cmd.Parameters.AddWithValue("os", OsVersion);
        cmd.Parameters.AddWithValue("av", AgentVersion);
        cmd.Parameters.AddWithValue("th", tokenHash);
        cmd.Parameters.AddWithValue("tags", new[] { plan.Persona.Tag });
        cmd.Parameters.AddWithValue("st", plan.Archived ? "archived" : "active");
        cmd.Parameters.AddWithValue("ls", (object?)lastSeen ?? DBNull.Value);
        cmd.Parameters.AddWithValue("co", plan.ClockSkew ? ClockSkewOffsetMs : (long)plan.Rng.Next(-1200, 1200));
        cmd.Parameters.AddWithValue("tz", TzOffsetMin);
        cmd.Parameters.AddWithValue("sq", plan.NextSeq - 1);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertDeviceUserAsync(NpgsqlConnection conn, Guid tenantId, DevicePlan plan, CancellationToken ct)
    {
        if (plan.FirstEventAt is null || plan.LastEventAt is null) return;
        await ExecAsync(conn, """
            INSERT INTO device_users (id, tenant_id, device_id, windows_sid, windows_username, display_name, first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, @dn, @fs, @ls)
            ON CONFLICT (tenant_id, device_id, windows_sid) DO NOTHING
            """,
            [
                ("id", Uuid7.NewUuid7()), ("t", tenantId), ("d", plan.Id), ("sid", plan.Sid),
                ("wu", plan.WindowsUser), ("dn", plan.DisplayName),
                ("fs", plan.FirstEventAt?.ToUniversalTime()), ("ls", plan.LastEventAt?.ToUniversalTime()),
            ], ct);
    }

    /// <summary>INSERT multi-row de raw_events (~1000/lote), MESMO shape e conflito da ingestão real.</summary>
    private async Task<long> InsertEventsAsync(
        NpgsqlConnection conn, Guid tenantId, Guid deviceId, List<RawRow> events, DateTimeOffset now, CancellationToken ct)
    {
        long inserted = 0;
        foreach (var chunk in events.Chunk(1000))
        {
            await using var cmd = new NpgsqlCommand { Connection = conn };
            var sql = new StringBuilder("""
                INSERT INTO raw_events
                  (tenant_id, device_id, event_id, seq, occurred_at, event_type, tz_offset_min, mono_ms, boot_id,
                   session_id, windows_sid, windows_username, process_name, window_title, payload, received_at)
                VALUES
                """);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("d", deviceId);

            var i = 0;
            foreach (var e in chunk)
            {
                sql.Append(i == 0 ? "\n" : ",\n");
                sql.Append(CultureInfo.InvariantCulture,
                    $"(@t, @d, @id{i}, @sq{i}, @at{i}, @ty{i}, {TzOffsetMin}, @mo{i}, @bo{i}, @se{i}, @si{i}, @us{i}, @pr{i}, @wt{i}, @pl{i}, @rc{i})");
                var received = e.OccurredAt.AddSeconds(75);
                if (received > now) received = now;
                cmd.Parameters.AddWithValue($"id{i}", e.EventId);
                cmd.Parameters.AddWithValue($"sq{i}", e.Seq);
                // Npgsql exige DateTimeOffset UTC para timestamptz (geramos em hora local -03:00)
                cmd.Parameters.AddWithValue($"at{i}", e.OccurredAt.ToUniversalTime());
                cmd.Parameters.AddWithValue($"ty{i}", e.EventType);
                cmd.Parameters.AddWithValue($"mo{i}", e.MonoMs);
                cmd.Parameters.AddWithValue($"bo{i}", e.BootId);
                cmd.Parameters.AddWithValue($"se{i}", (object?)e.SessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"si{i}", (object?)e.WindowsSid ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"us{i}", (object?)e.WindowsUser ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"pr{i}", (object?)e.ProcessName ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"wt{i}", (object?)e.WindowTitle ?? DBNull.Value);
                cmd.Parameters.Add(new NpgsqlParameter($"pl{i}", NpgsqlDbType.Jsonb) { Value = e.PayloadJson });
                cmd.Parameters.AddWithValue($"rc{i}", received.ToUniversalTime());
                i++;
            }

            // mesma idempotência da ingestão (re-execução nunca duplica)
            sql.Append("\nON CONFLICT (device_id, event_id, occurred_at) DO NOTHING");
            cmd.CommandText = sql.ToString();
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }

        return inserted;
    }

    private async Task InsertCurrentStateAndCursorAsync(
        NpgsqlConnection conn, Guid tenantId, DevicePlan plan, List<RawRow> events, DateTimeOffset now, CancellationToken ct)
    {
        if (events.Count == 0) return;

        var alive = !plan.Archived && !plan.Stale;
        var lastContact = alive
            ? now.AddSeconds(-plan.Rng.Next(30, 90))
            : plan.LastEventAt!.Value.AddSeconds(60).ToUniversalTime();
        var state = plan.Archived ? "off_clean" : plan.LastState;
        var isActive = state == "active";

        await ExecAsync(conn, """
            INSERT INTO device_current_state
              (tenant_id, device_id, state, windows_sid, windows_username, foreground_process,
               foreground_title, state_since, app_since, last_contact_at, updated_at)
            VALUES (@t, @d, @st, @sid, @wu, @fp, @ft, @ss, @aps, @lc, @lc)
            ON CONFLICT (device_id) DO UPDATE SET
              state = EXCLUDED.state, last_contact_at = EXCLUDED.last_contact_at, updated_at = EXCLUDED.updated_at
            """,
            [
                ("t", tenantId), ("d", plan.Id), ("st", state),
                ("sid", state is "off_clean" ? null : plan.Sid),
                ("wu", state is "off_clean" ? null : plan.WindowsUser),
                ("fp", isActive ? plan.ForegroundProcess : null),
                ("ft", isActive ? plan.ForegroundTitle : null),
                ("ss", plan.StateSince?.ToUniversalTime()),
                ("aps", isActive ? plan.AppSince?.ToUniversalTime() : null), ("lc", lastContact),
            ], ct);

        // cursor sujo desde o INÍCIO da janela: a intervalização reconstrói os 60 dias
        await ExecAsync(conn, """
            INSERT INTO ingest_cursors (tenant_id, device_id, processed_until, dirty_from, updated_at)
            VALUES (@t, @d, to_timestamp(0), @df, now())
            ON CONFLICT (device_id) DO UPDATE SET
              dirty_from = LEAST(COALESCE(ingest_cursors.dirty_from, EXCLUDED.dirty_from), EXCLUDED.dirty_from),
              updated_at = EXCLUDED.updated_at
            """, [("t", tenantId), ("d", plan.Id), ("df", events.Min(e => e.OccurredAt).ToUniversalTime())], ct);
    }

    // ------------------------------------------------------------ presença viva
    /// <summary>
    /// Re-toca a presença dos devices vivos do tenant demo (devices.last_seen_at +
    /// device_current_state.last_contact_at, com jitter de 15-75 s) — o MESMO efeito do lote
    /// vazio de keep-alive da ingestão real. Chamado ao fim do seed (o pipeline leva minutos
    /// e a janela N6 do "online agora" é 180 s) e pelo --keep-alive do CLI a cada 60 s durante
    /// a apresentação. Devices archived (status) e "sem comunicação" (excludeDeviceIds) ficam
    /// de fora; a cauda viva da timeline (gap N7 de 600 s) também se sustenta enquanto rodar.
    /// </summary>
    public static async Task<int> TouchPresenceAsync(
        NpgsqlDataSource dataSource, Guid tenantId, IReadOnlyCollection<Guid> excludeDeviceIds, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await TouchPresenceAsync(conn, tenantId, excludeDeviceIds, ct);
    }

    private static async Task<int> TouchPresenceAsync(
        NpgsqlConnection conn, Guid tenantId, IReadOnlyCollection<Guid> excludeDeviceIds, CancellationToken ct)
    {
        var exclude = excludeDeviceIds as Guid[] ?? [.. excludeDeviceIds];
        await using (var devices = new NpgsqlCommand("""
            UPDATE devices SET last_seen_at = now() - (15 + random() * 60) * interval '1 second'
            WHERE tenant_id = @t AND status = 'active' AND NOT (id = ANY(@ex))
            """, conn))
        {
            devices.Parameters.AddWithValue("t", tenantId);
            devices.Parameters.AddWithValue("ex", exclude);
            await devices.ExecuteNonQueryAsync(ct);
        }

        await using var state = new NpgsqlCommand("""
            UPDATE device_current_state s
            SET last_contact_at = now() - (15 + random() * 60) * interval '1 second', updated_at = now()
            FROM devices d
            WHERE d.id = s.device_id AND d.tenant_id = s.tenant_id
              AND s.tenant_id = @t AND d.status = 'active' AND NOT (s.device_id = ANY(@ex))
            """, conn);
        state.Parameters.AddWithValue("t", tenantId);
        state.Parameters.AddWithValue("ex", exclude);
        return await state.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Partições diárias de raw_events (mesmo DDL idempotente do RawEventPartitionManager).</summary>
    private static async Task EnsureRawPartitionsAsync(NpgsqlConnection conn, DateOnly from, DateOnly to, CancellationToken ct)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"CREATE TABLE IF NOT EXISTS raw_events_{day:yyyyMMdd} PARTITION OF raw_events FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{day.AddDays(1):yyyy-MM-dd}')";
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.UniqueViolation)
            {
                // corrida benigna: partição já existe
            }
        }
    }

    /// <summary>
    /// Apaga TODOS os dados do tenant demo na ordem certa de FKs — toda query com tenant_id;
    /// JAMAIS toca em dados de outros tenants (app_catalog é global e fica intacto).
    /// </summary>
    private async Task ResetTenantAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        string[] deletes =
        [
            "DELETE FROM dirty_days WHERE tenant_id = @t",
            "DELETE FROM ingest_cursors WHERE tenant_id = @t",
            "DELETE FROM daily_app_usage WHERE tenant_id = @t",
            "DELETE FROM daily_device_summaries WHERE tenant_id = @t",
            "DELETE FROM activity_intervals WHERE tenant_id = @t",
            "DELETE FROM raw_events WHERE tenant_id = @t",
            "DELETE FROM device_current_state WHERE tenant_id = @t",
            "DELETE FROM device_commands WHERE tenant_id = @t",
            "DELETE FROM device_users WHERE tenant_id = @t",
            "DELETE FROM devices WHERE tenant_id = @t",
            "DELETE FROM enrollment_keys WHERE tenant_id = @t",
            "DELETE FROM tenant_app_categories WHERE tenant_id = @t",
            "DELETE FROM categories WHERE tenant_id = @t",
            "DELETE FROM tenant_agent_configs WHERE tenant_id = @t",
            "DELETE FROM export_jobs WHERE tenant_id = @t",
            "DELETE FROM audit_log WHERE tenant_id = @t",
            "DELETE FROM refresh_tokens WHERE tenant_id = @t",
            "DELETE FROM invitations WHERE tenant_id = @t",
            "DELETE FROM users WHERE tenant_id = @t",
            "DELETE FROM organizations WHERE id = @t",
        ];
        foreach (var sql in deletes)
        {
            await ExecAsync(conn, sql, [("t", tenantId)], ct);
        }
    }

    // ------------------------------------------------------------ helpers
    private static DateTimeOffset LocalTime(DateOnly day, double minutes) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Tz).AddMinutes(minutes);

    private static DateOnly MostRecentWeekday(DateOnly day)
    {
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
        return day;
    }

    private static Guid NewGuid(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <summary>Senha forte aleatória (impressa UMA vez no console — não recuperável depois).</summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var chars = new char[14];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"Demo-{new string(chars)}";
    }

    private static async Task ExecAsync(
        NpgsqlConnection conn, string sql, (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection conn, string sql, (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return default;
        if (result is T typed) return typed;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }
}
