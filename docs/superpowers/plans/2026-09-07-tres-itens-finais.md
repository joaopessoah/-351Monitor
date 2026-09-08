# Três itens finais do estudo — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fechar os três itens que sobraram do estudo de produtividade — índice explicável, visão do colaborador dentro do painel e agregados mensais — sem criar acesso nenhum para o colaborador.

**Architecture:** Três frentes independentes sobre o que já existe. (1) Um endpoint novo decompõe a variação do índice com uma identidade algébrica **exata**, lida de `daily_app_usage` e `daily_device_summaries`. (2) Uma tabela `monthly_summaries` resume a diária por mês, alimentada por um job com marca-d'água, e destrava janelas de até 24 meses onde hoje o teto é 92 dias. (3) A página da pessoa ganha a "visão do colaborador" servida por um endpoint que calcula índice e cobertura no servidor, matando de quebra a duplicação da fórmula no cliente.

**Tech Stack:** .NET 9 / ASP.NET Core, Dapper + Npgsql (SQL cru, sem EF nas tabelas de agregado), PostgreSQL 16, Quartz no Worker, QuestPDF nos exports; portal em React + TypeScript + TanStack Query + ECharts + Tailwind.

**Spec:** `docs/superpowers/specs/2026-09-07-produto-produtividade-design.md` — em especial a decisão 4 (fórmula do índice), a decisão 8 (máquina não é pessoa) e a **decisão 10, seção 7.2** (o colaborador não é usuário do painel).

## Global Constraints

- **Nenhum acesso novo para o colaborador.** Sem login, token pessoal, magic link ou rota pública com dado dele. `PublicTransparencyController` e `/t/{token}` ficam **intocados**, só com a política de coleta.
- **Fórmula única do índice**, no servidor: `produtivo ÷ (produtivo + neutro + improdutivo)`. Cobertura: `(ativo − sem classificação) ÷ ativo`. Sem denominador → `null`, **nunca zero**.
- **Precedência da classificação (F5):** regra da equipe (`tenant_app_team_categories`) vence a da organização (`tenant_app_categories`); sem categoria = sem classificação.
- **Máquina não é pessoa (decisão 8):** a lane `00000000-0000-0000-0000-000000000000` fica fora de contagem de pessoas, mas **entra** nos totais de segundos.
- **Retenção dos agregados: 24 meses.** `monthly_summaries` entra na mesma purga — a página pública promete "Agregados: 24 meses" e guardar mais transformaria a promessa em mentira.
- **Migrations pelo EF**, nunca escritas à mão: `dotnet ef migrations add <Nome> --project src/M351.Infrastructure --startup-project src/M351.Infrastructure --output-dir Data/Migrations`. Corpo em DDL cru (as tabelas de agregado não são entidades do EF).
- **Dapper não converte** `smallint`→`int`, `numeric`→`double` nem `text[]`→`string[]`: use `::int`, `::double precision`, `array_to_string(..., chr(31))`.
- **Teste com Admin/Owner** exige `mfaEnabled: true` no `CreateUserAsync`; eventos de teste espaçados de **5 minutos** (600 s ou mais cai na regra de lacuna N7 e vira `no_data`).
- **Auditoria:** leitura de dado pessoal identificado → `[AuditRead]` (`view_report`). Agregado de equipe/organização → sem auditoria.
- **Verificação antes de cada commit:** `cd backend && dotnet build && dotnet test` e `cd portal && npx tsc --noEmit -p tsconfig.json && npm run build`. Referência: **479+ testes verdes**.

---

## File Structure

