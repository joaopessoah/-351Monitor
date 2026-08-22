// =============================================================================
// Card "Primeiros passos" da Visão Geral (funil de ativação). Quatro passos
// verificados AUTOMATICAMENTE a partir das APIs existentes, sem tabela nova:
//   1. agente instalado      -> GET /devices tem device com last_seen_at
//   2. horário de trabalho   -> organization.business_hours != null (GET /me)
//   3. primeiro app com categoria -> GET /categories tem categoria (fora a
//      seedada "Não categorizado") com app_count > 0
//   4. colega convidado      -> GET /users tem >= 2 usuários não desativados
// Reusa as MESMAS queryKeys das telas (cache compartilhado): ["devices",
// {page_size:100}] do useFilterDevices, ["categories"] e ["users"]. Visível
// para admin/owner enquanto onboarding_checklist_dismissed_at é null; com 4/4
// completos vira o estado concluído com o botão "Concluir" (POST dismiss).
// O "Dispensar" discreto faz o mesmo a qualquer momento; o checklist reabre
// em Configurações -> Organização (DELETE na mesma rota).
// =============================================================================

import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, CheckCircle2, Circle } from "lucide-react";
import { api } from "@/lib/api";
import { UNCATEGORIZED_LABEL } from "@/lib/classification";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  CategoriesResponse,
  DeviceItem,
  MeResponse,
  PagedResponse,
  UsersResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

interface Step {
  label: string;
  done: boolean;
  to: string;
  cta: string;
}

export function OnboardingChecklist() {
  const queryClient = useQueryClient();

  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const me = meQuery.data;
  const show =
    me !== undefined && isAdmin(me) && me.organization.onboarding_checklist_dismissed_at === null;

  // Mesma queryKey do useFilterDevices - resolve do cache quando já carregado.
  const devicesQuery = useQuery({
    queryKey: ["devices", { page_size: 100 }],
    queryFn: () => api<PagedResponse<DeviceItem>>("/devices?page_size=100"),
    staleTime: 60_000,
    enabled: show,
  });
  const categoriesQuery = useQuery({
    queryKey: ["categories"],
    queryFn: () => api<CategoriesResponse>("/categories"),
    staleTime: 60_000,
    enabled: show,
  });
  const usersQuery = useQuery({
    queryKey: ["users"],
    queryFn: () => api<UsersResponse>("/users"),
    staleTime: 5 * 60 * 1000,
    enabled: show,
  });

  const dismiss = useMutation({
    mutationFn: () => api<void>("/organization/onboarding-checklist/dismiss", { method: "POST" }),
    onSuccess: async () => {
      // onboarding_checklist_dismissed_at vive no GET /me: o card some sozinho.
      await queryClient.invalidateQueries({ queryKey: ["me"] });
    },
  });

  if (me === undefined || !isAdmin(me) || me.organization.onboarding_checklist_dismissed_at !== null) {
    return null;
  }

  // Skeleton com a geometria final enquanto as verificações carregam (evita o
  // flash de passos "pendentes" que viram concluídos logo em seguida).
  if (devicesQuery.isPending || categoriesQuery.isPending || usersQuery.isPending) {
    return <Skeleton className="h-[220px] rounded-lg" />;
  }

  const steps: Step[] = [
    {
      label: "Instale o agente na primeira máquina",
      done: (devicesQuery.data?.items ?? []).some((d) => d.last_seen_at !== null),
      to: "/configuracoes/chaves",
      cta: "Criar chave",
    },
    {
      label: "Defina o horário de trabalho da organização",
      done: me.organization.business_hours !== null,
      to: "/configuracoes/organizacao",
      cta: "Definir horário",
    },
    {
      label: "Categorize os primeiros aplicativos",
      // app_count de /categories = apps do tenant mapeados; a seedada
      // "Não categorizado" é o balde sem categoria e fica fora da conta.
      done: (categoriesQuery.data?.items ?? []).some(
        (c) => c.name !== UNCATEGORIZED_LABEL && c.app_count > 0,
      ),
      to: "/apps",
      cta: "Categorizar apps",
    },
    {
      label: "Convide um colega para o portal",
      done: (usersQuery.data?.items ?? []).filter((u) => u.status !== "disabled").length >= 2,
      to: "/configuracoes/usuarios",
      cta: "Convidar",
    },
  ];

  const doneCount = steps.filter((s) => s.done).length;
  const allDone = doneCount === steps.length;

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Primeiros passos</CardTitle>
            <CardDescription className="tabular-nums">
              {allDone
                ? "Tudo pronto: os quatro passos da ativação foram concluídos."
                : `${doneCount} de ${steps.length} passos concluídos.`}
            </CardDescription>
          </div>
          {allDone ? (
            <Button
              size="sm"
              className="h-9"
              onClick={() => dismiss.mutate()}
              disabled={dismiss.isPending}
            >
              {dismiss.isPending ? "Concluindo…" : "Concluir"}
            </Button>
          ) : (
            <Button
              variant="ghost"
              size="sm"
              className="h-9 text-muted-foreground"
              onClick={() => dismiss.mutate()}
              disabled={dismiss.isPending}
            >
              {dismiss.isPending ? "Dispensando…" : "Dispensar"}
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-2">
        <ul className="space-y-1">
          {steps.map((step) => (
            <li key={step.to} className="flex flex-wrap items-center gap-2 py-1">
              {step.done ? (
                <CheckCircle2 className="h-4 w-4 shrink-0 text-viz-produtivo" aria-hidden />
              ) : (
                <Circle className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
              )}
              <span className={cn("text-sm", step.done && "text-muted-foreground")}>
                <span className="sr-only">{step.done ? "Concluído: " : "Pendente: "}</span>
                {step.label}
              </span>
              {!step.done && (
                <Link
                  to={step.to}
                  className="ml-auto inline-flex items-center gap-1 text-sm font-medium text-primary underline-offset-4 hover:underline"
                >
                  {step.cta}
                  <ArrowRight className="h-3.5 w-3.5" aria-hidden />
                </Link>
              )}
            </li>
          ))}
        </ul>
        {dismiss.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(dismiss.error)}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
