import { cn } from "@/lib/utils";

export const MIN_PASSWORD_LENGTH = 12;

type Strength = 0 | 1 | 2 | 3;

function measure(password: string): Strength {
  if (password.length === 0) return 0;
  if (password.length < MIN_PASSWORD_LENGTH) return 1;
  let variety = 0;
  if (/[a-z]/.test(password)) variety += 1;
  if (/[A-Z]/.test(password)) variety += 1;
  if (/\d/.test(password)) variety += 1;
  if (/[^a-zA-Z0-9]/.test(password)) variety += 1;
  if (password.length >= 16 && variety >= 3) return 3;
  if (variety >= 2) return 2;
  return 1;
}

const labels: Record<Strength, string> = {
  0: "",
  1: "Fraca",
  2: "Média",
  3: "Forte",
};

const barColors: Record<Strength, string> = {
  0: "bg-muted",
  1: "bg-destructive",
  2: "bg-viz-improdutivo",
  3: "bg-viz-produtivo",
};

/** Medidor de força de senha (mínimo de 12 caracteres — N23). */
export function PasswordStrength({ password }: { password: string }) {
  const strength = measure(password);
  return (
    <div className="space-y-1" aria-live="polite">
      <div className="flex gap-1">
        {[1, 2, 3].map((step) => (
          <div
            key={step}
            className={cn(
              "h-1.5 flex-1 rounded-full bg-muted",
              strength >= step && barColors[strength],
            )}
          />
        ))}
      </div>
      <div className="flex justify-between text-xs text-muted-foreground">
        <span>Mínimo de {MIN_PASSWORD_LENGTH} caracteres</span>
        {strength > 0 && <span>{labels[strength]}</span>}
      </div>
    </div>
  );
}
