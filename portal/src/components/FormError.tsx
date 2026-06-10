interface FormErrorProps {
  message: string | null | undefined;
}

/** Mensagem de erro inline de formulário (pt-BR, sem detalhes técnicos). */
export function FormError({ message }: FormErrorProps) {
  if (!message) return null;
  return (
    <p role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive">
      {message}
    </p>
  );
}
