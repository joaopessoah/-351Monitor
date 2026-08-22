// =============================================================================
// Configurações -> Política de coleta (F5, spec §8.7). A config de coleta do
// agente vira OPERÁVEL pela controladora: política de títulos (MASKED_PATTERNS
// ou APP_ONLY; FULL só via operadora com registro em DPA), lista de mascaramento
// (regex validada no servidor), processos ignorados, limiar de ociosidade e
// janela de coleta. GET /organization/agent-config (Owner/Admin) e PATCH
// (somente Owner: mudar a coleta é decisão da CONTROLADORA). Toda mudança dá
// bump de config_version e chega à frota no próximo contato de cada agente,
// sem reinstalar nada; a página pública de transparência reflete na hora.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import { Info, ShieldCheck } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "@/lib/api";
import { genericErrorMessage } from "@/lib/messages";
import { isOwner } from "@/lib/roles";
import type { AgentConfigPatchRequest, AgentConfigResponse, MeResponse } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

const DIAS = [
  { value: 1, label: "Seg" },
  { value: 2, label: "Ter" },
  { value: 3, label: "Qua" },
  { value: 4, label: "Qui" },
  { value: 5, label: "Sex" },
  { value: 6, label: "Sáb" },
  { value: 0, label: "Dom" },
] as const;

export function ColetaPage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const canEdit = isOwner(meQuery.data);

  const configQuery = useQuery({
    queryKey: ["agent-config"],
    queryFn: () => api<AgentConfigResponse>("/organization/agent-config"),
    staleTime: 60_000,
  });

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold tracking-tight">Política de coleta</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          O que os agentes coletam e como os títulos de janela são protegidos. As mudanças chegam
          a cada máquina no próximo contato do agente (em até um minuto com a frota ativa), sem
          reinstalar nada, e a página pública de transparência reflete a política vigente.
        </p>
      </div>

      {configQuery.isPending || meQuery.isPending ? (
        <Card>
          <CardContent className="space-y-4 pt-6">
            <Skeleton className="h-5 w-64" />
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-10 w-full max-w-md" />
          </CardContent>
        </Card>
      ) : configQuery.isError ? (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <p className="text-sm text-muted-foreground">{genericErrorMessage(configQuery.error)}</p>
            <Button variant="outline" onClick={() => void configQuery.refetch()}>
              Tentar novamente
            </Button>
          </CardContent>
        </Card>
      ) : configQuery.data !== undefined ? (
        <ColetaForm config={configQuery.data} canEdit={canEdit} />
      ) : null}
    </div>
  );
}

