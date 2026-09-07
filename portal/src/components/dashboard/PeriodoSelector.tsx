// =============================================================================
// Seletor do PERÍODO GLOBAL: grupo segmentado Hoje | Esta semana | Este mês |
// [intervalo], o quarto botão abrindo os dois campos de data do mockup
// aprovado.
//
// POR QUE O PERSONALIZADO PRECISA EXISTIR: os três presets respondem "como
// estamos agora"; nenhum responde "como foi o trimestre que fechou" nem "como
// foi a semana da auditoria". O codec da URL (?periodo=custom&de=&ate=) já
// aceitava o intervalo livre desde o começo — faltava a porta para ele.
//
// POR QUE A VALIDAÇÃO É NO CLIENTE: o teto de janela dos endpoints históricos é
// de MAX_PERIOD_DAYS dias e o servidor responde 400 acima disso. Deixar o
// usuário pedir 120 dias para receber um erro genérico seria transformar uma
// regra conhecida em falha de rede. A mensagem aqui diz o teto e diz quantos
// dias ele pediu.
//
// POR QUE `max` NOS CAMPOS: nenhum recorte olha para o futuro, porque a API não
// tem nada a dizer sobre amanhã — e "hoje" é SEMPRE o hoje da organização, não
// o do navegador de quem está viajando.
//
// O rascunho do intervalo fica em localStorage (dentro de try/catch: aba
// privada e storage bloqueado lançam) só para reabrir o painel já preenchido.
// A VERDADE do recorte é a URL, nunca o storage: link compartilhado sempre
// vence o que este navegador lembrava.
// =============================================================================

import { useEffect, useMemo, useRef, useState } from "react";
import { CalendarRange } from "lucide-react";

import { isIsoDate } from "@/lib/format";
import {
  DEFAULT_PERIOD,
  MAX_PERIOD_DAYS,
  PERIOD_LABELS,
  daysBetweenInclusive,
  shortRangeLabel,
  type PeriodPreset,
  type PeriodState,
} from "@/lib/period";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";

/** Chave do rascunho — só conveniência de reabertura; a URL é a verdade. */
const DRAFT_KEY = "m351.visaoGeral.periodoCustom";

interface Draft {
  from: string;
  to: string;
}

/** Lê o rascunho salvo. Storage bloqueado/corrompido não pode derrubar a tela. */
function readDraft(): Draft | null {
  try {
    const raw = window.localStorage.getItem(DRAFT_KEY);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (
      typeof parsed === "object" &&
      parsed !== null &&
      "from" in parsed &&
      "to" in parsed &&
      typeof (parsed as Draft).from === "string" &&
      typeof (parsed as Draft).to === "string" &&
      isIsoDate((parsed as Draft).from) &&
      isIsoDate((parsed as Draft).to)
    ) {
      return { from: (parsed as Draft).from, to: (parsed as Draft).to };
    }
  } catch {
    // Sem rascunho é um estado normal: o painel abre vazio e segue.
  }
  return null;
}

function writeDraft(draft: Draft): void {
  try {
    window.localStorage.setItem(DRAFT_KEY, JSON.stringify(draft));
  } catch {
    // Não poder lembrar o intervalo não é motivo para não aplicá-lo.
  }
}

/**
 * Valida o intervalo escolhido. Devolve null quando está bom, ou a mensagem
 * exata do problema — mensagem clara é requisito, "intervalo inválido" não é.
 */
export function validateCustomRange(from: string, to: string, today: string | null): string | null {
  if (from === "" || to === "") return "Escolha as duas datas do intervalo.";
  if (!isIsoDate(from) || !isIsoDate(to)) return "Use datas no formato dia/mês/ano.";
  if (from > to) return "A data inicial tem de vir antes da final.";
  if (today !== null && to > today) {
    return "O intervalo não pode passar de hoje: não há dado sobre o futuro.";
  }
  const dias = daysBetweenInclusive(from, to);
  if (dias > MAX_PERIOD_DAYS) {
    return `O intervalo tem ${dias} dias e o máximo é ${MAX_PERIOD_DAYS}. Escolha um recorte menor.`;
  }
  return null;
}

