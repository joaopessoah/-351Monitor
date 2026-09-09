# Runbook - Trial (versão de teste) de um prospect

> Como abrir, acompanhar e encerrar um trial. Regra em vigor desde 09/09/2026: **todo trial nasce
> com 10 licenças** (N24) e fica a critério do cliente usar todas ou não. A 11ª máquina é recusada
> na instalação com a frase *"Como é uma versão de teste, tem somente 10 licenças. Entre em contato
> com o time da +351 Monitor."*, a recusa aparece na Auditoria do cliente e o portal mostra o
> medidor "N de 10 licenças em uso" em Visão Geral, Dispositivos e Chaves de instalação.
>
> Licença = **dispositivo** (estação Windows com o agente). Usuários do portal não têm limite.
> O sistema **não encerra o trial sozinho**: a duração é controle comercial (ver seção 4).

Legenda: **[SISTEMA]** o produto faz; **[VOCÊ]** ação manual do comercial/operação.

---

## 0. Antes de criar o tenant [VOCÊ]

1. **Termo assinado.** O kit LGPD (`kit-lgpd.md`) exige DPA/termo de tratamento antes de qualquer
   tenant com titulares reais - um trial monitora funcionários de verdade.
2. **Endereço do painel decidido.** O agente grava o `SERVERURL` na instalação; trocar de hostname
   depois exige reinstalar máquina por máquina. Um endereço definitivo para todos (trial e pago),
   nunca um "só para trials" - ver `infra/README.md`, seção "Trocar o domínio do painel".
3. **Máquinas elegíveis.** Windows 10/11 x64, sem Terminal Server. Kit para o TI do cliente em
   `kit-instalacao-ti.md`. O MSI vai por você (o download em `/api/v1/agent/releases` exige token de
   dispositivo, o cliente não baixa do portal).
4. **CRM.** Lead no status **Trial** (encerra a cadência automática de e-mail) e próxima ação na
   data de encerramento combinada.

## 1. Criar o tenant [SISTEMA]

No container da API (staging: `docker exec m351-staging-api-1 dotnet M351.Api.dll ...`):

```bash
# organização + Owner + convite (imprime o link; vale 7 dias; Owner ativa MFA no 1º acesso)
create-org --name "Empresa X" --owner-email dono@empresax.com.br --slug empresa-x
#   -> nasce com plan=trial e 10 licenças. Piloto negociado fora do padrão: --device-limit 3

# opcional: mostrar os recursos do Pro durante o teste (alertas de frota, jornada semanal)
set-org-plan --org-slug empresa-x --plan pro

# chave de instalação (o Owner também consegue criar em Configurações › Chaves)
create-enrollment-key --org-slug empresa-x --label "Trial"
```

Guarde o link do convite e a chave: os dois aparecem **uma vez só**. Comando de instalação para o
TI do cliente, como administrador, em cada máquina:

```
msiexec /i MonitorAgent.msi /qn ENROLLKEY=ek_XXXXXXXXXXXX SERVERURL=https://<PAINEL>
```

## 2. Durante o trial

- **[SISTEMA]** Enroll conta licença por dispositivo não arquivado e não revogado (pausado ocupa).
  Acima do teto: 422 `device_limit_exceeded` com a frase do trial + `enroll_refused_device_limit`
  na Auditoria, com o hostname da máquina barrada.
- **[SISTEMA]** Medidor "N de 10 licenças em uso" no portal; ao encher, o aviso com a frase.
- **[VOCÊ]** Onboarding assistido: call de 30 min instalando as primeiras máquinas. Meta: primeiro
  evento em menos de 24 h da criação do tenant.
- **[VOCÊ]** Se o cliente quiser mais que 10 no trial, é decisão comercial:
  `set-org-limit --org-slug empresa-x --device-limit 15`.

## 3. Converter em contrato [SISTEMA + VOCÊ]

```bash
set-org-plan  --org-slug empresa-x --plan essencial   # ou pro
set-org-limit --org-slug empresa-x --device-limit 40  # o contratado; --unlimited se for o caso
```

A primeira cobrança segue `cobranca-manual.md`. Nada mais muda para o cliente: mesmo endereço,
mesmas máquinas, mesmo login.

## 4. Encerrar sem conversão [VOCÊ]

Nada acontece sozinho no último dia. Sequência:

1. Peça ao TI do cliente para desinstalar: `msiexec /x MonitorAgent.msi /qn` em cada máquina.
2. No portal (ou como Owner do tenant), revogue a chave em Configurações › Chaves e os dispositivos
   em Dispositivos › Revogar (o agente recebe `UNENROLL`: para a coleta, descarta a fila local e
   esquece o token).
3. Bloqueie o acesso desativando os usuários (Configurações › Usuários). Sem acesso ao portal do
   cliente, direto no banco:

   ```sql
   UPDATE users SET status = 'disabled'
    WHERE tenant_id = (SELECT id FROM organizations WHERE slug = 'empresa-x');
   UPDATE enrollment_keys SET revoked_at = now()
    WHERE tenant_id = (SELECT id FROM organizations WHERE slug = 'empresa-x');
   ```

4. Dados ficam dentro da retenção (`kit-lgpd.md`, item 4); devolução ou exclusão antecipada é
   cláusula do DPA.

## 5. O que o produto ainda não faz (candidatos)

- Expirar o trial sozinho (`trial_ends_at` + aviso no portal + bloqueio no vencimento).
- Suspender uma organização com um comando (`organizations.status` existe, mas nada o lê).
- Tela de backoffice para criar org, trocar plano/limite e encerrar sem CLI.
