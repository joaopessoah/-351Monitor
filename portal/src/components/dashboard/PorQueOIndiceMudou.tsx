// =============================================================================
// POR QUE O ÍNDICE MUDOU (item 1 do estudo, "índice explicável")
//
// O diferencial nº 1 do estudo, e o que nenhum dos 26 concorrentes pesquisados
// faz: em vez de mostrar que o índice caiu, mostrar DE ONDE a queda veio,
// repartida por aplicativo, equipe e dia.
//
// A promessa deste painel é aritmética, não retórica: as parcelas SOMAM a
// variação total. O rodapé exibe a soma justamente para que a promessa seja
// verificável a olho nu, e não uma alegação do marketing. Quem garante a
// identidade é o servidor (IndexDecomposition, com teste próprio); aqui só se
// desenha o que ele devolveu, sem nenhum recálculo.
//
// LEITURA CORRETA DE UMA LINHA: "−4,1 pontos · mais 6 h" quer dizer que aquele
// membro respondeu por 4,1 pontos da queda, e que o tempo classificado dele
// cresceu 6 h. NÃO quer dizer que o índice dele é 4,1, nem que ele é ruim. Um
// aplicativo improdutivo que cresceu derruba o índice sem ter um segundo
// produtivo, porque engorda o denominador — e é esse o caso que o gestor
// precisa conseguir nomear.
//
// SEM JULGAMENTO (seção 8 do spec): o texto descreve variação e onde ela está.
// Nada aqui ordena pessoas, e a dimensão por equipe nomeia equipe, nunca gente.
// =============================================================================

import { useMemo, useState } from "react";
import { TrendingDown, TrendingUp } from "lucide-react";

import { formatDuration } from "@/lib/format";
import { formatPct } from "@/lib/period";
import type { IndexContributionRow, IndexExplainedResponse } from "@/lib/types";
import { BRAND } from "@/lib/brandTheme";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";

/** As três dimensões, na ordem em que o gestor pergunta. */
const DIMENSOES = [
  { id: "by_app", rotulo: "Aplicativo" },
  { id: "by_team", rotulo: "Equipe" },
  { id: "by_day", rotulo: "Dia" },
] as const;

type DimensaoId = (typeof DIMENSOES)[number]["id"];

export function PorQueOIndiceMudou({
  className,
  data,
  isPending,
}: {
  className?: string;
  data: IndexExplainedResponse | undefined;
  isPending: boolean;
}) {
  const [dimensao, setDimensao] = useState<DimensaoId>("by_app");

  const linhas = data?.[dimensao] ?? [];

  // escala das barras: a maior contribuição EM MÓDULO ocupa a metade inteira do
  // trilho. Sem isso, um período de variação pequena desenharia barras
  // invisíveis e o painel pareceria vazio quando na verdade tem o que dizer.
  const maiorModulo = useMemo(
    () => linhas.reduce((maior, l) => Math.max(maior, Math.abs(l.points)), 0),
    [linhas],
  );

  const soma = useMemo(() => linhas.reduce((total, l) => total + l.points, 0), [linhas]);

  if (isPending && data === undefined) {
    return (
      <Card className={cnJoin("p-4", className)}>
        <Skeleton className="mb-3 h-5 w-48" />
        <Skeleton className="h-40 w-full" />
      </Card>
    );
  }

  if (data === undefined) return null;

  const delta = data.delta_points;

  return (
    <Card className={cnJoin("p-4", className)}>
      <header className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <h2 className="font-display text-sm font-semibold leading-tight">
            Por que o índice mudou
          </h2>
          {delta !== null && (
            <span
              className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-xs font-semibold tabular-nums"
              style={{
                color: delta < 0 ? BRAND.vizImprodutivo : BRAND.vizProdutivo,
                background: `${delta < 0 ? BRAND.vizImprodutivo : BRAND.vizProdutivo}1f`,
              }}
            >
              {delta < 0 ? (
                <TrendingDown className="h-3 w-3" aria-hidden />
              ) : (
                <TrendingUp className="h-3 w-3" aria-hidden />
              )}
              {formatPontos(delta)}
            </span>
          )}
        </div>

        {/* segmentado das três dimensões: o repo não tem componente de abas, e um
            grupo de botões com aria-pressed diz a mesma coisa ao leitor de tela */}
        <div className="flex rounded-md border p-0.5" role="group" aria-label="Dimensão da decomposição">
          {DIMENSOES.map((d) => (
            <button
              key={d.id}
              type="button"
              aria-pressed={dimensao === d.id}
              onClick={() => setDimensao(d.id)}
              className={cnJoin(
                "rounded px-2 py-1 text-xs font-medium transition-colors",
                dimensao === d.id
                  ? "bg-primary text-primary-foreground"
                  : "text-muted-foreground hover:text-foreground",
              )}
            >
              {d.rotulo}
            </button>
          ))}
        </div>
      </header>

      {data.unavailable !== null ? (
        // número real ou frase ausente: zero aqui leria como "não mudou nada"
        // quando a verdade é "não dá para saber". Mesma régua do índice.
        <p className="text-xs leading-relaxed text-muted-foreground">{data.unavailable}</p>
      ) : linhas.length === 0 ? (
        <p className="text-xs leading-relaxed text-muted-foreground">
          Nenhum {dimensao === "by_app" ? "aplicativo" : dimensao === "by_team" ? "equipe" : "dia"} mexeu
          no índice neste período.
        </p>
      ) : (
        <>
          <p className="mb-2.5 text-xs leading-relaxed text-muted-foreground">
            O índice passou de{" "}
            <strong className="font-semibold text-foreground">{formatPct(data.previous_index)}</strong>{" "}
            para{" "}
            <strong className="font-semibold text-foreground">{formatPct(data.index)}</strong>. Cada
            linha abaixo é quanto daquela variação veio deste{" "}
            {dimensao === "by_app" ? "aplicativo" : dimensao === "by_team" ? "grupo" : "dia"}.
          </p>

          <ul className="m-0 list-none space-y-1.5 p-0">
            {linhas.map((linha) => (
              <Linha key={linha.key} linha={linha} maiorModulo={maiorModulo} />
            ))}
          </ul>

          {/* A PROMESSA DO PAINEL, à vista: as parcelas fecham a variação. Se um
              dia deixarem de fechar, é aqui que aparece — e é de propósito. */}
          <p className="mt-3 border-t pt-2 text-[11px] leading-relaxed text-muted-foreground">
            As parcelas listadas somam{" "}
            <strong className="font-semibold text-foreground tabular-nums">{formatPontos(soma)}</strong>
            {delta !== null && Math.abs(soma - delta) >= 0.05 && (
              <>
                {" "}
                de uma variação total de{" "}
                <strong className="font-semibold text-foreground tabular-nums">
                  {formatPontos(delta)}
                </strong>{" "}
                — o restante está fora das {linhas.length} maiores contribuições
              </>
            )}
            . Tempo sem classificação não entra: ele está fora da fórmula do índice.
          </p>
        </>
      )}
    </Card>
  );
}

