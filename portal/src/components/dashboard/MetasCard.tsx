// Renderizado em Configurações > Organização.
// =============================================================================
// Edição das metas SEMANAIS da organização (PATCH /organization): horas ativas
// da equipe e percentual do tempo em aplicativos relacionados ao trabalho. O
// progresso contra essas metas aparece na Visão Geral.
//
// VOCABULÁRIO: metas são sempre da EQUIPE, no agregado. O produto não tem meta
// individual e não faz ranking de pessoas — e o estado de máquina jamais é
// chamado de produtivo/improdutivo (a classificação vive na camada de
// categorias do cliente: relacionado ao trabalho, neutro, não relacionado).
//
// Gate: Admin+ (espelho do PolicyAdminPlus do PATCH). Viewer vê os valores em
// modo somente leitura, no mesmo padrão de Configurações > Organização.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import type { FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Info } from "lucide-react";
import { api } from "@/lib/api";
import { addDays, localDateOf, mondayOf } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  DashboardSummaryResponse,
  MeResponse,
  OrganizationPatchRequest,
  OrganizationResponse,
} from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";

// Limites do backend (PATCH /organization): 1 a 10000 horas, 1 a 100 por cento.
// O gate do cliente PRECISA bater com o servidor, senão o valor fora da faixa
// passa aqui e toma um 400 genérico no PATCH.
const HOURS_MIN = 1;
const HOURS_MAX = 10_000;
const PCT_MIN = 1;
const PCT_MAX = 100;

/** Quantas semanas fechadas entram na média sugerida. */
const SEMANAS_NA_MEDIA = 4;

/** "" -> null (remove a meta); número inteiro fora da faixa -> undefined (inválido). */
function parseMeta(value: string, min: number, max: number): number | null | undefined {
  const trimmed = value.trim();
  if (trimmed.length === 0) return null;
  if (!/^\d+$/.test(trimmed)) return undefined;
  const n = Number(trimmed);
  return n >= min && n <= max ? n : undefined;
}

export function MetasCard() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const canEdit = isAdmin(meQuery.data);
  const timezone = meQuery.data?.organization.timezone ?? null;

  const orgQuery = useQuery({
    queryKey: ["organization"],
    queryFn: () => api<OrganizationResponse>("/organization"),
    staleTime: 60_000,
  });

  // Média das 4 semanas FECHADAS anteriores à semana corrente (a semana em
  // curso ainda está incompleta e puxaria a sugestão para baixo). Mesma
  // convenção de queryKey do resto do painel: ["dashboard","summary",from,to].
  const currentMonday = timezone !== null ? mondayOf(localDateOf(new Date(), timezone)) : null;
  const mediaFrom = currentMonday !== null ? addDays(currentMonday, -7 * SEMANAS_NA_MEDIA) : null;
  const mediaTo = currentMonday !== null ? addDays(currentMonday, -1) : null;

  const mediaQuery = useQuery({
    queryKey: ["dashboard", "summary", mediaFrom, mediaTo],
    queryFn: () =>
      api<DashboardSummaryResponse>(
        `/dashboard/summary?from=${mediaFrom ?? ""}&to=${mediaTo ?? ""}`,
      ),
    enabled: mediaFrom !== null,
    placeholderData: (prev) => prev,
  });

  const mediaHoras = useMemo(() => {
    const totals = mediaQuery.data?.totals;
    if (totals === undefined) return null;
    return Math.round(totals.seconds_active / SEMANAS_NA_MEDIA / 3600);
  }, [mediaQuery.data]);

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Metas semanais da equipe</CardTitle>
        <CardDescription>
          As metas valem para o agregado da equipe e alimentam a barra de progresso da Visão Geral.
          Não existe meta por pessoa: o produto não compara nem classifica indivíduos.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {orgQuery.isPending || meQuery.isPending ? (
          <div className="space-y-4">
            <Skeleton className="h-10 w-full max-w-xs" />
            <Skeleton className="h-10 w-full max-w-xs" />
            <Skeleton className="h-10 w-40" />
          </div>
        ) : orgQuery.isError ? (
          <div className="flex flex-col items-center gap-3 py-8 text-center">
            <p className="text-sm text-muted-foreground">{genericErrorMessage(orgQuery.error)}</p>
            <Button variant="outline" onClick={() => void orgQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : orgQuery.data !== undefined ? (
          <MetasForm org={orgQuery.data} canEdit={canEdit} mediaHoras={mediaHoras} />
        ) : null}
      </CardContent>
    </Card>
  );
}

