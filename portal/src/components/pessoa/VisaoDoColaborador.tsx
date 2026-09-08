// =============================================================================
// VISÃO DO COLABORADOR (item 2 do estudo, decisão 10 do spec de 07/09/2026)
//
// "O que mostramos a esta pessoa": os mesmos números que ela veria sobre si,
// no enquadramento de quem vai olhar nos olhos dela e explicar.
//
// O COLABORADOR NÃO ENTRA AQUI. Ele não é usuário do sistema — nada de login,
// token pessoal ou rota pública com dado dele. Quem abre este bloco é quem já
// tem acesso ao painel, e o número chega às mãos da pessoa por dois caminhos:
// o resumo pessoal em PDF, que o botão daqui gera, e o digest pessoal por
// e-mail, que já sai sozinho toda semana.
//
// POR QUE UM BLOCO SEPARADO, se a página da pessoa já mostra os KPIs: porque a
// pergunta é outra. Os KPIs respondem "como está esta pessoa" para o gestor;
// este bloco responde "o que ela lê quando pergunta sobre si", que é o material
// de uma conversa de 1:1 e o que a LGPD chama de transparência. Misturar os dois
// faria o gestor mostrar a tela dele à pessoa — com contexto que não é dela.
//
// TUDO VEM DO SERVIDOR. Índice e cobertura chegam calculados (fórmula única da
// decisão 4), e este componente não refaz conta nenhuma.
// =============================================================================

import { useQuery } from "@tanstack/react-query";
import { FileText, MessageSquareWarning, ShieldCheck } from "lucide-react";

