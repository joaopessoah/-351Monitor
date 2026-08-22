// =============================================================================
// Central de Conformidade (/configuracoes/conformidade - F5). Reúne num só lugar
// as EVIDÊNCIAS que a controladora precisa quando alguém pergunta "como vocês
// cumprem a LGPD aqui?": manutenção rodando (a purga de retenção acontece?),
// ciência do aviso na frota (todo mundo foi avisado?), atividade da trilha no mês
// (quem olhou dado pessoal?) e pedidos de titular por status (foram atendidos?).
//
// Fonte única: GET /compliance/summary (Admin/Owner, read-only). Nada aqui é dado
// pessoal - são contagens e carimbos de tempo do próprio tenant.
//
// "Gerar dossiê de conformidade" imprime a própria página (window.print), com o
// mesmo par no-print/print-plain da página pública de transparência: o cabeçalho
// e os botões saem, os cartões viram papel branco legível. A data e o nome da
// organização vêm do servidor (generated_at), não do relógio do navegador.
//
// Owner/Admin apenas: o Viewer não vê a aba (ConfiguracoesLayout) nem esta tela
// (gate defensivo abaixo, espelhando a AuditoriaPage).
// =============================================================================

import { useQuery } from "@tanstack/react-query";
import {
  AlertTriangle,
  BadgeCheck,
  CalendarClock,
  Eye,
  FileText,
  Printer,
  ShieldCheck,
  Wrench,
} from "lucide-react";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type { ComplianceSummaryResponse, MeResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

/** Rótulos pt-BR dos jobs de manutenção (o backend devolve o nome interno). */
const JOB_LABELS: Record<string, string> = {
  RetentionPurge: "Purga de retenção",
  PartitionMaintenance: "Manutenção de partições",
  Housekeeping: "Limpeza geral",
};

/** Rótulos pt-BR dos status de pacote DSR (mesmos do contrato de exports). */
const DSR_STATUS_LABELS: Record<string, string> = {
  queued: "Na fila",
  running: "Gerando",
  done: "Concluído",
  failed: "Falhou",
};

export function ConformidadePage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const timezone = meQuery.data?.organization.timezone ?? null;

  return (
    <div className="space-y-4">
      <div className="no-print">
        <h1 className="text-2xl font-semibold tracking-tight">Conformidade</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Evidências do funcionamento dos controles de privacidade da organização: manutenção dos
          dados, ciência do aviso na frota, uso da trilha de auditoria e atendimento a pedidos de
          titular.
        </p>
      </div>

      {meQuery.isPending ? (
        <Card>
          <div className="space-y-3 p-6">
            <Skeleton className="h-6 w-48" />
            <Skeleton className="h-24 w-full" />
          </div>
        </Card>
      ) : !isAdmin(meQuery.data) ? (
        <ViewerNotice />
      ) : (
        <ComplianceContent timezone={timezone} />
      )}
    </div>
  );
}

function ViewerNotice() {
  return (
    <Card>
      <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
        <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
          <Eye className="h-6 w-6 text-muted-foreground" aria-hidden />
        </span>
        <p className="text-base font-medium">Sem permissão para ver a conformidade</p>
        <p className="max-w-md text-sm text-muted-foreground">
          As evidências de conformidade ficam disponíveis para Administradores e Proprietários.
        </p>
      </div>
    </Card>
  );
}

