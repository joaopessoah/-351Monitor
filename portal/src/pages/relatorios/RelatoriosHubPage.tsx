import { Link } from "react-router-dom";
import { ArrowRight, Clock } from "lucide-react";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

const reports = [
  {
    to: "/relatorios/jornada",
    title: "Jornada",
    description:
      "Uma linha por dispositivo × dia: primeiro e último evento, tempo ligada, ativa, ociosa e bloqueada.",
  },
  {
    to: "/relatorios/uso",
    title: "Uso de aplicativos",
    description:
      "Relatório tabular de uso por aplicativo, categoria, dispositivo ou usuário, com a aba de atividade fora do horário de trabalho.",
  },
  {
    to: "/relatorios/exportacoes",
    title: "Exportações",
    description: "Histórico das exportações CSV dos últimos 30 dias: quem gerou, quando e com quais filtros.",
  },
] as const;

export function RelatoriosHubPage() {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Relatórios</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Relatórios gerenciais de uso das estações de trabalho, com exportação em CSV.
        </p>
      </div>
      <div className="grid gap-4 md:grid-cols-3">
        {reports.map((r) => (
          <Link key={r.to} to={r.to} className="group">
            <Card className="h-full transition-colors group-hover:border-primary/50">
              <CardHeader>
                <CardTitle className="flex items-center justify-between text-base">
                  {r.title}
                  <ArrowRight className="h-4 w-4 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
                </CardTitle>
                <CardDescription>{r.description}</CardDescription>
              </CardHeader>
            </Card>
          </Link>
        ))}
      </div>

      {/* Atalho do DoD F3 ("quem ficou mais tempo ocioso esta semana?" em
          < 3 cliques): deep-link para o Uso já agrupado por dispositivo e
          ordenado por tempo ocioso - Relatórios (1) + atalho (2). */}
      <Card>
        <div className="flex flex-wrap items-center gap-2 px-4 py-3 text-sm">
          <Clock className="h-4 w-4 text-muted-foreground" aria-hidden />
          <span className="text-muted-foreground">Atalho:</span>
          <Link
            to="/relatorios/uso?group_by=device&sort=seconds_idle&dir=desc"
            className="font-medium text-primary underline-offset-2 hover:underline"
          >
            Quem ficou mais tempo ocioso esta semana?
          </Link>
          <span aria-hidden className="text-muted-foreground">·</span>
          <Link
            to="/relatorios/uso?aba=fora-do-horario"
            className="font-medium text-primary underline-offset-2 hover:underline"
          >
            Quanta atividade ficou fora do horário de trabalho?
          </Link>
        </div>
      </Card>
    </div>
  );
}