import { api } from "@/lib/api";
import { classificationColor, classificationLabelIn } from "@/lib/classification";
import type { ClassificationVocabulary } from "@/lib/classification";
import { formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { formatPct, type ResolvedPeriod } from "@/lib/period";
import type { PersonSelfViewResponse } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { ExportCsvBanner, useCsvExport } from "@/components/reports/ExportCsv";

/**
 * `GET /people/{sid}/self-view`. A chave aceita o windows_sid OU o uuid de
 * device_users — a resolução é do servidor, para nunca mostrar a pessoa errada.
 */
export function usePersonSelfView(personKey: string, period: ResolvedPeriod | null) {
  return useQuery({
    queryKey: ["people", "self-view", personKey, period?.from, period?.to],
    queryFn: () =>
      api<PersonSelfViewResponse>(
        `/people/${encodeURIComponent(personKey)}/self-view?from=${period?.from ?? ""}&to=${period?.to ?? ""}`,
      ),
    enabled: personKey.length > 0 && period !== null,
    placeholderData: (prev) => prev,
  });
}

export function VisaoDoColaborador({
  query,
  period,
  vocabulary,
}: {
  query: ReturnType<typeof usePersonSelfView>;
  period: ResolvedPeriod | null;
  vocabulary: ClassificationVocabulary;
}) {
  const data = query.data;
  const pdf = useCsvExport();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <ShieldCheck className="h-4 w-4 text-primary" aria-hidden />
          O que mostramos a esta pessoa
        </CardTitle>
        <CardDescription>
          Os mesmos números que ela vê sobre si, somando TODOS os dispositivos dela — por isso
          podem diferir dos indicadores acima, que são do registro aberto nesta página. É o
          material da conversa de 1:1, e o que a transparência exige que ela possa conferir.
        </CardDescription>
      </CardHeader>

      <CardContent className="space-y-4">
        {query.isError ? (
          /* ERRO ANTES DO ESQUELETO: num 404 (lane-máquina, SID de outro tenant)
             `data` nunca fica definido, e o esqueleto ficaria animando para
             sempre — o "Carregando…" que nunca termina. */
          <p role="alert" className="text-xs leading-relaxed text-destructive">
            {genericErrorMessage(query.error)}
          </p>
        ) : data === undefined ? (
          <div className="space-y-2">
            <Skeleton className="h-16 w-full" />
            <Skeleton className="h-24 w-full" />
          </div>
        ) : (
          <>
            {/* Índice e cobertura SEMPRE juntos (decisão 4): nunca um sem o outro,
                porque um índice sobre 30% do tempo classificado não descreve o
                período — descreve a amostra. */}
            <div className="rounded-md border bg-muted/30 p-3">
              <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
                <span className="text-2xl font-semibold tabular-nums">
                  {formatPct(data.productivity_index)}
                </span>
                <span className="text-xs text-muted-foreground">
                  índice de produtividade · cobertura da classificação{" "}
                  <strong className="font-semibold text-foreground">
                    {formatPct(data.classification_coverage)}
                  </strong>
                </span>
              </div>
              <p className="mt-1.5 text-[11px] leading-relaxed text-muted-foreground">
                Índice = tempo em aplicativos que a empresa classificou como de trabalho, dividido
                pelo tempo ativo já classificado. O tempo em aplicativos sem categoria fica de fora
                da conta e aparece na cobertura. A classificação é definida pela sua empresa, sobre
                aplicativos — nunca sobre pessoas.
              </p>
            </div>

            <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <Metrica rotulo="Máquina ligada" valor={formatDuration(data.seconds_on)} />
              <Metrica rotulo="Tempo ativo" valor={formatDuration(data.seconds_active)} />
              <Metrica rotulo="Ocioso" valor={formatDuration(data.seconds_idle)} />
              <Metrica
                rotulo="Dias com dado"
                valor={`${data.days_with_data} ${data.days_with_data === 1 ? "dia" : "dias"}`}
              />
            </dl>

            <div>
              <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                Composição do tempo ativo
              </h3>
              <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                <Metrica rotulo={classificationLabelIn(vocabulary, 1)} valor={formatDuration(data.seconds_work_related)} />
                <Metrica rotulo={classificationLabelIn(vocabulary, 0)} valor={formatDuration(data.seconds_neutral)} />
                <Metrica rotulo={classificationLabelIn(vocabulary, -1)} valor={formatDuration(data.seconds_not_work_related)} />
                <Metrica rotulo={classificationLabelIn(vocabulary, null)} valor={formatDuration(data.seconds_unclassified)} />
              </dl>
            </div>

            {data.top_apps.length > 0 && (
              <div>
                <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                  Aplicativos do período
                </h3>
                <ul className="m-0 list-none space-y-1 p-0">
                  {/* A chave inclui a CLASSIFICAÇÃO: o mesmo aplicativo pode
                      aparecer duas vezes quando as lanes da pessoa caem em
                      equipes diferentes pela etiqueta legada do dispositivo, e
                      cada equipe classifica de um jeito. As duas linhas são a
                      verdade (os segundos foram somados assim), mas com a chave
                      só no process_name o React veria duplicata. */}
                  {data.top_apps.map((app) => (
                    <li
                      key={`${app.process_name}|${app.classification ?? "sem"}`}
                      className="flex items-center justify-between gap-3 border-b py-1 text-xs last:border-b-0"
                    >
                      <span className="flex min-w-0 items-center gap-2">
                        <span
                          aria-hidden
                          className="h-2 w-2 shrink-0 rounded-full"
                          style={{ background: classificationColor(app.classification) }}
                        />
                        <span className="truncate" title={app.process_name}>
                          {app.display_name}
                        </span>
                        <span className="shrink-0 text-muted-foreground">
                          {classificationLabelIn(vocabulary, app.classification)}
                        </span>
                      </span>
                      <span className="shrink-0 tabular-nums text-muted-foreground">
                        {formatDuration(app.seconds_active)}
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {(data.open_notes > 0 || data.open_disputes > 0) && (
              <p className="flex items-start gap-2 rounded-md border border-dashed px-3 py-2 text-xs leading-relaxed text-muted-foreground">
                <MessageSquareWarning className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
                <span>
                  {data.open_notes > 0 && (
                    <>
                      {data.open_notes} {data.open_notes === 1 ? "anotação" : "anotações"} de período
                    </>
                  )}
                  {data.open_notes > 0 && data.open_disputes > 0 && " e "}
                  {data.open_disputes > 0 && (
                    <>
                      {data.open_disputes}{" "}
                      {data.open_disputes === 1 ? "contestação" : "contestações"} de classificação
                    </>
                  )}{" "}
                  {data.open_notes + data.open_disputes === 1 ? "aguarda" : "aguardam"} a sua
                  revisão. Enquanto estiverem em aberto, o contexto que a pessoa escreveu não
                  acompanha o período nesta tela.
                </span>
              </p>
            )}

            <div className="flex flex-wrap items-center gap-3 border-t pt-3">
              <Button
                variant="outline"
                size="sm"
                className="h-9"
                disabled={period === null || pdf.isPending}
                onClick={() => {
                  if (period === null) return;
                  pdf.mutate({
                    kind: "resumo_pdf",
                    params: { from: period.from, to: period.to, windows_sid: data.windows_sid },
                  });
                }}
              >
                <FileText className="h-4 w-4" aria-hidden />
                {pdf.isPending ? "Enviando…" : "Resumo pessoal em PDF"}
              </Button>
              <span className="text-[11px] leading-snug text-muted-foreground">
                Para entregar à pessoa impresso. Ela não tem acesso ao painel: os canais dela são
                este papel e o resumo pessoal por e-mail.
              </span>
            </div>

            <ExportCsvBanner mutation={pdf} />
          </>
        )}
      </CardContent>
    </Card>
  );
}

/** Uma métrica do bloco: rótulo pequeno em cima, número tabular embaixo. */
function Metrica({ rotulo, valor }: { rotulo: string; valor: string }) {
  return (
    <div className="rounded-md border px-2.5 py-2">
      <dt className="text-[11px] leading-tight text-muted-foreground">{rotulo}</dt>
      <dd className="mt-0.5 text-sm font-semibold tabular-nums">{valor}</dd>
    </div>
  );
}
