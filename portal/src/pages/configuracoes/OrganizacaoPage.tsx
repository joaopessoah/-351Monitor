// =============================================================================
// Configurações -> Organização (F4.8). Edição dos campos de transparência da
// organização: finalidade declarada, contato do encarregado (DPO) e data de
// vigência da política. Popula com GET /organization (qualquer papel
// autenticado lê) e grava com PATCH /organization (Owner/Admin); o Viewer vê
// os valores em modo somente leitura. Inclui o link "Ver página pública" para
// a /transparencia/{slug}, que reflete exatamente esses campos.
//
// Também edita o HORÁRIO DE TRABALHO da org (business_hours, Seção 8.5):
// checkboxes de seg a dom + início/fim, com sugestão de seg a sex 08:00 às
// 18:00 quando ainda não definido - a janela alimenta a linha de referência
// dos gráficos e do relatório de jornada. E, quando o checklist de primeiros
// passos foi dispensado, o link discreto para reabri-lo (DELETE dismiss).
//
// A mesma tela hospeda as METAS SEMANAIS da equipe (MetasCard, PATCH
// /organization) e as PREFERÊNCIAS DE E-MAIL da pessoa logada
// (PreferenciasEmailCard, GET/PATCH /me/email-prefs), que se protegem sozinhas
// pelo papel e cuidam do próprio carregamento.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import { ExternalLink, Info } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { parseHmToMinutes } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  BusinessHours,
  MeResponse,
  OrganizationPatchRequest,
  OrganizationResponse,
} from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { MetasCard } from "@/components/dashboard/MetasCard";
import { PreferenciasEmailCard } from "@/components/PreferenciasEmailCard";
import { cn } from "@/lib/utils";

// Espelha o limite real do backend (OrganizationController.MaxTextLength = 1000),
// aplicado a finalidade_declarada e contato_dpo. O gate do cliente PRECISA bater
// com o servidor; senão 1001..1000+ chars passam aqui e tomam 400 genérico no PATCH.
const FINALIDADE_MAX = 1000;
const DPO_MAX = 500;

export function OrganizacaoPage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const canEdit = isAdmin(meQuery.data);

  const orgQuery = useQuery({
    queryKey: ["organization"],
    queryFn: () => api<OrganizationResponse>("/organization"),
    staleTime: 60_000,
  });

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold tracking-tight">Transparência da organização</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Estes campos aparecem na página pública de transparência da sua organização. Mantenha-os
          atualizados para que clientes e titulares saibam a finalidade do monitoramento e como
          contatar o encarregado de dados.
        </p>
      </div>

      {orgQuery.isPending || meQuery.isPending ? (
        <Card>
          <CardContent className="space-y-4 pt-6">
            <Skeleton className="h-5 w-64" />
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-10 w-full max-w-md" />
            <Skeleton className="h-10 w-48" />
          </CardContent>
        </Card>
      ) : orgQuery.isError ? (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <p className="text-sm text-muted-foreground">{genericErrorMessage(orgQuery.error)}</p>
            <Button variant="outline" onClick={() => void orgQuery.refetch()}>
              Tentar novamente
            </Button>
          </CardContent>
        </Card>
      ) : orgQuery.data !== undefined ? (
        <>
          <OrgForm org={orgQuery.data} canEdit={canEdit} />
          <BusinessHoursCard org={orgQuery.data} canEdit={canEdit} />
          <ReopenChecklistLink me={meQuery.data} />
        </>
      ) : null}

      {/* Metas SEMANAIS da equipe (agregadas, nunca por pessoa): alimentam a
          barra de progresso da Visão Geral. Cuida do próprio carregamento. */}
      <MetasCard />

      {/* Preferências de e-mail da PESSOA logada: ficam aqui porque é a tela de
          configurações que todo papel enxerga, inclusive o Viewer. */}
      <PreferenciasEmailCard />
    </div>
  );
}