| Arquivo | Responsabilidade |
|---|---|
| `backend/src/M351.Api/Contracts/IndexExplainedContracts.cs` | **Criar.** Contrato da decomposição: resposta, contribuição, dimensão. |
| `backend/src/M351.Api/Controllers/IndexExplainedController.cs` | **Criar.** `GET /dashboard/index-explained`. Fica fora do `DashboardController` (já com 500+ linhas) porque a decomposição é um assunto inteiro, com SQL próprio. |
| `backend/src/M351.Infrastructure/Analytics/IndexDecomposition.cs` | **Criar.** A matemática pura da decomposição, sem SQL e sem HTTP — testável isoladamente. |
| `backend/src/M351.Infrastructure/Aggregation/MonthlyRollupService.cs` | **Criar.** Rollup de `daily_device_summaries` para `monthly_summaries`, com marca-d'água por tenant. |
| `backend/src/M351.Worker/MonthlyRollupJob.cs` | **Criar.** Quartz, de hora em hora. |
| `backend/src/M351.Api/Contracts/PersonSelfViewContracts.cs` | **Criar.** Contrato da visão do colaborador. |
| `backend/src/M351.Api/Controllers/PeopleController.cs` | **Modificar.** Ganha `GET /people/{sid}/self-view`. |
| `backend/src/M351.Api/Controllers/DashboardController.cs` | **Modificar.** `?grain=month` no `overview`, teto de 24 meses. |
| `backend/src/M351.Infrastructure/Maintenance/RetentionPurgeService.cs` | **Modificar.** Purga `monthly_summaries` no mesmo corte. |
| `backend/src/M351.Infrastructure/Exports/ExportService.cs` | **Modificar.** `resumo_pdf` com `windows_sid` = variante pessoal. |
| `portal/src/components/dashboard/PorQueOIndiceMudou.tsx` | **Criar.** O painel do índice explicável. |
| `portal/src/components/pessoa/VisaoDoColaborador.tsx` | **Criar.** O bloco "o que mostramos a esta pessoa". |
| `portal/src/lib/period.ts` | **Modificar.** Presets longos (trimestre, ano) com grão mensal. |

---

### Task 1: A matemática da decomposição, isolada

O índice é `P/C`. Para dois períodos (0 = anterior, 1 = atual) vale a identidade **exata**:

```
P₁/C₁ − P₀/C₀ = (C₀·ΔP − P₀·ΔC) / (C₁·C₀),   com ΔP = Σₖ Δpₖ e ΔC = Σₖ Δcₖ
```

Logo cada membro `k` contribui com `(C₀·Δpₖ − P₀·Δcₖ)/(C₁·C₀)`, e **as parcelas somam a variação total sem resíduo**. É isso que autoriza a frase "menos 4 pontos: mais 6 h em WhatsApp na equipe Comercial". Sem essa identidade sobraria um "outros" para fechar a conta, e o cartão mentiria por arredondamento.

**Files:**
- Create: `backend/src/M351.Infrastructure/Analytics/IndexDecomposition.cs`
- Test: `backend/tests/M351.IntegrationTests/IndexDecompositionTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace M351.Infrastructure.Analytics;

  /// <summary>Segundos de um membro (app, equipe ou dia) nos dois períodos.</summary>
  public readonly record struct MemberSeconds(
      string Key, string Label,
      long WorkCurrent, long WorkPrevious,
      long ClassifiedCurrent, long ClassifiedPrevious);

  /// <summary>Contribuição do membro para a variação do índice, em pontos, com sinal.</summary>
  public readonly record struct MemberContribution(
      string Key, string Label, double Points,
      long WorkCurrent, long WorkPrevious,
      long ClassifiedCurrent, long ClassifiedPrevious);

  public static class IndexDecomposition
  {
      /// <summary>null quando C₀ ou C₁ é zero: sem denominador não há índice, logo não há variação a explicar.</summary>
      public static IReadOnlyList<MemberContribution>? Decompose(IReadOnlyList<MemberSeconds> members);

      /// <summary>Variação total em pontos (índice₁ − índice₀) × 100, ou null sem denominador.</summary>
      public static double? DeltaPoints(IReadOnlyList<MemberSeconds> members);
  }
  ```

- [ ] **Step 1: Write the failing test**

