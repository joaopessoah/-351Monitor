import { Link } from "react-router-dom";

export function NotFoundPage() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 p-6 text-center">
      <h1 className="text-3xl font-semibold tracking-tight">Página não encontrada</h1>
      <p className="text-sm text-muted-foreground">
        O endereço acessado não existe ou foi movido.
      </p>
      <Link to="/visao-geral" className="text-sm text-primary underline underline-offset-4">
        Ir para a Visão Geral
      </Link>
    </div>
  );
}