export function PeriodoSelector({
  period,
  onChange,
  /** Hoje no fuso da ORGANIZAÇÃO; null enquanto o /me não respondeu. */
  today,
  /** Intervalo já resolvido (com o "até hoje" aplicado), para o rótulo do botão. */
  resolvedFrom,
  resolvedTo,
  className,
}: {
  period: PeriodState;
  onChange: (next: PeriodState) => void;
  today: string | null;
  resolvedFrom?: string;
  resolvedTo?: string;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [erro, setErro] = useState<string | null>(null);
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const firstFieldRef = useRef<HTMLInputElement | null>(null);

  const custom = period.preset === "custom";

  // Rótulo do quarto botão: o intervalo escolhido quando existe (como no
  // mockup), "Personalizado" quando o recorte atual é um dos presets.
  const customLabel = useMemo(() => {
    if (custom && resolvedFrom !== undefined && resolvedTo !== undefined) {
      return shortRangeLabel(resolvedFrom, resolvedTo);
    }
    if (custom && period.from !== null && period.to !== null) {
      return shortRangeLabel(period.from, period.to);
    }
    return PERIOD_LABELS.custom;
  }, [custom, period.from, period.to, resolvedFrom, resolvedTo]);

  // Abrir o painel: parte do recorte atual, do rascunho salvo ou de hoje —
  // nessa ordem de confiança (a URL é a verdade, o storage é lembrança).
  function abrir(): void {
    const rascunho = readDraft();
    const inicial =
      custom && period.from !== null && period.to !== null
        ? { from: period.from, to: period.to }
        : (rascunho ?? (today !== null ? { from: today, to: today } : { from: "", to: "" }));
    setFrom(inicial.from);
    setTo(inicial.to);
    setErro(null);
    setOpen(true);
  }

  // Fecha em Esc e em clique fora: o painel é transitório e não deve capturar
  // o teclado do resto do cabeçalho.
  useEffect(() => {
    if (!open) return;
    function onKey(event: KeyboardEvent): void {
      if (event.key === "Escape") setOpen(false);
    }
    function onPointer(event: MouseEvent): void {
      const wrap = wrapRef.current;
      if (wrap !== null && !wrap.contains(event.target as Node)) setOpen(false);
    }
    document.addEventListener("keydown", onKey);
    document.addEventListener("mousedown", onPointer);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("mousedown", onPointer);
    };
  }, [open]);

  // Foco no primeiro campo ao abrir: quem chegou pelo teclado continua nele.
  useEffect(() => {
    if (open) firstFieldRef.current?.focus();
  }, [open]);

  function aplicar(): void {
    const problema = validateCustomRange(from, to, today);
    if (problema !== null) {
      setErro(problema);
      return;
    }
    writeDraft({ from, to });
    onChange({ preset: "custom", from, to });
    setOpen(false);
  }

  const diasEscolhidos =
    isIsoDate(from) && isIsoDate(to) && from <= to ? daysBetweenInclusive(from, to) : null;

  return (
    <div ref={wrapRef} className={cn("relative", className)}>
      <div
        role="group"
        aria-label="Período"
        className="inline-flex h-9 items-stretch rounded-md border border-input bg-card p-0.5"
      >
        {(["dia", "semana", "mes"] as PeriodPreset[]).map((preset) => (
          <button
            key={preset}
            type="button"
            aria-pressed={period.preset === preset}
            onClick={() => {
              setOpen(false);
              onChange({ ...DEFAULT_PERIOD, preset });
            }}
            className={segmentClass(period.preset === preset)}
          >
            {PERIOD_LABELS[preset]}
          </button>
        ))}
        <button
          type="button"
          aria-pressed={custom}
          aria-expanded={open}
          aria-haspopup="dialog"
          title={`Intervalo livre de até ${MAX_PERIOD_DAYS} dias`}
          onClick={() => (open ? setOpen(false) : abrir())}
          className={cn(segmentClass(custom), "inline-flex items-center gap-1.5")}
        >
          <CalendarRange className="h-3.5 w-3.5" aria-hidden />
          <span className="tabular-nums">{customLabel}</span>
        </button>
      </div>

      {open && (
        <div
          role="dialog"
          aria-label="Período personalizado"
          className="absolute right-0 z-30 mt-1.5 w-[19rem] rounded-md border border-input bg-card p-3 shadow-lg"
        >
          <div className="grid grid-cols-2 gap-2">
            <label className="space-y-1 text-xs">
              <span className="text-muted-foreground">De</span>
              <input
                ref={firstFieldRef}
                type="date"
                value={from}
                max={today ?? undefined}
                onChange={(event) => {
                  setFrom(event.target.value);
                  setErro(null);
                }}
                className="h-9 w-full rounded-md border border-input bg-background px-2 text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
            </label>
            <label className="space-y-1 text-xs">
              <span className="text-muted-foreground">Até</span>
              <input
                type="date"
                value={to}
                min={from === "" ? undefined : from}
                max={today ?? undefined}
                onChange={(event) => {
                  setTo(event.target.value);
                  setErro(null);
                }}
                className="h-9 w-full rounded-md border border-input bg-background px-2 text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
            </label>
          </div>

          <p className="mt-2 text-[11px] leading-snug text-muted-foreground">
            {diasEscolhidos !== null
              ? `${diasEscolhidos} ${diasEscolhidos === 1 ? "dia" : "dias"} selecionados · máximo de ${MAX_PERIOD_DAYS} dias`
              : `Intervalo de até ${MAX_PERIOD_DAYS} dias, sem passar de hoje.`}
          </p>

          {erro !== null && (
            <p role="alert" className="mt-2 text-[11px] leading-snug text-destructive">
              {erro}
            </p>
          )}

          <div className="mt-3 flex items-center justify-end gap-2">
            <Button
              variant="outline"
              size="sm"
              className="h-8 px-2.5 text-xs"
              onClick={() => setOpen(false)}
            >
              Cancelar
            </Button>
            <Button size="sm" className="h-8 px-2.5 text-xs" onClick={aplicar}>
              Aplicar intervalo
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

/** Mesma pintura dos segmentos que a tela já usava (estado na URL, não local). */
function segmentClass(selected: boolean): string {
  return cn(
    "rounded-[5px] px-3 text-xs font-medium transition-colors",
    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
    selected
      ? "bg-primary/10 text-primary"
      : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
  );
}
