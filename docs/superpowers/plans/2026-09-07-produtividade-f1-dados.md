# Produtividade F1 — Fundamentos de dados Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dar ao backend os quatro fundamentos que faltam para o painel entregar produtividade: o balde "sem classificação" separado do neutro, o agregado por hora do dia, um endpoint de visão geral com índice e cobertura calculados no servidor, identidade de pessoa entre máquinas e reagregação retroativa até 12 meses.

**Architecture:** Nada de tabela nova de fatos além de `hourly_activity`: o balde novo é uma coluna em `daily_device_summaries` preenchida pelo mesmo `DailyAggregationService` (mesma transação, mesmo snapshot REPEATABLE READ), e a identidade de pessoa é derivada do `windows_sid` que `device_users` já guarda, com uma tabela `people` só para apelido e mesclagem. Os endpoints novos vivem no `DashboardController` e num `PeopleController` novo, seguindo o padrão dos existentes (Dapper sobre `NpgsqlDataSource`, `ValidateRange`, `NormalizeTeamTag`, 404 em vez de 403, auditoria só com filtro individual).

**Tech Stack:** ASP.NET Core 8, Npgsql + Dapper, EF Core migrations (só DDL), PostgreSQL 16, xUnit com `ApiTestFixture` (banco descartável por execução, pipeline real de ingestão → intervalização → agregação).

**Spec:** `docs/superpowers/specs/2026-09-07-produto-produtividade-design.md` (decisões 1, 3, 4, 7 e 8 da seção 7)

## Global Constraints

- **Multi-tenant desde a primeira linha:** `tenant_id uuid NOT NULL` em toda tabela nova; todo SQL filtra por `tenant_id`; todo endpoint novo entra na suíte canônica de isolamento (`TenantIsolationTests`).
- **Vocabulário neutro em estado de máquina:** `active | idle | locked | off_clean | no_data`. Nomes de campo do agregado seguem `seconds_*`; a classificação usa `work_related | neutral | not_work_related | unclassified` no contrato JSON (os rótulos em português vivem no portal).
- **Arredondamento canônico:** soma exata em `numeric` e `floor` **depois** da soma, por lane — igual ao `DailyAggregationService` e ao rodapé da timeline (gate 11.3).
- **`seconds_on` = active + idle + locked.** `off_clean` e `no_data` nunca contam. Decisão 8: `no_session` continua sem intervalo.
- **Invariante novo:** `seconds_work_related + seconds_neutral + seconds_not_work_related + seconds_unclassified == seconds_active` em toda linha de `daily_device_summaries`.
- **Índice e cobertura:** `índice = work_related / (work_related + neutral + not_work_related)`; `cobertura = (active - unclassified) / active`. Ambos `null` quando o denominador é 0. Calculados **no servidor**, arredondados a 4 casas.
- **Janela máxima dos endpoints históricos:** 92 dias (`ApiControllerBase.MaxReportRangeDays`). A reagregação sob demanda vai até 366 dias.
- **Fuso:** `summary_date` e `hour_local` são sempre do fuso do tenant (`organizations.timezone`); nenhuma matemática de fuso nos controllers.
- **Auditoria:** agregado de equipe não audita; leitura com recorte individual (device, device_user ou person) grava `view_report`.
- **Sem `dotnet ef` novo por conta própria:** migrations são escritas à mão no padrão do repo (`YYYYMMDDHHMMSS_NomeF6.cs` + entrada no `M351DbContextModelSnapshot` quando a entidade é mapeada no EF).
- **Comandos de verificação:** `cd backend && dotnet build` e `dotnet test --filter <Classe>`. PostgreSQL local em `localhost:5432`, usuário `postgres`/`postgres`.

---

### Task 1: Balde `seconds_unclassified` no agregado diário

Hoje `seconds_neutral` é resto (`active - work - not_work`), então app sem mapeamento no tenant se mistura com app mapeado em categoria de classificação 0. O índice e a cobertura da decisão 4 exigem os dois separados.

**Files:**
- Create: `backend/src/M351.Infrastructure/Data/Migrations/20260907130000_BaldeSemClassificacaoF6.cs`
- Modify: `backend/src/M351.Infrastructure/Aggregation/DailyAggregationService.cs` (cabeçalho de regras + SQL do INSERT de `daily_device_summaries`)
- Modify: `backend/src/M351.Api/Contracts/DashboardContracts.cs` (dois records)
- Modify: `backend/src/M351.Api/Controllers/DashboardController.cs` (SELECT e projeção do summary)
- Modify: `backend/src/M351.Api/Contracts/ReportsContracts.cs` (linha de device e de device_user do relatório de uso)
- Modify: `backend/src/M351.Api/Controllers/ReportsController.cs` (SELECT do `group_by=device|device_user`)
- Test: `backend/tests/M351.IntegrationTests/DailyAggregationTests.cs`

**Interfaces:**
- Consumes: nada de tarefas anteriores.
- Produces: coluna `daily_device_summaries.seconds_unclassified int NOT NULL DEFAULT 0`; campo `seconds_unclassified` (long) em `DashboardSummaryDayResponse`, `DashboardSummaryTotalsResponse`, `UsageDeviceRowResponse` e `UsageDeviceUserRowResponse`.

- [ ] **Step 1: Escrever o teste que falha**

Em `DailyAggregationTests.cs`, seguindo o padrão dos testes existentes da classe (mesmos helpers de seed e pipeline):

```csharp
// ------------------------------------------------------------ balde sem classificação (F6)
[Fact]
public async Task Agregacao_AppSemMapeamento_CaiEmSemClassificacao_NaoEmNeutro()
{
    var (client, tenantId, fullKey) = await SetupAsync("AggUnclass");
    var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-AGG-UNCLASS");

    // "protheus.exe" recebe categoria +1 do tenant; "solitaire.exe" fica SEM mapeamento.
    await SeedActiveAsync(client, device, "protheus.exe", T(0, 9, 0), minutes: 30);
    await SeedActiveAsync(client, device, "solitaire.exe", T(0, 10, 0), minutes: 20);
    await RunPipelineAsync();
    await MapAppToClassificationAsync(tenantId, "protheus.exe", classification: 1);
    await RunPipelineAsync();

    var row = await ReadSummaryAsync(tenantId, device.DeviceId, LocalDate(T(0, 9, 0)));

    Assert.Equal(30 * 60, row.SecondsWorkRelated);
    Assert.Equal(20 * 60, row.SecondsUnclassified);
    Assert.Equal(0, row.SecondsNeutral);
    Assert.Equal(0, row.SecondsNotWorkRelated);
    // invariante do plano: os quatro baldes somam o tempo ativo
    Assert.Equal(row.SecondsActive,
        row.SecondsWorkRelated + row.SecondsNeutral + row.SecondsNotWorkRelated + row.SecondsUnclassified);
}

[Fact]
public async Task Agregacao_AppEmCategoriaNeutra_ContaComoNeutro_NaoComoSemClassificacao()
{
    var (client, tenantId, fullKey) = await SetupAsync("AggNeutro");
    var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-AGG-NEUTRO");

    await SeedActiveAsync(client, device, "chrome.exe", T(0, 9, 0), minutes: 25);
    await RunPipelineAsync();
    await MapAppToClassificationAsync(tenantId, "chrome.exe", classification: 0);
    await RunPipelineAsync();

    var row = await ReadSummaryAsync(tenantId, device.DeviceId, LocalDate(T(0, 9, 0)));

    Assert.Equal(25 * 60, row.SecondsNeutral);
    Assert.Equal(0, row.SecondsUnclassified);
}
```

Helpers a acrescentar na mesma classe (só se ainda não existirem com outro nome — reusar os da classe quando existirem):

```csharp
/// <summary>Cria a categoria com a classificação pedida e mapeia o app do catálogo nela.</summary>
private async Task MapAppToClassificationAsync(Guid tenantId, string processName, int classification)
{
    var categoryId = Uuid7.NewUuid7();
    await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
        """
        INSERT INTO categories (id, tenant_id, name, classification, color, created_at)
        VALUES (@c, @t, @name, @cls, NULL, now())
        """,
        ("c", categoryId), ("t", tenantId),
        ("name", $"Cat {classification} {processName}"), ("cls", classification));

    await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
        """
        INSERT INTO tenant_app_categories (tenant_id, app_id, category_id, custom_display_name)
        SELECT @t, a.id, @c, NULL FROM app_catalog a WHERE a.process_name = @p
        ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
        """,
        ("t", tenantId), ("c", categoryId), ("p", processName));
}

private sealed record SummaryRowDto(
    int SecondsActive, int SecondsWorkRelated, int SecondsNeutral,
    int SecondsNotWorkRelated, int SecondsUnclassified);

private async Task<SummaryRowDto> ReadSummaryAsync(Guid tenantId, Guid deviceId, string localDate)
{
    await using var conn = new NpgsqlConnection(fixture.Database.ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        """
        SELECT sum(seconds_active)::int, sum(seconds_work_related)::int, sum(seconds_neutral)::int,
               sum(seconds_not_work_related)::int, sum(seconds_unclassified)::int
        FROM daily_device_summaries
        WHERE tenant_id = @t AND device_id = @d AND summary_date = @day::date
        """, conn);
    cmd.Parameters.AddWithValue("t", tenantId);
    cmd.Parameters.AddWithValue("d", deviceId);
    cmd.Parameters.AddWithValue("day", localDate);
    await using var reader = await cmd.ExecuteReaderAsync();
    Assert.True(await reader.ReadAsync());
    return new SummaryRowDto(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
}
```