```csharp
using M351.Infrastructure.Analytics;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// A identidade exata da decomposição do índice. O teste que importa é o da SOMA: se as
/// parcelas não fecharem a variação total, o cartão "por que o índice mudou" mente.
/// </summary>
public class IndexDecompositionTests
{
    private static MemberSeconds M(string k, long w1, long w0, long c1, long c0) =>
        new(k, k, w1, w0, c1, c0);

    [Fact]
    public void Parcelas_Somam_Exatamente_A_Variacao_Total()
    {
        // três apps: um piora (mais improdutivo), um melhora, um fica igual
        var membros = new[]
        {
            M("zap", 0, 0, 21_600, 3_600),      // +5 h improdutivas
            M("erp", 28_800, 25_200, 28_800, 25_200), // +1 h produtiva
            M("mail", 3_600, 3_600, 3_600, 3_600),    // estável
        };

        var parcelas = IndexDecomposition.Decompose(membros);
        var total = IndexDecomposition.DeltaPoints(membros);

        Assert.NotNull(parcelas);
        Assert.NotNull(total);
        Assert.Equal(total!.Value, parcelas!.Sum(p => p.Points), precision: 9);
    }

    [Fact]
    public void Sinal_Da_Parcela_Aponta_Quem_Puxou_Para_Baixo()
    {
        var membros = new[]
        {
            M("zap", 0, 0, 21_600, 3_600),
            M("erp", 28_800, 25_200, 28_800, 25_200),
        };

        var parcelas = IndexDecomposition.Decompose(membros)!;

        Assert.True(parcelas.Single(p => p.Key == "zap").Points < 0);
        Assert.True(parcelas.Single(p => p.Key == "erp").Points > 0);
    }

    [Fact]
    public void Sem_Denominador_Devolve_Null_E_Nunca_Zero()
    {
        // período anterior vazio: não há índice anterior, logo não há variação a explicar
        var membros = new[] { M("erp", 3_600, 0, 3_600, 0) };

        Assert.Null(IndexDecomposition.Decompose(membros));
        Assert.Null(IndexDecomposition.DeltaPoints(membros));
    }

    [Fact]
    public void Periodo_Sem_Mudanca_Nenhuma_Da_Zero_Pontos()
    {
        var membros = new[] { M("erp", 25_200, 25_200, 28_800, 28_800) };

        Assert.Equal(0d, IndexDecomposition.DeltaPoints(membros)!.Value, precision: 9);
        Assert.All(IndexDecomposition.Decompose(membros)!, p => Assert.Equal(0d, p.Points, precision: 9));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/dev/351-monitor/backend && dotnet test --filter FullyQualifiedName~IndexDecompositionTests`
Expected: FAIL — `IndexDecomposition` não existe (erro de compilação).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace M351.Infrastructure.Analytics;

/// <summary>
/// DECOMPOSIÇÃO EXATA DA VARIAÇÃO DO ÍNDICE (item 1 do estudo, "índice explicável").
///
/// O índice é P/C — produtivo sobre classificado (decisão 4 do spec). A variação entre dois
/// períodos admite uma identidade algébrica que reparte a mudança entre os membros SEM RESÍDUO:
///
///     P₁/C₁ − P₀/C₀ = (P₁C₀ − P₀C₁)/(C₁C₀) = (C₀·ΔP − P₀·ΔC)/(C₁C₀)
///                    = Σₖ (C₀·Δpₖ − P₀·Δcₖ)/(C₁C₀)
///
/// porque ΔP = Σₖ Δpₖ e ΔC = Σₖ Δcₖ. Cada membro k (um aplicativo, uma equipe, um dia) leva
/// exatamente a sua parcela, e a soma das parcelas É a variação total.
///
/// POR QUE ISSO IMPORTA: sem a identidade sobraria uma categoria "outros" para fechar a conta, e
/// o cartão que diz "menos 4 pontos porque subiu WhatsApp" estaria chutando a atribuição. Aqui a
/// frase é verificável — o teste da soma é o contrato.
///
/// Note que a parcela NÃO é "o índice deste app": um app improdutivo que cresceu empurra o
/// índice para baixo mesmo sem nenhum segundo produtivo, porque engorda C sem engordar P. É esse
/// o segundo termo, −P₀·Δcₖ.
/// </summary>
public static class IndexDecomposition
{
    /// <summary>
    /// Parcelas em PONTOS do índice (0–100), com sinal. null quando falta denominador em
    /// qualquer um dos dois períodos: sem índice anterior não existe variação a explicar, e
    /// devolver zero seria afirmar "não mudou nada" quando a verdade é "não dá para saber".
    /// </summary>
    public static IReadOnlyList<MemberContribution>? Decompose(IReadOnlyList<MemberSeconds> members)
    {
        if (!TryTotals(members, out var p0, out var c0, out var c1)) return null;

        var denominator = (double)c1 * c0;
        return members.Select(m => new MemberContribution(
            m.Key, m.Label,
            Points: (c0 * (double)(m.WorkCurrent - m.WorkPrevious)
                     - p0 * (double)(m.ClassifiedCurrent - m.ClassifiedPrevious)) / denominator * 100d,
            m.WorkCurrent, m.WorkPrevious, m.ClassifiedCurrent, m.ClassifiedPrevious)).ToList();
    }

    /// <summary>Variação total em pontos, calculada direto dos totais (não da soma das parcelas).</summary>
    public static double? DeltaPoints(IReadOnlyList<MemberSeconds> members)
    {
        if (!TryTotals(members, out var p0, out var c0, out var c1)) return null;

        var p1 = members.Sum(m => m.WorkCurrent);
        return ((double)p1 / c1 - (double)p0 / c0) * 100d;
    }

