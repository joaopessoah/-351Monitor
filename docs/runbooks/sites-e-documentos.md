# Runbook — Sites e documentos

Como ligar, operar e explicar a coleta de **domínio de site** e **nome de arquivo**. Escrito em
16/09/2026, quando a funcionalidade entrou.

---

## 1. O que é coletado, exatamente

| Campo | O que é | O que NUNCA é |
|---|---|---|
| `site_domain` | Domínio **registrável** do site aberto no navegador em foco: `mercadolivre.com.br`, `gov.br`, `docs.google.com` | URL completa, caminho, query string, credencial na URL, aba em segundo plano, histórico, conteúdo da página |
| `document_name` | Nome do arquivo aberto: `Contrato 2026.docx` | Caminho/pasta (`C:\Users\fulano\...`), conteúdo do arquivo, arquivo que não seja documento (lista fechada de extensões) |

**De onde sai cada um:**
- o domínio vem da **barra de endereço** da janela em foco, lida por UI Automation. A leitura toca
  só a interface do navegador, nunca o conteúdo da página — não liga a acessibilidade do renderer,
  não percorre DOM e não abre banco de histórico;
- o nome do arquivo vem do **título da janela JÁ MASCARADO**. Consequência estrutural: o que a
  política de títulos esconde não reaparece pelo nome do arquivo.

**Quando os dois são null, sem exceção:**
1. política de títulos `APP_ONLY` (ou processo em `ignored_processes`);
2. janela anônima/privativa — e aí a barra de endereço nem chega a ser lida;
3. chave `site_capture` / `document_capture` desligada pela controladora;
4. para o domínio: se ele casar com algum `masked_patterns` do tenant, é descartado INTEIRO;
5. para o arquivo: se o mascaramento atingiu o nome (candidato com `***`).

---

## 2. Ligar e desligar (controladora)

**Portal › Configurações › Coleta › "Sites e arquivos"** (somente Proprietário).

- **Ligar qualquer uma das duas sobe `notice_version`**: o aviso de ciência reaparece em toda a
  frota no próximo contato de cada agente e gera `NOTICE_ACK` novo. É deliberado — escopo de
  coleta que aumenta sem o funcionário ser reavisado contradiz o produto.
- **Desligar não reavisa**: coletar menos não precisa de novo aviso.
- A página pública de transparência (`/transparencia/{slug}`) passa a declarar — ou a NÃO declarar —
  as duas coletas, derivado do estado real das chaves. Sob `APP_ONLY` ela não as anuncia mesmo com
  as chaves ligadas, porque ali elas não acontecem.
- A janela **"O que está sendo coletado agora"** (ícone do agente na bandeja) mostra, na máquina do
  funcionário, o site e o arquivo correntes — ou a frase "coleta desligada pela empresa".

Migração de 16/09/2026: as duas chaves nasceram **ligadas para todo mundo**, com bump de
`notice_version` junto, para que ninguém passasse a ser observado a mais sem ser avisado.

---

## 3. Classificar

**Portal › Configurações › Sites** (ou `/sites`): mesma curadoria da tela de Aplicativos.

- a lista é o recorte do tenant nos últimos 30 dias, ordenada por impacto (sem categoria primeiro);
- o dicionário brasileiro (`sites-br.csv`, ~190 domínios) sugere categoria; aplicar é decisão do
  gestor, linha a linha ou em lote com prévia;
- classificar reagrega os últimos 30 dias automaticamente.

**A regra que muda o número:** quando o intervalo tem site conhecido, vale a categoria do SITE —
ela vence a do navegador. A precedência completa:

```
site (equipe) > site (organização) > app (equipe) > app (organização) > sem classificação
```

Consequência prática: `chrome.exe` pode continuar em "Navegação" (+1) e ainda assim o tempo em
`mercadolivre.com.br` contar como improdutivo. Sem essa precedência, classificar sites não mudaria
nada.

---

## 4. Ver

| Pergunta | Onde |
|---|---|
| Onde o tempo foi, por site? | Visão Geral › card **Aplicativos e sites** › lente "Sites" |
| Que sites estão abertos AGORA? | Visão Geral › faixa **Agora** (contagem de máquinas ativas por domínio) |
| Em quais sites a equipe passou mais tempo? | Relatórios › Uso › agrupar por **Site** (CSV incluso) |
| Quais arquivos foram abertos? | Relatórios › **Documentos** (filtros de período, dispositivo, equipe e busca por nome) |
| O que essa pessoa estava vendo às 14h? | Linha do Tempo › coluna **Site / arquivo** |
| Quanto da navegação já está classificado? | Indicador de cobertura no topo da tela de Sites |