- [ ] **Step 2: Rodar o teste e ver falhar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~DailyAggregationTests.Agregacao_AppSemMapeamento"`
Expected: FAIL — `column "seconds_unclassified" does not exist`.

- [ ] **Step 3: Migration da coluna**

`20260907130000_BaldeSemClassificacaoF6.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — quarto balde do tempo ativo: seconds_unclassified separa "app sem categoria no tenant"
    /// de "app em categoria neutra". Até aqui os dois caíam em seconds_neutral (resto), o que
    /// impedia calcular o índice de produtividade (produtivo ÷ classificado) e a cobertura da
    /// classificação sem mentir sobre a curadoria incompleta.
    ///
    /// DEFAULT 0 e sem backfill: linha antiga fica com o neutro inflado, que é exatamente o que
    /// ela media. Corrigir o histórico é ação explícita do admin pela reagregação sob demanda
    /// (POST /reaggregation, até 12 meses) — nunca um UPDATE cego na migration.
    /// </summary>
    public partial class BaldeSemClassificacaoF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<int>(
                name: "seconds_unclassified",
                table: "daily_device_summaries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "seconds_unclassified", table: "daily_device_summaries");
    }
}
```

- [ ] **Step 4: SQL da agregação**

Em `DailyAggregationService.ProcessDayAsync`, o INSERT de `daily_device_summaries` passa a ter a coluna e o balde. Substituir a lista de colunas, a projeção e a subquery:

```csharp
await ExecAsync(conn, tx, """
    INSERT INTO daily_device_summaries (
        tenant_id, summary_date, device_id, device_user_id,
        seconds_active, seconds_idle, seconds_locked, seconds_on,
        seconds_work_related, seconds_neutral, seconds_not_work_related, seconds_unclassified,
        first_event_at, last_event_at, data_incomplete, computed_at)
    SELECT @t, @day, @d, lane,
           s_active, s_idle, s_locked,
           s_active + s_idle + s_locked,
           s_work,
           s_active - s_work - s_not_work - s_unclass,
           s_not_work,
           s_unclass,
           first_event_at, last_event_at, incomplete, now()
    FROM (
        SELECT COALESCE(i.device_user_id, '00000000-0000-0000-0000-000000000000'::uuid) AS lane,
               floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                   FILTER (WHERE i.state = 'active'), 0))::int AS s_active,
               floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                   FILTER (WHERE i.state = 'idle'), 0))::int AS s_idle,
               floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                   FILTER (WHERE i.state = 'locked'), 0))::int AS s_locked,
               floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                   FILTER (WHERE i.state = 'active' AND c.classification = 1), 0))::int AS s_work,
               floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                   FILTER (WHERE i.state = 'active' AND c.classification = -1), 0))::int AS s_not_work,
               floor(COALESCE(sum(extract(epoch FROM i.ended_at - i.started_at))
                   FILTER (WHERE i.state = 'active' AND c.id IS NULL), 0))::int AS s_unclass,
               min(i.started_at) FILTER (WHERE i.state IN ('active','idle','locked')) AS first_event_at,
               max(i.ended_at) FILTER (WHERE i.state IN ('active','idle','locked')) AS last_event_at,
               bool_or(i.data_incomplete) AS incomplete
        FROM activity_intervals i
        LEFT JOIN tenant_app_categories tac ON tac.tenant_id = i.tenant_id AND tac.app_id = i.app_id
        LEFT JOIN categories c ON c.tenant_id = i.tenant_id AND c.id = tac.category_id
        WHERE i.tenant_id = @t AND i.device_id = @d AND i.source_day = @day
        GROUP BY 1
    ) lanes
    """, [("t", tenantId), ("d", deviceId), ("day", day)], ct);
```

`c.id IS NULL` cobre os dois casos de "sem classificação": `app_id` nulo em intervalo ativo e app sem linha em `tenant_app_categories` (ou mapeado numa categoria que foi apagada). O neutro continua sendo resto, agora dos três baldes, o que preserva o invariante mesmo com truncamento.

No cabeçalho da classe, trocar as duas linhas de regra:

```
///  - classificação: SÓ intervalos active contam; app_id → tenant_app_categories →
///    categories.classification (+1/0/−1). App SEM mapeamento no tenant (ou active com
///    app_id NULL) cai em seconds_unclassified — é "sem classificação", não neutro (F6:
///    o índice de produtividade e a cobertura da classificação dependem dessa separação);
///  - seconds_neutral = seconds_active − work − not_work − unclassified (resto): garante o
///    invariante work + neutral + not_work + unclassified == seconds_active mesmo com
///    truncamento por balde;
```

- [ ] **Step 5: Rodar o teste e ver passar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~DailyAggregationTests"`
Expected: PASS (inclusive os testes antigos da classe — o neutro de app mapeado em categoria 0 não muda).

- [ ] **Step 6: Expor no contrato do dashboard e do relatório de uso**

Em `DashboardContracts.cs`, acrescentar o campo **depois** de `SecondsNotWorkRelated` nos dois records (posição importa: são records posicionais consumidos pelo controller):

```csharp
public sealed record DashboardSummaryDayResponse(
    string Date,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsOn,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    long SecondsUnclassified,
    bool DataIncomplete,
    int DeviceCount);
```

Idem em `DashboardSummaryTotalsResponse`. No `DashboardController.Summary`, acrescentar ao SELECT, ao `SummaryRow` e às duas projeções:

```sql
COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
```

```csharp
private sealed record SummaryRow(
    string? Date, long SecondsActive, long SecondsIdle, long SecondsLocked, long SecondsOn,
    long SecondsWorkRelated, long SecondsNeutral, long SecondsNotWorkRelated, long SecondsUnclassified,
    bool DataIncomplete, int DeviceCount);
```

O mesmo em `ReportsContracts.cs` / `ReportsController` para `group_by=device` e `group_by=device_user` (as duas linhas que já trazem os três baldes).

- [ ] **Step 7: Rodar a suíte de leitura afetada**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~DashboardEndpointTests|FullyQualifiedName~UsageReportEndpointTests|FullyQualifiedName~DailyAggregationTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add backend/src/M351.Infrastructure/Data/Migrations/20260907130000_BaldeSemClassificacaoF6.cs \
        backend/src/M351.Infrastructure/Aggregation/DailyAggregationService.cs \
        backend/src/M351.Api/Contracts/DashboardContracts.cs \
        backend/src/M351.Api/Controllers/DashboardController.cs \
        backend/src/M351.Api/Contracts/ReportsContracts.cs \
        backend/src/M351.Api/Controllers/ReportsController.cs \
        backend/tests/M351.IntegrationTests/DailyAggregationTests.cs