    /// <summary>Totais dos dois períodos; false quando qualquer classificado é zero.</summary>
    private static bool TryTotals(IReadOnlyList<MemberSeconds> members, out long p0, out long c0, out long c1)
    {
        p0 = members.Sum(m => m.WorkPrevious);
        c0 = members.Sum(m => m.ClassifiedPrevious);
        c1 = members.Sum(m => m.ClassifiedCurrent);
        return c0 > 0 && c1 > 0;
    }
}

/// <summary>Segundos de um membro (app, equipe ou dia) nos dois períodos.</summary>
public readonly record struct MemberSeconds(
    string Key, string Label,
    long WorkCurrent, long WorkPrevious,
    long ClassifiedCurrent, long ClassifiedPrevious);

/// <summary>Contribuição do membro para a variação do índice, em pontos, com sinal.</summary>
public readonly record struct MemberContribution(
    string Key, string Label, double Points,
    long WorkCurrent, long WorkPrevious,
    long ClassifiedCurrent, long ClassifiedPrevious);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /c/dev/351-monitor/backend && dotnet test --filter FullyQualifiedName~IndexDecompositionTests`
Expected: PASS, 4 testes.

- [ ] **Step 5: Commit**

```bash
git add backend/src/M351.Infrastructure/Analytics/IndexDecomposition.cs backend/tests/M351.IntegrationTests/IndexDecompositionTests.cs
git commit -m "feat(backend): decomposicao exata da variacao do indice"
```

---

### Task 2: Endpoint `GET /dashboard/index-explained`

**Files:**
- Create: `backend/src/M351.Api/Contracts/IndexExplainedContracts.cs`
- Create: `backend/src/M351.Api/Controllers/IndexExplainedController.cs`
- Test: `backend/tests/M351.IntegrationTests/IndexExplainedTests.cs`

**Interfaces:**
- Consumes: `IndexDecomposition.Decompose`, `IndexDecomposition.DeltaPoints`, `MemberSeconds` (Task 1); `ApiControllerBase.ValidateRange`, `NormalizeTeamTag`.
- Produces: `GET /api/v1/dashboard/index-explained?from&to[&tag][&limit]` → `IndexExplainedResponse`.

```csharp
public sealed record IndexExplainedResponse(
    OverviewPeriodResponse Period,
    OverviewPeriodResponse PreviousPeriod,
    double? Index,
    double? PreviousIndex,
    double? DeltaPoints,
    string? Unavailable,
    IReadOnlyList<IndexContributionResponse> ByApp,
    IReadOnlyList<IndexContributionResponse> ByTeam,
    IReadOnlyList<IndexContributionResponse> ByDay);

public sealed record IndexContributionResponse(
    string Key, string Label, double Points,
    long SecondsWorkRelated, long SecondsWorkRelatedPrevious,
    long SecondsClassified, long SecondsClassifiedPrevious);