function OrgForm({ org, canEdit }: { org: OrganizationResponse; canEdit: boolean }) {
  const queryClient = useQueryClient();

  const [finalidade, setFinalidade] = useState(org.finalidade_declarada ?? "");
  const [dpo, setDpo] = useState(org.contato_dpo ?? "");
  const [vigencia, setVigencia] = useState(org.data_vigencia ?? "");
  const [saved, setSaved] = useState(false);

  // Re-sincroniza os campos quando o cache da org muda (ex.: após salvar).
  useEffect(() => {
    setFinalidade(org.finalidade_declarada ?? "");
    setDpo(org.contato_dpo ?? "");
    setVigencia(org.data_vigencia ?? "");
  }, [org.finalidade_declarada, org.contato_dpo, org.data_vigencia]);

  const mutation = useMutation({
    mutationFn: (body: OrganizationPatchRequest) =>
      api<OrganizationResponse>("/organization", { method: "PATCH", body }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["organization"], updated);
      setSaved(true);
    },
  });

  // String vazia -> null (limpa o campo). Trim para evitar gravar só espaços.
  const draft = useMemo<OrganizationPatchRequest>(() => {
    const norm = (s: string): string | null => {
      const t = s.trim();
      return t.length === 0 ? null : t;
    };
    return {
      finalidade_declarada: norm(finalidade),
      contato_dpo: norm(dpo),
      data_vigencia: vigencia.length === 0 ? null : vigencia,
    };
  }, [finalidade, dpo, vigencia]);

  const dirty =
    draft.finalidade_declarada !== (org.finalidade_declarada ?? null) ||
    draft.contato_dpo !== (org.contato_dpo ?? null) ||
    draft.data_vigencia !== (org.data_vigencia ?? null);

  const tooLong = finalidade.trim().length > FINALIDADE_MAX || dpo.trim().length > DPO_MAX;
  const canSubmit = canEdit && dirty && !tooLong && !mutation.isPending;

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    setSaved(false);
    mutation.mutate(draft);
  }

  const publicHref = `/transparencia/${encodeURIComponent(org.slug)}`;

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <CardTitle className="text-base">{org.name}</CardTitle>
            <CardDescription>
              Endereço público: <span className="font-mono text-foreground">/transparencia/{org.slug}</span>
            </CardDescription>
          </div>
          <Button
            variant="outline"
            size="sm"
            className="shrink-0"
            onClick={() => window.open(publicHref, "_blank", "noopener")}
          >
            <ExternalLink className="h-4 w-4" aria-hidden="true" />
            Ver página pública
          </Button>
        </div>
      </CardHeader>

      <CardContent>
        {!canEdit && (
          <div className="mb-4 flex gap-3 rounded-md border border-viz-neutro/30 bg-viz-neutro/10 px-4 py-3 text-sm text-viz-neutro">
            <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
            <p>
              Você está vendo estes campos em modo somente leitura. A edição fica disponível para
              Administradores e Proprietários da organização.
            </p>
          </div>
        )}

        <form className="space-y-5" onSubmit={handleSubmit}>
          <div className="space-y-1.5">
            <Label htmlFor="org-finalidade">Finalidade declarada</Label>
            <textarea
              id="org-finalidade"
              value={finalidade}
              onChange={(e) => {
                setFinalidade(e.target.value);
                setSaved(false);
              }}
              disabled={!canEdit}
              rows={4}
              maxLength={FINALIDADE_MAX + 100}
              placeholder="Ex.: monitoramento de uso das estações de trabalho corporativas para gestão de produtividade e segurança da informação."
              className={cn(
                "flex w-full rounded-md border border-input bg-card px-3 py-2 text-sm",
                "placeholder:text-muted-foreground",
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
                "disabled:cursor-not-allowed disabled:opacity-60",
              )}
            />
            <p className="text-xs text-muted-foreground">
              Aparece na página pública como a finalidade do tratamento. {finalidade.trim().length}/
              {FINALIDADE_MAX}
            </p>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="org-dpo">Contato do encarregado (DPO)</Label>
            <Input
              id="org-dpo"
              value={dpo}
              onChange={(e) => {
                setDpo(e.target.value);
                setSaved(false);
              }}
              disabled={!canEdit}
              maxLength={DPO_MAX + 50}
              placeholder="Ex.: dpo@empresa.com.br ou +351 210 000 000"
            />
            <p className="text-xs text-muted-foreground">
              E-mail ou telefone do encarregado de proteção de dados, exibido publicamente para
              contato de titulares.
            </p>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="org-vigencia">Data de vigência da política</Label>
            <Input
              id="org-vigencia"
              type="date"
              value={vigencia}
              onChange={(e) => {
                setVigencia(e.target.value);
                setSaved(false);
              }}
              disabled={!canEdit}
              className="max-w-xs"
            />
            <p className="text-xs text-muted-foreground">
              Data a partir da qual esta política de coleta está em vigor.
            </p>
          </div>

          {tooLong && (
            <p role="alert" className="text-sm text-destructive">
              Um dos campos excedeu o tamanho máximo permitido. Reduza o texto antes de salvar.
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
                {mutation.isPending ? "Salvando..." : "Salvar alterações"}
              </Button>
              {saved && !dirty && (
                <span className="text-sm text-viz-produtivo">Alterações salvas.</span>
              )}
            </div>
          )}
        </form>
      </CardContent>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Horário de trabalho (business_hours) - PATCH /organization
