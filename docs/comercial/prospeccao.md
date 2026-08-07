# Playbook de prospecção outbound — +351 Monitor

> Par do gerador de listas (`tools/leadgen/`) e do CRM (`/crm/`). Meta: **10 demos/mês**
> (dashboard do CRM acompanha). Lista mensal: ~300 empresas, 75 por vertical, operada
> em **lotes semanais de ~75**.

## O funil em uma linha

Lista scorada → cadência de 5 toques em 14 dias → demo de 10 min no WhatsApp →
trial 14 dias assistido → cliente. Tudo registrado no CRM (cada toque = interação).

## Cadência padrão (5 toques / 14 dias)

| Dia | Toque | Quem | Como |
|---|---|---|---|
| D0 | **E-mail 1** — dor da vertical | João/Bruna | template da vertical, personalizar 2 linhas |
| D+2 | **Ligação 1** | Bruna | script 30s abaixo; se atender e topar, manda WhatsApp na hora |
| D+4 | **E-mail 2** — prova (LGPD + preço em real) | João/Bruna | template único |
| D+7 | **LinkedIn** — conexão + nota curta | João | manual, sem automação |
| D+12 | **E-mail 3** — encerramento educado | João/Bruna | template único |

Regras de ouro:
- **WhatsApp nunca é frio**: só depois que a pessoa respondeu e-mail, atendeu ligação
  ou aceitou no LinkedIn. Cold WhatsApp queima a marca e o número.
- Respondeu em qualquer canal → **sai da cadência** e vira conversa de gente.
- Priorização diária: 1º quem respondeu/atendeu · 2º score mais alto do lote da semana.
- Capacidade alvo: ~15 e-mails novos/dia + follow-ups (~30 envios/dia no total)
  e ~10 ligações/dia. Semana 1 é aquecimento do e-mail: máx. 15–20 envios/dia.

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

- Todo toque vira **interação** (tipo certo: email/ligacao/whatsapp/reuniao) com 1 linha de resumo.
- Resposta positiva → status **Demo agendada** + próxima ação com data/hora.
- Demo feita → interação tipo **demo** (alimenta a meta 10/mês) + status **Demo realizada**.
- Sem resposta após o D+12 → **Perdido**, motivo `cadência concluída sem resposta`
  (pode reativar em 6 meses).
- **Opt-out ("SAIR", "remove", "não quero")** → Perdido, motivo `opt-out` — o dedupe
  do gerador bloqueia o e-mail para sempre. Responder confirmando a remoção.

## LGPD do outbound (resumo operacional)

Contato B2B com dados públicos (RFB) = legítimo interesse. Obrigatório: remetente real
(bruna@mais351monitor.com.br), assunto honesto, texto curto sem imagem/anexo, **rodapé
com opt-out** em todo e-mail, opt-out honrado imediatamente e para sempre. Leads sem
avanço são eliminados do CRM em até 12 meses (política publicada no site).

## Ritual semanal (segunda, 30 min)

1. Dashboard do CRM: demos vs meta · follow-ups vencidos · novos sem contato 48h+.
2. Puxar o lote da semana (75 do CSV mensal, na ordem do score).
3. Olhar taxas do lote anterior: resposta de e-mail ≥5%? ligação atendida ≥25%?
   demo/lista ≥3%? Se abaixo, ajustar assunto/horário/script antes de escalar volume.
4. Mensal: pedir ao Claude o **relatório de calibração** (conversão por vertical/UF/porte
   via API do CRM) e ajustar pesos do score em `tools/leadgen/config.py`.