function ComplianceContent({ timezone }: { timezone: string | null }) {
  const summaryQuery = useQuery({
    queryKey: ["compliance", "summary"],
    queryFn: () => api<ComplianceSummaryResponse>("/compliance/summary"),
    staleTime: 60_000,
  });

  if (summaryQuery.isError) {
    return (
      <Card>
        <div className="flex flex-col items-center gap-3 px-6 py-12 text-center">
          <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
          <p className="text-sm text-muted-foreground">{genericErrorMessage(summaryQuery.error)}</p>
          <Button variant="outline" onClick={() => void summaryQuery.refetch()}>
            Tentar novamente
          </Button>
        </div>
      </Card>
    );
  }

  const data = summaryQuery.data;
  if (data === undefined) {
    return (
      <div className="grid gap-4 sm:grid-cols-2">
        {Array.from({ length: 4 }, (_, i) => (
          <Card key={i}>
            <div className="space-y-3 p-6">
              <Skeleton className="h-5 w-40" />
              <Skeleton className="h-20 w-full" />
            </div>
          </Card>
        ))}
      </div>
    );
  }

  // Fuso do tenant quando disponível; sem ele, o do navegador (a tela nunca fica sem data).
  const tz = timezone ?? Intl.DateTimeFormat().resolvedOptions().timeZone;
  const coverage = data.notice_coverage;
  const activity = data.audit_activity;

  return (
    <div className="space-y-4">
      {/* Cabeçalho do DOSSIÊ: só aparece no papel (a tela já tem o título acima). */}
      <div className="hidden print:block">
        <h2 className="text-xl font-semibold">Dossiê de conformidade</h2>
        <p className="mt-1 text-sm">
          {data.organization_name} · emitido em {formatDateTime(data.generated_at, tz)}
        </p>
      </div>

      <div className="no-print flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-muted-foreground">
          Dados apurados em {formatDateTime(data.generated_at, tz)}.
        </p>
        <Button variant="outline" size="sm" onClick={() => window.print()}>
          <Printer className="h-4 w-4" aria-hidden />
          Gerar dossiê de conformidade
        </Button>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        {/* ---- ciência do aviso na frota */}
        <Card className="print-plain">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <BadgeCheck className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
              Ciência do aviso
            </CardTitle>
            <CardDescription>
              Dispositivos ativos em que o agente confirmou a exibição do aviso de monitoramento.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div className="flex flex-wrap items-baseline gap-x-2">
              <span className="text-3xl font-semibold tabular-nums">{coverage.acknowledged}</span>
              <span className="text-sm text-muted-foreground">
                de {coverage.active_devices}{" "}
                {coverage.active_devices === 1 ? "dispositivo ativo" : "dispositivos ativos"}
              </span>
            </div>
            <p
              className={cn(
                "mt-3 text-sm",
                coverage.pending > 0 ? "text-viz-improdutivo" : "text-muted-foreground",
              )}
            >
              {coverage.active_devices === 0
                ? "Nenhum dispositivo ativo na frota."
                : coverage.pending === 0
                  ? "Cobertura completa: todos os dispositivos ativos registraram a ciência."
                  : `${coverage.pending} ${
                      coverage.pending === 1 ? "dispositivo pendente" : "dispositivos pendentes"
                    } de registro da ciência.`}
            </p>
          </CardContent>
        </Card>

        {/* ---- manutenção dos dados */}
        <Card className="print-plain">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <Wrench className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
              Manutenção dos dados
            </CardTitle>
            <CardDescription>
              Última execução de cada rotina automática, inclusive a purga que apaga dados fora do
              prazo de retenção.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <dl className="divide-y">
              {data.maintenance_runs.map((run) => (
                <div key={run.job_name} className="flex items-baseline justify-between gap-3 py-2">
                  <dt className="text-sm text-muted-foreground">
                    {JOB_LABELS[run.job_name] ?? run.job_name}
                  </dt>
                  <dd className="text-right text-sm">
                    {run.finished_at !== null ? (
                      <>
                        <span className="tabular-nums">{formatDateTime(run.finished_at, tz)}</span>
                        {run.status !== "ok" && (
                          <span className="ml-2 text-xs font-medium text-destructive">
                            Falha na execução
                          </span>
                        )}
                      </>
                    ) : (
                      <span className="text-muted-foreground">Sem execução registrada</span>
                    )}
                  </dd>
                </div>
              ))}
            </dl>
          </CardContent>
        </Card>

        {/* ---- trilha do mês */}
        <Card className="print-plain">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <CalendarClock className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
              Trilha de auditoria em {monthLabel(activity.month)}
            </CardTitle>
            <CardDescription>
              Registros do mês corrente: acessos a dados pessoais e atos ligados a direitos do
              titular.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <dl className="divide-y">
              <ActivityRow label="Linhas do tempo visualizadas" value={activity.view_timeline} />
              <ActivityRow label="Relatórios visualizados" value={activity.view_report} />
              <ActivityRow label="Exportações em CSV" value={activity.export_csv} />
              <ActivityRow label="Pacotes de dados de titular solicitados" value={activity.dsr_export} />
              <ActivityRow label="Exclusões de dados de titular" value={activity.dsr_delete} />
            </dl>
          </CardContent>
        </Card>

        {/* ---- pedidos de titular */}
        <Card className="print-plain">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <FileText className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
              Pacotes de dados de titular
            </CardTitle>
            <CardDescription>
              Todos os pacotes já gerados para atender a pedidos de acesso, por situação.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {data.dsr_exports.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                Nenhum pedido de acesso atendido até agora.
              </p>
            ) : (
              <dl className="divide-y">
                {data.dsr_exports.map((item) => (
                  <ActivityRow
                    key={item.status}
                    label={DSR_STATUS_LABELS[item.status] ?? item.status}
                    value={item.count}
                  />
                ))}
              </dl>
            )}
          </CardContent>
        </Card>
      </div>

      <Card className="print-plain">
        <CardContent className="flex gap-3 p-6">
          <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
          <p className="text-xs text-muted-foreground">
            Documento emitido pelo +351 Monitor a partir dos registros da organização{" "}
            {data.organization_name} em {formatDateTime(data.generated_at, tz)}. As contagens
            refletem o estado no momento da emissão e não substituem parecer jurídico. Conteúdo
            sujeito a revisão jurídica.
          </p>
        </CardContent>
      </Card>
    </div>
  );
}

function ActivityRow({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex items-baseline justify-between gap-3 py-2">
      <dt className="text-sm text-muted-foreground">{label}</dt>
      <dd className="text-sm font-medium tabular-nums">{value.toLocaleString("pt-BR")}</dd>
    </div>
  );
}

/** "2026-08" -> "agosto de 2026"; devolve o valor cru se não for um mês ISO. */
function monthLabel(month: string): string {
  const match = /^(\d{4})-(\d{2})$/.exec(month);
  if (match === null) return month;
  const label = new Intl.DateTimeFormat("pt-BR", { month: "long", year: "numeric", timeZone: "UTC" }).format(
    new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, 1)),
  );
  return label;
}
