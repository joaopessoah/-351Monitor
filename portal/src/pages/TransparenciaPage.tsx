import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { AlertTriangle, Check, Eye, Laptop, Printer, X } from "lucide-react";
import { api, ApiError } from "@/lib/api";
import type { TransparenciaDeviceBlock, TransparenciaPublicResponse } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

/**
 * Página PÚBLICA de transparência, SEM login, SEM cookies, nenhum dado pessoal.
 * Serve DUAS rotas com o mesmo conteúdo:
 *  - /transparencia/:slug - a página da ORGANIZAÇÃO (link divulgável);
 *  - /t/:token - a página DO FUNCIONÁRIO, aberta pelo tray da própria máquina.
 *    A resposta traz `device` e a tela ganha o bloco "Este dispositivo": ciência
 *    registrada e última comunicação daquela instalação.
 *
 * O bloco do dispositivo NÃO mostra hora ativa/ociosa nem aplicativo: o backend
 * não os envia de propósito, porque a URL não tem autenticação. Quem quiser os
 * próprios dados pede ao DPO da organização (pacote DSR).
 *
 * O backend entrega tudo em pt-BR amigável e jamais expõe window_title ou os
 * masked_patterns crus.
 */
export function TransparenciaPage() {
  const { slug, token } = useParams<{ slug?: string; token?: string }>();

  const [data, setData] = useState<TransparenciaPublicResponse | null>(null);
  const [status, setStatus] = useState<"loading" | "ok" | "not_found" | "error">("loading");

  // A rota por token vence quando presente; as duas respondem o mesmo contrato.
  const path =
    token !== undefined && token.length > 0
      ? `/public/t/${encodeURIComponent(token)}`
      : slug !== undefined && slug.length > 0
        ? `/public/transparencia/${encodeURIComponent(slug)}`
        : null;

  useEffect(() => {
    if (path === null) {
      setStatus("not_found");
      return;
    }
    const controller = new AbortController();
    setStatus("loading");
    setData(null);

    // Endpoint público: SEM Authorization, SEM retry de refresh (auth:false).
    api<TransparenciaPublicResponse>(path, { auth: false, signal: controller.signal })
      .then((res) => {
        setData(res);
        setStatus("ok");
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setStatus(err instanceof ApiError && err.status === 404 ? "not_found" : "error");
      });

    return () => controller.abort();
  }, [path]);

  return (
    <div className="min-h-screen bg-background">
      <header className="no-print border-b bg-card">
        <div className="mx-auto flex h-14 max-w-3xl items-center justify-between gap-4 px-4">
          <span className="text-base font-bold tracking-tight text-primary">+351 Monitor</span>
          <span className="text-xs text-muted-foreground">Página pública de transparência</span>
        </div>
      </header>

      <main className="mx-auto max-w-3xl space-y-6 px-4 py-10">
        {status === "loading" && <LoadingState />}
        {status === "not_found" && <NotFoundState slug={slug} />}
        {status === "error" && <ErrorState slug={slug ?? token} />}
        {status === "ok" && data !== null && <Content data={data} />}
      </main>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Conteúdo (política real)
// -----------------------------------------------------------------------------

function Content({ data }: { data: TransparenciaPublicResponse }) {
  return (
    <>
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Transparência do monitoramento</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Política de coleta de <span className="font-medium text-foreground">{data.organization_name}</span>. Esta
            página descreve o que o monitoramento corporativo coleta nas estações de trabalho, e o
            que ele jamais coleta. Nenhum dado pessoal é exibido aqui.
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => window.print()}
          className="no-print shrink-0 self-start"
        >
          <Printer className="h-4 w-4" aria-hidden="true" />
          Imprimir
        </Button>
      </div>

      {/* Só na rota por token: o estado da instalação desta máquina. */}
      {data.device !== null && <DeviceCard device={data.device} />}

      <Card className="print-plain">
        <CardHeader>
          <CardTitle className="text-base">O que é coletado</CardTitle>
          <CardDescription>
            Lista fechada de coleta, limitada ao necessário para gestão de uso das estações.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <ul className="space-y-2">
            {data.coletado.map((item) => (
              <li key={item} className="flex items-start gap-2 text-sm">
                <Check className="mt-0.5 h-4 w-4 shrink-0 text-viz-produtivo" aria-hidden="true" />
                <span>{item}</span>
              </li>
            ))}
          </ul>
        </CardContent>
      </Card>

      <Card className="print-plain">
        <CardHeader>
          <CardTitle className="text-base">O que NUNCA é coletado</CardTitle>
          <CardDescription>
            Estas proibições fazem parte da arquitetura do produto: o código de coleta não existe.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <ul className="space-y-2">
            {data.nunca_coletado.map((item) => (
              <li key={item} className="flex items-start gap-2 text-sm">
                <X className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
                <span>{item}</span>
              </li>
            ))}
          </ul>
        </CardContent>
      </Card>

      <Card className="print-plain">
        <CardHeader>
          <CardTitle className="text-base">Política vigente</CardTitle>
          <CardDescription>
            Como e quando a coleta acontece, conforme configurado pela organização.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <dl className="divide-y">
            <DefRow label="Títulos de janela" value={data.window_title_policy.descricao} />
            <DefRow label="Janela de coleta" value={data.collection_window.descricao} />
            <DefRow
              label="Finalidade declarada"
              value={data.finalidade_declarada}
              fallback="Não informada pela organização."
            />
            <DefRow
              label="Contato do encarregado (DPO)"
              value={data.contato_dpo}
              fallback="Não informado pela organização."
            />
            <DefRow
              label="Vigência da política"
              value={data.vigencia !== null ? formatDate(data.vigencia) : null}
              fallback="Não informada pela organização."
            />
          </dl>
        </CardContent>
      </Card>

      <Card className="print-plain">
        <CardHeader>
          <CardTitle className="text-base">Retenção dos dados</CardTitle>
          <CardDescription>
            Prazos máximos de guarda. Após cada prazo os dados são apagados automaticamente.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <dl className="divide-y">
            <DefRow label="Eventos brutos" value={`${data.retencoes.eventos_dias} dias`} />
            <DefRow label="Intervalos de atividade" value={mesesLabel(data.retencoes.intervalos_meses)} />
            <DefRow label="Agregados (resumos diários)" value={mesesLabel(data.retencoes.agregados_meses)} />
            <DefRow label="Trilha de auditoria" value={mesesLabel(data.retencoes.auditoria_meses)} />
            <DefRow
              label="Última purga executada"
              value={data.ultima_purga !== null ? formatDateTime(data.ultima_purga) : null}
              fallback="Ainda não houve purga registrada."
            />
          </dl>
        </CardContent>
      </Card>

      <p className="text-xs text-muted-foreground">
        Esta página reflete a configuração vigente da organização no momento do acesso e não exibe
        nenhum dado pessoal.
      </p>
    </>
  );
}

/**
 * "Este dispositivo" - presente só na página aberta pelo tray da máquina
 * (/t/:token). Mostra o que a EMPRESA vê sobre a INSTALAÇÃO: hostname, se a
 * ciência do aviso foi registrada e quando o agente falou com o servidor pela
 * última vez. Sem hora ativa/ociosa e sem aplicativo, por decisão do contrato.
 */
function DeviceCard({ device }: { device: TransparenciaDeviceBlock }) {
  return (
    <Card className="print-plain">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Laptop className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          Este dispositivo
        </CardTitle>
        <CardDescription>
          Você vê o que sua empresa vê sobre esta instalação. Nenhuma informação da sua atividade
          de hoje aparece nesta página.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <dl className="divide-y">
          <DefRow label="Nome do dispositivo" value={device.hostname} />
          <DefRow
            label="Ciência do aviso"
            value={
              device.notice_acked_at !== null
                ? `Registrada em ${formatDateTime(device.notice_acked_at)}`
                : null
            }
            fallback="Pendente: o aviso ainda não foi confirmado nesta máquina."
          />
          <DefRow
            label="Última comunicação"
            value={device.last_seen_at !== null ? formatDateTime(device.last_seen_at) : null}
            fallback="Sem comunicação registrada."
          />
          <DefRow label="Situação do monitoramento" value={deviceStatusLabel(device.status)} />
        </dl>
        <p className="mt-4 text-xs text-muted-foreground">
          Para receber os dados registrados sobre você, peça o acesso ao encarregado de dados
          (DPO) da sua organização, indicado acima.
        </p>
      </CardContent>
    </Card>
  );
}

/** Situação da coleta na máquina, em linguagem para o funcionário. */
function deviceStatusLabel(status: TransparenciaDeviceBlock["status"]): string {
  switch (status) {
    case "active":
      return "Ativo: a coleta segue a política descrita nesta página.";
    case "paused":
      return "Pausado pela organização: nada está sendo coletado agora.";
    case "archived":
      return "Arquivado: a coleta foi encerrada e o histórico segue a retenção abaixo.";
    case "revoked":
      return "Desativado: o agente foi desligado desta máquina.";
    default:
      return status;
  }
}

function DefRow({
  label,
  value,
  fallback,
}: {
  label: string;
  value: string | null;
  fallback?: string;
}) {
  const empty = value === null || value.length === 0;
  return (
    <div className="grid grid-cols-1 gap-1 py-3 sm:grid-cols-3 sm:gap-4">
      <dt className="text-sm font-medium text-foreground">{label}</dt>
      <dd
        className={
          empty
            ? "text-sm italic text-muted-foreground sm:col-span-2"
            : "whitespace-pre-line break-words text-sm text-foreground sm:col-span-2"
        }
      >
        {empty ? (fallback ?? "Não informado.") : value}
      </dd>
    </div>
  );
}

// -----------------------------------------------------------------------------
// Estados (loading / 404 / erro)
// -----------------------------------------------------------------------------

function LoadingState() {
  return (
    <div className="flex flex-col items-center gap-3 py-16 text-center">
      <span className="h-6 w-6 animate-spin rounded-full border-2 border-muted border-t-primary" aria-hidden="true" />
      <p className="text-sm text-muted-foreground">Carregando a política de transparência…</p>
    </div>
  );
}

function NotFoundState({ slug }: { slug: string | undefined }) {
  return (
    <Card>
      <div className="flex flex-col items-center gap-3 px-6 py-16 text-center">
        <span className="flex h-12 w-12 items-center justify-center rounded-full bg-muted">
          <Eye className="h-6 w-6 text-muted-foreground" aria-hidden="true" />
        </span>
        <p className="text-base font-medium">Página não encontrada</p>
        <p className="max-w-md text-sm text-muted-foreground">
          Não localizamos uma organização para o endereço
          {slug !== undefined && slug.length > 0 ? (
            <>
              {" "}
              <span className="font-medium text-foreground">{slug}</span>
            </>
          ) : (
            " informado"
          )}
          . Verifique o link recebido pela sua empresa.
        </p>
      </div>
    </Card>
  );
}

function ErrorState({ slug }: { slug: string | undefined }) {
  // Recarrega a rota atual (sem expor detalhe técnico).
  return (
    <Card>
      <div className="flex flex-col items-center gap-3 px-6 py-16 text-center">
        <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden="true" />
        <p className="text-base font-medium">Não foi possível carregar a página</p>
        <p className="max-w-md text-sm text-muted-foreground">
          Ocorreu um erro ao buscar a política de transparência. Tente novamente em instantes.
        </p>
        <Button
          variant="outline"
          onClick={() => window.location.reload()}
          disabled={slug === undefined}
        >
          Tentar novamente
        </Button>
      </div>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Formatação local (a página pública não tem o fuso do tenant em mãos;
// usa o fuso do navegador do visitante, que é o esperado para uma página
// voltada ao público).
// -----------------------------------------------------------------------------

function mesesLabel(meses: number): string {
  return meses === 1 ? "1 mês" : `${meses} meses`;
}

/** "10/06/2026" a partir de yyyy-MM-dd (sem deslocamento de fuso). */
function formatDate(dateStr: string): string {
  const [y, m, d] = dateStr.split("-");
  if (y === undefined || m === undefined || d === undefined) return dateStr;
  return `${d}/${m}/${y}`;
}

/** "10/06/2026 14:32" de um instante ISO no fuso do navegador do visitante. */
function formatDateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  const parts = new Intl.DateTimeFormat("pt-BR", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).formatToParts(date);
  const get = (type: string): string => parts.find((p) => p.type === type)?.value ?? "";
  return `${get("day")}/${get("month")}/${get("year")} ${get("hour")}:${get("minute")}`;
}
