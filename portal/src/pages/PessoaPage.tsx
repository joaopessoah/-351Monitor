// =============================================================================
// Visão individual da PESSOA (/pessoas/:id) - o titular por trás de um usuário
// do Windows num dispositivo. Fontes:
//  - GET /device-users/{id}: identidade (usuário do Windows, apelido editável,
//    primeiro e último evento) e o dispositivo do registro;
//  - GET /device-users?q={windows_username}: outros REGISTROS com o mesmo
//    usuário do Windows. O modelo é POR DISPOSITIVO - esta página NÃO atravessa
//    dispositivos: cada máquina é um registro separado, e a tela diz isso em vez
//    de fingir uma visão unificada;
//  - GET /dashboard/summary?device_user_id=...: tempos e composição por
//    classificação no período, incluindo o balde seconds_unclassified (F6).
//
// REFORMA F6 (decisões 1, 2, 4 e 6 do spec de 07/09/2026), acrescentada acima
// do que já existia, sem remover nada:
//  - PERÍODO GLOBAL: o preset local de 7/14/30 dias desta tela deu lugar ao
//    período global (lib/period.ts, PERIOD_CODEC na URL) - o mesmo Hoje/Esta
//    semana/Este mês/Personalizado das outras telas de análise. O recorte
//    continua alimentando a MESMA query GET /dashboard/summary de sempre;
//  - KPIs da pessoa no período (ligada, ativa, índice, ociosidade, sem
//    classificação) em components/pessoa/PersonKpis.tsx. O ÍNDICE e a
//    COBERTURA vêm PRONTOS de GET /people/{sid}/self-view (F9), pela fórmula
//    única da decisão 4 - a conta que esta tela refazia no cliente foi apagada.
//    `null` imprime "–", nunca 0%;
//  - VISÃO DO COLABORADOR em components/pessoa/VisaoDoColaborador.tsx: o que
//    mostramos à própria pessoa, e o botão do resumo pessoal em PDF. O
//    colaborador NÃO tem acesso ao painel (decisão 10): os canais dele são o
//    papel que o gestor imprime e o digest pessoal por e-mail;
//  - COMPOSIÇÃO POR DIA em components/pessoa/PersonDailyComposition.tsx:
//    colunas empilhadas (ECharts) com os quatro baldes de classificação mais
//    o Ocioso, linha tracejada da jornada declarada (business_hours do /me,
//    fallback 8h) e toggle "Ver dados" com tabela equivalente;
//  - BLOCO DE IDENTIDADE (Admin+) em components/pessoa/PersonIdentityCard.tsx:
//    apelido e mesclagem por pessoa são feitos por PATCH /people/{sid}
//    (decisão 6), mas dependem de um windows_sid que este registro NÃO tem -
//    ver o comentário daquele arquivo. Por ora o bloco só explica a pendência.
//
// VOCABULÁRIO: a classificação passa a usar Produtivo / Neutro / Improdutivo /
// Sem classificação (decisão 1), sempre com o enquadramento "classificação
// definida pela sua empresa" (CLASSIFICATION_FRAMING, mostrado uma vez pelo
// PersonKpis). Os ESTADOS DE MÁQUINA seguem neutros (Ativo, Ocioso,
// Bloqueado) e ocioso NUNCA é somado como improdutivo. O módulo
// lib/classification.ts ainda carrega o conjunto neutro anterior (opção da
// organização) e é migrado na fase de Classificação 2.0.
//
// SEM SID: GET /device-users/{id} não devolve windows_sid - conferido no
// contrato (backend/.../DeviceUserContracts.cs: DeviceUserResponse só tem
// Id/DeviceId/DeviceName/WindowsUsername/DisplayName/FirstSeenAt/LastSeenAt) -
// e é o SID que identifica a pessoa em /people. Por isso o bloco de
// identidade fica bloqueado (ver componente) e esta tela NÃO tenta descobrir
// o SID por outro caminho (ex.: cruzar por nome, como a tela de
// Colaboradores faz na direção contrária, pessoa -> registro): seria uma
// mesclagem/renomeação arriscada demais para adivinhar. A unificação das duas
// identidades (registro por dispositivo e pessoa) entra na fase seguinte.
//
// USO POR APLICATIVO, que esta página explicava como ausência, EXISTE desde a
// F9: GET /people/{sid}/self-view recorta por TITULAR (as lanes que resolvem
// para o SID canônico), e não por dispositivo. Era esse o impedimento — o
// /reports/usage só aceita device_ids, e passar o device de um registro
// mostraria o uso de TODAS as pessoas daquela máquina rotulado como se fosse
// desta pessoa. O cartão de ausência deu lugar à visão do colaborador, que traz
// os aplicativos dela e mais nada de ninguém.
//
// Vocabulário NEUTRO: nada de ranking de pessoas, "Primeiro/Último evento" -
// jamais "Entrada/Saída".
// =============================================================================

import { useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Check, MonitorSmartphone, Pencil, Scale, UserRound, X } from "lucide-react";
import { api, ApiError } from "@/lib/api";
import { classificationColor, classificationLabel, classificationVocabularyOf } from "@/lib/classification";
import { formatDateTime, formatDuration } from "@/lib/format";
import { genericErrorMessage, JORNADA_DISCLAIMER } from "@/lib/messages";
import { PERIOD_CODEC, PERIOD_LABELS, resolvePeriod, type PeriodPreset } from "@/lib/period";
import { isAdmin } from "@/lib/roles";
import { deviceUserLabel } from "@/lib/types";
import type {
  DashboardSummaryResponse,
  DeviceUserItem,
  DeviceUserPatchRequest,
  MeResponse,
  PagedResponse,
} from "@/lib/types";
import { useUrlState } from "@/lib/useUrlState";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { journeyHoursOf } from "@/components/pessoa/pessoaMetrics";
import { PersonDailyComposition } from "@/components/pessoa/PersonDailyComposition";
import { PersonIdentityCard } from "@/components/pessoa/PersonIdentityCard";
import { PersonKpis } from "@/components/pessoa/PersonKpis";
import { PersonNotes } from "@/components/pessoa/PersonNotes";
import { VisaoDoColaborador, usePersonSelfView } from "@/components/pessoa/VisaoDoColaborador";

const MAX_DISPLAY_NAME = 200;