/** Uma contribuição: rótulo, barra divergente a partir do eixo central e os números. */
function Linha({ linha, maiorModulo }: { linha: IndexContributionRow; maiorModulo: number }) {
  const negativa = linha.points < 0;
  const largura = maiorModulo > 0 ? (Math.abs(linha.points) / maiorModulo) * 50 : 0;
  const deltaSegundos = linha.seconds_classified - linha.seconds_classified_previous;

  return (
    <li className="grid grid-cols-[minmax(0,7rem)_1fr_minmax(0,6.5rem)] items-center gap-2">
      <span className="truncate text-xs text-foreground" title={linha.label}>
        {linha.label}
      </span>

      {/* trilho com eixo no meio: negativo cresce para a esquerda, positivo para
          a direita. O eixo visível é o que faz a leitura ser "de que lado", que
          é a pergunta, em vez de "qual o maior", que seria um pódio. */}
      <span className="relative block h-3 rounded bg-muted/40" aria-hidden>
        <span className="absolute inset-y-0 left-1/2 w-px -translate-x-1/2 bg-border" />
        <span
          className="absolute inset-y-0.5 rounded"
          style={{
            width: `${largura}%`,
            [negativa ? "right" : "left"]: "50%",
            background: negativa ? BRAND.vizImprodutivo : BRAND.vizProdutivo,
          }}
        />
      </span>

      <span className="text-right text-[11px] leading-tight tabular-nums">
        <span
          className="font-semibold"
          style={{ color: negativa ? BRAND.vizImprodutivo : BRAND.vizProdutivo }}
        >
          {formatPontos(linha.points)}
        </span>
        {deltaSegundos !== 0 && (
          <span className="block text-muted-foreground">
            {deltaSegundos > 0 ? "+" : "−"}
            {formatDuration(Math.abs(deltaSegundos))}
          </span>
        )}
      </span>
    </li>
  );
}

/**
 * Pontos do índice com sinal explícito e uma casa. O sinal vem primeiro porque a
 * direção é a informação — "−4,1 pontos" se lê antes de "4,1".
 * O menos é U+2212 (menos matemático), não hífen: alinha com os dígitos tabulares.
 */
function formatPontos(pontos: number): string {
  const arredondado = Math.abs(pontos) < 0.05 ? 0 : pontos;
  const sinal = arredondado > 0 ? "+" : arredondado < 0 ? "−" : "";
  return `${sinal}${Math.abs(arredondado).toFixed(1)} pt`;
}

/**
 * Concatenação de classes local. O `cn()` do repo não tem tailwind-merge, e aqui
 * o padding custom precisa vencer o default do Card — a mesma armadilha
 * documentada no ResumoDoPeriodo, no WeeklyChartsRow e no BlockCard.
 */
function cnJoin(...parts: (string | undefined | false)[]): string {
  return parts.filter((p): p is string => typeof p === "string" && p.length > 0).join(" ");
}
