// =============================================================================
// Configurações › Equipes (F7, spec seção 2.3 e fase F7 da seção 6).
//
// DUAS NOÇÕES DE EQUIPE COEXISTEM, e a tela diz isso em voz alta em vez de
// esconder:
//  - a ETIQUETA livre do dispositivo (devices.tags) continua existindo e
//    continua sendo o filtro "equipe" de todos os painéis, relatórios e
//    exports. Nada foi removido;
//  - a EQUIPE de verdade é vinculada à PESSOA (windows_sid), tem nome
//    canônico, jornada declarada e pode ter regra de classificação própria.
// Onde as duas respondem, a equipe da PESSOA vence. A equipe declara em
// "etiqueta equivalente" qual rótulo legado ela representa - é o atalho de
// migração para quem já etiquetava máquinas.
//
// O que a tela edita, e por quê:
//  - COMPOSIÇÃO (pessoas): decide qual regra de classificação vale para o
//    tempo daquelas pessoas, então mudá-la recalcula os últimos 30 dias;
//  - JORNADA: entra só no DENOMINADOR da capacidade utilizada (não muda
//    nenhum dado medido). Ausência = herda a jornada da organização;
//  - FERIADOS: o dia sai do denominador da capacidade. Sem isso, uma semana
//    com feriado aparece com capacidade subestimada - era a pendência aberta
//    no cartão de equipes da Visão Geral.
//
// NADA aqui é sobre pessoas: equipe é agrupamento, jornada é contrato de
// trabalho declarado e feriado é calendário. Classificação segue sendo sobre
// APLICATIVOS (Configurações › Classificação), nunca sobre quem os usa.
// =============================================================================

