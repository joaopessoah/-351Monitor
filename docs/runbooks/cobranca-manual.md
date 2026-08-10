# Runbook - Cobrança manual do piloto

> Cumpre a F5 (Piloto, Seção 10): "processo de cobrança manual operando com o relatório de
> cobráveis". Insumo do sistema: `GET /api/v1/billing/billable-devices` (papel **Owner**).
> No MVP a cobrança é **manual** (Pix/boleto + NFS-e). Não há gateway de pagamento nem cobrança
> recorrente automática - isso é roadmap (CONSIDERACOES Seção 6, "Billing").

Legenda usada neste runbook:

- **[SISTEMA]** o produto fornece/calcula isto. É fato verificável no código.
- **[DECISÃO DO JOÃO]** decisão comercial do dono do produto. Os números abaixo são a hipótese
  atual; ajuste conforme fechar com cada cliente. O sistema não os aplica sozinho.

---

## 1. Modelo comercial e planos

Origem: PROMPT-DESENVOLVIMENTO Seção 1 e CONSIDERACOES Seção 3 (tabela "Preço (hipótese)").

| Item | Valor (hipótese atual) | Quem define |
|---|---|---|
| Unidade de cobrança | **por dispositivo / mês** | [SISTEMA] o relatório conta dispositivos cobráveis no mês |
| Plano Essencial | **R$ 19,90** / device / mês | [DECISÃO DO JOÃO] |
| Plano Pro | **R$ 34,90** / device / mês | [DECISÃO DO JOÃO] |
| Piso de faturamento | **10 devices** (≈ R$ 199 no Essencial) | [DECISÃO DO JOÃO] |
| Plano anual | **~2 meses grátis** (hipótese) | [DECISÃO DO JOÃO] |
| Trial | **sem prazo fixo** (caso a caso), limitado a **25 devices**, onboarding assistido | [SISTEMA] limite de 25 devices é enforced no enroll (N24); a duração é controle comercial |
| Forma de pagamento (MVP) | **Pix ou boleto**, com **NFS-e** | [DECISÃO DO JOÃO] / processo manual |
| Criação de conta | **via backoffice** (sem signup self-service) | [SISTEMA] - DPA assinado é pré-condição de provisionamento |

Importante: os **preços e o piso são hipótese comercial**, não regra codificada. O sistema só
**conta** os dispositivos cobráveis; multiplicar pela tarifa, aplicar piso, desconto anual e
emitir a cobrança é trabalho manual do João (ou do comercial). O único limite que o sistema
**aplica de fato** é o teto de 25 devices no trial (recusa enroll acima disso).

A coluna `plan` da org (`trial | essencial | pro`) e `device_limit` existem no banco, mas no MVP
não geram fatura automática. Servem para o controle de trial e como referência da tarifa a
aplicar manualmente.

---

## 2. Relatório mensal de cobráveis (o que o sistema fornece)

Endpoint: `GET /api/v1/billing/billable-devices?month=YYYY-MM`
Autorização: papel **Owner** do tenant (um Admin recebe 403).

### 2.1 Como chamar (como Owner)

1. **Logar como Owner** para obter o token de acesso:
   `POST /api/v1/auth/login` com e-mail + senha do Owner → `{ access_token, expires_in }`.
   Se a conta tiver MFA habilitada, o login retorna `mfa_required` + token temporário e exige o
   segundo fator antes de devolver o `access_token`.
2. Chamar o relatório com `Authorization: Bearer <access_token>`.

Exemplo (PowerShell, contra o staging - ver memória `staging-acesso`; em produção troque a base):

```powershell
$base  = 'https://painel.2-25-193-15.sslip.io'
$mes   = '2026-05'   # mês fechado que você quer faturar (YYYY-MM)

# 1) login do Owner
$login = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" `
  -ContentType 'application/json' `
  -Body (@{ email = 'owner@cliente.com.br'; password = 'SENHA' } | ConvertTo-Json)
# (se a org tiver MFA, complete o desafio antes de ter o access_token)

# 2) relatório de cobráveis
$rel = Invoke-RestMethod -Method Get -Uri "$base/api/v1/billing/billable-devices?month=$mes" `
  -Headers @{ Authorization = "Bearer $($login.access_token)" }