```

Regras que o controller aplica:
- Período anterior = mesma duração, imediatamente antes (idêntico ao `compare` do `overview`), para as duas telas contarem a mesma história.
- `limit` default 8, máximo 25: as maiores contribuições por `|Points|`, com sinal preservado.
- `ByDay` pareia o dia *i* do período com o dia *i* do anterior; a `Key` é a data atual, o `Label` traz as duas.
- Sem auditoria: é agregado de organização/equipe, como o `overview`.
- `Unavailable` traz o motivo em pt-BR quando a decomposição é `null` (ex.: "Não há tempo classificado no período anterior.").

- [ ] **Step 1: Write the failing test**

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
/// GET /api/v1/dashboard/index-explained — "por que o índice mudou", decomposto por aplicativo,
/// equipe e dia. O contrato central é a SOMA: as parcelas fecham a variação total.
/// </summary>
[Collection(ApiCollection.Name)]
public class IndexExplainedTests(ApiTestFixture fixture)
{
    // (mesmos helpers de DashboardOverviewTests: Base, T, LocalDate, RunPipelineAsync,
    //  GetJsonAsync, MapAppAsync — copiar de lá, o repo já repete esses helpers por teste)

    [Fact]
    public async Task Parcelas_Por_App_Somam_A_Variacao_Total()
    {
        // Semana anterior: 15 min de erp (produtivo). Semana atual: 15 min de erp + 15 min de
        // zap (improdutivo) — o índice cai e a queda é inteiramente do zap.
        // ... monta org, device, eventos nos dois períodos, MapAppAsync(erp, 1), MapAppAsync(zap, -1)
        // ... RunPipelineAsync()

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={inicioAtual}&to={fimAtual}");
        var root = doc.RootElement;

        var total = root.GetProperty("delta_points").GetDouble();
        var soma = root.GetProperty("by_app").EnumerateArray().Sum(e => e.GetProperty("points").GetDouble());

        Assert.Equal(total, soma, precision: 6);
        Assert.True(total < 0, "o índice caiu no período");

        var zap = root.GetProperty("by_app").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString()!.Contains("zap"));
        Assert.True(zap.GetProperty("points").GetDouble() < 0);
    }

    [Fact]
    public async Task Sem_Periodo_Anterior_Devolve_Unavailable_E_Nao_Zero()
    {
        // org nova, dados só no período atual
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={inicio}&to={fim}");

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("delta_points").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("unavailable").GetString()));
        Assert.Empty(doc.RootElement.GetProperty("by_app").EnumerateArray());
    }

    [Fact]
    public async Task Janela_Maior_Que_92_Dias_Da_400()
    {
        using var _ = await GetJsonAsync(client, token,
            "/api/v1/dashboard/index-explained?from=2026-01-01&to=2026-12-31",
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Regra_Da_Equipe_Vence_A_Da_Organizacao_Na_Decomposicao()
    {
        // o mesmo app classificado como produtivo na organização e improdutivo na equipe da
        // lane: a parcela tem de refletir a regra da EQUIPE (precedência F5)
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/dev/351-monitor/backend && dotnet test --filter FullyQualifiedName~IndexExplainedTests`
Expected: FAIL — 404 na rota (controller não existe).

- [ ] **Step 3: Write the controller**

SQL por aplicativo (reaplica a precedência F5 sobre `daily_app_usage`; o período anterior e o
atual são contíguos, então uma varredura só resolve os dois com `FILTER`):

```sql
WITH lane_team AS (
    SELECT du.id AS device_user_id, tm.team_id
    FROM device_users du
    LEFT JOIN people p ON p.tenant_id = du.tenant_id AND p.windows_sid = du.windows_sid
    LEFT JOIN team_members tm ON tm.tenant_id = du.tenant_id
                             AND tm.windows_sid = COALESCE(p.merged_into_sid, du.windows_sid)
    WHERE du.tenant_id = @TenantId
),
uso AS (
    SELECT a.app_id,
           a.summary_date >= @From::date AS atual,
           a.seconds_active,
           c.classification
    FROM daily_app_usage a
    JOIN devices d ON d.id = a.device_id AND d.tenant_id = a.tenant_id
    LEFT JOIN lane_team lt ON lt.device_user_id = a.device_user_id
    LEFT JOIN tenant_app_team_categories tatc
           ON tatc.tenant_id = a.tenant_id AND tatc.app_id = a.app_id AND tatc.team_id = lt.team_id
    LEFT JOIN tenant_app_categories tac
           ON tac.tenant_id = a.tenant_id AND tac.app_id = a.app_id
    JOIN categories c ON c.tenant_id = a.tenant_id
                     AND c.id = COALESCE(tatc.category_id, tac.category_id)
    WHERE a.tenant_id = @TenantId
      AND d.status <> 'archived'
      AND (@Tag::text IS NULL OR @Tag = ANY(d.tags))
      AND a.summary_date BETWEEN @PrevFrom::date AND @To::date
)
SELECT ac.process_name AS key,
       COALESCE(ac.display_name, ac.process_name) AS label,
       COALESCE(sum(u.seconds_active) FILTER (WHERE u.atual AND u.classification = 1), 0)::bigint AS work_current,
       COALESCE(sum(u.seconds_active) FILTER (WHERE NOT u.atual AND u.classification = 1), 0)::bigint AS work_previous,
       COALESCE(sum(u.seconds_active) FILTER (WHERE u.atual), 0)::bigint AS classified_current,
       COALESCE(sum(u.seconds_active) FILTER (WHERE NOT u.atual), 0)::bigint AS classified_previous
FROM uso u
JOIN app_catalog ac ON ac.id = u.app_id
GROUP BY 1, 2
```

O `JOIN categories` (não `LEFT JOIN`) é deliberado: sem categoria o tempo é *sem classificação*, fica fora de `C` e portanto fora da decomposição — coerente com a fórmula da decisão 4.