import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CalendarDays, Info, Pencil, Plus, Trash2, Users } from "lucide-react";
import { api, ApiError } from "@/lib/api";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type {
  BusinessHours,
  CapacityResponse,
  Holiday,
  HolidayListResponse,
  MeResponse,
  SeedHolidaysResponse,
  SetTeamMembersResponse,
  Team,
  TeamListResponse,
  TeamMembersResponse,
  TeamWriteRequest,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";

const DIAS = [
  { iso: 1, label: "Seg" },
  { iso: 2, label: "Ter" },
  { iso: 3, label: "Qua" },
  { iso: 4, label: "Qui" },
  { iso: 5, label: "Sex" },
  { iso: 6, label: "Sáb" },
  { iso: 7, label: "Dom" },
] as const;

const inputClass = "h-9 w-full";

/** 409 = nome ou etiqueta já em uso; o resto cai na mensagem genérica. */
function teamErrorMessage(err: unknown): string {
  if (err instanceof ApiError && err.status === 409) {
    return err.problem?.title ?? "Já existe uma equipe com esse nome ou etiqueta.";
  }
  return genericErrorMessage(err);
}

/** "08:00–18:00 · seg a sex" a partir da jornada declarada. */
function describeWorkHours(hours: BusinessHours | null): string {
  if (hours === null) return "não declarada";
  const dias = DIAS.filter((d) => hours.days.includes(d.iso)).map((d) => d.label);
  const quando = dias.length === 0 ? "nenhum dia" : dias.join(", ");
  return `${hours.start}–${hours.end} · ${quando}`;
}

export function EquipesPage() {
  const meQuery = useQuery({
    queryKey: ["me"],
    queryFn: () => api<MeResponse>("/me"),
    staleTime: 5 * 60 * 1000,
  });
  const admin = isAdmin(meQuery.data);

  return (
    <div className="space-y-4">
      <EquipesCard admin={admin} />
      <FeriadosCard admin={admin} />
    </div>
  );
}

// -----------------------------------------------------------------------------
// Equipes: CRUD + composição + jornada
// -----------------------------------------------------------------------------

function EquipesCard({ admin }: { admin: boolean }) {
  const [form, setForm] = useState<{ team: Team | null } | null>(null);
  const [deleting, setDeleting] = useState<Team | null>(null);
  const [members, setMembers] = useState<Team | null>(null);

  const teamsQuery = useQuery({
    queryKey: ["teams"],
    queryFn: () => api<TeamListResponse>("/teams"),
  });
  const teams = teamsQuery.data?.items ?? [];
  const orgHours = teamsQuery.data?.organization_work_hours ?? null;

  return (
    <>
      <Card>
        <CardHeader className="pb-3">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="space-y-1.5">
              <CardTitle className="text-base">Equipes</CardTitle>
              <CardDescription>
                Agrupamento por PESSOA (não por máquina): quem troca de computador não troca de
                equipe. A equipe define a jornada usada na capacidade e pode ter regra de
                classificação própria.
                {!admin && " Somente administradores e proprietários editam."}
              </CardDescription>
            </div>
            {admin && (
              <Button onClick={() => setForm({ team: null })}>
                <Plus className="mr-2 h-4 w-4" aria-hidden />
                Nova equipe
              </Button>
            )}
          </div>
        </CardHeader>

        <div
          role="note"
          className="flex items-start gap-2 border-y bg-viz-neutro/10 px-6 py-2.5 text-sm text-viz-neutro"
        >
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <span>
            As etiquetas de dispositivo continuam funcionando como filtro em todo o painel. Uma
            equipe pode declarar a etiqueta equivalente para aproveitar o que já estava marcado —
            quando as duas respondem, <strong className="font-medium">a equipe da pessoa vence</strong>.
            Mudar a composição recalcula os últimos 30 dias.
          </span>
        </div>

        <div className="px-6 pb-6 pt-4">
          {teamsQuery.isError ? (
            <div className="flex flex-col items-center gap-3 py-6 text-center">
              <AlertTriangle className="h-8 w-8 text-destructive" aria-hidden />
              <p className="text-sm text-muted-foreground">{genericErrorMessage(teamsQuery.error)}</p>
              <Button variant="outline" onClick={() => void teamsQuery.refetch()}>
                Tentar novamente
              </Button>
            </div>
          ) : teamsQuery.isPending ? (
            <div className="space-y-2">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          ) : teams.length === 0 ? (
            <p className="py-6 text-center text-sm text-muted-foreground">
              Nenhuma equipe ainda. Enquanto não houver, tudo continua funcionando pelas etiquetas
              de dispositivo e pela regra de classificação da organização.
            </p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-xs uppercase tracking-wide text-muted-foreground">
                    <th className="py-2 pr-3 font-medium">Equipe</th>
                    <th className="px-3 py-2 font-medium">Etiqueta equivalente</th>
                    <th className="px-3 py-2 font-medium">Jornada</th>
                    <th className="px-3 py-2 text-right font-medium">Pessoas</th>
                    <th className="px-3 py-2 text-right font-medium">Regras próprias</th>
                    {admin && <th className="py-2 pl-3 text-right font-medium">Ações</th>}
                  </tr>
                </thead>
                <tbody>
                  {teams.map((team) => (
                    <tr key={team.id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                      <td className="py-2 pr-3 font-medium">{team.name}</td>
                      <td className="px-3 py-2">
                        {team.tag === null ? (
                          <span className="text-muted-foreground">—</span>
                        ) : (
                          <span className="rounded-full bg-secondary px-2 py-0.5 text-xs text-secondary-foreground">
                            {team.tag}
                          </span>
                        )}
                      </td>
                      <td className="px-3 py-2">
                        <span>{describeWorkHours(team.effective_work_hours)}</span>
                        {team.work_hours_inherited && (
                          <span
                            className="ml-2 whitespace-nowrap text-xs text-muted-foreground"
                            title="Esta equipe não declarou jornada própria: vale a da organização."
                          >
                            herdada da organização
                          </span>
                        )}
                      </td>
                      <td className="px-3 py-2 text-right tabular-nums">
                        <button
                          type="button"
                          onClick={() => setMembers(team)}
                          className="rounded-sm underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        >
                          {team.member_count}
                        </button>
                      </td>
                      <td className="px-3 py-2 text-right tabular-nums">{team.team_rule_count}</td>
                      {admin && (
                        <td className="py-2 pl-3 text-right">
                          <span className="inline-flex gap-1">
                            <Button
                              variant="ghost"
                              size="icon"
                              aria-label={`Pessoas de ${team.name}`}
                              onClick={() => setMembers(team)}
                            >
                              <Users className="h-4 w-4" aria-hidden />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              aria-label={`Editar ${team.name}`}
                              onClick={() => setForm({ team })}
                            >
                              <Pencil className="h-4 w-4" aria-hidden />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              aria-label={`Excluir ${team.name}`}
                              onClick={() => setDeleting(team)}
                            >
                              <Trash2 className="h-4 w-4 text-destructive" aria-hidden />
                            </Button>
                          </span>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </Card>

      {form !== null && (
        <TeamFormDialog team={form.team} organizationHours={orgHours} onClose={() => setForm(null)} />
      )}
      {deleting !== null && <DeleteTeamDialog team={deleting} onClose={() => setDeleting(null)} />}
      {members !== null && <MembersDialog team={members} admin={admin} onClose={() => setMembers(null)} />}
    </>
  );
}

/** Criar/editar equipe - POST /teams ou PATCH /teams/{id}. */
function TeamFormDialog({
  team,
  organizationHours,
  onClose,
}: {
  team: Team | null;
  organizationHours: BusinessHours | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(team?.name ?? "");
  const [tag, setTag] = useState(team?.tag ?? "");
  // "declara jornada própria?" é um estado à parte: desmarcado envia null, que
  // no backend significa HERDAR a da organização (ausência é herança).
  const [ownHours, setOwnHours] = useState(team?.work_hours !== null && team !== null);
  const [days, setDays] = useState<number[]>(
    team?.work_hours?.days ?? organizationHours?.days ?? [1, 2, 3, 4, 5],
  );
  const [start, setStart] = useState(team?.work_hours?.start ?? organizationHours?.start ?? "08:00");
  const [end, setEnd] = useState(team?.work_hours?.end ?? organizationHours?.end ?? "18:00");

  const mutation = useMutation({
    mutationFn: () => {
      const body: TeamWriteRequest = {
        name: name.trim(),
        tag: tag.trim() === "" ? null : tag.trim(),
        work_hours: ownHours ? { days: [...days].sort((a, b) => a - b), start, end } : null,
      };
      return team === null
        ? api<Team>("/teams", { method: "POST", body })
        : api<Team>(`/teams/${encodeURIComponent(team.id)}`, { method: "PATCH", body });
    },
    onSuccess: async () => {
      // criar/editar equipe pode mexer na etiqueta, o que reagrega no backend:
      // invalida também o que depende dos agregados
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["teams"] }),
        queryClient.invalidateQueries({ queryKey: ["app-catalog"] }),
      ]);
      onClose();
    },
  });

  const valid = name.trim().length > 0 && (!ownHours || (days.length > 0 && start < end));

  return (
    <Dialog open onOpenChange={(open) => !open && !mutation.isPending && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{team === null ? "Nova equipe" : `Editar ${team.name}`}</DialogTitle>
          <DialogDescription>
            A equipe agrupa PESSOAS. A jornada entra só no cálculo de capacidade utilizada; ela não
            altera nenhum dado medido.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="space-y-1.5">
            <Label htmlFor="team-name">Nome</Label>
            <Input
              id="team-name"
              value={name}
              maxLength={100}
              onChange={(e) => setName(e.target.value)}
              className={inputClass}
            />
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="team-tag">Etiqueta equivalente (opcional)</Label>
            <Input
              id="team-tag"
              value={tag}
              maxLength={60}
              placeholder="ex.: comercial"
              onChange={(e) => setTag(e.target.value)}
              className={inputClass}
            />
            <p className="text-xs text-muted-foreground">
              Se você já marca dispositivos com uma etiqueta, informe-a aqui: as máquinas com essa
              etiqueta passam a resolver para esta equipe enquanto as pessoas não estiverem
              vinculadas. O vínculo da pessoa sempre vence a etiqueta.
            </p>
          </div>

          <div className="space-y-2">
            <label className="flex cursor-pointer items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={ownHours}
                onChange={(e) => setOwnHours(e.target.checked)}
                className="h-4 w-4 accent-primary"
              />
              Declarar jornada própria desta equipe
            </label>
            {!ownHours && (
              <p className="text-xs text-muted-foreground">
                Sem jornada própria, a equipe herda a da organização (
                {describeWorkHours(organizationHours)}).
              </p>
            )}
            {ownHours && (
              <div className="space-y-3 rounded-md border p-3">
                <div className="flex flex-wrap gap-1.5">
                  {DIAS.map((d) => {
                    const on = days.includes(d.iso);
                    return (
                      <button
                        key={d.iso}
                        type="button"
                        aria-pressed={on}
                        onClick={() =>
                          setDays((prev) =>
                            prev.includes(d.iso) ? prev.filter((x) => x !== d.iso) : [...prev, d.iso],
                          )
                        }
                        className={cn(
                          "rounded-[5px] border px-2.5 py-1 text-xs font-medium transition-colors",
                          on
                            ? "border-primary bg-primary/10 text-primary"
                            : "border-input text-muted-foreground hover:bg-accent",
                        )}
                      >
                        {d.label}
                      </button>
                    );
                  })}
                </div>
                <div className="flex flex-wrap items-end gap-3">
                  <div className="space-y-1.5">
                    <Label htmlFor="team-start">Início</Label>
                    <Input
                      id="team-start"
                      type="time"
                      value={start}
                      onChange={(e) => setStart(e.target.value)}
                      className="h-9 w-32"
                    />
                  </div>
                  <div className="space-y-1.5">
                    <Label htmlFor="team-end">Fim</Label>
                    <Input
                      id="team-end"
                      type="time"
                      value={end}
                      onChange={(e) => setEnd(e.target.value)}
                      className="h-9 w-32"
                    />
                  </div>
                </div>
                {start >= end && (
                  <p className="text-xs text-destructive">O início deve ser anterior ao fim.</p>
                )}
              </div>
            )}
          </div>

          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {teamErrorMessage(mutation.error)}
            </p>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" disabled={mutation.isPending} onClick={onClose}>
            Cancelar
          </Button>
          <Button disabled={!valid || mutation.isPending} onClick={() => mutation.mutate()}>
            {mutation.isPending ? "Salvando…" : "Salvar"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function DeleteTeamDialog({ team, onClose }: { team: Team; onClose: () => void }) {
  const queryClient = useQueryClient();
  const mutation = useMutation({
    mutationFn: () => api<void>(`/teams/${encodeURIComponent(team.id)}`, { method: "DELETE" }),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["teams"] }),
        queryClient.invalidateQueries({ queryKey: ["app-catalog"] }),
      ]);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(open) => !open && !mutation.isPending && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Excluir {team.name}?</DialogTitle>
          <DialogDescription>
            As {team.member_count === 1 ? "1 pessoa" : `${team.member_count} pessoas`} vinculadas
            voltam a ficar sem equipe e as{" "}
            {team.team_rule_count === 1 ? "1 regra própria" : `${team.team_rule_count} regras próprias`}{" "}
            de classificação são removidas — esses apps voltam a valer pela regra da organização. Os
            últimos 30 dias são recalculados.
          </DialogDescription>
        </DialogHeader>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {genericErrorMessage(mutation.error)}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" disabled={mutation.isPending} onClick={onClose}>
            Cancelar
          </Button>
          <Button variant="destructive" disabled={mutation.isPending} onClick={() => mutation.mutate()}>
            {mutation.isPending ? "Excluindo…" : "Excluir equipe"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Composição da equipe. O PUT é DECLARATIVO: a lista enviada passa a ser a
 * composição exata. Uma pessoa está em no máximo uma equipe, então adicionar
 * alguém que estava em outra equipe a MOVE.
 */
function MembersDialog({ team, admin, onClose }: { team: Team; admin: boolean; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [sids, setSids] = useState<string[]>([]);
  const [novo, setNovo] = useState("");
  const [result, setResult] = useState<SetTeamMembersResponse | null>(null);

  const membersQuery = useQuery({
    queryKey: ["teams", team.id, "members"],
    queryFn: () => api<TeamMembersResponse>(`/teams/${encodeURIComponent(team.id)}/members`),
  });

  // nomes já resolvidos pelo servidor; o estado local guarda só os SIDs
  const nameBySid = useMemo(() => {
    const map = new Map<string, string>();
    for (const m of membersQuery.data?.items ?? []) map.set(m.windows_sid, m.display_name);
    for (const m of result?.items ?? []) map.set(m.windows_sid, m.display_name);
    return map;
  }, [membersQuery.data, result]);

  useEffect(() => {
    if (membersQuery.data !== undefined) {
      setSids(membersQuery.data.items.map((m) => m.windows_sid));
    }
  }, [membersQuery.data]);

  const mutation = useMutation({
    mutationFn: () =>
      api<SetTeamMembersResponse>(`/teams/${encodeURIComponent(team.id)}/members`, {
        method: "PUT",
        body: { windows_sids: sids },
      }),
    onSuccess: async (data) => {
      setResult(data);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["teams"] }),
        queryClient.invalidateQueries({ queryKey: ["dashboard"] }),
      ]);
    },
  });

  function adicionar(): void {
    const sid = novo.trim();
    if (sid.length === 0 || sids.includes(sid)) return;
    setSids((prev) => [...prev, sid]);
    setNovo("");
  }

  return (
    <Dialog open onOpenChange={(open) => !open && !mutation.isPending && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Pessoas de {team.name}</DialogTitle>
          <DialogDescription>
            A identidade da pessoa é o SID do Windows — o mesmo que aparece em Colaboradores e na
            página da pessoa. Uma pessoa pertence a uma equipe por vez: adicionar alguém que já
            está em outra equipe a move para esta.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          {membersQuery.isPending ? (
            <Skeleton className="h-20 w-full" />
          ) : sids.length === 0 ? (
            <p className="text-sm text-muted-foreground">Nenhuma pessoa vinculada.</p>
          ) : (
            <ul className="max-h-56 space-y-1 overflow-y-auto">
              {sids.map((sid) => (
                <li key={sid} className="flex items-center justify-between gap-2 rounded-md border px-3 py-1.5">
                  <span className="min-w-0">
                    <span className="block truncate text-sm">{nameBySid.get(sid) ?? sid}</span>
                    <span className="block truncate text-xs text-muted-foreground">{sid}</span>
                  </span>
                  {admin && (
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={`Remover ${nameBySid.get(sid) ?? sid}`}
                      onClick={() => setSids((prev) => prev.filter((s) => s !== sid))}
                    >
                      Remover
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}

          {admin && (
            <div className="flex flex-wrap items-end gap-2">
              <div className="min-w-[16rem] flex-1 space-y-1.5">
                <Label htmlFor="team-sid">Adicionar pessoa pelo SID</Label>
                <Input
                  id="team-sid"
                  value={novo}
                  placeholder="S-1-5-21-…"
                  onChange={(e) => setNovo(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") {
                      e.preventDefault();
                      adicionar();
                    }
                  }}
                  className={inputClass}
                />
              </div>
              <Button variant="outline" onClick={adicionar} disabled={novo.trim().length === 0}>
                Adicionar
              </Button>
            </div>
          )}

          {result !== null && (
            <p className="text-sm text-muted-foreground">
              {result.added} vinculada(s), {result.removed} desvinculada(s).{" "}
              {result.reaggregation_enqueued > 0
                ? `${result.reaggregation_enqueued} par(es) (dispositivo, dia) enfileirados para recálculo.`
                : "Nada a recalcular no período recente."}
            </p>
          )}
          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {genericErrorMessage(mutation.error)}
            </p>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" disabled={mutation.isPending} onClick={onClose}>
            Fechar
          </Button>
          {admin && (
            <Button disabled={mutation.isPending} onClick={() => mutation.mutate()}>
              {mutation.isPending ? "Salvando…" : "Salvar composição"}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// -----------------------------------------------------------------------------
// Feriados + base de capacidade
// -----------------------------------------------------------------------------

function FeriadosCard({ admin }: { admin: boolean }) {
  const queryClient = useQueryClient();
  const anoAtual = new Date().getFullYear();
  const [year, setYear] = useState(anoAtual);
  const [date, setDate] = useState("");
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);

  const holidaysQuery = useQuery({
    queryKey: ["holidays", year],
    queryFn: () => api<HolidayListResponse>(`/organization/holidays?year=${year}`),
  });
  const items = holidaysQuery.data?.items ?? [];
  const suggestions = holidaysQuery.data?.suggestions ?? [];

  async function invalidate(): Promise<void> {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["holidays"] }),
      // a base de capacidade depende do calendário
      queryClient.invalidateQueries({ queryKey: ["capacity"] }),
    ]);
  }

  const criar = useMutation({
    mutationFn: (holiday: Holiday) =>
      api<Holiday>("/organization/holidays", { method: "POST", body: holiday }),
    onSuccess: async () => {
      setDate("");
      setName("");
      setError(null);
      await invalidate();
    },
    onError: (err) => setError(genericErrorMessage(err)),
  });

  const remover = useMutation({
    mutationFn: (d: string) =>
      api<void>(`/organization/holidays/${encodeURIComponent(d)}`, { method: "DELETE" }),
    onSuccess: invalidate,
    onError: (err) => setError(genericErrorMessage(err)),
  });

  const semear = useMutation({
    mutationFn: () =>
      api<SeedHolidaysResponse>("/organization/holidays/seed", {
        method: "POST",
        body: { years: [year, year + 1] },
      }),
    onSuccess: invalidate,
    onError: (err) => setError(genericErrorMessage(err)),
  });

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1.5">
            <CardTitle className="text-base">Feriados</CardTitle>
            <CardDescription>
              O dia de feriado sai do denominador da capacidade utilizada — sem ele, uma semana com
              feriado aparece com a capacidade subestimada. Feriado não altera nenhum dado medido.
            </CardDescription>
          </div>
          <label className="flex items-center gap-2 text-sm">
            <CalendarDays className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            <span className="text-muted-foreground">Ano</span>
            <select
              aria-label="Ano do calendário"
              value={year}
              onChange={(e) => setYear(Number(e.target.value))}
              className="h-9 rounded-md border border-input bg-card px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {[anoAtual - 1, anoAtual, anoAtual + 1].map((y) => (
                <option key={y} value={y}>
                  {y}
                </option>
              ))}
            </select>
          </label>
        </div>
      </CardHeader>

      <div className="space-y-4 px-6 pb-6">
        {error !== null && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}

        {holidaysQuery.isPending ? (
          <Skeleton className="h-24 w-full" />
        ) : items.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            Nenhum feriado cadastrado em {year}.{" "}
            {admin && "Use “Semear calendário nacional” para começar."}
          </p>
        ) : (
          <ul className="grid gap-1 sm:grid-cols-2">
            {items.map((h) => (
              <li key={h.date} className="flex items-center justify-between gap-2 rounded-md border px-3 py-1.5">
                <span className="min-w-0">
                  <span className="block truncate text-sm">{h.name}</span>
                  <span className="block text-xs tabular-nums text-muted-foreground">{h.date}</span>
                </span>
                {admin && (
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={`Remover ${h.name}`}
                    disabled={remover.isPending}
                    onClick={() => remover.mutate(h.date)}
                  >
                    <Trash2 className="h-4 w-4 text-destructive" aria-hidden />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}

        {admin && (
          <>
            <div className="flex flex-wrap items-end gap-2 border-t pt-4">
              <div className="space-y-1.5">
                <Label htmlFor="holiday-date">Data</Label>
                <Input
                  id="holiday-date"
                  type="date"
                  value={date}
                  onChange={(e) => setDate(e.target.value)}
                  className="h-9 w-44"
                />
              </div>
              <div className="min-w-[14rem] flex-1 space-y-1.5">
                <Label htmlFor="holiday-name">Nome</Label>
                <Input
                  id="holiday-name"
                  value={name}
                  maxLength={120}
                  placeholder="ex.: Aniversário da cidade"
                  onChange={(e) => setName(e.target.value)}
                  className={inputClass}
                />
              </div>
              <Button
                variant="outline"
                disabled={date === "" || name.trim().length === 0 || criar.isPending}
                onClick={() => criar.mutate({ date, name: name.trim() })}
              >
                Adicionar
              </Button>
              <Button variant="outline" disabled={semear.isPending} onClick={() => semear.mutate()}>
                {semear.isPending ? "Semeando…" : "Semear calendário nacional"}
              </Button>
            </div>

            {suggestions.length > 0 && (
              <div className="space-y-2 rounded-md border bg-muted/40 p-3">
                <p className="text-xs text-muted-foreground">
                  Carnaval e Corpus Christi são <strong>ponto facultativo</strong> federal, não
                  feriado: não são semeados porque muita empresa trabalha nesses dias. Se a sua não
                  trabalha, adicione-os aqui.
                </p>
                <div className="flex flex-wrap gap-2">
                  {suggestions.map((s) => (
                    <Button
                      key={s.date}
                      variant="outline"
                      size="sm"
                      disabled={criar.isPending}
                      onClick={() => criar.mutate(s)}
                    >
                      <Plus className="mr-1.5 h-3.5 w-3.5" aria-hidden />
                      {s.name} ({s.date})
                    </Button>
                  ))}
                </div>
              </div>
            )}
          </>
        )}

        <CapacityPreview year={year} />
      </div>
    </Card>
  );
}

/**
 * Prévia da base de capacidade do mês corrente, por escopo. É a MESMA fonte
 * (`GET /teams/capacity`) que o cartão "Equipes lado a lado" da Visão Geral vai
 * consumir para trocar o denominador que ele hoje deriva no cliente sem
 * feriados — ver o comentário no TeamsController.Capacity do backend. Aqui ela
 * serve de conferência: o gestor vê quantos dias úteis sobraram depois dos
 * feriados antes de olhar a capacidade no painel.
 */
function CapacityPreview({ year }: { year: number }) {
  const range = useMemo(() => {
    const hoje = new Date();
    // mês corrente quando o ano selecionado é o de hoje; senão, janeiro do ano
    const base = hoje.getFullYear() === year ? hoje : new Date(Date.UTC(year, 0, 1));
    const first = new Date(Date.UTC(base.getUTCFullYear(), base.getUTCMonth(), 1));
    const last = new Date(Date.UTC(base.getUTCFullYear(), base.getUTCMonth() + 1, 0));
    const iso = (d: Date): string => d.toISOString().slice(0, 10);
    return { from: iso(first), to: iso(last) };
  }, [year]);

  const capacityQuery = useQuery({
    queryKey: ["capacity", range.from, range.to],
    queryFn: () => api<CapacityResponse>(`/teams/capacity?from=${range.from}&to=${range.to}`),
  });
  const scopes = capacityQuery.data?.scopes ?? [];
  if (scopes.length === 0) return null;

  return (
    <div className="space-y-2 border-t pt-4">
      <p className="text-sm font-medium">
        Base de capacidade de {range.from} a {range.to}
      </p>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b text-left text-xs uppercase tracking-wide text-muted-foreground">
              <th className="py-2 pr-3 font-medium">Escopo</th>
              <th className="px-3 py-2 text-right font-medium">Jornada (h/dia)</th>
              <th className="px-3 py-2 text-right font-medium">Dias úteis</th>
              <th className="px-3 py-2 text-right font-medium">Feriados descontados</th>
              <th className="py-2 pl-3 text-right font-medium">Capacidade por pessoa</th>
            </tr>
          </thead>
          <tbody>
            {scopes.map((s) => (
              <tr key={s.team_id ?? "org"} className="border-b last:border-b-0">
                <td className="py-2 pr-3">
                  {s.scope_label}
                  {s.scope_type === "organization" && (
                    <span className="ml-2 text-xs text-muted-foreground">organização</span>
                  )}
                </td>
                <td className="px-3 py-2 text-right tabular-nums">{s.daily_hours}</td>
                <td className="px-3 py-2 text-right tabular-nums">{s.business_days}</td>
                <td className="px-3 py-2 text-right tabular-nums">{s.holidays_excluded}</td>
                <td className="py-2 pl-3 text-right tabular-nums">
                  {Math.round(s.capacity_seconds_per_person / 3600)} h
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-xs text-muted-foreground">
        Capacidade utilizada = tempo ativo ÷ (capacidade por pessoa × pessoas da equipe). Os dias
        úteis acima já estão sem os feriados.
      </p>
    </div>
  );
}
