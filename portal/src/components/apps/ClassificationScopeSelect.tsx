// =============================================================================
// F5 - SELETOR DE ESCOPO da regra de classificação (spec seção 2.4: "o mesmo
// app pode ser produtivo no Marketing e improdutivo no Financeiro").
//
// A curadoria continua sendo sobre APLICATIVOS, nunca sobre pessoas: o que o
// escopo escolhe é PARA QUEM aquela regra vale, não quem é julgado por ela.
//
// Ordem de precedência que a tela precisa comunicar (a mesma do servidor):
//   1. regra da EQUIPE selecionada     -> "definida por esta equipe"
//   2. regra da ORGANIZAÇÃO            -> "herdado da organização"
//   3. nenhuma das duas                -> sem classificação
// Ausência de regra de equipe é HERANÇA, não omissão - por isso o rótulo
// "herdado" existe: sem ele o gestor acharia que a equipe decidiu algo que ela
// nunca decidiu.
//
// A organização é sempre a primeira opção e o padrão: quem nunca criou equipe
// vê exatamente a tela de antes.
// =============================================================================

import { useQuery } from "@tanstack/react-query";
import { Users } from "lucide-react";
import { api } from "@/lib/api";
import type { ClassificationScope, TeamListResponse } from "@/lib/types";
import { cn } from "@/lib/utils";

/** Valor do select quando o escopo é a organização inteira (o padrão). */
export const ORGANIZATION_SCOPE = "";

/** Lista de equipes para o seletor - compartilhada por Mapeamento e Fila. */
export function useTeamsQuery() {
  return useQuery({
    queryKey: ["teams"],
    queryFn: () => api<TeamListResponse>("/teams"),
    staleTime: 5 * 60 * 1000,
  });
}

/**
 * Sufixo de querystring do escopo (`&team_id=...` ou vazio). Uma função só
 * para o parâmetro nunca ser montado de dois jeitos diferentes nas telas.
 */
export function scopeQuery(teamId: string): string {
  return teamId === ORGANIZATION_SCOPE ? "" : `&team_id=${encodeURIComponent(teamId)}`;
}

/** `team_id` do corpo dos PUTs - null quando o escopo é a organização. */
export function scopeTeamId(teamId: string): string | null {
  return teamId === ORGANIZATION_SCOPE ? null : teamId;
}

export function ClassificationScopeSelect({
  value,
  onChange,
  disabled = false,
}: {
  value: string;
  onChange: (teamId: string) => void;
  disabled?: boolean;
}) {
  const teamsQuery = useTeamsQuery();
  const teams = teamsQuery.data?.items ?? [];

  // Sem nenhuma equipe cadastrada o seletor não tem o que oferecer: some da
  // tela em vez de mostrar um select com uma opção só.
  if (teams.length === 0) return null;

  return (
    <label className="flex items-center gap-2 text-sm">
      <Users className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
      <span className="text-muted-foreground">Regra para</span>
      <select
        aria-label="Escopo da regra de classificação"
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        className={cn(
          "h-9 rounded-md border border-input bg-card px-2 text-sm",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          "disabled:cursor-not-allowed disabled:opacity-50",
        )}
      >
        <option value={ORGANIZATION_SCOPE}>toda a organização</option>
        {teams.map((t) => (
          <option key={t.id} value={t.id}>
            {t.name}
            {t.team_rule_count > 0 ? ` (${t.team_rule_count} regras próprias)` : ""}
          </option>
        ))}
      </select>
    </label>
  );
}

/**
 * Etiqueta "herdado da organização" ao lado do select da linha, quando a
 * categoria exibida no escopo de uma equipe veio da regra geral. Não aparece
 * no escopo da organização (lá não existe herança) nem em app sem regra.
 */
export function ScopeBadge({ scope, teamId }: { scope: ClassificationScope | null; teamId: string }) {
  if (teamId === ORGANIZATION_SCOPE || scope !== "organization") return null;
  return (
    <span
      className="whitespace-nowrap rounded-full bg-secondary px-2 py-0.5 text-[11px] text-muted-foreground"
      title="Esta equipe não declarou regra própria para este app: vale a regra da organização."
    >
      herdado da organização
    </span>
  );
}

/**
 * Frase do escopo, exibida na faixa de aviso acima da tabela. Devolve conteúdo
 * INLINE (sem bloco próprio) porque o chamador já a envolve na faixa: no escopo
 * de organização repete o aviso de sempre; no de equipe explica a precedência e
 * o que "remover" significa lá — voltar a HERDAR, não ficar sem classificação.
 */
export function ScopeNotice({ teamId, teamName }: { teamId: string; teamName: string | undefined }) {
  if (teamId === ORGANIZATION_SCOPE) {
    return <>A categoria vale para toda a organização e reagrega os últimos 30 dias.</>;
  }
  return (
    <>
      Regras de <strong className="font-medium">{teamName ?? "equipe"}</strong>: valem só para as
      pessoas vinculadas a esta equipe e vencem a regra da organização. Deixar um app em
      &ldquo;Não categorizado&rdquo; aqui remove a regra da equipe — o app volta a{" "}
      <em>herdar</em> a regra da organização, não fica sem classificação. Reagrega os últimos 30
      dias.
    </>
  );
}