Por equipe, o mesmo esqueleto sobre `daily_device_summaries`, com
`COALESCE(t.name, 'Sem equipe')` como rótulo (a lane-máquina cai em "Sem equipe": não é pessoa,
mas os segundos dela contam nos totais, então precisa aparecer para a soma fechar) e
`classified = seconds_work_related + seconds_neutral + seconds_not_work_related`.

Por dia, agrupar `daily_device_summaries` por `summary_date` nos dois intervalos e parear pelo
deslocamento ordinal (`dia i` ↔ `dia i` do anterior).

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /c/dev/351-monitor/backend && dotnet test --filter FullyQualifiedName~IndexExplainedTests`
Expected: PASS.

- [ ] **Step 5: Full suite + commit**

```bash
cd /c/dev/351-monitor/backend && dotnet build && dotnet test
git add backend/src/M351.Api/Contracts/IndexExplainedContracts.cs backend/src/M351.Api/Controllers/IndexExplainedController.cs backend/tests/M351.IntegrationTests/IndexExplainedTests.cs
git commit -m "feat(backend): endpoint do indice explicavel por app, equipe e dia"
```

---

### Task 3: Painel "Por que o índice mudou" no portal

**Files:**
- Create: `portal/src/components/dashboard/PorQueOIndiceMudou.tsx`
- Modify: `portal/src/lib/types.ts` (tipos `IndexExplainedResponse`, `IndexContributionRow`)
- Modify: `portal/src/pages/VisaoGeralPage.tsx` (monta o painel abaixo do Resumo do período)

**Interfaces:**
- Consumes: `GET /dashboard/index-explained` (Task 2); `formatDuration`, `formatPct` de `@/lib/format` e `@/lib/period`; `BRAND` de `@/lib/brandTheme`.

Comportamento:
- Três abas — **Aplicativo / Equipe / Dia** — cada uma com barras divergentes ordenadas por `|points|`, negativas à esquerda em `BRAND.vizImprodutivo` e positivas à direita em `BRAND.vizProdutivo`.
- Cada linha lê como frase: *"WhatsApp — 4,1 pontos abaixo · +6 h 12 min"*.
- Rodapé fixo com a soma: *"as parcelas somam a variação total de −4,1 pontos"*. É a promessa do cartão, visível.
- `unavailable` preenchido → o painel mostra só essa frase. Zero nunca substitui "não sei" (mesma regra do `ResumoDoPeriodo`).
- Sem julgamento: descreve variação e onde ela está, jamais quem é bom ou ruim (seção 8 do spec).

- [ ] **Step 1: Escrever o componente e ligar na Visão Geral**
- [ ] **Step 2: Verificar** — `cd /c/dev/351-monitor/portal && npx tsc --noEmit -p tsconfig.json && npm run build`
- [ ] **Step 3: Commit** — `git commit -m "feat(portal): painel por que o indice mudou"`

---

### Task 4: `monthly_summaries` e o job de rollup

**Files:**
- Create: migration `MensaisF9` (via `dotnet ef migrations add`)
- Create: `backend/src/M351.Infrastructure/Aggregation/MonthlyRollupService.cs`
- Create: `backend/src/M351.Worker/MonthlyRollupJob.cs`
- Modify: `backend/src/M351.Worker/Program.cs` (registro Quartz, de hora em hora)
- Modify: `backend/src/M351.Infrastructure/Maintenance/RetentionPurgeService.cs`
- Test: `backend/tests/M351.IntegrationTests/MonthlyRollupTests.cs`

DDL (corpo da migration em SQL cru — não são entidades do EF):

```sql
CREATE TABLE monthly_summaries (
  tenant_id uuid NOT NULL,
  month_start date NOT NULL,           -- primeiro dia do mês, no fuso do tenant já resolvido
  device_id uuid NOT NULL,
  device_user_id uuid NOT NULL,
  seconds_active int NOT NULL DEFAULT 0, seconds_idle int NOT NULL DEFAULT 0,
  seconds_locked int NOT NULL DEFAULT 0, seconds_on int NOT NULL DEFAULT 0,
  seconds_work_related int NOT NULL DEFAULT 0, seconds_neutral int NOT NULL DEFAULT 0,
  seconds_not_work_related int NOT NULL DEFAULT 0, seconds_unclassified int NOT NULL DEFAULT 0,
  days_with_data int NOT NULL DEFAULT 0,
  data_incomplete boolean NOT NULL DEFAULT false,
  computed_at timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, month_start, device_id, device_user_id)
);