"Mês: $($rel.month) - devices cobráveis: $($rel.deviceCount)"
$rel.criteria
$rel.items | Select-Object displayName, hostname, status, evidence, lastSeenAt | Format-Table -Auto
```

`month` é obrigatório no formato `YYYY-MM`. Mês **futuro** (no fuso do tenant) é rejeitado (400);
o mês **corrente** é aceito, mas ainda é parcial - só feche cobrança sobre mês encerrado.

### 2.2 O que cada campo significa

Envelope da resposta:

| Campo | Significado |
|---|---|
| `month` | o mês solicitado (`YYYY-MM`) |
| `deviceCount` | **total de dispositivos cobráveis** no mês - este é o número que entra na fatura (antes do piso) |
| `criteria` | texto legível da regra aplicada, incluindo o fuso do tenant e o aviso de quando gerar o relatório (ver 2.4) |
| `items[]` | um item por dispositivo cobrável |

Cada `item`:

| Campo | Significado |
|---|---|
| `deviceId` | id interno do dispositivo |
| `displayName` | nome de exibição (pode ser nulo; cai para o hostname) |
| `hostname` | nome da máquina |
| `status` | estado atual do dispositivo (nunca `archived` aqui - arquivados são excluídos) |
| `enrolledAt` | instante do registro (derivado do id do device; não há coluna `enrolled_at`) |
| `lastSeenAt` | último contato conhecido do dispositivo |
| `evidence` | **por que o dispositivo é cobrável** neste mês (ver 2.3) |

### 2.3 O campo `evidence` (por que o device contou)

Cobrável = dispositivo **não arquivado** com **pelo menos um sinal de uso** no mês, **no fuso do
tenant**. `evidence` reporta a **primeira** regra que casou, nesta ordem de prioridade:

| `evidence` | Significa |
|---|---|
| `events` | recebeu **eventos** no mês (uso normal: sessão, janela, ociosidade) |
| `enrolled` | foi **registrado (enroll)** dentro do mês - cobre o device recém-instalado e ainda silencioso |
| `keep_alive` | teve **último contato** no mês via lote vazio (keep-alive não gera eventos, só atualiza o `last_seen_at`) |

Notas importantes:

- **Arquivado (`archived`) é excluído** do relatório - não conta na fatura.
- **Revogado NÃO é excluído**: se o dispositivo usou o serviço no mês, ele conta (a regra só
  exclui arquivados). Revogar não é o mesmo que arquivar para efeito de cobrança.
- **Cuidado com a assimetria revogar vs. faturar (regra do trial ≠ regra do billing).** O teto de
  25 devices do trial, conferido no enroll, **desconta tanto `archived` quanto `revoked`** - então
  **revogar um device libera vaga para enrolar outro**. Já a contagem de cobráveis só desconta
  `archived`. Consequência: um device revogado deixa de ocupar cota de trial, mas **continua na
  fatura do mês em que foi usado**. Para tirar um device da cobrança do mês, **arquive-o antes do
  fechamento** (revogar sozinho não basta).
- A janela do mês é calculada no **fuso do tenant** (campo `timezone` da org), então um evento das
  23:30 do dia 31 (que em UTC já é dia 1 do mês seguinte) é contado no mês correto.

### 2.4 Janela de validade - gere e arquive logo após o fechamento do mês

[SISTEMA] O relatório **não é um snapshot congelado** de mês fechado: `status` e `last_seen_at`
são lidos no instante da execução. Consequências:

1. Um device cujo único sinal do mês seria `keep_alive` (só lotes vazios) **some** do relatório
   assim que contactar de novo num mês posterior, porque `last_seen_at` é uma coluna única e
   mutável.
2. **Arquivar** um device hoje o remove **retroativamente** de relatórios de meses passados.

Procedimento: **gere o relatório do mês logo após o fechamento (início do mês seguinte) e arquive
o resultado** (salve o JSON/PDF junto da fatura). Esse arquivo é a evidência de faturamento daquele
mês - não confie em reexecutar o mesmo `month` meses depois. (Congelar o sinal por mês é follow-up
de v1.1, exige migration.)

---

## 3. Conferir contra a contagem manual

A F5 pede que "o relatório de cobráveis **bata com a contagem manual** do mês". Como conferir:

1. **Total do sistema:** anote `deviceCount` do relatório do mês fechado.
2. **Contagem manual de referência:** no portal (Dispositivos / Equipe), conte os dispositivos
   **não arquivados** que tiveram atividade no mês. Para um piloto pequeno (10–80 devices) isso é
   viável de olho.
3. **Conferir item a item:** o `items[]` lista nome + hostname + `evidence`. Espera-se ver:
   - todo device com uso real → `evidence = events`;
   - device instalado no meio do mês mas ainda quieto → `evidence = enrolled`;
   - device que só mandou keep-alive (ligado, sem atividade registrada) → `evidence = keep_alive`.
4. **Diferenças esperadas (não são erro):**
   - device **arquivado** durante o mês: ausente do relatório (excluído por design);
   - device **revogado** mas que usou no mês: **presente** (conta);
   - device sem nenhum sinal no mês (desinstalado/desligado o mês todo): ausente.
5. Se o número do sistema e a contagem manual divergirem **fora** desses casos, investigar antes
   de faturar (ex.: device contado como `keep_alive` que você considerava inativo, ou device
   arquivado tarde demais). Em caso de dúvida real de regra, registre e me avise - a regra está
   documentada no `BillingController` e pode ser ajustada com teste.

A fatura usa `max(deviceCount, piso)` - ou seja, mesmo com menos de 10 devices cobráveis,
fatura-se o piso de 10 [DECISÃO DO JOÃO].

---

## 4. Régua de cobrança (sugestão - não está no sistema)

> [DECISÃO DO JOÃO] A spec **não define** uma régua de inadimplência. A proposta abaixo é uma
> **sugestão** sensata para o piloto; ajuste/forme contrato com cada cliente. **Nada disto é
> automatizado** - não há gateway nem suspensão automática. Suspender uma conta no MVP é ação
> operacional manual (ex.: revogar/arquivar devices ou bloquear acesso via backoffice).

Ciclo sugerido, com o mês fechando no dia 1:

| Marco | Ação |
|---|---|
| **D+0** (início do mês seguinte) | Gerar o relatório de cobráveis do mês fechado, arquivar, calcular `max(deviceCount, 10) × tarifa do plano`, emitir NFS-e e enviar a cobrança (Pix/boleto) com vencimento em ~7 dias |
| **D+3 após o vencimento** | 1º lembrete cordial (e-mail), reenviando o boleto/Pix |
| **D+10 após o vencimento** | 2º lembrete, agora com aviso de que a conta pode ser suspensa |
| **D+20 após o vencimento** | Suspensão manual da conta (combinada em contrato), preservando os dados dentro da retenção |

Esta cadência D+3 / D+10 / D+20 é apenas uma sugestão de partida - não há base na spec para
adotá-la como regra. Confirme prazos e consequências no contrato/DPA de cada cliente.

---

## 5. Trial e conversão

| Item | Detalhe | Quem aplica |
|---|---|---|
| Duração | **sem prazo fixo, caso a caso** | [DECISÃO DO JOÃO] - controle comercial; a oferta pública de 14 dias foi descontinuada em 08/2026; o sistema não expira o trial sozinho |
| Limite de dispositivos | **25 devices** (N24) | **[SISTEMA]** - enforced no enroll: tentar enrolar o 26º device é recusado |
| Onboarding | **assistido** (comercial/João acompanham) | processo manual |
| Criação | via **backoffice** (`create-org`), com DPA assinado | [SISTEMA] sem signup self-service |

Conversão do trial em contrato pago:

1. Definir o **plano** (Essencial ou Pro) e o **número de devices** contratado com o cliente
   [DECISÃO DO JOÃO].
2. Atualizar a org no backoffice: `plan` para `essencial`/`pro` e `device_limit` para o teto
   contratado (acima de 25, conforme o contrato). Sem isso, o cliente continua preso ao teto de 25
   do trial.
3. A **primeira cobrança** segue o fluxo da Seção 4, usando o relatório de cobráveis do primeiro
   mês pago.
4. Meta de aceite da F5 (Seção 10): pelo menos 1 piloto converte em contrato pago e o relatório de
   cobráveis bate com a contagem manual.

> O número de 25 é o **único** limite que o produto impõe sozinho. Duração de trial, plano,
> tarifa, piso, desconto anual e suspensão por inadimplência são todos decisão comercial e ação
> manual - o sistema apenas fornece a **contagem de dispositivos cobráveis** sobre a qual você fatura.