export function PessoaPage() {
  const { id = "" } = useParams<{ id: string }>();
  // Período GLOBAL (F6): troca o preset local de 7/14/30 dias pelo mesmo
  // Hoje/Esta semana/Este mês/Personalizado das outras telas de análise,
  // vivendo na URL (?periodo=&de=&ate=).
  const [period, setPeriod] = useUrlState(PERIOD_CODEC);

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const canEdit = isAdmin(meQuery.data);
  // Jornada declarada da organização - referência da composição por dia;
  // sem configuração, cai no fallback de 8h do spec.
  const journeyHours = journeyHoursOf(meQuery.data?.organization.business_hours ?? null);

  const personQuery = useQuery({
    queryKey: ["device-users", id],
    queryFn: () => api<DeviceUserItem>(`/device-users/${encodeURIComponent(id)}`),
    enabled: id.length > 0,
  });
  const person = personQuery.data;

  // Período no fuso do TENANT (mesma âncora dos relatórios e da Visão Geral).
  const resolved = useMemo(() => resolvePeriod(period, timezone), [period, timezone]);

  const summaryQuery = useQuery({
    queryKey: ["dashboard", "summary", { device_user_id: id, from: resolved?.from, to: resolved?.to }],
    queryFn: () =>
      api<DashboardSummaryResponse>(
        `/dashboard/summary?from=${resolved?.from ?? ""}&to=${resolved?.to ?? ""}&device_user_id=${encodeURIComponent(id)}`,
      ),
    enabled: id.length > 0 && resolved !== null && person !== undefined,
  });

  // Visão do colaborador (F9): os mesmos números que a pessoa vê sobre si, com
  // índice e cobertura CALCULADOS NO SERVIDOR. Alimenta também os KPIs acima,
  // que por isso não refazem mais a fórmula.
  const selfView = usePersonSelfView(id, resolved);

  function setPreset(preset: PeriodPreset): void {
    if (preset === "custom" && resolved !== null) {
      // "Personalizado" começa do intervalo que está na tela.
      setPeriod({ preset, from: resolved.from, to: resolved.to });
      return;
    }
    setPeriod({ preset, from: null, to: null });
  }

  // Outros registros do MESMO usuário do Windows (outras máquinas). Busca exata
  // pelo windows_username; o próprio registro sai da lista.
  const siblingsQuery = useQuery({
    queryKey: ["device-users", { q: person?.windows_username }],
    queryFn: () =>
      api<PagedResponse<DeviceUserItem>>(
        `/device-users?q=${encodeURIComponent(person?.windows_username ?? "")}&page_size=100`,
      ),
    enabled: person !== undefined,
    staleTime: 60_000,
  });
  const siblings = useMemo(
    () =>
      (siblingsQuery.data?.items ?? []).filter(
        (i) => i.id !== person?.id && i.windows_username === person?.windows_username,
      ),
    [siblingsQuery.data, person],
  );

  if (personQuery.isError) {
    const notFound = personQuery.error instanceof ApiError && personQuery.error.status === 404;
    return (
      <div className="space-y-4">
        <Card>
          <div className="flex flex-col items-center gap-3 px-6 py-16 text-center">
            <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
            <p className="text-base font-medium">
              {notFound ? "Pessoa não encontrada" : "Não foi possível carregar a pessoa"}
            </p>
            <p className="max-w-md text-sm text-muted-foreground">
              {notFound
                ? "O registro pode ter sido removido por um pedido de exclusão do titular (LGPD)."
                : genericErrorMessage(personQuery.error)}
            </p>
            {notFound ? (
              <Link
                to="/relatorios/uso?group_by=device_user"
                className="text-sm font-medium text-primary underline underline-offset-2"
              >
                Ver pessoas nos relatórios
              </Link>
            ) : (
              <Button variant="outline" onClick={() => void personQuery.refetch()}>
                Tentar novamente
              </Button>
            )}
          </div>
        </Card>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <PersonHeader person={person} canEdit={canEdit} />

      {/* Período GLOBAL da tela (F6): controla os KPIs, o resumo e a
          composição por dia logo abaixo - uma única fonte de recorte. */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          {resolved !== null ? `Período: ${resolved.label}.` : "Carregando o período…"}
        </p>
        <div className="flex flex-wrap items-center gap-2">
          <div
            role="group"
            aria-label="Período"
            className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
          >
            {(["dia", "semana", "mes", "custom"] as const).map((preset) => (
              <button
                key={preset}
                type="button"
                aria-pressed={period.preset === preset}
                disabled={timezone === null}
                onClick={() => setPreset(preset)}
                className={cn(
                  "rounded-[5px] px-3 text-xs font-medium transition-colors disabled:opacity-40",
                  period.preset === preset
                    ? "bg-primary/10 text-primary"
                    : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
                )}
              >
                {PERIOD_LABELS[preset]}
              </button>
            ))}
          </div>
          {period.preset === "custom" && resolved !== null && (
            <span className="inline-flex items-center gap-1.5">
              <Input
                type="date"
                aria-label="De"
                value={resolved.from}
                max={resolved.to}
                onChange={(e) => setPeriod({ preset: "custom", from: e.target.value, to: resolved.to })}
                className="h-9 w-[9.5rem]"
              />
              <span className="text-xs text-muted-foreground">a</span>
              <Input
                type="date"
                aria-label="Até"
                value={resolved.to}
                min={resolved.from}
                onChange={(e) => setPeriod({ preset: "custom", from: resolved.from, to: e.target.value })}
                className="h-9 w-[9.5rem]"
              />
            </span>
          )}
        </div>
      </div>

      {/* KPIs da pessoa no período (F6, decisão 4): ligada, ativa, índice,
          ociosidade e sem classificação - mesma query do resumo abaixo. */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Indicadores do período</CardTitle>
        </CardHeader>
        <CardContent>
          {summaryQuery.isError ? (
            <SummaryQueryError error={summaryQuery.error} onRetry={() => void summaryQuery.refetch()} />
          ) : summaryQuery.data === undefined ? (
            <div className="space-y-3">
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
                {Array.from({ length: 5 }).map((_, i) => (
                  <Skeleton key={i} className="h-20 w-full" />
                ))}
              </div>
              <Skeleton className="h-3 w-2/3" />
              <Skeleton className="h-3 w-1/2" />
            </div>
          ) : (
            <PersonKpis
              totals={summaryQuery.data.totals}
              productivityIndex={selfView.data?.productivity_index}
              classificationCoverage={selfView.data?.classification_coverage}
            />
          )}
        </CardContent>
      </Card>

      {/* Dispositivo do registro + os outros registros da mesma conta do Windows. */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Onde esta pessoa aparece</CardTitle>
          <CardDescription>
            Cada registro é o par entre uma conta do Windows e um dispositivo. A mesma pessoa em
            outra máquina tem um registro separado, com histórico próprio.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {person === undefined ? (
            <Skeleton className="h-16 w-full" />
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                    <th scope="col" className="py-2 pr-3">Dispositivo</th>
                    <th scope="col" className="px-3 py-2">Primeiro evento</th>
                    <th scope="col" className="px-3 py-2">Último evento</th>
                    <th scope="col" className="px-3 py-2">Registro</th>
                  </tr>
                </thead>
                <tbody>
                  <DeviceRow item={person} timezone={timezone} current />
                  {siblings.map((item) => (
                    <DeviceRow key={item.id} item={item} timezone={timezone} current={false} />
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Bloco de identidade (Admin+, F6) - ver comentário do componente
          sobre a pendência do SID. */}
      {canEdit && <PersonIdentityCard />}

      {/* Resumo do período por classificação. */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Resumo do período</CardTitle>
          <CardDescription>
            Tempos deste registro (esta conta do Windows neste dispositivo)
            {resolved !== null ? ` na ${resolved.label}` : ""}.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {summaryQuery.isError ? (
            <div className="flex flex-col items-center gap-3 py-8 text-center">
              <AlertTriangle className="h-6 w-6 text-destructive" aria-hidden />
              <p className="text-sm text-muted-foreground">{genericErrorMessage(summaryQuery.error)}</p>
              <Button variant="outline" size="sm" onClick={() => void summaryQuery.refetch()}>
                Tentar novamente
              </Button>
            </div>
          ) : summaryQuery.data === undefined ? (
            <Skeleton className="h-40 w-full" />
          ) : (
            <SummaryTables data={summaryQuery.data} />
          )}
        </CardContent>
      </Card>

      {/* Composição por dia (F6): os quatro baldes de classificação mais o
          Ocioso, com a jornada declarada como referência. */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Composição por dia</CardTitle>
          <CardDescription>
            Horas por dia neste registro, com a jornada declarada da organização como referência.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {summaryQuery.isError ? (
            <SummaryQueryError error={summaryQuery.error} onRetry={() => void summaryQuery.refetch()} />
          ) : summaryQuery.data === undefined ? (
            <Skeleton className="h-[260px] w-full" />
          ) : (
            <PersonDailyComposition days={summaryQuery.data.days} journeyHours={journeyHours} />
          )}
        </CardContent>
      </Card>

      {/* Anotação de período e contestação de classificação (F6, decisão 6):
          o contexto que o número não tem, com a revisão de quem administra.
          Recebe o id da ROTA (device_user_id) - a rota de anotações aceita o
          device_user_id ou o windows_sid e resolve a pessoa no servidor, pelo
          par (tenant, id), o que destrava aqui o que o bloco de identidade
          acima ainda não consegue fazer. Aceitar uma contestação NÃO altera
          nenhum agregado: a frase que a tela mostra depois da revisão vem do
          próprio servidor. */}
      <PersonNotes
        personKey={id}
        from={resolved?.from ?? null}
        to={resolved?.to ?? null}
        timezone={timezone}
        canReview={canEdit}
      />

      {/* VISÃO DO COLABORADOR (F9, decisão 10) — no lugar do cartão que
          explicava a ausência do uso por aplicativo. O recorte por TITULAR que
          faltava existe agora, e vem no mesmo bloco que responde "o que esta
          pessoa lê quando pergunta sobre si".

          O colaborador NÃO abre esta tela: ele não é usuário do sistema. O que
          chega a ele é o PDF que o botão daqui gera e o digest pessoal por
          e-mail — os dois canais da decisão 10. */}
      <VisaoDoColaborador
        query={selfView}
        period={resolved}
        vocabulary={classificationVocabularyOf(meQuery.data)}
      />

      {/* Disclaimer FIXO da Portaria 671/MTE (DoD 11.3) - verbatim, sem botão de fechar. */}
      <div
        role="note"
        className="flex items-start gap-2 rounded-md border bg-muted/50 px-4 py-3 text-xs text-muted-foreground"
      >
        <Scale className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <span>{JORNADA_DISCLAIMER}</span>
      </div>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Erro com retry compartilhado pelos KPIs e pela composição por dia (F6) -
// os dois cartões novos consomem a MESMA summaryQuery. O card "Resumo do
// período" mantém o bloco inline de antes (não mexido, ver comentário do
// topo sobre preservar comportamento).
// -----------------------------------------------------------------------------

function SummaryQueryError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  return (
    <div className="flex flex-col items-center gap-3 py-8 text-center">
      <AlertTriangle className="h-6 w-6 text-destructive" aria-hidden />
      <p className="text-sm text-muted-foreground">{genericErrorMessage(error)}</p>
      <Button variant="outline" size="sm" onClick={onRetry}>
        Tentar novamente
      </Button>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Cabeçalho com o nome editável (Admin/Owner)
// -----------------------------------------------------------------------------

function PersonHeader({ person, canEdit }: { person: DeviceUserItem | undefined; canEdit: boolean }) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  const mutation = useMutation({
    mutationFn: (body: DeviceUserPatchRequest) =>
      api<DeviceUserItem>(`/device-users/${encodeURIComponent(person?.id ?? "")}`, {
        method: "PATCH",
        body,
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["device-users", updated.id], updated);
      // O nome novo aparece nas lanes/relatórios e na busca de titulares do DSR.
      void queryClient.invalidateQueries({ queryKey: ["device-users"] });
      void queryClient.invalidateQueries({ queryKey: ["reports", "usage"] });
      void queryClient.invalidateQueries({ queryKey: ["dsr"] });
      setEditing(false);
    },
  });

  if (person === undefined) {
    return (
      <div className="space-y-2">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-4 w-48" />
      </div>
    );
  }

  const trimmed = draft.trim();
  const canSave = trimmed.length <= MAX_DISPLAY_NAME && !mutation.isPending;

  function startEditing() {
    setDraft(person!.display_name ?? "");
    setEditing(true);
  }

  function save() {
    if (!canSave) return;
    // Vazio limpa o apelido: a tela volta a exibir o usuário do Windows.
    mutation.mutate({ display_name: trimmed.length > 0 ? trimmed : null });
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-3">
        <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-muted">
          <UserRound className="h-5 w-5 text-muted-foreground" aria-hidden />
        </span>
        {editing ? (
          <form
            className="flex flex-wrap items-center gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              save();
            }}
          >
            <Input
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              maxLength={MAX_DISPLAY_NAME}
              placeholder={person.windows_username}
              aria-label="Nome da pessoa"
              autoFocus
              className="h-9 w-64"
            />
            <Button type="submit" size="sm" disabled={!canSave}>
              <Check className="h-4 w-4" aria-hidden />
              {mutation.isPending ? "Salvando..." : "Salvar"}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={mutation.isPending}
              onClick={() => setEditing(false)}
            >
              <X className="h-4 w-4" aria-hidden />
              Cancelar
            </Button>
          </form>
        ) : (
          <>
            <h1 className="text-2xl font-semibold tracking-tight">{deviceUserLabel(person)}</h1>
            {canEdit && (
              <Button variant="outline" size="sm" onClick={startEditing}>
                <Pencil className="h-4 w-4" aria-hidden />
                Editar nome
              </Button>
            )}
          </>
        )}
      </div>

      <p className="text-sm text-muted-foreground">
        Conta do Windows <span className="font-medium text-foreground">{person.windows_username}</span> no
        dispositivo <span className="font-medium text-foreground">{person.device_name}</span>.
      </p>

      {mutation.isError && (
        <p role="alert" className="text-sm text-destructive">
          {genericErrorMessage(mutation.error)}
        </p>
      )}
      {person.display_name === null && !editing && (
        <p className="text-xs text-muted-foreground">
          Sem nome definido: as telas exibem a conta do Windows.
        </p>
      )}
    </div>
  );
}

// -----------------------------------------------------------------------------
// Tabelas do resumo
// -----------------------------------------------------------------------------

function DeviceRow({
  item,
  timezone,
  current,
}: {
  item: DeviceUserItem;
  timezone: string | null;
  current: boolean;
}) {
  return (
    <tr className="border-b last:border-b-0">
      <td className="py-2 pr-3">
        <span className="flex items-center gap-2">
          <MonitorSmartphone className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
          <span className="max-w-[16rem] truncate font-medium">{item.device_name}</span>
        </span>
      </td>
      <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">
        {timezone !== null ? formatDateTime(item.first_seen_at, timezone) : "-"}
      </td>
      <td className="whitespace-nowrap px-3 py-2 tabular-nums text-muted-foreground">
        {timezone !== null ? formatDateTime(item.last_seen_at, timezone) : "-"}
      </td>
      <td className="whitespace-nowrap px-3 py-2">
        {current ? (
          <span className="text-xs font-medium text-primary">Este registro</span>
        ) : (
          <Link
            to={`/pessoas/${item.id}`}
            className="text-xs font-medium text-primary underline underline-offset-2"
          >
            Abrir
          </Link>
        )}
      </td>
    </tr>
  );
}

const STATE_ROWS = [
  { key: "seconds_active", label: "Tempo ativo" },
  { key: "seconds_idle", label: "Tempo ocioso" },
  { key: "seconds_locked", label: "Tempo bloqueado" },
  { key: "seconds_on", label: "Tempo ligada" },
] as const;

/** Classificação com a MESMA nomenclatura fixa da Seção 8.7 (classificationLabel). */
const CLASSIFICATION_ROWS = [
  { key: "seconds_work_related", classification: 1 },
  { key: "seconds_neutral", classification: 0 },
  { key: "seconds_not_work_related", classification: -1 },
] as const;

function SummaryTables({ data }: { data: DashboardSummaryResponse }) {
  const totals = data.totals;
  const classificationTotal =
    totals.seconds_work_related + totals.seconds_neutral + totals.seconds_not_work_related;
  const noData = totals.seconds_on === 0 && classificationTotal === 0;

  if (noData) {
    return (
      <p className="py-6 text-center text-sm text-muted-foreground">
        Nenhum dado coletado para este registro no período.
      </p>
    );
  }

  return (
    <div className="grid gap-6 sm:grid-cols-2">
      <div>
        <h3 className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Tempos
        </h3>
        <dl className="divide-y rounded-md border">
          {STATE_ROWS.map((row) => (
            <div key={row.key} className="flex items-center justify-between px-4 py-2 text-sm">
              <dt className="text-muted-foreground">{row.label}</dt>
              <dd className="font-medium tabular-nums">{formatDuration(totals[row.key])}</dd>
            </div>
          ))}
        </dl>
        {totals.data_incomplete && (
          <p className="mt-2 text-xs text-muted-foreground">
            Há dias com dados incompletos no período: os totais podem estar subestimados.
          </p>
        )}
      </div>

      <div>
        <h3 className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Composição por classificação
        </h3>
        <dl className="divide-y rounded-md border">
          {CLASSIFICATION_ROWS.map((row) => {
            const seconds = totals[row.key];
            const pct =
              classificationTotal > 0 ? Math.round((seconds / classificationTotal) * 100) : 0;
            return (
              <div key={row.key} className="flex items-center justify-between gap-3 px-4 py-2 text-sm">
                <dt className="flex min-w-0 items-center gap-2 text-muted-foreground">
                  <span
                    aria-hidden
                    className="h-2.5 w-2.5 shrink-0 rounded-full"
                    style={{ backgroundColor: classificationColor(row.classification) }}
                  />
                  <span className="truncate">{classificationLabel(row.classification)}</span>
                </dt>
                <dd className="whitespace-nowrap font-medium tabular-nums">
                  {formatDuration(seconds)}
                  <span className="ml-2 text-xs font-normal text-muted-foreground">{pct}%</span>
                </dd>
              </div>
            );
          })}
        </dl>
        <p className="mt-2 text-xs text-muted-foreground">
          Percentuais sobre o tempo ativo classificado do período.
        </p>
      </div>
    </div>
  );
}