-- marca-d'água por tenant: o rollup só reprocessa os meses tocados desde a última passada,
-- o que faz a reagregação retroativa (decisão 7) se propagar sozinha para o mensal
CREATE TABLE monthly_rollup_state (
  tenant_id uuid PRIMARY KEY REFERENCES organizations(id),
  watermark timestamptz NOT NULL DEFAULT '-infinity',
  updated_at timestamptz NOT NULL DEFAULT now()
);

-- o rollup varre daily por (tenant, computed_at): sem este índice a varredura é sequencial
CREATE INDEX ix_dds_tenant_computed ON daily_device_summaries (tenant_id, computed_at);
```

`int` nos segundos, como nas tabelas irmãs: o teto por (mês, device, lane) é 31 × 86 400 = 2 678 400, longe dos 2,1 bilhões do `int`.

Serviço, por tenant e em transação por mês:
1. `meses := SELECT DISTINCT date_trunc('month', summary_date)::date FROM daily_device_summaries WHERE tenant_id = @t AND computed_at > watermark`
2. `novaMarca := SELECT max(computed_at) ...` (lido **antes** de recomputar, para não perder escrita concorrente)
3. Para cada mês: `DELETE` do mês + `INSERT ... SELECT` somando a diária, com `days_with_data = count(*) FILTER (WHERE seconds_on > 0)`.
4. `UPSERT monthly_rollup_state`.

Testes que importam:
- `Rollup_Soma_A_Diaria_Do_Mes` — totais do mensal batem com a soma da diária.
- `Rollup_E_Idempotente` — rodar duas vezes não duplica nem muda nada.
- `Reagregacao_Retroativa_Reflete_No_Mensal` — mexer numa diária antiga e rodar de novo atualiza o mês.
- `Purga_Remove_Mensal_Alem_De_24_Meses` — `monthly_summaries` some no mesmo corte da diária.

- [ ] **Step 1:** escrever os quatro testes
- [ ] **Step 2:** rodar e ver falhar (tabela inexistente)
- [ ] **Step 3:** gerar a migration pelo EF, escrever `MonthlyRollupService`, o job e a purga
- [ ] **Step 4:** `dotnet build && dotnet test` — verde
- [ ] **Step 5:** `git commit -m "feat(backend): agregados mensais com rollup incremental e retencao de 24 meses"`

---

### Task 5: Grão mensal nos endpoints históricos

**Files:**
- Modify: `backend/src/M351.Api/Controllers/DashboardController.cs`
- Modify: `backend/src/M351.Api/Contracts/DashboardContracts.cs`
- Test: `backend/tests/M351.IntegrationTests/DashboardMonthGrainTests.cs`

`GET /dashboard/overview?grain=month` lê de `monthly_summaries` em vez de `daily_device_summaries`
e valida contra um teto novo, `MaxMonthGrainMonths = 24`, em vez dos 92 dias. O contrato **não
muda**: a série volta no mesmo `days[]`, com `date` = primeiro dia do mês. O portal só troca o
rótulo — é o que torna trimestre e ano possíveis sem um segundo contrato para manter em sincronia.

- `grain` ausente ou `day` → comportamento atual, bit a bit.
- `grain=month` com janela > 24 meses → 400 ProblemDetails.
- `grain` desconhecido → 400.
- Índice e cobertura seguem calculados no servidor, pela mesma fórmula.

- [ ] **Step 1:** testes (`grain=month` soma igual ao `grain=day` no mesmo intervalo; 24 meses passa; 25 dá 400; `grain` inválido dá 400)
- [ ] **Step 2:** rodar e ver falhar
- [ ] **Step 3:** implementar
- [ ] **Step 4:** `dotnet build && dotnet test`
- [ ] **Step 5:** `git commit -m "feat(backend): grao mensal no overview com teto de 24 meses"`

---

### Task 6: Períodos longos no portal

**Files:**
- Modify: `portal/src/lib/period.ts` (presets `trimestre`, `ano`, `12meses` com `grain: "month"`)
- Modify: `portal/src/pages/VisaoGeralPage.tsx` (passa `grain` ao overview; eixo rotulado por mês)

O seletor de período ganha **Últimos 3 meses**, **Este ano** e **Últimos 12 meses**. Ao escolher
um deles o grão vira mensal e a legenda do gráfico diz "por mês", para ninguém ler barra de mês
como barra de dia.

- [ ] **Step 1:** implementar
- [ ] **Step 2:** `npx tsc --noEmit -p tsconfig.json && npm run build`
- [ ] **Step 3:** `git commit -m "feat(portal): periodos de trimestre e ano com grao mensal"`

---

### Task 7: Visão do colaborador na página da pessoa

Fecha o item 2 **sem criar acesso nenhum** (decisão 10). De quebra remove a duplicação da fórmula
no cliente, que era pendência conhecida do `RETOMAR-AQUI`.

**Files:**
- Create: `backend/src/M351.Api/Contracts/PersonSelfViewContracts.cs`
- Modify: `backend/src/M351.Api/Controllers/PeopleController.cs`
- Create: `portal/src/components/pessoa/VisaoDoColaborador.tsx`
- Modify: `portal/src/pages/PessoaPage.tsx`
- Modify: `backend/src/M351.Infrastructure/Exports/ExportService.cs`
- Test: `backend/tests/M351.IntegrationTests/PersonSelfViewTests.cs`

**Interfaces:**
- Produces: `GET /api/v1/people/{sid}/self-view?from&to` → `PersonSelfViewResponse`

```csharp
public sealed record PersonSelfViewResponse(
    string WindowsSid, string? DisplayName, string? TeamName,
    OverviewPeriodResponse Period,
    long SecondsOn, long SecondsActive, long SecondsIdle, long SecondsLocked,
    long SecondsWorkRelated, long SecondsNeutral, long SecondsNotWorkRelated, long SecondsUnclassified,
    double? ProductivityIndex, double? ClassificationCoverage,
    int DaysWithData,
    IReadOnlyList<PersonSelfViewAppRow> TopApps,
    int OpenNotes, int OpenDisputes);