// -----------------------------------------------------------------------------

const WEEKDAYS: { value: number; label: string }[] = [
  { value: 1, label: "seg" },
  { value: 2, label: "ter" },
  { value: 3, label: "qua" },
  { value: 4, label: "qui" },
  { value: 5, label: "sex" },
  { value: 6, label: "sáb" },
  { value: 7, label: "dom" },
];

/** Sugestão exibida enquanto a org não definiu (seg a sex, das 08:00 às 18:00). */
const DEFAULT_BUSINESS_HOURS: BusinessHours = { days: [1, 2, 3, 4, 5], start: "08:00", end: "18:00" };

function BusinessHoursCard({ org, canEdit }: { org: OrganizationResponse; canEdit: boolean }) {
  const queryClient = useQueryClient();
  const configured = org.business_hours;

  const [days, setDays] = useState<number[]>(configured?.days ?? DEFAULT_BUSINESS_HOURS.days);
  const [start, setStart] = useState(configured?.start ?? DEFAULT_BUSINESS_HOURS.start);
  const [end, setEnd] = useState(configured?.end ?? DEFAULT_BUSINESS_HOURS.end);
  const [saved, setSaved] = useState(false);

  // Re-sincroniza quando o VALOR salvo muda (deps por valor, não por identidade
  // do objeto: um refetch sem mudança não pode descartar uma edição em curso).
  const confDays = configured !== null ? configured.days.join(",") : null;
  const confStart = configured?.start ?? null;
  const confEnd = configured?.end ?? null;
  useEffect(() => {
    setDays(
      confDays !== null
        ? confDays.length > 0
          ? confDays.split(",").map(Number)
          : []
        : DEFAULT_BUSINESS_HOURS.days,
    );
    setStart(confStart ?? DEFAULT_BUSINESS_HOURS.start);
    setEnd(confEnd ?? DEFAULT_BUSINESS_HOURS.end);
  }, [confDays, confStart, confEnd]);

  const mutation = useMutation({
    mutationFn: (businessHours: BusinessHours | null) => {
      const body: OrganizationPatchRequest = { business_hours: businessHours };
      return api<OrganizationResponse>("/organization", { method: "PATCH", body });
    },
    onSuccess: async (updated) => {
      queryClient.setQueryData(["organization"], updated);
      // business_hours também sai em GET /me (gráficos, jornada e checklist).
      await queryClient.invalidateQueries({ queryKey: ["me"] });
      setSaved(true);
    },
  });

  const startMin = parseHmToMinutes(start);
  const endMin = parseHmToMinutes(end);
  const valid = days.length > 0 && startMin !== null && endMin !== null && startMin < endMin;

  const draft: BusinessHours = { days: [...days].sort((a, b) => a - b), start, end };
  const dirty =
    configured === null ||
    draft.days.join(",") !== configured.days.join(",") ||
    draft.start !== configured.start ||
    draft.end !== configured.end;

  function toggleDay(value: number): void {
    setDays((prev) => (prev.includes(value) ? prev.filter((d) => d !== value) : [...prev, value]));
    setSaved(false);
  }

  function handleSubmit(e: React.FormEvent): void {
    e.preventDefault();
    if (!canEdit || !valid || !dirty || mutation.isPending) return;
    mutation.mutate(draft);
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Horário de trabalho</CardTitle>
        <CardDescription>
          A janela de horário de trabalho alimenta a linha de referência dos gráficos da Visão
          Geral e do relatório de jornada.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {!canEdit && (
          <p className="mb-4 text-sm text-muted-foreground">
            Somente Administradores e Proprietários editam o horário de trabalho.
          </p>
        )}
        <form className="space-y-4" onSubmit={handleSubmit}>
          <fieldset className="space-y-1.5">
            <legend className="text-sm font-medium leading-none">Dias da semana</legend>
            <div className="flex flex-wrap gap-x-4 gap-y-2 pt-2">
              {WEEKDAYS.map((day) => (
                <label
                  key={day.value}
                  className={cn(
                    "flex items-center gap-1.5 text-sm",
                    canEdit ? "cursor-pointer" : "cursor-not-allowed opacity-60",
                  )}
                >
                  <input
                    type="checkbox"
                    checked={days.includes(day.value)}
                    disabled={!canEdit}
                    onChange={() => toggleDay(day.value)}
                    className="h-4 w-4 accent-primary disabled:cursor-not-allowed"
                  />
                  {day.label}
                </label>
              ))}
            </div>
          </fieldset>

          <div className="flex flex-wrap items-end gap-4">
            <div className="space-y-1.5">
              <Label htmlFor="bh-start">Início</Label>
              <Input
                id="bh-start"
                type="time"
                value={start}
                disabled={!canEdit}
                onChange={(e) => {
                  setStart(e.target.value);
                  setSaved(false);
                }}
                className="w-32"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="bh-end">Fim</Label>
              <Input
                id="bh-end"
                type="time"
                value={end}
                disabled={!canEdit}
                onChange={(e) => {
                  setEnd(e.target.value);
                  setSaved(false);
                }}
                className="w-32"
              />
            </div>
          </div>

          {configured === null && (
            <p className="text-xs text-muted-foreground">
              Nenhum horário definido ainda. Sugestão: segunda a sexta, das 08:00 às 18:00.
            </p>
          )}
          {!valid && (
            <p role="alert" className="text-sm text-destructive">
              Selecione ao menos um dia e um intervalo com início antes do fim.
            </p>
          )}
          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {genericErrorMessage(mutation.error)}
            </p>
          )}

          {canEdit && (
            <div className="flex flex-wrap items-center gap-3">
              <Button type="submit" disabled={!valid || !dirty || mutation.isPending}>
                {mutation.isPending ? "Salvando..." : "Salvar horário"}
              </Button>
              {configured !== null && (
                <Button
                  type="button"
                  variant="outline"
                  disabled={mutation.isPending}
                  onClick={() => {
                    setSaved(false);
                    mutation.mutate(null);
                  }}
                >
                  Limpar horário
                </Button>
              )}
              {saved && !dirty && <span className="text-sm text-viz-produtivo">Horário salvo.</span>}
            </div>
          )}
        </form>
      </CardContent>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Reabrir o checklist de primeiros passos - DELETE
