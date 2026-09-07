// =============================================================================
// RESUMO DO PERÍODO (F6) — o "cartão de insight com brilho em gradiente" da
// referência visual aprovada (Dribbble 27479829), com o conteúdo em português
// gerado dos agregados que a tela JÁ carregou.
//
// Três frases, na ordem das perguntas que o gestor faz:
//  1. o que mudou no índice e qual equipe puxou a variação;
//  2. quantas horas ficaram sem classificação e o que a cobertura ganharia;
//  3. quanta ociosidade houve e onde ela se concentra.
//
// HEURÍSTICO, NÃO LLM (spec seção 5, item 7: "heurístico primeiro, LLM depois,
// sem dado pessoal"). Zero requisição nova: consome o overview do período e as
// linhas de equipe que os outros blocos já buscaram, pelo mesmo cache do
// TanStack.
//
// REGRA DE OURO DESTE CARTÃO: número real ou frase ausente. Se falta o dado, a
// frase simplesmente não aparece — nunca uma frase com "0%" no lugar de "não
// sei", nunca uma projeção apresentada como fato. E nada de julgamento: o texto
// descreve variação e onde ela está, jamais quem é bom ou ruim.
// =============================================================================

import { useMemo } from "react";
import { Link } from "react-router-dom";
import { Sparkles } from "lucide-react";

import { formatDuration } from "@/lib/format";
import { deltaPoints, formatPct } from "@/lib/period";
import type { OverviewResponse, TeamComparisonRow } from "@/lib/types";
import { Card } from "@/components/ui/card";
import { BRAND } from "@/lib/brandTheme";
import { hoursLabel } from "@/components/dashboard/overviewKit";

/** Uma frase do resumo, com o destino de quem quiser puxar o fio. */
interface Frase {
  key: string;
  texto: React.ReactNode;
}