public sealed record PersonSelfViewAppRow(
    string ProcessName, string DisplayName, int? Classification, long SecondsActive);
```

Regras:
- `{sid}` aceita as duas formas do `PersonNotesController` (windows_sid **ou** uuid de `device_users.id`) e devolve o SID canônico — a mesma resolução, para nunca mostrar a pessoa errada.
- `[AuditRead]` → `view_report`: é leitura de dado pessoal identificado (decisão 2).
- Índice e cobertura **no servidor**, pela fórmula única. A `PessoaPage` passa a consumir isto e o cálculo no cliente sai — o comentário que pedia a remoção some junto.
- Nenhuma rota anônima, nenhum token: quem abre é Viewer+ autenticado.

No `ExportService`, `resumo_pdf` aceita `params.windows_sid` e vira a variante **pessoal**: o mesmo
resumo, restrito àquela pessoa, para o gestor imprimir e entregar no 1:1. `device_ids` e `tag`
continuam recusados nesse kind.

No portal, `VisaoDoColaborador.tsx` é um cartão com moldura própria e o título **"O que mostramos
a esta pessoa"**, com os quatro baldes, índice com cobertura ao lado (nunca um sem o outro), as
horas, os aplicativos e o contador de anotações e contestações abertas, mais um botão que dispara
o `resumo_pdf` pessoal. Uma linha no rodapé diz de onde vêm os números e que classificação é
definida pela empresa — o mesmo enquadramento da decisão 1.

- [ ] **Step 1:** testes (`self-view` devolve índice e cobertura do servidor; aceita SID e device_user_id; grava `view_report`; 404 para pessoa de outro tenant; `resumo_pdf` com `windows_sid` gera o PDF pessoal)
- [ ] **Step 2:** rodar e ver falhar
- [ ] **Step 3:** implementar backend, depois portal
- [ ] **Step 4:** `dotnet build && dotnet test` e `npx tsc --noEmit && npm run build`
- [ ] **Step 5:** `git commit -m "feat: visao do colaborador na pagina da pessoa e resumo pessoal em PDF"`

---

### Task 8: Fechamento

- [ ] Atualizar `docs/superpowers/plans/2026-09-07-RETOMAR-AQUI.md`: os três itens saem da seção "COMECE POR AQUI"; entram o que ficou aberto (recorte por equipe para líder; `management_alerts` fora do dossiê de Conformidade; feriados na capacidade) e a decisão 10.
- [ ] Suíte cheia verde nos dois lados.
- [ ] Deploy manual de staging:
  ```bash
  cd /c/dev/351-monitor
  export SSH_KEY_FILE="$HOME/.ssh/ci_deploy_351monitor" STAGING_SSH_USER=deploy
  bash infra/scripts/deploy-staging.sh
  ```
  Sucesso = `[deploy] health-gate ok`, `[deploy] remote-ok`, `[deploy] concluído`.
- [ ] Commit final e push.
