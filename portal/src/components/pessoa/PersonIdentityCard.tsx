// =============================================================================
// Bloco de identidade da PESSOA (Admin+, F6 - decisão 6 do spec de 07/09/2026:
// "página do colaborador entra, com os próprios dados"): apelido e mesclagem
// de registros da mesma pessoa, via PATCH /people/{sid}.
//
// BLOQUEADO POR CONTRATO (verificado, não presumido): GET /device-users/{id}
// - o único dado que esta página tem sobre o titular - NÃO devolve
// windows_sid. Conferido em
// backend/src/M351.Api/Contracts/DeviceUserContracts.cs: DeviceUserResponse
// só tem Id/DeviceId/DeviceName/WindowsUsername/DisplayName/FirstSeenAt/
// LastSeenAt. Sem SID não há como chamar PATCH /people/{sid} com segurança -
// arriscaria mesclar ou renomear a PESSOA ERRADA. Por isso este bloco NÃO
// tenta adivinhar o SID por outro caminho (ex.: cruzar por nome, como a tela
// de Colaboradores faz na direção contrária, pessoa -> registro) e apenas
// explica a pendência. Quando o contrato do device-user passar a expor
// windows_sid, este componente ganha o formulário de apelido/mesclagem
// consumindo PersonPatchRequest (já definido no fim de lib/types.ts).
// =============================================================================

import { AlertTriangle } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function PersonIdentityCard() {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Identidade da pessoa</CardTitle>
        <CardDescription>
          Apelido e mesclagem de registros da mesma pessoa (Admin+) - diferente do nome deste
          registro específico, editável no cabeçalho acima.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="flex items-start gap-2 rounded-md border border-dashed p-4 text-sm text-muted-foreground">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <p>
            Ainda não disponível para este registro: a rota de apelido e mesclagem por pessoa
            (<code>PATCH /people/&#123;sid&#125;</code>) exige o identificador (SID) da pessoa, e o
            contrato de <code>GET /device-users</code> não o devolve hoje. Mesclar ou renomear sem
            esse identificador arriscaria afetar a pessoa errada, então este formulário fica
            pendente até o contrato do dispositivo passar a expor o SID.
          </p>
        </div>
      </CardContent>
    </Card>
  );
}
