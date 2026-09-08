# Playbook de prospecção outbound — +351 Monitor

> Par do gerador de listas (`tools/leadgen/`) e do CRM (`/crm/`). Meta: **10 demos/mês**
> (dashboard do CRM acompanha). Lista mensal: ~300 empresas, 75 por vertical, operada
> em **lotes semanais de ~75**.

## O funil em uma linha

Lista scorada → cadência de 5 toques em 14 dias → demo de 10 min no WhatsApp →
onboarding assistido → cliente. Tudo registrado no CRM (cada toque = interação).

## Cadência padrão (5 toques / 14 dias)

**Os e-mails são automáticos desde setembro de 2026.** O motor do CRM envia cada etapa
na data certa, registra a interação no lead, valida o endereço antes de sair e para
sozinho quando a pessoa responde. Ligar, conectar no LinkedIn e **responder** continuam
sendo trabalho de gente — e é para isso que o tempo economizado serve.
Como ligar e operar: `docs/runbooks/cadencia-automatica.md`.

| Dia útil | Toque | Quem faz | Como |
|---|---|---|---|
| D0 | **E-mail 1** — dor da vertical | 🤖 motor | modelo da etapa + a linha pessoal daquela empresa |
| D+2 | **Ligação 1** | Bruna | tarefa criada sozinha no quadro; script de 30s abaixo |
| D+4 | **E-mail 2** — prova (LGPD + preço em real) | 🤖 motor | vai na mesma conversa do 1º |
| D+7 | **LinkedIn** — conexão + nota curta | João | tarefa criada sozinha no quadro |
| D+7 | **E-mail 3** — o que aparece na 1ª semana | 🤖 motor | |
| D+10 | **E-mail 4** — ainda faz sentido? | 🤖 motor | |
| D+13 | **E-mail 5** — encerramento educado | 🤖 motor | conclui a cadência |
| +30 du | Retomar ou marcar perdido | Bruna | tarefa de retomada |

Para voltar aos 3 toques de e-mail do desenho original, desligue as etapas 3 e 4 em
Configurações → Cadência automática → Etapas ativas (`1,2,5`). Nenhum modelo muda.

Regras de ouro:
- **WhatsApp nunca é frio**: só depois que a pessoa respondeu e-mail, atendeu ligação
  ou aceitou no LinkedIn. Cold WhatsApp queima a marca e o número.
- Respondeu em qualquer canal → **sai da cadência** e vira conversa de gente. Por
  e-mail isso é automático; por telefone ou WhatsApp, mude o status do lead (avançar
  no funil encerra a cadência sozinho).
- Priorização diária: 1º quem respondeu/atendeu · 2º score mais alto do lote da semana.
- Capacidade alvo: ~15 e-mails novos/dia + follow-ups (~30 envios/dia no total)
  e ~10 ligações/dia. Semana 1 é aquecimento do e-mail: máx. 15–20 envios/dia — o
  motor aplica esse teto sozinho (15 → 20 → 30, pela data de início).

## Script da ligação (30 segundos)

> "Oi, {contato}? Aqui é a Bruna, do +351 Monitor — tudo bem? Te ligo rapidinho:
> a gente ajuda {vertical: escritórios de contabilidade / equipes de TI / escritórios
> de advocacia / operações de BPO} a enxergar como as horas do time viram produção
> no Windows — **sem print de tela e sem keylogger**, dentro da LGPD.
> Faz sentido eu te mostrar em **10 minutos pelo WhatsApp** como fica o painel?"

- Objeção "já uso X / não preciso": "Entendi — posso mandar por e-mail um comparativo
  de 1 página com preço em real e a parte de LGPD? Se fizer sentido depois, você me chama."
- Objeção "manda por e-mail": mandar o E-mail 2 na hora e agendar retorno em 3 dias.
- Não atendeu: não deixar recado no 1º ciclo; registrar `ligacao` no CRM e seguir cadência.

## Qualificação (perguntar na conversa/demo)