function ColetaForm({ config, canEdit }: { config: AgentConfigResponse; canEdit: boolean }) {
  const queryClient = useQueryClient();

  const [policy, setPolicy] = useState(config.window_title_policy);
  const [patternsText, setPatternsText] = useState(config.masked_patterns.join("\n"));
  const [ignoredText, setIgnoredText] = useState(config.ignored_processes.join("\n"));
  const [idleMin, setIdleMin] = useState(String(Math.round(config.idle_threshold_sec / 60)));
  const [windowMode, setWindowMode] = useState(config.collection_window.mode);
  const [days, setDays] = useState<number[]>(config.collection_window.days ?? [1, 2, 3, 4, 5]);
  const [start, setStart] = useState(config.collection_window.start ?? "08:00");
  const [end, setEnd] = useState(config.collection_window.end ?? "18:00");
  const [saved, setSaved] = useState(false);
  const [sample, setSample] = useState("Consulta senha do banco - Maria Silva.pdf");

  // Re-sincroniza quando o cache muda (ex.: após salvar).
  useEffect(() => {
    setPolicy(config.window_title_policy);
    setPatternsText(config.masked_patterns.join("\n"));
    setIgnoredText(config.ignored_processes.join("\n"));
    setIdleMin(String(Math.round(config.idle_threshold_sec / 60)));
    setWindowMode(config.collection_window.mode);
    setDays(config.collection_window.days ?? [1, 2, 3, 4, 5]);
    setStart(config.collection_window.start ?? "08:00");
    setEnd(config.collection_window.end ?? "18:00");
  }, [config]);

  const patterns = useMemo(
    () => patternsText.split("\n").map((p) => p.trim()).filter((p) => p.length > 0),
    [patternsText],
  );
  const ignored = useMemo(
    () => ignoredText.split("\n").map((p) => p.trim().toLowerCase()).filter((p) => p.length > 0),
    [ignoredText],
  );

  const idleSec = Math.round(Number(idleMin) * 60);
  const idleValid = Number.isFinite(idleSec) && idleSec >= 60 && idleSec <= 1800;

  const mutation = useMutation({
    mutationFn: (body: AgentConfigPatchRequest) =>
      api<AgentConfigResponse>("/organization/agent-config", { method: "PATCH", body }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["agent-config"], updated);
      setSaved(true);
    },
  });

  const draft = useMemo<AgentConfigPatchRequest>(
    () => ({
      // FULL só existe via operadora (DPA); o PATCH não a envia nem a remove
      ...(policy === "FULL" ? {} : { window_title_policy: policy }),
      masked_patterns: patterns,
      ignored_processes: ignored,
      idle_threshold_sec: idleValid ? idleSec : config.idle_threshold_sec,
      collection_window:
        windowMode === "BUSINESS_HOURS"
          ? { mode: "BUSINESS_HOURS", days: [...days].sort((a, b) => a - b), start, end }
          : { mode: "ALWAYS" },
    }),
    [policy, patterns, ignored, idleValid, idleSec, config.idle_threshold_sec, windowMode, days, start, end],
  );

  const dirty =
    policy !== config.window_title_policy ||
    patterns.join("\n") !== config.masked_patterns.join("\n") ||
    ignored.join("\n") !== config.ignored_processes.join("\n") ||
    (idleValid && idleSec !== config.idle_threshold_sec) ||
    windowMode !== config.collection_window.mode ||
    (windowMode === "BUSINESS_HOURS" &&
      ([...days].sort((a, b) => a - b).join(",") !== (config.collection_window.days ?? []).join(",") ||
        start !== (config.collection_window.start ?? "") ||
        end !== (config.collection_window.end ?? "")));

  const windowValid = windowMode === "ALWAYS" || (days.length > 0 && start.length === 5 && end.length === 5);
  const canSubmit = canEdit && dirty && idleValid && windowValid && !mutation.isPending;

  // Preview aproximado do mascaramento (o agente usa regex .NET; aqui é uma
  // demonstração com a engine do navegador, suficiente para os padrões comuns).
  const preview = useMemo(() => {
    if (policy === "APP_ONLY") return "(título não coletado, apenas o nome do aplicativo)";
    let masked = false;
    for (const p of patterns) {
      try {
        if (new RegExp(p, "iu").test(sample)) {
          masked = true;
          break;
        }
      } catch {
        // padrão que o navegador não entende: o servidor valida na hora de salvar
      }
    }
    return masked ? "•••••• (título mascarado)" : sample;
  }, [policy, patterns, sample]);

  function toggleDay(value: number) {
    setSaved(false);
    setDays((prev) => (prev.includes(value) ? prev.filter((d) => d !== value) : [...prev, value]));
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    setSaved(false);
    mutation.mutate(draft);
  }

  const patchError =
    mutation.isError && mutation.error instanceof ApiError && mutation.error.problem?.detail
      ? mutation.error.problem.detail
      : mutation.isError
        ? genericErrorMessage(mutation.error)
        : null;

  const inputCls = cn(
    "flex w-full rounded-md border border-input bg-card px-3 py-2 text-sm font-mono",
    "placeholder:text-muted-foreground",
    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
    "disabled:cursor-not-allowed disabled:opacity-60",
  );

  return (
    <form className="space-y-6" onSubmit={handleSubmit}>
      {!canEdit && (
        <div className="flex gap-3 rounded-md border border-viz-neutro/30 bg-viz-neutro/10 px-4 py-3 text-sm text-viz-neutro">
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <p>
            Você está vendo a política em modo somente leitura. Mudar a política de coleta é uma
            decisão da controladora e fica disponível apenas para o Proprietário.
          </p>
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Títulos de janela</CardTitle>
          <CardDescription>
            O mascaramento acontece NO AGENTE, antes de qualquer envio: o servidor nunca recebe o
            que foi mascarado. Títulos completos sem mascaramento (política FULL) exigem decisão
            registrada em contrato/DPA e são aplicados pela operadora, nunca por aqui.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-5">
          <fieldset className="space-y-2" disabled={!canEdit}>
            <legend className="text-sm font-medium">Política</legend>
            <label className="flex items-start gap-3 text-sm">
              <input
                type="radio"
                name="policy"
                checked={policy === "MASKED_PATTERNS"}
                onChange={() => {
                  setPolicy("MASKED_PATTERNS");
                  setSaved(false);
                }}
                className="mt-1"
              />
              <span>
                <strong>Título com mascaramento</strong> (recomendado): coleta o título da janela,
                mascarando qualquer trecho que case com a lista abaixo.
              </span>
            </label>
            <label className="flex items-start gap-3 text-sm">
              <input
                type="radio"
                name="policy"
                checked={policy === "APP_ONLY"}
                onChange={() => {
                  setPolicy("APP_ONLY");
                  setSaved(false);
                }}
                className="mt-1"
              />
              <span>
                <strong>Somente o aplicativo</strong>: nenhum título é coletado, apenas o nome do
                programa em uso (coleta mínima).
              </span>
            </label>
          </fieldset>

          {policy === "MASKED_PATTERNS" && (
            <div className="space-y-1.5">
              <Label htmlFor="masked-patterns">Padrões de mascaramento (um por linha)</Label>
              <textarea
                id="masked-patterns"
                value={patternsText}
                onChange={(e) => {
                  setPatternsText(e.target.value);
                  setSaved(false);
                }}
                disabled={!canEdit}
                rows={5}
                className={inputCls}
              />
              <p className="text-xs text-muted-foreground">
                Expressões regulares aplicadas ao título no próprio agente. O servidor valida cada
                padrão ao salvar, um padrão inválido é recusado antes de chegar à frota.
              </p>
            </div>
          )}

          <div className="space-y-1.5 rounded-md border bg-muted/30 p-4">
            <Label htmlFor="mask-sample" className="flex items-center gap-2">
              <ShieldCheck className="h-4 w-4 text-primary" aria-hidden="true" />
              Teste: como um título chegaria ao servidor
            </Label>
            <Input
              id="mask-sample"
              value={sample}
              onChange={(e) => setSample(e.target.value)}
              placeholder="Digite um título de janela hipotético"
            />
            <p className="text-sm font-mono" aria-live="polite">
              {preview}
            </p>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="ignored-processes">Aplicativos nunca coletados (um por linha)</Label>
            <textarea
              id="ignored-processes"
              value={ignoredText}
              onChange={(e) => {
                setIgnoredText(e.target.value);
                setSaved(false);
              }}
              disabled={!canEdit}
              rows={4}
              className={inputCls}
            />
            <p className="text-xs text-muted-foreground">
              Nome do executável (ex.: nomedoapp.exe). Estes aplicativos aparecem como "(privado)"
              e nenhum título deles é coletado. Gerenciadores de senha já são ignorados de fábrica.
            </p>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Ociosidade e janela de coleta</CardTitle>
          <CardDescription>
            A janela define QUANDO os agentes coletam janela ativa e ociosidade; fora dela, apenas
            sessão e funcionamento do agente são registrados. A escolha fica registrada na trilha
            de auditoria, quem decide é a controladora.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-5">
          <div className="space-y-1.5">
            <Label htmlFor="idle-threshold">Considerar ocioso após (minutos sem atividade)</Label>
            <Input
              id="idle-threshold"
              type="number"
              min={1}
              max={30}
              value={idleMin}
              onChange={(e) => {
                setIdleMin(e.target.value);
                setSaved(false);
              }}
              disabled={!canEdit}
              className="max-w-32"
            />
            {!idleValid && (
              <p role="alert" className="text-xs text-destructive">
                Informe entre 1 e 30 minutos.
              </p>
            )}
          </div>

          <fieldset className="space-y-2" disabled={!canEdit}>
            <legend className="text-sm font-medium">Janela de coleta</legend>
            <label className="flex items-start gap-3 text-sm">
              <input
                type="radio"
                name="window"
                checked={windowMode === "ALWAYS"}
                onChange={() => {
                  setWindowMode("ALWAYS");
                  setSaved(false);
                }}
                className="mt-1"
              />
              <span>
                <strong>Coletar sempre</strong>: a coleta acompanha qualquer uso da máquina.
              </span>
            </label>
            <label className="flex items-start gap-3 text-sm">
              <input
                type="radio"
                name="window"
                checked={windowMode === "BUSINESS_HOURS"}
                onChange={() => {
                  setWindowMode("BUSINESS_HOURS");
                  setSaved(false);
                }}
                className="mt-1"
              />
              <span>
                <strong>Coletar apenas no horário de trabalho</strong>: fora da janela, só sessão e
                funcionamento do agente são registrados.
              </span>
            </label>
          </fieldset>

          {windowMode === "BUSINESS_HOURS" && (
            <div className="space-y-3 rounded-md border p-4">
              <div className="flex flex-wrap gap-2">
                {DIAS.map((dia) => (
                  <label
                    key={dia.value}
                    className={cn(
                      "cursor-pointer rounded-md border px-3 py-1.5 text-sm",
                      days.includes(dia.value)
                        ? "border-primary bg-primary/10 text-primary"
                        : "text-muted-foreground",
                      !canEdit && "cursor-not-allowed opacity-60",
                    )}
                  >
                    <input
                      type="checkbox"
                      className="sr-only"
                      checked={days.includes(dia.value)}
                      onChange={() => toggleDay(dia.value)}
                      disabled={!canEdit}
                    />
                    {dia.label}
                  </label>
                ))}
              </div>
              <div className="flex flex-wrap items-center gap-3">
                <div className="space-y-1">
                  <Label htmlFor="window-start">Início</Label>
                  <Input
                    id="window-start"
                    type="time"
                    value={start}
                    onChange={(e) => {
                      setStart(e.target.value);
                      setSaved(false);
                    }}
                    disabled={!canEdit}
                    className="w-32"
                  />
                </div>
                <div className="space-y-1">
                  <Label htmlFor="window-end">Fim</Label>
                  <Input
                    id="window-end"
                    type="time"
                    value={end}
                    onChange={(e) => {
                      setEnd(e.target.value);
                      setSaved(false);
                    }}
                    disabled={!canEdit}
                    className="w-32"
                  />
                </div>
              </div>
              {!windowValid && (
                <p role="alert" className="text-xs text-destructive">
                  Selecione ao menos um dia e informe início e fim.
                </p>
              )}
            </div>
          )}
        </CardContent>
      </Card>

      {patchError && (
        <p role="alert" className="text-sm text-destructive">
          {patchError}
        </p>
      )}

      {canEdit && (
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" disabled={!canSubmit}>
            {mutation.isPending ? "Aplicando..." : "Aplicar à frota"}
          </Button>
          {saved && !dirty && (
            <span className="text-sm text-viz-produtivo">
              Política aplicada (versão {config.config_version}). Cada agente recebe no próximo
              contato.
            </span>
          )}
          <span className="text-xs text-muted-foreground">
            Versão atual da configuração: {config.config_version}
          </span>
        </div>
      )}
    </form>
  );
}