function MetasForm({
  org,
  canEdit,
  mediaHoras,
}: {
  org: OrganizationResponse;
  canEdit: boolean;
  mediaHoras: number | null;
}) {
  const queryClient = useQueryClient();

  const [horas, setHoras] = useState(org.goal_weekly_active_hours?.toString() ?? "");
  const [pct, setPct] = useState(org.goal_work_related_pct?.toString() ?? "");
  const [saved, setSaved] = useState(false);

  // Re-sincroniza os campos quando o cache da org muda (ex.: após salvar).
  useEffect(() => {
    setHoras(org.goal_weekly_active_hours?.toString() ?? "");
    setPct(org.goal_work_related_pct?.toString() ?? "");
  }, [org.goal_weekly_active_hours, org.goal_work_related_pct]);

  const mutation = useMutation({
    mutationFn: (body: OrganizationPatchRequest) =>
      api<OrganizationResponse>("/organization", { method: "PATCH", body }),
    onSuccess: async (updated) => {
      queryClient.setQueryData(["organization"], updated);
      // GET /me também carrega as metas (é a fonte da barra da Visão Geral).
      await queryClient.invalidateQueries({ queryKey: ["me"] });
      setSaved(true);
    },
  });

  const parsedHoras = parseMeta(horas, HOURS_MIN, HOURS_MAX);
  const parsedPct = parseMeta(pct, PCT_MIN, PCT_MAX);
  const invalid = parsedHoras === undefined || parsedPct === undefined;

  const dirty =
    (parsedHoras ?? null) !== (org.goal_weekly_active_hours ?? null) ||
    (parsedPct ?? null) !== (org.goal_work_related_pct ?? null);

  const canSubmit = canEdit && dirty && !invalid && !mutation.isPending;

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (!canSubmit || parsedHoras === undefined || parsedPct === undefined) return;
    setSaved(false);
    // null remove a meta; campo inalterado é enviado igual (PATCH idempotente).
    mutation.mutate({
      goal_weekly_active_hours: parsedHoras,
      goal_work_related_pct: parsedPct,
    });
  }

  return (
    <form className="space-y-5" onSubmit={handleSubmit}>
      {!canEdit && (
        <div className="flex gap-3 rounded-md border border-viz-neutro/30 bg-viz-neutro/10 px-4 py-3 text-sm text-viz-neutro">
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <p>
            Você está vendo as metas em modo somente leitura. A edição fica disponível para
            Administradores e Proprietários da organização.
          </p>
        </div>
      )}

      <div className="space-y-1.5">
        <Label htmlFor="meta-horas">Horas ativas da equipe por semana</Label>
        <Input
          id="meta-horas"
          value={horas}
          onChange={(e) => {
            setHoras(e.target.value);
            setSaved(false);
          }}
          disabled={!canEdit}
          inputMode="numeric"
          autoComplete="off"
          placeholder="Ex.: 160"
          className="max-w-xs tabular-nums"
        />
        <p className="text-xs text-muted-foreground">
          Soma das horas ativas de todos os dispositivos na semana. Deixe em branco para não ter
          meta.
          {mediaHoras !== null && (
            <>
              {" "}
              Média das últimas {SEMANAS_NA_MEDIA} semanas:{" "}
              <span className="font-medium tabular-nums text-foreground">{mediaHoras} h</span>.
            </>
          )}
        </p>
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="meta-pct">Tempo em aplicativos relacionados ao trabalho</Label>
        <div className="flex items-center gap-2">
          <Input
            id="meta-pct"
            value={pct}
            onChange={(e) => {
              setPct(e.target.value);
              setSaved(false);
            }}
            disabled={!canEdit}
            inputMode="numeric"
            autoComplete="off"
            placeholder="Ex.: 70"
            className="max-w-[7rem] tabular-nums"
          />
          <span className="text-sm text-muted-foreground">% do tempo ativo</span>
        </div>
        <p className="text-xs text-muted-foreground">
          Percentual do tempo ativo da equipe nas categorias marcadas como relacionadas ao
          trabalho. Deixe em branco para não ter meta.
        </p>
      </div>

      {invalid && (
        <p role="alert" className="text-sm text-destructive">
          Informe as metas em números inteiros: de {HOURS_MIN} a {HOURS_MAX} horas por semana e de{" "}
          {PCT_MIN} a {PCT_MAX} por cento.
        </p>
      )}
      {mutation.isError && (
        <p role="alert" className="text-sm text-destructive">
          {genericErrorMessage(mutation.error)}
        </p>
      )}

      {canEdit && (
        <div className="flex items-center gap-3">
          <Button type="submit" disabled={!canSubmit}>
            {mutation.isPending ? "Salvando..." : "Salvar metas"}
          </Button>
          {saved && !dirty && <span className="text-sm text-viz-produtivo">Metas salvas.</span>}
        </div>
      )}
    </form>
  );
}
