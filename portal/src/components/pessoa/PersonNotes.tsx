// =============================================================================
// ANOTAÇÃO DE PERÍODO e CONTESTAÇÃO DE CLASSIFICAÇÃO (F6, decisão 6 do spec de
// 07/09/2026) - `GET/POST /people/{sid}/notes` e `PATCH .../notes/{id}`.
//
// POR QUE ESTE BLOCO EXISTE: a medição sabe QUANTO tempo a máquina ficou ativa e
// o que estava na tela - nunca POR QUE. Reunião presencial, treinamento, visita
// a cliente, dia de máquina em manutenção: tudo isso aparece como ausência de
// atividade, e o número sozinho mente por omissão. Aqui a pessoa (ou quem
// acompanha) escreve o contexto, e discorda por escrito da classificação de um
// aplicativo. O gestor (Admin+) responde: aceita ou recusa, sempre com texto.
//
// ACEITAR NÃO MUDA NENHUM NÚMERO. A frase que a tela mostra depois da revisão
// vem do SERVIDOR (`effect` do PATCH) exatamente para isso: o gestor precisa
// saber, no clique, que o caminho para o tempo passar a ser contado de outra
// forma é remapear a categoria em Configurações > Classificação e recalcular o
// histórico. Sem essa frase ele ficaria esperando um número que não vai mudar.
//
// IDENTIDADE: esta página navega por `device_user_id` (o contrato de
// `GET /device-users/{id}` não devolve o windows_sid - ver PersonIdentityCard).
// O segmento {sid} da rota de anotações aceita AS DUAS formas de propósito e
// resolve o SID no servidor, pelo par (tenant, id), aplicando a mesclagem de
// `people`. Nada de adivinhar SID por nome no cliente, que anotaria a pessoa
// errada.
//
// VOCABULÁRIO: nenhum julgamento, nenhuma linguagem de ponto. "Registrada em",
// jamais "batida"; "período", jamais "jornada cumprida"; a recusa é uma
// resposta fundamentada, não uma reprovação.
// =============================================================================

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Check, MessageSquarePlus, Scale, X } from "lucide-react";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import type {
  AppCatalogResponse,
  PersonNote,
  PersonNoteCreateRequest,
  PersonNoteKind,
  PersonNoteReviewRequest,
  PersonNoteReviewResponse,
  PersonNotesResponse,
} from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";

const MAX_BODY = 2000;

/** Mesmas classes do Input, para o textarea acompanhar o resto do formulário. */
const TEXTAREA_CLASS =
  "flex w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-sm " +
  "placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 " +
  "focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50";

const KIND_LABEL: Record<PersonNoteKind, string> = {
  anotacao: "Anotação de período",
  contestacao: "Contestação de classificação",
};

const KIND_HINT: Record<PersonNoteKind, string> = {
  anotacao:
    "Contexto de um período em que a atividade no computador não conta a história toda - reunião presencial, treinamento, visita a cliente, manutenção da máquina.",
  contestacao:
    "Discordância sobre como um aplicativo foi classificado. Fica registrada e vira insumo da curadoria; os números já agregados não mudam por causa dela.",
};

const STATUS_LABEL: Record<PersonNote["status"], string> = {
  aberta: "Aguardando revisão",
  aceita: "Aceita",
  recusada: "Recusada",
};

const STATUS_CLASS: Record<PersonNote["status"], string> = {
  aberta: "border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-400",
  aceita: "border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400",
  recusada: "border-muted-foreground/30 bg-muted text-muted-foreground",
};

/**
 * `datetime-local` entrega "yyyy-MM-ddTHH:mm" no fuso do NAVEGADOR; o contrato
 * quer ISO 8601 com fuso. A conversão assume o fuso de quem digita - o normal,
 * porque quem anota "reunião das 14h" fala do relógio da própria parede. Se o
 * fuso do navegador diferir do da organização, a hora exibida na lista (sempre
 * no fuso do tenant) sai deslocada, e é por isso que a lista imprime o fuso.
 */