1. Quantas **estações Windows** individuais? (piso do produto: 10 · sweet spot 20–80)
2. O trabalho roda **local/híbrido** ou tudo via **Terminal Server/Citrix**? (TS não é
   suportado — se for 100% TS, desqualificar com elegância e registrar motivo)
3. Quem decide? (dono/diretor · RH/DP compra, TI influencia)
4. Dor declarada: produtividade no híbrido? embasar feedback? inventário de software?
5. **Screenshot/keylog como condição** → desqualificar: "não construímos isso nem por
   dinheiro — é nossa postura de LGPD" (perdido + motivo `exige screenshot/keylog`).

## Registro no CRM (disciplina de dados)

O que o motor registra sozinho: cada e-mail enviado (com a etapa), a resposta recebida
com o trecho, a devolução, o pedido de saída e as tarefas de ligação e LinkedIn.

O que continua sendo seu:

- **Ligação e WhatsApp** viram interação na mão (tipo certo, 1 linha de resumo).
- Resposta positiva → status **Demo agendada** + próxima ação com data/hora.
- Demo feita → interação tipo **demo** (alimenta a meta 10/mês) + status **Demo realizada**.
- Sem resposta ao fim da sequência → **Perdido**, motivo `cadência concluída sem resposta`
  (pode reativar em 6 meses). A tarefa de retomada aparece sozinha 30 dias úteis depois.
- **Opt-out ("SAIR", "remove", "não quero")** → o motor já marca "não contactar" e
  responde confirmando a remoção; o dedupe do gerador bloqueia o e-mail para sempre.
  Se o pedido chegar por telefone ou WhatsApp, marque na mão no detalhe do lead.
- **Devolução (bounce)** → o endereço é marcado como inválido e nasce a tarefa
  "corrigir o e-mail". Corrigir na tela limpa a marcação e libera a cadência de novo.

## LGPD do outbound (resumo operacional)

Contato B2B com dados públicos (RFB) = legítimo interesse. Obrigatório: remetente real
(bruna@mais351monitor.com.br), assunto honesto, texto curto sem imagem/anexo, **rodapé
com opt-out** em todo e-mail, opt-out honrado imediatamente e para sempre. Leads sem
avanço são eliminados do CRM em até 12 meses (política publicada no site).

## Ritual semanal (segunda, 30 min)

1. Dashboard do CRM: demos vs meta · follow-ups vencidos · novos sem contato 48h+ ·
   **retornos não tratados** (o número ao lado de "Envios" no menu).
2. Puxar o lote da semana (75 do CSV mensal, na ordem do score) — a caixa
   "já iniciar a cadência de e-mail" na tela da Fila faz as duas coisas de uma vez.
   Pela tela de Leads dá para escolher a dedo: selecionar, "Iniciar cadência…", conferir
   a prévia (quem entra, quem fica de fora e por quê) e confirmar.
3. Olhar taxas do lote anterior: resposta de e-mail ≥5%? ligação atendida ≥25%?
   demo/lista ≥3%? bounce <3%? Se estiver fora, ajustar assunto/horário/script antes
   de escalar volume — nunca subir o teto com taxa ruim.
4. Mensal: pedir ao Claude o **relatório de calibração** (rota `cadence-report` da API
   do CRM) e ajustar pesos do score em `tools/leadgen/config.py`.

## Do que o dia a dia passou a ser feito

Antes: abrir o lead, clicar no e-mail, colar no Outlook, enviar, voltar, registrar a
interação. Depois: a fila sai sozinha e sobra o trabalho que dá resultado.

| Todo dia | Onde |
|---|---|
| Tratar quem respondeu (é a única coisa urgente) | Envios → Retornos não tratados |
| Ligar para quem recebeu o 1º e-mail há 2 dias | Quadro → tarefas de ligação |
| Aprovar a fila do dia (só nas 2 primeiras semanas) | Envios → Aguardando aprovação |
| Conferir o resumo das 17h30 | e-mail |