git commit -m "feat(backend): balde seconds_unclassified separado do neutro no agregado diário"
```

---

### Task 2: Agregado por hora do dia e `GET /dashboard/activity-by-hour`

O gráfico "Atividade ao longo do dia" da nova Visão Geral não tem fonte: o endpoint `activity-by-hour` foi desenhado na F2 e nunca construído. Varrer `activity_intervals` a cada leitura não escala; o agregado por hora entra na **mesma transação** da agregação diária, do mesmo snapshot.

**Files:**
- Create: `backend/src/M351.Infrastructure/Data/Migrations/20260907131000_AtividadePorHoraF6.cs`
- Modify: `backend/src/M351.Infrastructure/Aggregation/DailyAggregationService.cs` (DELETE + INSERT novos na transação)
- Modify: `backend/src/M351.Api/Contracts/DashboardContracts.cs`
- Modify: `backend/src/M351.Api/Controllers/DashboardController.cs`
- Modify: `backend/src/M351.Infrastructure/Retention/RetentionPurgeService.cs` (purga junto dos `daily_*`, 24 meses)
- Test: `backend/tests/M351.IntegrationTests/ActivityByHourTests.cs`

**Interfaces:**
- Consumes: Task 1 (mesma transação de `ProcessDayAsync`).
- Produces: tabela `hourly_activity`; `GET /api/v1/dashboard/activity-by-hour?from&to[&tag]` devolvendo `ActivityByHourResponse(IReadOnlyList<ActivityByHourItemResponse> Hours, int DaysWithData)` com `ActivityByHourItemResponse(int Hour, long SecondsActive, long SecondsIdle, double? AvgPeopleActive)`.

- [ ] **Step 1: Escrever o teste que falha**

`ActivityByHourTests.cs` (novo arquivo, no padrão de `DashboardEndpointTests`):

```csharp
using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// hourly_activity + GET /api/v1/dashboard/activity-by-hour (F6): o agregado por hora sai da
/// MESMA transação da agregação diária, em hora LOCAL do tenant, e o endpoint devolve as 24
/// horas com tempo ativo, ocioso e a média de pessoas ativas simultâneas no período.
/// </summary>
[Collection(ApiCollection.Name)]
public class ActivityByHourTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
    private static DateTimeOffset T(int dayOffset, int h, int m) => Base.AddDays(dayOffset).AddHours(h).AddMinutes(m);
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    [Fact]
    public async Task ActivityByHour_DistribuiIntervaloEntreHorasLocais_ECalculaMediaDePessoas()
    {
        var org = await fixture.CreateOrganizationAsync($"Hora {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-HORA-1");

        // 13:40Z → 14:20Z = 10:40 → 11:20 local (GMT-3): 20 min na hora 10, 20 min na hora 11.
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 13, 40), new Dictionary<string, object?> { ["process_name"] = "excel.exe" }),
            f.Event("LOCK", T(0, 14, 20)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new IntervalizationService(ds).RunOnceAsync();
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        var day = LocalDate(T(0, 13, 40));
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/activity-by-hour?from={day}&to={day}", token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var json = JsonDocument.Parse(body);
        var hours = json.RootElement.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal(24, hours.Count);
        Assert.Equal(1, json.RootElement.GetProperty("days_with_data").GetInt32());

        var h10 = hours.Single(h => h.GetProperty("hour").GetInt32() == 10);
        var h11 = hours.Single(h => h.GetProperty("hour").GetInt32() == 11);
        Assert.Equal(20 * 60, h10.GetProperty("seconds_active").GetInt64());
        Assert.Equal(20 * 60, h11.GetProperty("seconds_active").GetInt64());
        // 1200 s ÷ 3600 s ÷ 1 dia = 0,3333 pessoa ativa em média na hora
        Assert.Equal(0.3333, h10.GetProperty("avg_people_active").GetDouble(), 4);

        var h3 = hours.Single(h => h.GetProperty("hour").GetInt32() == 3);
        Assert.Equal(0, h3.GetProperty("seconds_active").GetInt64());
    }

    [Fact]
    public async Task ActivityByHour_RangeInvalido_400()
    {
        var org = await fixture.CreateOrganizationAsync($"HoraErr {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/dashboard/activity-by-hour?from=2026-01-01&to=2026-12-31", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

- [ ] **Step 2: Rodar o teste e ver falhar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~ActivityByHourTests"`
Expected: FAIL — 404 no endpoint.

- [ ] **Step 3: Migration da tabela**

`20260907131000_AtividadePorHoraF6.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — hourly_activity: distribuição do tempo ativo e ocioso pelas 24 horas LOCAIS do
    /// tenant, por (dia, device, lane de usuário). Alimenta o gráfico "Atividade ao longo do
    /// dia" da Visão Geral, que antes não tinha fonte (o endpoint activity-by-hour foi
    /// desenhado na F2 e nunca construído; varrer activity_intervals a cada leitura não
    /// escala em N25).
    ///
    /// Preenchida na MESMA transação REPEATABLE READ da agregação diária, do mesmo snapshot,
    /// com delete-and-rebuild por (tenant, device, dia) — então herda de graça a idempotência
    /// e a coerência com daily_device_summaries.
    ///
    /// Sem particionamento: 24 linhas por (dia × device × lane) é uma ordem de grandeza abaixo
    /// de daily_app_usage, e a purga de retenção (24 meses, igual aos demais daily_*) é um
    /// DELETE por data. Índice único é a própria PK.
    /// </summary>
    public partial class AtividadePorHoraF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE hourly_activity (
                    tenant_id       uuid     NOT NULL,
                    summary_date    date     NOT NULL,
                    hour_local      smallint NOT NULL,
                    device_id       uuid     NOT NULL,
                    device_user_id  uuid     NOT NULL,
                    seconds_active  int      NOT NULL,
                    seconds_idle    int      NOT NULL,
                    CONSTRAINT pk_hourly_activity
                        PRIMARY KEY (tenant_id, summary_date, hour_local, device_id, device_user_id),
                    CONSTRAINT ck_hourly_activity_hour CHECK (hour_local BETWEEN 0 AND 23)
                );
                CREATE INDEX ix_hourly_activity_tenant_date
                    ON hourly_activity (tenant_id, summary_date);
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP TABLE hourly_activity;");
    }
}
```

- [ ] **Step 4: Preencher na transação da agregação diária**

Em `ProcessDayAsync`, depois do DELETE de `daily_app_usage` acrescentar o DELETE novo, e depois do INSERT de `daily_app_usage` o INSERT novo:

```csharp
await ExecAsync(conn, tx,
    "DELETE FROM hourly_activity WHERE tenant_id = @t AND device_id = @d AND summary_date = @day",
    [("t", tenantId), ("d", deviceId), ("day", day)], ct);
```

```csharp
// Distribuição horária (F6): recorta cada intervalo active/idle nas fronteiras de hora
// LOCAL do tenant. generate_series anda de hora em hora do início ao fim do intervalo já
// convertido para o relógio local; o recorte é GREATEST/LEAST e segmento degenerado
// (intervalo terminando exatamente na virada) sai pelo e > s.
await ExecAsync(conn, tx, """
    INSERT INTO hourly_activity (
        tenant_id, summary_date, hour_local, device_id, device_user_id, seconds_active, seconds_idle)
    SELECT @t, @day, extract(hour FROM seg.hour_start)::smallint, @d, seg.lane,
           floor(COALESCE(sum(extract(epoch FROM (seg.e - seg.s))) FILTER (WHERE seg.state = 'active'), 0))::int,
           floor(COALESCE(sum(extract(epoch FROM (seg.e - seg.s))) FILTER (WHERE seg.state = 'idle'), 0))::int
    FROM (
        SELECT COALESCE(i.device_user_id, '00000000-0000-0000-0000-000000000000'::uuid) AS lane,
               i.state,
               g.h AS hour_start,
               GREATEST(i.started_at AT TIME ZONE o.timezone, g.h) AS s,
               LEAST(i.ended_at AT TIME ZONE o.timezone, g.h + interval '1 hour') AS e
        FROM activity_intervals i
        JOIN organizations o ON o.id = i.tenant_id
        CROSS JOIN LATERAL generate_series(
            date_trunc('hour', i.started_at AT TIME ZONE o.timezone),
            date_trunc('hour', i.ended_at   AT TIME ZONE o.timezone),
            interval '1 hour') AS g(h)
        WHERE i.tenant_id = @t AND i.device_id = @d AND i.source_day = @day
          AND i.state IN ('active', 'idle')
    ) seg
    WHERE seg.e > seg.s
    GROUP BY 3, seg.lane
    HAVING sum(extract(epoch FROM (seg.e - seg.s))) >= 1
    """, [("t", tenantId), ("d", deviceId), ("day", day)], ct);
```

No cabeçalho da classe, acrescentar à lista de regras:

```
///  - hourly_activity (F6): mesmo snapshot, recorte de cada intervalo active/idle nas
///    fronteiras de hora LOCAL do tenant (organizations.timezone). Linha com menos de 1 s
///    no balde não é gravada (HAVING) — evita 24 linhas de ruído por dia e device;
```

- [ ] **Step 5: Purga de retenção**

Em `RetentionPurgeService`, acrescentar `hourly_activity` à mesma lista de tabelas `daily_*` purgadas em 24 meses, no mesmo formato das existentes (a classe tem um array/lista de comandos DELETE por tabela; seguir o padrão local do arquivo).

- [ ] **Step 6: Contrato e endpoint**

Em `DashboardContracts.cs`:

```csharp
// ----- GET /api/v1/dashboard/activity-by-hour (F6 — de hourly_activity) -----

/// <summary>
/// As 24 horas locais do tenant, SEMPRE presentes (hora sem dado vem com zeros — o gráfico
/// não pode ter buraco). days_with_data é o denominador de avg_people_active.
/// </summary>
public sealed record ActivityByHourResponse(
    IReadOnlyList<ActivityByHourItemResponse> Hours,
    int DaysWithData);

/// <summary>
/// avg_people_active = seconds_active ÷ 3600 ÷ days_with_data: a média de pessoas ativas
/// SIMULTÂNEAS naquela hora ao longo do período. null quando o período não tem nenhum dia
/// com dado (dividir por zero seria mentira, e zero também).
/// </summary>
public sealed record ActivityByHourItemResponse(
    int Hour,
    long SecondsActive,
    long SecondsIdle,
    double? AvgPeopleActive);
```

No `DashboardController`:

```csharp
/// <summary>
/// GET /api/v1/dashboard/activity-by-hour?from&amp;to[&amp;tag] (F6): distribuição do tempo
/// ativo/ocioso pelas 24 horas LOCAIS do tenant, de hourly_activity. Devices archived ficam
/// fora (mesma régua do summary). Agregado de equipe: sem auditoria.
/// </summary>
[HttpGet("activity-by-hour")]
public async Task<IActionResult> ActivityByHour(
    [FromQuery(Name = "from")] string? from,
    [FromQuery(Name = "to")] string? to,
    [FromQuery(Name = "tag")] string? tag,
    CancellationToken ct)
{
    var invalid = ValidateRange(from, to);
    if (invalid is not null) return invalid;

    var tenantId = Auth.CurrentUser.TenantId(User);
    await using var connection = await dataSource.OpenConnectionAsync(ct);

    var rows = (await connection.QueryAsync<HourRow>(new CommandDefinition(
        """
        SELECT h.hour_local AS hour,
               COALESCE(sum(h.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(h.seconds_idle), 0)::bigint AS seconds_idle
        FROM hourly_activity h
        JOIN devices d ON d.id = h.device_id AND d.tenant_id = h.tenant_id
        WHERE h.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND h.summary_date BETWEEN @From::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
        GROUP BY h.hour_local
        """,
        new { TenantId = tenantId, From = from, To = to, Tag = NormalizeTeamTag(tag) },
        cancellationToken: ct))).ToDictionary(r => r.Hour);

    // dias COM dado no recorte: denominador da média de pessoas simultâneas
    var days = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT count(DISTINCT s.summary_date)::int
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND s.summary_date BETWEEN @From::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
          AND s.seconds_on > 0
        """,
        new { TenantId = tenantId, From = from, To = to, Tag = NormalizeTeamTag(tag) },
        cancellationToken: ct));

    var hours = Enumerable.Range(0, 24).Select(h =>
    {
        rows.TryGetValue(h, out var row);
        var active = row?.SecondsActive ?? 0;
        var idle = row?.SecondsIdle ?? 0;
        double? avg = days > 0 ? Math.Round(active / 3600.0 / days, 4) : null;
        return new ActivityByHourItemResponse(h, active, idle, avg);
    }).ToList();

    return Ok(new ActivityByHourResponse(hours, days));
}

private sealed record HourRow(int Hour, long SecondsActive, long SecondsIdle);
```

- [ ] **Step 7: Rodar o teste e ver passar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~ActivityByHourTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add backend/src/M351.Infrastructure/Data/Migrations/20260907131000_AtividadePorHoraF6.cs \
        backend/src/M351.Infrastructure/Aggregation/DailyAggregationService.cs \
        backend/src/M351.Infrastructure/Retention/RetentionPurgeService.cs \
        backend/src/M351.Api/Contracts/DashboardContracts.cs \
        backend/src/M351.Api/Controllers/DashboardController.cs \
        backend/tests/M351.IntegrationTests/ActivityByHourTests.cs
git commit -m "feat(backend): agregado hourly_activity e endpoint activity-by-hour"
```

---

### Task 3: `GET /dashboard/overview` com índice, cobertura e período anterior

A Visão Geral nova precisa de seis KPIs, a composição em seis baldes, a comparação com o período anterior e a série por dia. Hoje isso são três chamadas e três cálculos no cliente, e o índice não existe em lugar nenhum. Decisão 4: fórmula única, no servidor.

**Files:**
- Modify: `backend/src/M351.Api/Contracts/DashboardContracts.cs`
- Modify: `backend/src/M351.Api/Controllers/DashboardController.cs`
- Test: `backend/tests/M351.IntegrationTests/DashboardOverviewTests.cs`

**Interfaces:**
- Consumes: Task 1 (`seconds_unclassified`).
- Produces: `GET /api/v1/dashboard/overview?from&to[&tag][&compare=true]` devolvendo
  `OverviewResponse(OverviewPeriodResponse Period, OverviewTotalsResponse Totals, OverviewPeriodTotalsResponse? Previous, IReadOnlyList<DashboardSummaryDayResponse> Days, OverviewGoalsResponse Goals)`.

- [ ] **Step 1: Escrever o teste que falha**

`DashboardOverviewTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET /api/v1/dashboard/overview (F6): KPIs da Visão Geral com índice de produtividade e
/// cobertura da classificação calculados no SERVIDOR (fonte única da fórmula, decisão 4 do
/// spec), seis baldes de composição, série por dia e o período anterior de mesma duração.
/// </summary>
[Collection(ApiCollection.Name)]
public class DashboardOverviewTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
    private static DateTimeOffset T(int dayOffset, int h, int m) => Base.AddDays(dayOffset).AddHours(h).AddMinutes(m);
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Overview_IndiceECobertura_CalculadosNoServidor()
    {
        var org = await fixture.CreateOrganizationAsync($"Ovw {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-OVW-1");

        // 60 min produtivo + 20 min improdutivo + 20 min sem classificação
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 12, 0), new Dictionary<string, object?> { ["process_name"] = "protheus.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 13, 0), new Dictionary<string, object?> { ["process_name"] = "whatsapp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(0, 13, 20), new Dictionary<string, object?> { ["process_name"] = "desconhecido.exe" }),
            f.Event("LOCK", T(0, 13, 40)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new IntervalizationService(ds).RunOnceAsync();
            await new DailyAggregationService(ds).RunOnceAsync();
        }
        await MapAsync(org.Id, "protheus.exe", 1);
        await MapAsync(org.Id, "whatsapp.exe", -1);
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(org.Id);
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        var day = LocalDate(T(0, 12, 0));
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/overview?from={day}&to={day}&compare=true", token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var json = JsonDocument.Parse(body);
        var totals = json.RootElement.GetProperty("totals");
        Assert.Equal(100 * 60, totals.GetProperty("seconds_active").GetInt64());
        Assert.Equal(60 * 60, totals.GetProperty("seconds_work_related").GetInt64());
        Assert.Equal(20 * 60, totals.GetProperty("seconds_not_work_related").GetInt64());
        Assert.Equal(20 * 60, totals.GetProperty("seconds_unclassified").GetInt64());
        Assert.Equal(0, totals.GetProperty("seconds_neutral").GetInt64());
        // índice = 3600 / (3600 + 0 + 1200) = 0,75 · cobertura = (6000-1200)/6000 = 0,8
        Assert.Equal(0.75, totals.GetProperty("productivity_index").GetDouble(), 4);
        Assert.Equal(0.8, totals.GetProperty("classification_coverage").GetDouble(), 4);
        Assert.Equal(1, totals.GetProperty("person_days").GetInt32());
        Assert.Equal(1, totals.GetProperty("person_count").GetInt32());

        // período anterior existe no contrato mesmo sem dado, com índice null
        var previous = json.RootElement.GetProperty("previous");
        Assert.Equal(0, previous.GetProperty("seconds_active").GetInt64());
        Assert.Equal(JsonValueKind.Null, previous.GetProperty("productivity_index").ValueKind);

        Assert.Single(json.RootElement.GetProperty("days").EnumerateArray());
    }

    [Fact]
    public async Task Overview_SemCompare_NaoTrazPeriodoAnterior()
    {
        var org = await fixture.CreateOrganizationAsync($"OvwNC {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        var day = LocalDate(T(0, 12, 0));
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/overview?from={day}&to={day}", token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("previous").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("totals").GetProperty("productivity_index").ValueKind);
    }

    private async Task MapAsync(Guid tenantId, string processName, int classification)
    {
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO categories (id, tenant_id, name, classification, color, created_at) VALUES (@c, @t, @n, @cls, NULL, now())",
            ("c", categoryId), ("t", tenantId), ("n", $"C{classification}-{processName}"), ("cls", classification));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id, custom_display_name)
            SELECT @t, a.id, @c, NULL FROM app_catalog a WHERE a.process_name = @p
            ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
            """,
            ("t", tenantId), ("c", categoryId), ("p", processName));
    }
}
```

- [ ] **Step 2: Rodar o teste e ver falhar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~DashboardOverviewTests"`
Expected: FAIL — 404.

- [ ] **Step 3: Contratos**

Em `DashboardContracts.cs`:

```csharp
// ----- GET /api/v1/dashboard/overview (F6 — KPIs da Visão Geral) -----

/// <summary>
/// Tudo o que a Visão Geral precisa numa chamada: período resolvido, totais com índice e
/// cobertura, o período anterior de mesma duração (quando compare=true) e a série por dia.
/// </summary>
public sealed record OverviewResponse(
    OverviewPeriodResponse Period,
    OverviewTotalsResponse Totals,
    OverviewTotalsResponse? Previous,
    IReadOnlyList<DashboardSummaryDayResponse> Days,
    OverviewGoalsResponse Goals);

/// <summary>Período INCLUSIVO no fuso do tenant; days é a contagem de dias do intervalo.</summary>
public sealed record OverviewPeriodResponse(string From, string To, int Days);

/// <summary>
/// Baldes somados do período + os dois indicadores derivados (decisão 4 do spec):
/// productivity_index = work_related ÷ (work_related + neutral + not_work_related);
/// classification_coverage = (active − unclassified) ÷ active. Ambos null quando o
/// denominador é zero — nunca 0, que leria como "índice péssimo" em vez de "sem dado".
/// person_days = pares (lane de usuário, dia) com tempo; person_count = lanes distintas.
/// A lane-máquina (UUID zero) NÃO conta como pessoa (decisão 8).
/// </summary>
public sealed record OverviewTotalsResponse(
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    long SecondsUnclassified,
    double? ProductivityIndex,
    double? ClassificationCoverage,
    int DeviceCount,
    int PersonCount,
    int PersonDays,
    bool DataIncomplete);

/// <summary>Metas semanais da organização, repetidas aqui para a tela não precisar do /me.</summary>
public sealed record OverviewGoalsResponse(int? WeeklyActiveHours, int? WorkRelatedPct);
```

- [ ] **Step 4: Endpoint**

No `DashboardController`:

```csharp
/// <summary>
/// GET /api/v1/dashboard/overview?from&amp;to[&amp;tag][&amp;compare] (F6): a Visão Geral numa
/// chamada. Índice de produtividade e cobertura da classificação são calculados AQUI — fonte
/// única da fórmula (decisão 4 do spec de 07/09/2026); o portal nunca recalcula. compare=true
/// devolve o período imediatamente anterior de MESMA duração. Agregado de equipe: sem audit.
/// </summary>
[HttpGet("overview")]
public async Task<IActionResult> Overview(
    [FromQuery(Name = "from")] string? from,
    [FromQuery(Name = "to")] string? to,
    [FromQuery(Name = "tag")] string? tag,
    [FromQuery(Name = "compare")] bool compare,
    CancellationToken ct)
{
    var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
    if (invalid is not null) return invalid;

    var tenantId = Auth.CurrentUser.TenantId(User);
    var normalizedTag = NormalizeTeamTag(tag);
    await using var connection = await dataSource.OpenConnectionAsync(ct);

    var days = await QueryDaysAsync(connection, tenantId, fromDay, toDay, normalizedTag, ct);
    var totals = await QueryTotalsAsync(connection, tenantId, fromDay, toDay, normalizedTag, ct);

    OverviewTotalsResponse? previous = null;
    if (compare)
    {
        var length = toDay.DayNumber - fromDay.DayNumber + 1;
        var prevTo = fromDay.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(length - 1));
        previous = await QueryTotalsAsync(connection, tenantId, prevFrom, prevTo, normalizedTag, ct);
    }

    var goals = await connection.QuerySingleAsync<GoalsRow>(new CommandDefinition(
        "SELECT goal_weekly_active_hours AS weekly, goal_work_related_pct AS pct FROM organizations WHERE id = @TenantId",
        new { TenantId = tenantId }, cancellationToken: ct));

    return Ok(new OverviewResponse(
        new OverviewPeriodResponse(fromDay.ToString("yyyy-MM-dd"), toDay.ToString("yyyy-MM-dd"),
            toDay.DayNumber - fromDay.DayNumber + 1),
        totals, previous, days,
        new OverviewGoalsResponse(goals.Weekly, goals.Pct)));
}

private async Task<List<DashboardSummaryDayResponse>> QueryDaysAsync(
    NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, string? tag, CancellationToken ct) =>
    (await connection.QueryAsync<SummaryRow>(new CommandDefinition(
        """
        SELECT s.summary_date::text AS date,
               COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
               COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS seconds_work_related,
               COALESCE(sum(s.seconds_neutral), 0)::bigint AS seconds_neutral,
               COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS seconds_not_work_related,
               COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
               COALESCE(bool_or(s.data_incomplete), false) AS data_incomplete,
               count(DISTINCT s.device_id)::int AS device_count
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND s.summary_date BETWEEN @From::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
        GROUP BY s.summary_date
        ORDER BY s.summary_date
        """,
        new { TenantId = tenantId, From = from.ToString("yyyy-MM-dd"), To = to.ToString("yyyy-MM-dd"), Tag = tag },
        cancellationToken: ct)))
    .Select(r => new DashboardSummaryDayResponse(
        r.Date!, r.SecondsActive, r.SecondsIdle, r.SecondsLocked, r.SecondsOn,
        r.SecondsWorkRelated, r.SecondsNeutral, r.SecondsNotWorkRelated, r.SecondsUnclassified,
        r.DataIncomplete, r.DeviceCount))
    .ToList();

private async Task<OverviewTotalsResponse> QueryTotalsAsync(
    NpgsqlConnection connection, Guid tenantId, DateOnly from, DateOnly to, string? tag, CancellationToken ct)
{
    var row = await connection.QuerySingleAsync<TotalsRow>(new CommandDefinition(
        """
        SELECT COALESCE(sum(s.seconds_on), 0)::bigint AS seconds_on,
               COALESCE(sum(s.seconds_active), 0)::bigint AS seconds_active,
               COALESCE(sum(s.seconds_idle), 0)::bigint AS seconds_idle,
               COALESCE(sum(s.seconds_locked), 0)::bigint AS seconds_locked,
               COALESCE(sum(s.seconds_work_related), 0)::bigint AS seconds_work_related,
               COALESCE(sum(s.seconds_neutral), 0)::bigint AS seconds_neutral,
               COALESCE(sum(s.seconds_not_work_related), 0)::bigint AS seconds_not_work_related,
               COALESCE(sum(s.seconds_unclassified), 0)::bigint AS seconds_unclassified,
               COALESCE(bool_or(s.data_incomplete), false) AS data_incomplete,
               count(DISTINCT s.device_id)::int AS device_count,
               count(DISTINCT s.device_user_id) FILTER (
                   WHERE s.device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
                     AND s.seconds_on > 0)::int AS person_count,
               count(*) FILTER (
                   WHERE s.device_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
                     AND s.seconds_on > 0)::int AS person_days
        FROM daily_device_summaries s
        JOIN devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
        WHERE s.tenant_id = @TenantId
          AND d.status <> 'archived'
          AND s.summary_date BETWEEN @From::date AND @To::date
          AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
        """,
        new { TenantId = tenantId, From = from.ToString("yyyy-MM-dd"), To = to.ToString("yyyy-MM-dd"), Tag = tag },
        cancellationToken: ct));

    var classified = row.SecondsWorkRelated + row.SecondsNeutral + row.SecondsNotWorkRelated;
    double? index = classified > 0 ? Math.Round((double)row.SecondsWorkRelated / classified, 4) : null;
    double? coverage = row.SecondsActive > 0
        ? Math.Round((double)(row.SecondsActive - row.SecondsUnclassified) / row.SecondsActive, 4)
        : null;

    return new OverviewTotalsResponse(
        row.SecondsOn, row.SecondsActive, row.SecondsIdle, row.SecondsLocked,
        row.SecondsWorkRelated, row.SecondsNeutral, row.SecondsNotWorkRelated, row.SecondsUnclassified,
        index, coverage, row.DeviceCount, row.PersonCount, row.PersonDays, row.DataIncomplete);
}

private sealed record TotalsRow(
    long SecondsOn, long SecondsActive, long SecondsIdle, long SecondsLocked,
    long SecondsWorkRelated, long SecondsNeutral, long SecondsNotWorkRelated, long SecondsUnclassified,
    bool DataIncomplete, int DeviceCount, int PersonCount, int PersonDays);

private sealed record GoalsRow(int? Weekly, int? Pct);
```

`ValidateRange` com `out` já existe no `ApiControllerBase`; a sobrecarga sem `out` usada pelos outros endpoints continua válida.

- [ ] **Step 5: Rodar o teste e ver passar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~DashboardOverviewTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add backend/src/M351.Api/Contracts/DashboardContracts.cs \
        backend/src/M351.Api/Controllers/DashboardController.cs \
        backend/tests/M351.IntegrationTests/DashboardOverviewTests.cs
git commit -m "feat(backend): GET /dashboard/overview com índice e cobertura no servidor"
```

---

### Task 4: Identidade de pessoa e `GET /people`

`device_users` é (dispositivo, SID): a mesma pessoa em duas máquinas são dois registros, e o portal não tem lista de colaboradores. A identidade canônica de pessoa no tenant é o `windows_sid`, que já está gravado; a tabela `people` só guarda apelido e mesclagem.

**Files:**
- Create: `backend/src/M351.Infrastructure/Data/Migrations/20260907132000_PessoasF6.cs`
- Create: `backend/src/M351.Api/Contracts/PeopleContracts.cs`
- Create: `backend/src/M351.Api/Controllers/PeopleController.cs`
- Test: `backend/tests/M351.IntegrationTests/PeopleEndpointTests.cs`
- Modify: `backend/tests/M351.IntegrationTests/TenantIsolationTests.cs` (o endpoint novo entra na suíte canônica)

**Interfaces:**
- Consumes: Task 1 (`seconds_unclassified`), Task 3 (`OverviewTotalsResponse` não é reusado aqui — a linha de pessoa tem contrato próprio).
- Produces: tabela `people`; `GET /api/v1/people?from&to[&tag][&q][&sort][&dir][&page][&page_size]` devolvendo `PeopleReportResponse(IReadOnlyList<PersonRowResponse> Items, int Total, int Page, int PageSize)`; `PATCH /api/v1/people/{sid}` para apelido e mesclagem.

- [ ] **Step 1: Escrever o teste que falha**

`PeopleEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET /api/v1/people (F6): a lista de colaboradores que o portal não tinha. Identidade =
/// windows_sid do tenant, então a MESMA pessoa em duas máquinas vira UMA linha (o que
/// device_users, chaveado por (device, sid), não conseguia). Ordem alfabética por padrão,
/// leitura auditada como view_report (decisão 2 do spec).
/// </summary>
[Collection(ApiCollection.Name)]
public class PeopleEndpointTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    [Fact]
    public async Task People_MesmaPessoaEmDuasMaquinas_UmaLinhaSomada()
    {
        var org = await fixture.CreateOrganizationAsync($"Pess {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        var nb = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-PESS-1");
        var desktop = await AgentClient.EnrollAsync(client, fullKey, hostname: "PC-PESS-2");

        const string sid = "S-1-5-21-1111-2222-3333-1001";
        await SeedActiveAsync(client, nb, sid, "ana.oliveira", "protheus.exe", T(12, 0), 30);
        await SeedActiveAsync(client, desktop, sid, "ana.oliveira", "excel.exe", T(14, 0), 20);
        await RunPipelineAsync();

        var day = LocalDate(T(12, 0));
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/people?from={day}&to={day}", token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        var ana = items.Single(i => i.GetProperty("windows_sid").GetString() == sid);
        Assert.Equal(50 * 60, ana.GetProperty("seconds_active").GetInt64());
        Assert.Equal(2, ana.GetProperty("device_count").GetInt32());
        Assert.Equal("ana.oliveira", ana.GetProperty("display_name").GetString());

        // decisão 2: leitura da lista de pessoas é dado pessoal → auditada
        var audited = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'view_report'", ("t", org.Id));
        Assert.True(audited >= 1);
    }

    [Fact]
    public async Task People_ApelidoDoAdmin_VenceOUsuarioDoWindows()
    {
        var org = await fixture.CreateOrganizationAsync($"PessNick {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var nb = await AgentClient.EnrollAsync(client, fullKey, hostname: "NB-NICK-1");

        const string sid = "S-1-5-21-9999-8888-7777-1002";
        await SeedActiveAsync(client, nb, sid, "b.santos", "protheus.exe", T(12, 0), 15);
        await RunPipelineAsync();

        using var patch = AuthClient.AuthorizedRequest(HttpMethod.Patch, $"/api/v1/people/{sid}", token);
        patch.Content = JsonContent.Create(new { display_name = "Bruno Santos" });
        var patchResponse = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var day = LocalDate(T(12, 0));
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/people?from={day}&to={day}", token);
        using var json = JsonDocument.Parse(await (await client.SendAsync(request)).Content.ReadAsStringAsync());
        var row = json.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("windows_sid").GetString() == sid);
        Assert.Equal("Bruno Santos", row.GetProperty("display_name").GetString());
    }

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(ds).RunOnceAsync();
        await new DailyAggregationService(ds).RunOnceAsync();
    }

    private static async Task SeedActiveAsync(
        HttpClient client, EnrolledDevice device, string sid, string username,
        string process, DateTimeOffset start, int minutes)
    {
        var f = new EventFactory(windowsSid: sid, windowsUser: username);
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", start, new Dictionary<string, object?> { ["process_name"] = process }),
            f.Event("LOCK", start.AddMinutes(minutes)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
    }
}
```

Se `EventFactory` ainda não aceitar SID/usuário no construtor, acrescentar os dois parâmetros opcionais em `Support/EventFactory.cs` mantendo os defaults atuais (nenhum teste existente muda).

- [ ] **Step 2: Rodar o teste e ver falhar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~PeopleEndpointTests"`
Expected: FAIL — 404.

- [ ] **Step 3: Migration**

`20260907132000_PessoasF6.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — pessoas. A identidade canônica de uma pessoa no tenant é o windows_sid, que
    /// device_users JÁ grava; o que faltava era (a) um lugar para o apelido dado pelo admin e
    /// (b) mesclagem quando a MESMA pessoa aparece com dois SIDs (conta local numa máquina
    /// fora do domínio, por exemplo).
    ///
    /// Por que não uma tabela people com person_id em device_users: exigiria mexer no caminho
    /// quente da ingestão (IngestService.UpsertDeviceUsers) e um backfill por tenant. Chavear
    /// people pelo próprio SID dá identidade cross-device de graça — a lista de colaboradores
    /// agrupa daily_device_summaries por (tenant, sid resolvido) — sem tocar na ingestão.
    ///
    /// merged_into_sid aponta para o SID que absorve este; a leitura resolve com COALESCE e
    /// UM salto (mesclagem em cadeia é impedida na aplicação: o alvo precisa estar sem
    /// merged_into_sid). Linha em people existe SÓ quando houve apelido ou mesclagem — a
    /// lista funciona com a tabela vazia.
    /// </summary>
    public partial class PessoasF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE people (
                    tenant_id       uuid NOT NULL,
                    windows_sid     text NOT NULL,
                    display_name    text NULL,
                    merged_into_sid text NULL,
                    created_at      timestamptz NOT NULL DEFAULT now(),
                    updated_at      timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT pk_people PRIMARY KEY (tenant_id, windows_sid),
                    CONSTRAINT ck_people_merge_nao_aponta_para_si
                        CHECK (merged_into_sid IS NULL OR merged_into_sid <> windows_sid)
                );
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP TABLE people;");
    }
}
```

- [ ] **Step 4: Contratos**

`PeopleContracts.cs`:

```csharp
namespace M351.Api.Contracts;

// ----- GET /api/v1/people (F6 — lista de colaboradores) -----

/// <summary>
/// Uma linha por PESSOA do tenant no período (identidade = windows_sid resolvido pela
/// mesclagem). Paginado no padrão dos relatórios; total é a contagem do período inteiro.
/// </summary>
public sealed record PeopleReportResponse(
    IReadOnlyList<PersonRowResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// display_name resolvido pelo servidor: apelido de people, senão o windows_username mais
/// recente, senão o próprio SID. Nunca reimplementar essa regra no cliente.
/// productivity_index e classification_coverage seguem a fórmula única do overview.
/// </summary>
public sealed record PersonRowResponse(
    string WindowsSid,
    string DisplayName,
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    long SecondsUnclassified,
    double? ProductivityIndex,
    double? ClassificationCoverage,
    int DeviceCount,
    int DaysWithData,
    IReadOnlyList<string> Teams);

/// <summary>PATCH /api/v1/people/{sid}: apelido e mesclagem. Campo ausente não muda nada.</summary>
public sealed record PersonPatchRequest(string? DisplayName, string? MergedIntoSid);
```

- [ ] **Step 5: Controller**

`PeopleController.cs`:

```csharp
using System.Text.Json;
using Dapper;
using M351.Api.Contracts;
using M351.Api.Security;
using M351.Infrastructure.Audit;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace M351.Api.Controllers;

/// <summary>
/// Colaboradores (F6): a lista que o portal não tinha. Identidade = windows_sid do tenant,
/// resolvido pela mesclagem de people. A lane-máquina (device_user_id UUID zero) NÃO é pessoa
/// (decisão 8 do spec) e fica fora.
///
/// Decisão 2 do spec: existe lista com métricas e ordenação por coluna, restrita a
/// Gestor/Admin/Owner e AUDITADA. Não existe a palavra ranking, nem gamificação, nem
/// publicação — a ordem default é alfabética.
/// </summary>
[Authorize]
[Route("api/v1/people")]
public sealed class PeopleController(
    NpgsqlDataSource dataSource, M351DbContext db, IAuditLogger audit) : ApiControllerBase
{
    /// <summary>Colunas ordenáveis: nome ou uma das métricas do período.</summary>
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "display_name",
        ["seconds_on"] = "seconds_on",
        ["seconds_active"] = "seconds_active",
        ["seconds_idle"] = "seconds_idle",
        ["seconds_unclassified"] = "seconds_unclassified",
        ["productivity_index"] = "productivity_index",
    };

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "from")] string? from,
        [FromQuery(Name = "to")] string? to,
        [FromQuery(Name = "tag")] string? tag,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "sort")] string? sort,
        [FromQuery(Name = "dir")] string? dir,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 50,
        CancellationToken ct = default)
    {
        var invalid = ValidateRange(from, to, out var fromDay, out var toDay);
        if (invalid is not null) return invalid;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var sortColumn = SortColumns.TryGetValue(sort ?? "name", out var column) ? column : "display_name";
        var descending = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        // ORDER BY interpolado a partir de uma allow-list fechada (nunca do input cru)
        var orderBy = sortColumn == "display_name"
            ? $"display_name {(descending ? "DESC" : "ASC")}"
            : $"{sortColumn} {(descending ? "DESC" : "ASC")} NULLS LAST, display_name ASC";

        var tenantId = Auth.CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var rows = (await connection.QueryAsync<PersonRow>(new CommandDefinition(
            $"""
            WITH lanes AS (
                SELECT COALESCE(p.merged_into_sid, du.windows_sid) AS sid,
                       du.display_name AS lane_display_name,
                       du.windows_username,
                       du.last_seen_at,
                       s.device_id,
                       d.tags,
                       s.summary_date,
                       s.seconds_on, s.seconds_active, s.seconds_idle, s.seconds_locked,
                       s.seconds_work_related, s.seconds_neutral, s.seconds_not_work_related,
                       s.seconds_unclassified
                FROM daily_device_summaries s
                JOIN device_users du ON du.tenant_id = s.tenant_id AND du.id = s.device_user_id
                JOIN devices d ON d.tenant_id = s.tenant_id AND d.id = s.device_id
                LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
                WHERE s.tenant_id = @TenantId
                  AND d.status <> 'archived'
                  AND s.summary_date BETWEEN @From::date AND @To::date
                  AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
            ),
            agg AS (
                SELECT l.sid AS windows_sid,
                       COALESCE(
                           (SELECT pp.display_name FROM people pp
                             WHERE pp.tenant_id = @TenantId AND pp.windows_sid = l.sid AND pp.display_name IS NOT NULL),
                           (array_agg(l.lane_display_name ORDER BY l.last_seen_at DESC NULLS LAST)
                                FILTER (WHERE l.lane_display_name IS NOT NULL))[1],
                           (array_agg(l.windows_username ORDER BY l.last_seen_at DESC NULLS LAST)
                                FILTER (WHERE l.windows_username IS NOT NULL))[1],
                           l.sid) AS display_name,
                       sum(l.seconds_on)::bigint AS seconds_on,
                       sum(l.seconds_active)::bigint AS seconds_active,
                       sum(l.seconds_idle)::bigint AS seconds_idle,
                       sum(l.seconds_locked)::bigint AS seconds_locked,
                       sum(l.seconds_work_related)::bigint AS seconds_work_related,
                       sum(l.seconds_neutral)::bigint AS seconds_neutral,
                       sum(l.seconds_not_work_related)::bigint AS seconds_not_work_related,
                       sum(l.seconds_unclassified)::bigint AS seconds_unclassified,
                       count(DISTINCT l.device_id)::int AS device_count,
                       count(DISTINCT l.summary_date) FILTER (WHERE l.seconds_on > 0)::int AS days_with_data,
                       COALESCE((SELECT array_agg(DISTINCT t ORDER BY t)
                                   FROM unnest(array_agg(l.tags)) AS x(arr)
                                   CROSS JOIN LATERAL unnest(x.arr) AS y(t)), '{{}}'::text[]) AS teams
                FROM lanes l
                GROUP BY l.sid
            ),
            calc AS (
                SELECT a.*,
                       CASE WHEN (a.seconds_work_related + a.seconds_neutral + a.seconds_not_work_related) > 0
                            THEN round((a.seconds_work_related::numeric
                                 / (a.seconds_work_related + a.seconds_neutral + a.seconds_not_work_related)), 4)
                       END AS productivity_index,
                       CASE WHEN a.seconds_active > 0
                            THEN round(((a.seconds_active - a.seconds_unclassified)::numeric / a.seconds_active), 4)
                       END AS classification_coverage
                FROM agg a
            )
            SELECT *, count(*) OVER ()::int AS total_count
            FROM calc
            WHERE (@Q::text IS NULL OR display_name ILIKE '%' || @Q || '%')
            ORDER BY {orderBy}
            LIMIT @PageSize OFFSET @Offset
            """,
            new
            {
                TenantId = tenantId,
                From = fromDay.ToString("yyyy-MM-dd"),
                To = toDay.ToString("yyyy-MM-dd"),
                Tag = NormalizeTeamTag(tag),
                Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
                PageSize = pageSize,
                Offset = (page - 1) * pageSize,
            },
            cancellationToken: ct))).ToList();

        // Decisão 2: a lista É dado pessoal — audita a leitura (uma entrada por consulta,
        // com o recorte, no padrão do view_report dos relatórios).
        audit.Add(tenantId, AuditActions.ViewReport,
            actorUserId: Auth.CurrentUser.UserId(User),
            targetType: "people",
            targetId: null,
            detailJson: JsonSerializer.Serialize(new { from, to, tag, q, sort, dir, page, page_size = pageSize }));
        await db.SaveChangesAsync(ct);

        var items = rows.Select(r => new PersonRowResponse(
            r.WindowsSid, r.DisplayName, r.SecondsOn, r.SecondsActive, r.SecondsIdle, r.SecondsLocked,
            r.SecondsWorkRelated, r.SecondsNeutral, r.SecondsNotWorkRelated, r.SecondsUnclassified,
            r.ProductivityIndex, r.ClassificationCoverage, r.DeviceCount, r.DaysWithData,
            r.Teams ?? [])).ToList();

        return Ok(new PeopleReportResponse(items, rows.FirstOrDefault()?.TotalCount ?? 0, page, pageSize));
    }

    /// <summary>
    /// PATCH /api/v1/people/{sid}: apelido e mesclagem (Admin+). Mesclagem em cadeia é
    /// recusada — o alvo precisa existir no tenant e estar sem merged_into_sid.
    /// </summary>
    [HttpPatch("{sid}")]
    [Authorize(Policy = Policies.AdminPlus)]
    public async Task<IActionResult> Patch(string sid, [FromBody] PersonPatchRequest body, CancellationToken ct)
    {
        var tenantId = Auth.CurrentUser.TenantId(User);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM device_users WHERE tenant_id = @TenantId AND windows_sid = @Sid)",
            new { TenantId = tenantId, Sid = sid }, cancellationToken: ct));
        if (!exists) return NotFoundProblem();

        if (body.MergedIntoSid is not null)
        {
            if (body.MergedIntoSid == sid)
                return ProblemResponse(StatusCodes.Status400BadRequest, "Uma pessoa não pode ser mesclada em si mesma.");

            var targetOk = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM device_users WHERE tenant_id = @TenantId AND windows_sid = @Target)
                   AND NOT EXISTS (SELECT 1 FROM people
                                    WHERE tenant_id = @TenantId AND windows_sid = @Target
                                      AND merged_into_sid IS NOT NULL)
                """,
                new { TenantId = tenantId, Target = body.MergedIntoSid }, cancellationToken: ct));
            if (!targetOk)
                return ProblemResponse(StatusCodes.Status400BadRequest,
                    "Destino da mesclagem inválido: precisa ser uma pessoa do tenant que ainda não foi mesclada.");
        }

        var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO people (tenant_id, windows_sid, display_name, merged_into_sid, created_at, updated_at)
            VALUES (@TenantId, @Sid, @DisplayName, @Merged, now(), now())
            ON CONFLICT (tenant_id, windows_sid) DO UPDATE SET
                display_name = EXCLUDED.display_name,
                merged_into_sid = EXCLUDED.merged_into_sid,
                updated_at = now()
            """,
            new { TenantId = tenantId, Sid = sid, DisplayName = displayName, Merged = body.MergedIntoSid },
            cancellationToken: ct));

        audit.Add(tenantId, AuditActions.UpdatePerson,
            actorUserId: Auth.CurrentUser.UserId(User),
            targetType: "person",
            targetId: null,
            detailJson: JsonSerializer.Serialize(new { windows_sid = sid, display_name = displayName, merged_into_sid = body.MergedIntoSid }));
        await db.SaveChangesAsync(ct);

        return Ok(new { windows_sid = sid, display_name = displayName, merged_into_sid = body.MergedIntoSid });
    }

    private sealed record PersonRow(
        string WindowsSid, string DisplayName,
        long SecondsOn, long SecondsActive, long SecondsIdle, long SecondsLocked,
        long SecondsWorkRelated, long SecondsNeutral, long SecondsNotWorkRelated, long SecondsUnclassified,
        double? ProductivityIndex, double? ClassificationCoverage,
        int DeviceCount, int DaysWithData, string[]? Teams, int TotalCount);
}
```

Acrescentar `UpdatePerson = "update_person"` em `AuditActions` (mesmo padrão das constantes existentes). O `Policies.AdminPlus` é o nome já usado pelos outros controllers — conferir a constante exata em `M351.Api/Security/Policies.cs` e usar a de lá.

- [ ] **Step 6: Rodar o teste e ver passar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~PeopleEndpointTests"`
Expected: PASS.

- [ ] **Step 7: Entrar na suíte de isolamento**

Em `TenantIsolationTests`, acrescentar o caso do endpoint novo no mesmo formato dos existentes: usuário do tenant A consultando `/api/v1/people?from&to` não vê nenhuma linha do tenant B, e `PATCH /api/v1/people/{sid}` com SID do tenant B devolve 404.

Run: `cd backend && dotnet test --filter "FullyQualifiedName~TenantIsolationTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add backend/src/M351.Infrastructure/Data/Migrations/20260907132000_PessoasF6.cs \
        backend/src/M351.Api/Contracts/PeopleContracts.cs \
        backend/src/M351.Api/Controllers/PeopleController.cs \
        backend/src/M351.Infrastructure/Audit/AuditActions.cs \
        backend/tests/M351.IntegrationTests/PeopleEndpointTests.cs \
        backend/tests/M351.IntegrationTests/TenantIsolationTests.cs
git commit -m "feat(backend): identidade de pessoa por SID e endpoint /people"
```

---

### Task 5: Reagregação retroativa até 12 meses sob demanda

Decisão 7: mudar a classificação hoje só corrige 30 dias de histórico; relatórios longos misturam classificações. O admin passa a poder pedir a reagregação da janela que quiser, até 366 dias.

**Files:**
- Modify: `backend/src/M351.Infrastructure/Aggregation/ReaggregationRequester.cs`
- Create: `backend/src/M351.Api/Controllers/ReaggregationController.cs`
- Test: `backend/tests/M351.IntegrationTests/ReaggregationEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 (a reagregação é o que corrige o histórico do balde novo).
- Produces: `ReaggregationRequester.RequestAsync(Guid tenantId, int days, CancellationToken)` e a estática `RequestAsync(NpgsqlConnection, NpgsqlTransaction?, Guid, int, CancellationToken)`; `POST /api/v1/reaggregation` com corpo `{ "days": 365 }` devolvendo `{ "enqueued": N, "days": 365 }`.

- [ ] **Step 1: Escrever o teste que falha**

`ReaggregationEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// POST /api/v1/reaggregation (F6, decisão 7 do spec): reagregação retroativa sob demanda até
/// 366 dias, para o histórico refletir a classificação vigente. Admin+, auditada, validando a
/// janela.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReaggregationEndpointTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Reaggregation_JanelaAcimaDoTeto_400()
    {
        var org = await fixture.CreateOrganizationAsync($"Reag {Guid.NewGuid():N}"[..20]);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { days = 400 });
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reaggregation_Viewer_403()
    {
        var org = await fixture.CreateOrganizationAsync($"ReagV {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { days = 30 });
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reaggregation_Admin_EnfileiraEAudita()
    {
        var org = await fixture.CreateOrganizationAsync($"ReagOk {Guid.NewGuid():N}"[..20]);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);

        using var request = AuthClient.AuthorizedRequest(HttpMethod.Post, "/api/v1/reaggregation", token);
        request.Content = JsonContent.Create(new { days = 365 });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);

        using var json = JsonDocument.Parse(body);
        Assert.Equal(365, json.RootElement.GetProperty("days").GetInt32());
        Assert.True(json.RootElement.GetProperty("enqueued").GetInt32() >= 0);

        var audited = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'reaggregate'", ("t", org.Id));
        Assert.Equal(1, audited);
    }
}
```

- [ ] **Step 2: Rodar o teste e ver falhar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~ReaggregationEndpointTests"`
Expected: FAIL — 404.

- [ ] **Step 3: Janela parametrizável no requester**

Em `ReaggregationRequester`, trocar as constantes e os dois métodos por versões com `days`, mantendo os nomes antigos como atalho para não quebrar chamadores:

```csharp
/// <summary>Janela default da reagregação automática, em dias (spec linha 777).</summary>
public const int WindowDays = 30;

/// <summary>Teto da reagregação sob demanda: 12 meses, a retenção de activity_intervals (N11).</summary>
public const int MaxWindowDays = 366;

private const string Sql = """
    INSERT INTO dirty_days (tenant_id, device_id, day)
    SELECT DISTINCT i.tenant_id, i.device_id, i.source_day
    FROM activity_intervals i
    WHERE i.tenant_id = @t
      AND i.started_at >= now() - make_interval(days => @days + 3)
      AND i.source_day >=
          ((now() AT TIME ZONE (SELECT timezone FROM organizations WHERE id = @t))::date - @days)
    ON CONFLICT (tenant_id, device_id, day) DO UPDATE SET day = EXCLUDED.day
    """;

/// <summary>Conexão própria. days é clampado a [1, MaxWindowDays]. Retorna linhas enfileiradas.</summary>
public async Task<int> RequestAsync(Guid tenantId, int days = WindowDays, CancellationToken ct = default)
{
    await using var connection = await dataSource.OpenConnectionAsync(ct);
    return await RequestAsync(connection, null, tenantId, days, ct);
}

/// <summary>Variante para participar da transação do chamador. Retorna linhas enfileiradas.</summary>
public static async Task<int> RequestAsync(
    NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenantId,
    int days = WindowDays, CancellationToken ct = default)
{
    await using var command = new NpgsqlCommand(Sql, connection, transaction);
    command.Parameters.AddWithValue("t", tenantId);
    command.Parameters.AddWithValue("days", Math.Clamp(days, 1, MaxWindowDays));
    return await command.ExecuteNonQueryAsync(ct);
}

/// <summary>Atalho da janela default — os chamadores de curadoria de categoria usam este.</summary>
public Task<int> RequestLast30DaysAsync(Guid tenantId, CancellationToken ct = default) =>
    RequestAsync(tenantId, WindowDays, ct);

/// <summary>Atalho transacional da janela default.</summary>
public static Task<int> RequestLast30DaysAsync(
    NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenantId, CancellationToken ct = default) =>
    RequestAsync(connection, transaction, tenantId, WindowDays, ct);
```

O comentário do cabeçalho ganha a linha: `- janela sob demanda (F6, decisão 7): até 366 dias via POST /reaggregation; o default automático da curadoria segue 30 dias, e a folga de partition pruning acompanha a janela (days + 3).`

- [ ] **Step 4: Controller**

`ReaggregationController.cs`:

```csharp
using System.Text.Json;
using M351.Api.Security;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Audit;
using M351.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace M351.Api.Controllers;

/// <summary>
/// Reagregação retroativa sob demanda (F6, decisão 7 do spec): enfileira em dirty_days todos
/// os (device, dia) com intervalos na janela pedida, e o DailyAggregationService recomputa os
/// baldes com a classificação VIGENTE. Admin+, auditada.
///
/// Não é síncrono de propósito: 12 meses de uma frota grande são milhares de pares, que o
/// worker drena em ciclos de 15 min. A resposta é 202 com quantos pares entraram na fila.
/// </summary>
[Authorize(Policy = Policies.AdminPlus)]
[Route("api/v1/reaggregation")]
public sealed class ReaggregationController(
    ReaggregationRequester requester, M351DbContext db, IAuditLogger audit) : ApiControllerBase
{
    public sealed record ReaggregationRequest(int? Days);

    [HttpPost]
    public async Task<IActionResult> Request([FromBody] ReaggregationRequest body, CancellationToken ct)
    {
        var days = body.Days ?? ReaggregationRequester.WindowDays;
        if (days < 1 || days > ReaggregationRequester.MaxWindowDays)
            return ProblemResponse(StatusCodes.Status400BadRequest,
                $"Janela inválida: informe de 1 a {ReaggregationRequester.MaxWindowDays} dias.");

        var tenantId = Auth.CurrentUser.TenantId(User);
        var enqueued = await requester.RequestAsync(tenantId, days, ct);

        audit.Add(tenantId, AuditActions.Reaggregate,
            actorUserId: Auth.CurrentUser.UserId(User),
            targetType: "organization",
            targetId: tenantId,
            detailJson: JsonSerializer.Serialize(new { days, enqueued }));
        await db.SaveChangesAsync(ct);

        return Accepted(new { enqueued, days });
    }
}
```

Acrescentar `Reaggregate = "reaggregate"` em `AuditActions`. Conferir que `ReaggregationRequester` está registrado no DI da API (`Program.cs`); se só o Worker o registra, acrescentar `builder.Services.AddSingleton<ReaggregationRequester>()` no mesmo lugar em que os outros serviços de Infrastructure são registrados.

- [ ] **Step 5: Rodar o teste e ver passar**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~ReaggregationEndpointTests"`
Expected: PASS.

- [ ] **Step 6: Suíte inteira**

Run: `cd backend && dotnet test`
Expected: PASS (inclusive `CategoriesEndpointTests` e `AppCatalogEndpointTests`, que chamam os atalhos antigos do requester).

- [ ] **Step 7: Commit**

```bash
git add backend/src/M351.Infrastructure/Aggregation/ReaggregationRequester.cs \
        backend/src/M351.Api/Controllers/ReaggregationController.cs \
        backend/src/M351.Api/Program.cs \
        backend/src/M351.Infrastructure/Audit/AuditActions.cs \
        backend/tests/M351.IntegrationTests/ReaggregationEndpointTests.cs
git commit -m "feat(backend): reagregação retroativa sob demanda até 12 meses"
```

---

## Self-Review

**Cobertura do spec (seção 6, fase F1):** `seconds_unclassified` + reagregação (Task 1 e 5) · `hourly_activity` e `activity-by-hour` (Task 2) · `GET /dashboard/overview` (Task 3) · entidade pessoa com vínculo por SID (Task 4) · decisão 8 sobre "ligada sem usuário" honrada em Task 1 (nada muda no pipeline) e Task 3/4 (lane-máquina fora da contagem de pessoas). A entidade `teams` e feriados ficam para a F7, como o spec define; a comparação de equipes lado a lado (decisão 3) usa o `?tag` que já existe e ganha a régua de mínimo de grupo na F7.

**Placeholders:** nenhum "TBD"; todo passo de código tem o código. Os dois pontos que exigem conferência local no repo estão nomeados com o arquivo e o motivo (a constante exata de `Policies.AdminPlus`, o formato da lista de tabelas do `RetentionPurgeService` e o construtor do `EventFactory`).

**Consistência de tipos:** `seconds_unclassified` é `int` na tabela e `long` no contrato (igual aos irmãos, que já são `long` no JSON e `int` na coluna). `ProductivityIndex`/`ClassificationCoverage` são `double?` no C# e `numeric` arredondado no SQL do `/people` — Dapper converte `numeric` para `double` sem perda nas 4 casas usadas. `ValidateRange` com `out` já existe no `ApiControllerBase`. `RequestLast30DaysAsync` continua existindo nas duas formas, então nenhum chamador atual quebra.
