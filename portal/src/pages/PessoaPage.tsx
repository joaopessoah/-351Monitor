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
//    classificação no período (presets de 7/14/30 dias).
//
// USO POR APLICATIVO NÃO ENTRA nesta versão: o GET /reports/usage só aceita
// filtro por device_ids (parâmetro real do ReportsController) - não há filtro por
// titular. Passar o device do registro mostraria o uso de TODAS as pessoas
// daquela máquina rotulado como se fosse desta pessoa, o que seria falso num
// dispositivo compartilhado. Enquanto o endpoint não aceitar um recorte por
// titular, a página mostra só o resumo e explica a ausência.
//
// Primeira versão TABULAR de propósito (sem canvas). Vocabulário NEUTRO: nada de
// produtivo/improdutivo, nada de ranking de pessoas, "Primeiro/Último evento" -
// jamais "Entrada/Saída".
// =============================================================================

import { useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Check, MonitorSmartphone, Pencil, Scale, UserRound, X } from "lucide-react";
import { api, ApiError } from "@/lib/api";
import { classificationColor, classificationLabel } from "@/lib/classification";
import { addDays, ddmm, formatDateTime, formatDuration, localDateOf } from "@/lib/format";
import { genericErrorMessage, JORNADA_DISCLAIMER } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import { deviceUserLabel } from "@/lib/types";
import type {
  DashboardSummaryResponse,
  DeviceUserItem,
  DeviceUserPatchRequest,
  MeResponse,
  PagedResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";

/** Presets do período desta tela (a régua de 92 dias dos relatórios não é necessária aqui). */
const PERIOD_PRESETS = [7, 14, 30] as const;

const MAX_DISPLAY_NAME = 200;

export function PessoaPage() {
  const { id = "" } = useParams<{ id: string }>();
  const [periodDays, setPeriodDays] = useState<number>(7);

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;
  const canEdit = isAdmin(meQuery.data);

  const personQuery = useQuery({
    queryKey: ["device-users", id],
    queryFn: () => api<DeviceUserItem>(`/device-users/${encodeURIComponent(id)}`),
    enabled: id.length > 0,
  });
  const person = personQuery.data;

  // Período no fuso do TENANT (mesma âncora dos relatórios).
  const range = useMemo(() => {
    if (timezone === null) return null;
    const today = localDateOf(new Date(), timezone);
    return { from: addDays(today, -(periodDays - 1)), to: today };
  }, [timezone, periodDays]);

  const summaryQuery = useQuery({
    queryKey: ["dashboard", "summary", { device_user_id: id, from: range?.from, to: range?.to }],
    queryFn: () =>
      api<DashboardSummaryResponse>(
        `/dashboard/summary?from=${range?.from ?? ""}&to=${range?.to ?? ""}&device_user_id=${encodeURIComponent(id)}`,
      ),
    enabled: id.length > 0 && range !== null && person !== undefined,
  });

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

      {/* Resumo do período por classificação. */}
      <Card>
        <CardHeader className="gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Resumo do período</CardTitle>
            <CardDescription>
              Tempos deste registro (esta conta do Windows neste dispositivo)
              {range !== null ? ` de ${ddmm(range.from)} a ${ddmm(range.to)}` : ""}.
            </CardDescription>
          </div>
          <div
            role="group"
            aria-label="Período"
            className="inline-flex h-9 shrink-0 items-stretch rounded-md border border-input bg-card p-0.5"
          >
            {PERIOD_PRESETS.map((days) => (
              <button
                key={days}
                type="button"
                aria-pressed={periodDays === days}
                onClick={() => setPeriodDays(days)}
                className={cn(
                  "rounded-[5px] px-3 text-xs font-medium transition-colors",
                  periodDays === days
                    ? "bg-primary/10 text-primary"
                    : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
                )}
              >
                {days} dias
              </button>
            ))}
          </div>
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

      {/* Ausência EXPLÍCITA do uso por aplicativo (ver comentário do topo). */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Uso por aplicativo</CardTitle>
          <CardDescription>
            Ainda não disponível por pessoa. O relatório de uso recorta por dispositivo, e num
            dispositivo compartilhado isso somaria o uso de todas as pessoas da máquina, o que
            atribuiria a esta pessoa um tempo que não é dela. Enquanto o recorte por pessoa não
            existir, consulte o uso por aplicativo do dispositivo em{" "}
            <Link
              to="/relatorios/uso?group_by=app"
              className="font-medium text-primary underline underline-offset-2"
            >
              Relatórios de uso
            </Link>
            .
          </CardDescription>
        </CardHeader>
      </Card>

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