export function ResumoDoPeriodo({
  className,
  data,
  equipes,
}: {
  className?: string;
  data: OverviewResponse | undefined;
  equipes: TeamComparisonRow[];
}) {
  const frases = useMemo<Frase[]>(() => {
    if (data === undefined) return [];

    const lista: Frase[] = [];
    const totals = data.totals;
    const previous = data.previous;

    // ---- 1. o que mudou no índice, e qual equipe puxou
    const pontos = deltaPoints(totals.productivity_index, previous?.productivity_index);
    if (totals.productivity_index !== null && pontos !== null) {
      // a equipe que mais mudou EM PONTOS: é onde a variação está, não um pódio.
      // Só entra quem atende ao mínimo de grupo (decisão 3).
      const comparaveis = equipes
        .filter((e) => e.meets_group_minimum)
        .map((e) => ({
          tag: e.tag,
          pontos: deltaPoints(e.productivity_index, e.productivity_index_previous),
        }))
        .filter((e): e is { tag: string; pontos: number } => e.pontos !== null);

      const puxou = comparaveis.reduce<{ tag: string; pontos: number } | null>(
        (maior, atual) =>
          maior === null || Math.abs(atual.pontos) > Math.abs(maior.pontos) ? atual : maior,
        null,
      );

      lista.push({
        key: "indice",
        texto: (
          <>
            O índice fechou o período em{" "}
            <strong className="font-semibold text-foreground">
              {formatPct(totals.productivity_index)}
            </strong>
            {pontos === 0 ? (
              <>, sem variação em relação ao período anterior</>
            ) : (
              <>
                ,{" "}
                <strong className="font-semibold text-foreground">
                  {Math.abs(pontos)} {Math.abs(pontos) === 1 ? "ponto" : "pontos"}{" "}
                  {pontos > 0 ? "acima" : "abaixo"}
                </strong>{" "}
                do período anterior
              </>
            )}
            {puxou !== null && puxou.pontos !== 0 && (
              <>
                {" "}
                — a maior variação está na equipe{" "}
                <Link
                  to={`/visao-geral?tag=${encodeURIComponent(puxou.tag)}`}
                  className="font-semibold text-foreground underline underline-offset-2"
                >
                  {puxou.tag}
                </Link>{" "}
                ({Math.abs(puxou.pontos)} {puxou.pontos > 0 ? "acima" : "abaixo"})
              </>
            )}
            .
          </>
        ),
      });
    }

    // ---- 2. horas sem classificação e o ganho de cobertura
    if (totals.seconds_unclassified > 0 && totals.classification_coverage !== null) {
      lista.push({
        key: "cobertura",
        texto: (
          <>
            <strong className="font-semibold text-foreground">
              {formatDuration(totals.seconds_unclassified)}
            </strong>{" "}
            de tempo ativo ficaram em aplicativos sem categoria, o que deixa a cobertura em{" "}
            <strong className="font-semibold text-foreground">
              {formatPct(totals.classification_coverage)}
            </strong>
            . Classificar esses aplicativos levaria a cobertura a 100% e é o que faz o índice
            descrever o período inteiro —{" "}
            <Link
              to="/configuracoes/categorias"
              className="font-semibold text-foreground underline underline-offset-2"
            >
              a fila de classificação
            </Link>{" "}
            mostra quais pesam mais.
          </>
        ),
      });
    }

    // ---- 3. ociosidade e onde ela se concentra
    if (totals.seconds_on > 0 && totals.seconds_idle > 0) {
      const ociosidade = totals.seconds_idle / totals.seconds_on;

      // a equipe com maior ociosidade, só entre as que atendem ao mínimo de grupo
      const concentrada = equipes
        .filter((e) => e.meets_group_minimum && e.idle_share !== null)
        .reduce<TeamComparisonRow | null>(
          (maior, atual) =>
            maior === null || (atual.idle_share ?? 0) > (maior.idle_share ?? 0) ? atual : maior,
          null,
        );

      lista.push({
        key: "ociosidade",
        texto: (
          <>
            <strong className="font-semibold text-foreground">{formatPct(ociosidade)}</strong> do
            tempo com a máquina ligada foi ocioso ({hoursLabel(totals.seconds_idle)})
            {concentrada !== null && (
              <>
                , concentrado na equipe{" "}
                <Link
                  to={`/visao-geral?tag=${encodeURIComponent(concentrada.tag)}`}
                  className="font-semibold text-foreground underline underline-offset-2"
                >
                  {concentrada.tag}
                </Link>{" "}
                ({formatPct(concentrada.idle_share)})
              </>
            )}
            . Ocioso é estado de máquina, não de pessoa: reunião presencial, chamada e leitura
            aparecem aqui, e nada disso conta como improdutivo.
          </>
        ),
      });
    }

    return lista;
  }, [data, equipes]);

  // sem nenhuma frase possível o cartão não aparece: um cartão de resumo vazio
  // é pior que a ausência dele — sugere que não houve nada a dizer quando a
  // verdade é que não houve dado.
  if (frases.length === 0) return null;

  return (
    <Card
      className={cnJoin("relative overflow-hidden p-4", className)}
      // brilho em gradiente da referência visual: um halo do verde da marca no
      // canto, atrás do texto, sem cor de fundo cheia (o painel é escuro e
      // denso; fundo cheio brigaria com os cartões vizinhos)
      style={{
        backgroundImage:
          `radial-gradient(120% 140% at 0% 0%, ${BRAND.green}1f 0%, transparent 55%),` +
          `radial-gradient(90% 120% at 100% 100%, ${BRAND.vizNeutro}14 0%, transparent 60%)`,
      }}
    >
      <div className="mb-2.5 flex items-center gap-2">
        <span
          aria-hidden
          className="flex h-6 w-6 items-center justify-center rounded-md"
          style={{ background: `${BRAND.green}26` }}
        >
          <Sparkles className="h-3.5 w-3.5" style={{ color: BRAND.green }} aria-hidden />
        </span>
        <h2 className="font-display text-sm font-semibold leading-tight">Resumo do período</h2>
        <span className="text-[10px] uppercase tracking-wide text-muted-foreground">
          gerado dos seus dados
        </span>
      </div>

      <ul className="m-0 list-none space-y-2 p-0">
        {frases.map((frase) => (
          <li
            key={frase.key}
            className="border-l-2 pl-2.5 text-xs leading-relaxed text-muted-foreground"
            style={{ borderColor: `${BRAND.green}59` }}
          >
            {frase.texto}
          </li>
        ))}
      </ul>
    </Card>
  );
}

/**
 * Concatenação de classes local. O `cn()` do repo não tem tailwind-merge, e
 * aqui o padding custom precisa vencer o default do Card — a mesma armadilha
 * documentada no WeeklyChartsRow e no BlockCard.
 */
function cnJoin(...parts: (string | undefined)[]): string {
  return parts.filter((p) => p !== undefined && p.length > 0).join(" ");
}