// /organization/onboarding-checklist/dismiss (o card volta à Visão Geral)
// -----------------------------------------------------------------------------

function ReopenChecklistLink({ me }: { me: MeResponse | undefined }) {
  const queryClient = useQueryClient();
  const mutation = useMutation({
    mutationFn: () => api<void>("/organization/onboarding-checklist/dismiss", { method: "DELETE" }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["me"] });
    },
  });

  // Só faz sentido quando o checklist FOI dispensado; a rota é AdminPlus.
  if (
    me === undefined ||
    !isAdmin(me) ||
    me.organization.onboarding_checklist_dismissed_at === null
  ) {
    return null;
  }

  return (
    <div className="space-y-1">
      <button
        type="button"
        onClick={() => mutation.mutate()}
        disabled={mutation.isPending}
        className={cn(
          "text-sm text-muted-foreground underline underline-offset-4 transition-colors",
          "hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          "disabled:cursor-not-allowed disabled:opacity-50",
        )}
      >
        {mutation.isPending
          ? "Reabrindo o checklist..."
          : "Reabrir o checklist de primeiros passos"}
      </button>
      {mutation.isError && (
        <p role="alert" className="text-sm text-destructive">
          {genericErrorMessage(mutation.error)}
        </p>
      )}
    </div>
  );
}