O relatório de Documentos **sempre** registra `view_report` na trilha de auditoria, com ou sem
filtro: nome de arquivo é dado pessoal.

---

## 5. Quando o domínio não aparece

Ordem de verificação, do mais comum ao mais raro:

1. **A política de títulos está em `APP_ONLY`?** Então site e arquivo não são coletados, por
   projeto. Configurações › Coleta mostra isso na própria tela.
2. **As chaves estão ligadas?** Configurações › Coleta › "Sites e arquivos".
3. **A janela era anônima?** Comportamento esperado: nada é coletado.
4. **O navegador está na lista?** `BrowserProcesses` cobre Chrome, Edge, Firefox, Opera, Brave,
   Vivaldi, Yandex e derivados. Navegador fora da lista não tem a barra lida — é lista fechada de
   propósito, para a leitura de UI Automation nunca acontecer em outro aplicativo.
5. **O que estava na barra era endereço?** Busca digitada, `chrome://`, `about:blank` e `file://`
   devolvem null de propósito: é melhor perder o domínio do que transformar o que o funcionário
   DIGITOU em dado coletado.
6. **UI Automation indisponível na máquina?** Depois de 3 falhas ou estouros de prazo seguidos, o
   agente desliga a leitura por 10 minutos (disjuntor) e emite `AGENT_ERROR`. O tempo de navegador
   continua sendo contado normalmente — só o domínio não aparece. Procure `AGENT_ERROR` no painel
   de saúde do dispositivo.

Custo medido em 16/09/2026 (Windows 11, Chrome e Edge): **8–25 ms por leitura**, uma vez por ciclo
de polling (5 s por padrão) e só com navegador em foco.

---

## 6. Perguntas que o cliente vai fazer

**"Vocês veem o que a pessoa pesquisou no Google?"**
Não. Só o domínio `google.com`. O texto da busca vive no caminho/query da URL, que é descartado na
própria máquina, antes de qualquer envio. Busca ainda não navegada (texto sendo digitado na barra)
também não vira dado: sem ponto e TLD plausível, o agente devolve nulo.

**"E se a pessoa usar janela anônima?"**
Nada é coletado: nem título, nem domínio, nem arquivo. O agente reconhece a janela anônima pelo
título e pelo nome acessível dela, e nem lê a barra de endereço.

**"Vocês leem o conteúdo dos arquivos?"**
Não. Só o nome, e apenas o que já aparece no título da janela — a mesma informação que a barra de
tarefas do Windows mostra. Se a política de mascaramento da empresa esconde parte do título, o nome
sai escondido junto (ou não sai).

**"Por que `docs.google.com` aparece separado de `google.com`?"**
Porque serviços de natureza diferente moram no mesmo domínio. Uma lista curta e explícita
(`google.com`, `amazon.com`, `microsoft.com`, `globo.com`, `uol.com.br`, `atlassian.net` e mais
algumas) preserva um rótulo a mais, para não jogar trabalho e lazer no mesmo balde. Para todo o
resto vale só o domínio registrável.

---

## 7. Onde mexer no código

| Preciso… | Arquivo |
|---|---|
| ajustar a redução de URL a domínio (sufixos públicos, serviços compartilhados) | `agent/src/M351.Agent.Core/Privacy/SiteDomain.cs` |
| incluir/excluir extensão de documento | `agent/src/M351.Agent.Core/Privacy/DocumentName.cs` |
| incluir navegador | `agent/src/M351.Agent.Core/Collectors/ForegroundSample.cs` (`BrowserProcesses`) |
| ajustar a detecção de janela anônima | `agent/src/M351.Agent.Core/Privacy/TitleMasker.cs` (`PrivateBrowsingMarkers`) |
| curar domínio no dicionário BR | `backend/src/M351.Infrastructure/Data/AppDictionary/sites-br.csv` |
| mexer na precedência da classificação | `backend/src/M351.Infrastructure/Aggregation/DailyAggregationService.cs` |

Depois de mexer no dicionário, basta o deploy: o seeder roda no startup da API e é idempotente —
ele toca só o catálogo global, nunca a decisão de um cliente.
