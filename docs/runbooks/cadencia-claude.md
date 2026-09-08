# Runbook — a camada Claude da cadência (opcional, F4)

> Depende da cadência automática já rodando (`docs/runbooks/cadencia-automatica.md`).
> Ligue **depois de um mês de dados**: antes disso não há o que calibrar.

## O princípio

O motor de envio é determinístico e continua sendo. A IA entra só onde existe
julgamento, e **nunca com a mão no gatilho**:

| A IA faz | A IA não faz |
|---|---|
| Escreve a **linha pessoal** do 1º e-mail lendo o site da empresa | Enviar e-mail |
| **Triar** os retornos e sugerir a réplica na tarefa | Responder sozinha |
| Montar o **relatório mensal de calibração** | Mudar teto, janela ou modelo |

Garantias de projeto, não de prompt:

- A linha pessoal é gravada como **rascunho** (`estado: "rascunho"`). O motor só usa a
  linha marcada como `aprovada`, e quem aprova é gente, clicando em "Salvar linha" no
  lead. Um rascunho nunca chega a um prospect.
- O Claude fala com o CRM pela API com token Bearer (`crm/api/index.php`), a mesma que
  já existia. Ele **não tem** credencial de SMTP nem de IMAP.
- Toda escrita da IA aparece na tela como qualquer outra: linha de rascunho no card da
  cadência, sugestão no corpo da tarefa.

## Rotina diária (manhã)

Agende no Claude Code (`/schedule`, dias úteis às 8h) ou rode à mão. Prompt:

```
Você é o assistente comercial do +351 Monitor. Use a API do CRM
(https://www.mais351monitor.com.br/crm/api/index.php, Bearer $MAIS351_CRM_TOKEN).

1. GET ?r=leads&status=novo — para cada lead em cadência ativa e SEM linha pessoal
   (GET ?r=lead&id=N mostra o estado), leia o site da empresa (campo website) e
   escreva UMA frase específica sobre ela: algo verificável no site, sem elogio
   genérico, no máximo 200 caracteres, terceira linha do e-mail.
   Grave com POST ?r=cadence-line {lead_id, linha_pessoal, estado: "rascunho"}.
   Se o site não carregar ou não houver nada específico a dizer, NÃO invente:
   pule o lead e diga isso no resumo.

2. GET ?r=inbox&status=nao_tratado — para cada resposta humana, classifique em
   {interessado, depois, pessoa errada, objeção, desqualificado} e escreva um
   rascunho de réplica de até 5 linhas, no tom dos modelos em
   docs/comercial/templates-email.md. Poste como tarefa do lead:
   POST ?r=tasks {lead_id, title: "Responder: <classificação>", due_at: hoje 14:00}.
   Não marque o retorno como tratado — quem trata é gente.

3. Me devolva um resumo de 5 linhas: quantas linhas pessoais escreveu, quantos
   retornos triou, e o que precisa de decisão humana hoje.
```

## Relatório mensal de calibração (dia 1º)

```
GET ?r=cadence-report&mes=AAAA-MM na API do CRM. Com os números:

1. Compare com as metas do playbook: resposta >= 5%, bounce < 3%, opt-out < 1%,
   demo/lista >= 3%, 10 demos no mês.
2. Diga em qual ETAPA a resposta acontece (por_etapa vs respostas): se o 4º e o 5º
   não trouxerem resposta, proponha desligá-los em auto_etapas.
3. Olhe `saidas` (motivo de saída da cadência). Muito "contato sem nome de pessoa"
   significa que a fila precisa de enriquecimento, não que a cadência está ruim.
4. Cruze com o score do leadgen: proponha ajuste de peso em tools/leadgen/config.py
   com o número que justifica cada mudança.
5. Entregue no máximo 1 página, com no máximo 3 recomendações, cada uma com o
   número que a sustenta.
```

## Rotas da API que esta camada usa

| Rota | Para quê |
|---|---|
| `GET ?r=leads` / `?r=lead&id=` | achar quem está em cadência e sem linha pessoal |
| `POST ?r=cadence-line` | gravar a linha como rascunho (nunca como aprovada) |
| `GET ?r=inbox&status=nao_tratado` | os retornos que ainda pedem gente |
| `POST ?r=tasks` | deixar a sugestão de réplica no lead |
| `GET ?r=cadence-report&mes=` | os números do mês |
| `GET ?r=outbox&dia=hoje` | conferir o que saiu, quando pedirem |

## O que não automatizar

- **Responder o prospect.** A resposta é a conversa; automatizá-la é o oposto da
  postura que o produto vende.
- **Aprovar a própria linha pessoal.** Se a IA aprovasse, o rascunho viraria envio.
- **Mudar configuração da cadência.** Teto, janela e modelo são decisão de gente, e
  a tela de Configurações registra quem mexeu.