function toIso(local: string): string | null {
  if (local.length === 0) return null;
  const parsed = new Date(local);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/** Valor inicial do formulário: o dia do recorte, das 9h às 18h no relógio local. */
function defaultLocal(day: string, hour: number): string {
  return `${day}T${String(hour).padStart(2, "0")}:00`;
}

export function PersonNotes({
  personKey,
  from,
  to,
  timezone,
  canReview,
}: {
  /** device_user_id da rota OU windows_sid - o servidor resolve os dois. */
  personKey: string;
  /** Recorte do período global da página, yyyy-MM-dd no fuso do tenant. */
  from: string | null;
  to: string | null;
  timezone: string | null;
  /** Admin+ (isAdmin de lib/roles): só o gestor aceita ou recusa. */
  canReview: boolean;
}) {
  const enabled = personKey.length > 0 && from !== null && to !== null;

  const notesQuery = useQuery({
    queryKey: ["person-notes", personKey, from, to],
    queryFn: () =>
      api<PersonNotesResponse>(
        `/people/${encodeURIComponent(personKey)}/notes?from=${from ?? ""}&to=${to ?? ""}`,
      ),
    enabled,
  });

  const notes = notesQuery.data?.items ?? [];

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Anotações e contestações do período</CardTitle>
        <CardDescription>
          O computador registra quanto tempo e em quais aplicativos - nunca o motivo. Este espaço
          guarda o contexto que falta e as discordâncias sobre a classificação, com a resposta de
          quem revisou. Nada aqui altera os números já medidos.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        {enabled && (
          <NoteForm personKey={personKey} from={from!} to={to!} disabled={notesQuery.isError} />
        )}

        {notesQuery.isError ? (
          <div className="flex flex-col items-center gap-3 py-6 text-center">
            <AlertTriangle className="h-6 w-6 text-destructive" aria-hidden />
            <p className="text-sm text-muted-foreground">{genericErrorMessage(notesQuery.error)}</p>
            <Button variant="outline" size="sm" onClick={() => void notesQuery.refetch()}>
              Tentar novamente
            </Button>
          </div>
        ) : notesQuery.isPending ? (
          <div className="space-y-3">
            <Skeleton className="h-20 w-full" />
            <Skeleton className="h-20 w-full" />
          </div>
        ) : notes.length === 0 ? (
          <p className="rounded-md border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">
            Nenhuma anotação registrada neste período.
          </p>
        ) : (
          <ul className="space-y-3">
            {notes.map((note) => (
              <li key={note.id}>
                <NoteCard
                  note={note}
                  personKey={personKey}
                  timezone={timezone}
                  canReview={canReview}
                />
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

// -----------------------------------------------------------------------------
// Formulário de criação (Viewer+)
// -----------------------------------------------------------------------------

function NoteForm({
  personKey,
  from,
  to,
  disabled,
}: {
  personKey: string;
  from: string;
  to: string;
  disabled: boolean;
}) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [kind, setKind] = useState<PersonNoteKind>("anotacao");
  const [startedAt, setStartedAt] = useState(() => defaultLocal(from, 9));
  const [endedAt, setEndedAt] = useState(() => defaultLocal(to, 18));
  const [body, setBody] = useState("");
  const [appQuery, setAppQuery] = useState("");
  const [appId, setAppId] = useState<string | null>(null);

  // O catálogo só é consultado quando o texto tem 2+ caracteres: a lista inteira
  // não ajuda ninguém a achar um aplicativo e custaria uma consulta por abertura.
  const appsQuery = useQuery({
    queryKey: ["app-catalog", "notes", appQuery],
    queryFn: () => api<AppCatalogResponse>(`/app-catalog?q=${encodeURIComponent(appQuery)}`),
    enabled: kind === "contestacao" && appQuery.trim().length >= 2,
    staleTime: 60_000,
  });

  const appOptions = useMemo(() => (appsQuery.data?.items ?? []).slice(0, 8), [appsQuery.data]);
  const selectedApp = useMemo(
    () => appOptions.find((a) => a.app_id === appId) ?? null,
    [appOptions, appId],
  );

  const mutation = useMutation({
    mutationFn: (payload: PersonNoteCreateRequest) =>
      api<PersonNote>(`/people/${encodeURIComponent(personKey)}/notes`, {
        method: "POST",
        body: payload,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["person-notes", personKey] });
      setBody("");
      setAppId(null);
      setAppQuery("");
      setOpen(false);
    },
  });

  const trimmed = body.trim();
  const isoStart = toIso(startedAt);
  const isoEnd = toIso(endedAt);
  const periodValid = isoStart !== null && isoEnd !== null && isoEnd >= isoStart;
  const canSubmit = trimmed.length > 0 && trimmed.length <= MAX_BODY && periodValid && !mutation.isPending;

  if (!open) {
    return (
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-xs text-muted-foreground">
          Quem enxerga esta pessoa pode registrar contexto; a revisão é de quem administra.
        </p>
        <Button variant="outline" size="sm" disabled={disabled} onClick={() => setOpen(true)}>
          <MessageSquarePlus className="h-4 w-4" aria-hidden />
          Registrar anotação
        </Button>
      </div>
    );
  }

  return (
    <form
      className="space-y-4 rounded-md border p-4"
      onSubmit={(e) => {
        e.preventDefault();
        if (!canSubmit) return;
        mutation.mutate({
          kind,
          started_at: isoStart!,
          ended_at: isoEnd!,
          // app_id só existe na contestação: o servidor recusa numa anotação.
          app_id: kind === "contestacao" ? appId : null,
          body: trimmed,
        });
      }}
    >
      <div role="group" aria-label="Tipo do registro" className="flex flex-wrap gap-2">
        {(["anotacao", "contestacao"] as const).map((option) => (
          <button
            key={option}
            type="button"
            aria-pressed={kind === option}
            onClick={() => {
              setKind(option);
              if (option === "anotacao") setAppId(null);
            }}
            className={cn(
              "rounded-md border px-3 py-1.5 text-xs font-medium transition-colors",
              kind === option
                ? "border-primary/40 bg-primary/10 text-primary"
                : "border-input text-muted-foreground hover:bg-accent hover:text-accent-foreground",
            )}
          >
            {KIND_LABEL[option]}
          </button>
        ))}
      </div>
      <p className="text-xs text-muted-foreground">{KIND_HINT[kind]}</p>

      <div className="grid gap-3 sm:grid-cols-2">
        <label className="space-y-1 text-xs font-medium text-muted-foreground">
          Início do período
          <Input
            type="datetime-local"
            value={startedAt}
            onChange={(e) => setStartedAt(e.target.value)}
            className="h-9"
            required
          />
        </label>
        <label className="space-y-1 text-xs font-medium text-muted-foreground">
          Fim do período
          <Input
            type="datetime-local"
            value={endedAt}
            min={startedAt}
            onChange={(e) => setEndedAt(e.target.value)}
            className="h-9"
            required
          />
        </label>
      </div>
      {!periodValid && (
        <p className="text-xs text-destructive">
          O fim do período precisa ser igual ou posterior ao início.
        </p>
      )}

      {kind === "contestacao" && (
        <div className="space-y-2">
          <label className="space-y-1 text-xs font-medium text-muted-foreground">
            Aplicativo contestado (opcional)
            <Input
              value={appQuery}
              onChange={(e) => {
                setAppQuery(e.target.value);
                setAppId(null);
              }}
              placeholder="Digite parte do nome do aplicativo"
              className="h-9"
            />
          </label>
          {selectedApp !== null ? (
            <p className="text-xs text-muted-foreground">
              Selecionado:{" "}
              <span className="font-medium text-foreground">{selectedApp.display_name}</span> (
              {selectedApp.process_name}).{" "}
              <button
                type="button"
                className="underline underline-offset-2"
                onClick={() => setAppId(null)}
              >
                limpar
              </button>
            </p>
          ) : (
            appOptions.length > 0 && (
              <ul className="flex flex-wrap gap-2">
                {appOptions.map((app) => (
                  <li key={app.app_id}>
                    <button
                      type="button"
                      onClick={() => setAppId(app.app_id)}
                      className="rounded-md border border-input px-2 py-1 text-xs text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                    >
                      {app.display_name}
                    </button>
                  </li>
                ))}
              </ul>
            )
          )}
        </div>
      )}

      <label className="block space-y-1 text-xs font-medium text-muted-foreground">
        {kind === "anotacao" ? "O que aconteceu neste período" : "Por que a classificação não descreve este uso"}
        <textarea
          value={body}
          onChange={(e) => setBody(e.target.value)}
          maxLength={MAX_BODY}
          rows={3}
          className={TEXTAREA_CLASS}
          placeholder={
            kind === "anotacao"
              ? "Ex.: reunião presencial com o cliente, fora da estação de trabalho."
              : "Ex.: este aplicativo é usado para atendimento, não para uso pessoal."
          }
          required
        />
      </label>
      <p className="text-xs text-muted-foreground">
        {trimmed.length}/{MAX_BODY} caracteres. O registro nasce aguardando revisão.
      </p>

      {mutation.isError && (
        <p role="alert" className="text-sm text-destructive">
          {genericErrorMessage(mutation.error)}
        </p>
      )}

      <div className="flex flex-wrap gap-2">
        <Button type="submit" size="sm" disabled={!canSubmit}>
          <Check className="h-4 w-4" aria-hidden />
          {mutation.isPending ? "Registrando…" : "Registrar"}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={mutation.isPending}
          onClick={() => setOpen(false)}
        >
          <X className="h-4 w-4" aria-hidden />
          Cancelar
        </Button>
      </div>
    </form>
  );
}

// -----------------------------------------------------------------------------
// Uma anotação, com a revisão do gestor quando ele pode revisar
// -----------------------------------------------------------------------------

function NoteCard({
  note,
  personKey,
  timezone,
  canReview,
}: {
  note: PersonNote;
  personKey: string;
  timezone: string | null;
  canReview: boolean;
}) {
  const queryClient = useQueryClient();
  const [reviewNote, setReviewNote] = useState("");
  const [effect, setEffect] = useState<string | null>(null);
  const [refusing, setRefusing] = useState(false);

  const mutation = useMutation({
    mutationFn: (payload: PersonNoteReviewRequest) =>
      api<PersonNoteReviewResponse>(
        `/people/${encodeURIComponent(personKey)}/notes/${note.id}`,
        { method: "PATCH", body: payload },
      ),
    onSuccess: (result) => {
      // A frase vem do SERVIDOR: é ela que diz ao gestor que aceitar registra,
      // mas não reescreve nenhum agregado.
      setEffect(result.effect);
      void queryClient.invalidateQueries({ queryKey: ["person-notes", personKey] });
    },
  });

  const when = (iso: string): string =>
    timezone !== null ? formatDateTime(iso, timezone) : iso;

  const trimmedReview = reviewNote.trim();
  const showReview = canReview && note.status === "aberta";

  return (
    <div className="space-y-3 rounded-md border p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-sm font-medium">{KIND_LABEL[note.kind]}</span>
        <span
          className={cn(
            "rounded-full border px-2 py-0.5 text-xs font-medium",
            STATUS_CLASS[note.status],
          )}
        >
          {STATUS_LABEL[note.status]}
        </span>
      </div>

      <p className="text-xs text-muted-foreground">
        Período de {when(note.started_at)} a {when(note.ended_at)}
        {note.app_display_name !== null && (
          <>
            {" · aplicativo "}
            <span className="font-medium text-foreground">{note.app_display_name}</span>
          </>
        )}
      </p>

      <p className="whitespace-pre-wrap text-sm">{note.body}</p>

      <p className="text-xs text-muted-foreground">
        Registrada por {note.created_by_name ?? "usuário removido"} em {when(note.created_at)}.
      </p>

      {note.reviewed_at !== null && (
        <div className="rounded-md bg-muted/50 px-3 py-2 text-xs text-muted-foreground">
          <p>
            Revisada por {note.reviewed_by_name ?? "usuário removido"} em {when(note.reviewed_at)}.
          </p>
          {note.review_note !== null && (
            <p className="mt-1 whitespace-pre-wrap text-foreground">{note.review_note}</p>
          )}
        </div>
      )}

      {effect !== null && (
        <div
          role="status"
          className="flex items-start gap-2 rounded-md border bg-muted/50 px-3 py-2 text-xs text-muted-foreground"
        >
          <Scale className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <span>{effect}</span>
        </div>
      )}

      {showReview && (
        <div className="space-y-2 border-t pt-3">
          <label className="block space-y-1 text-xs font-medium text-muted-foreground">
            Resposta de quem revisa {refusing ? "(obrigatória na recusa)" : "(opcional ao aceitar)"}
            <textarea
              value={reviewNote}
              onChange={(e) => setReviewNote(e.target.value)}
              maxLength={MAX_BODY}
              rows={2}
              className={TEXTAREA_CLASS}
              placeholder="O que foi considerado nesta decisão."
            />
          </label>

          {mutation.isError && (
            <p role="alert" className="text-sm text-destructive">
              {genericErrorMessage(mutation.error)}
            </p>
          )}

          <div className="flex flex-wrap gap-2">
            <Button
              size="sm"
              disabled={mutation.isPending}
              onClick={() =>
                mutation.mutate({
                  status: "aceita",
                  review_note: trimmedReview.length > 0 ? trimmedReview : null,
                })
              }
            >
              <Check className="h-4 w-4" aria-hidden />
              Aceitar
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={mutation.isPending}
              onClick={() => {
                if (trimmedReview.length === 0) {
                  // Recusar sem dizer por quê é o oposto do que esta feature faz:
                  // o primeiro clique pede a resposta, o segundo envia.
                  setRefusing(true);
                  return;
                }
                mutation.mutate({ status: "recusada", review_note: trimmedReview });
              }}
            >
              <X className="h-4 w-4" aria-hidden />
              Recusar
            </Button>
          </div>
          {refusing && trimmedReview.length === 0 && (
            <p className="text-xs text-destructive">
              Escreva a resposta antes de recusar: quem registrou precisa saber o motivo.
            </p>
          )}
          <p className="text-xs text-muted-foreground">
            A decisão fica registrada com o seu nome e não é reescrita depois. Aceitar não altera
            os números já agregados.
          </p>
        </div>
      )}
    </div>
  );
}
