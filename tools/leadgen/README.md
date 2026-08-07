# leadgen — gerador mensal de leads (dados abertos do CNPJ / RFB)

Transforma os dumps mensais da Receita Federal em uma lista scorada de empresas-alvo
do +351 Monitor, deduplicada contra o CRM, no formato do import (`crm/import.php`).
Estratégia completa (cadência, templates): `docs/comercial/`.

## Como rodar (mensal)

```bash
cd tools/leadgen
pip install -r requirements.txt          # 1ª vez (duckdb>=1.4.3)
cp .env.example .env                     # 1ª vez: preencher MAIS351_CRM_TOKEN
python gerar.py --por-vertical 75        # mês mais recente, top 300 (75 por vertical)
```

Primeira execução do mês baixa ~6,5 GB (pico ~35 GB em disco, limpo ao final) e
leva 30–75 min; re-execuções usam o parquet cacheado (<1 min). Flags úteis:
`--simular` (ensaio, não grava histórico), `--limite N`, `--uf-boost SP`,
`--sem-crm` (offline), `--refazer`, `--manter-zips`, `--enviar-crm` (cria direto
via API em vez de só gerar o CSV).

### Checklist mensal

1. `git pull` (traz o histórico de exportados atualizado).
2. `python gerar.py --por-vertical 75` — descobre o mês novo sozinho.
3. Conferir o resumo (funil, verticais, UFs) e amostrar ~15 linhas do CSV.
4. Importar `saida/leads-AAAA-MM.csv` no CRM (menu Importar) e conferir contagens.
5. Commitar `data/historico/exportados.csv` (a memória de quem já foi prospectado).

## O que o filtro seleciona (onda 1)

- **CNAE principal** em 12 códigos das 4 verticais: contabilidade (6920601/02),
  software/TI (6201501, 6202300, 6203100, 6204000, 6209100 — MSPs), advocacia
  (6911701), BPO/call center/agências (8211300, 8219999, 8220200, 7311400).
- Situação **ATIVA**, só **matriz**, **≥ 2 anos** de fundação, com e-mail ou telefone.
- **Exclui**: MEI (Simples.zip), administração pública (natureza 1xxx).
- Porte **EPP/DEMAIS**; ME/não-informado apenas com capital ≥ R$ 100 mil.

## Score (0–100) — recalibrar mensalmente com a conversão real do CRM

| Componente | Máx | Regra |
|---|---|---|
| CNAE | 25 | contabilidade 25 · BPO/teleatendimento 22 · software/TI 20 · advocacia 18 · publicidade 15 |
| Porte | 20 | EPP 20 · DEMAIS 16 · ME c/ capital 8 |
| Capital | 15 | ≥1M 15 · 500k 12 · 200k 9 · 100k 6 · resto 3 |
| Geografia | 15 | SP capital 15 · SP interior 13 · SE/Sul 10 · demais 5 (`--uf-boost` muda a UF destaque) |
| E-mail | 10 | domínio próprio 10 · gratuito 3 · sem e-mail 0 |
| Contatabilidade | 10 | e-mail E fone 10 · só e-mail 6 · só fone 4 |
| Idade | 5 | 5–15 anos 5 · >15 anos 4 · 2–5 anos 3 |

**Estações estimadas** (proxy do ICP 20–80; preenche `estacoes` no CRM):
`base_porte (ME 8 · EPP 25 · DEMAIS 60) × mult_CNAE (call center 2,5 · BPO 1,5 ·
contabilidade 1,2 · TI 1,0 · publicidade 0,9 · advocacia 0,8) × ajuste_capital
(≥1M 1,3 · ≥500k 1,15 · ≥100k 1,0 · resto 0,7)`, limitado a [5, 200].
É estimativa para priorização — confirmar na qualificação.

## Dedupe (3 camadas)

1. `data/historico/exportados.csv` (commitado) — nunca re-sugerir empresa já exportada.
2. CNPJs e e-mails já no CRM (API `?r=leads` paginada).
3. O próprio import do CRM flaga duplicados por CNPJ/e-mail/fone.

Opt-outs vivem no CRM (perdido + motivo) e são bloqueados pela camada 2 via e-mail.

## Fontes e avisos

- Espelho CDN: `https://dados-abertos-rf-cnpj.casadosdados.com.br/arquivos/<AAAA-MM-DD>/`
  (plano B: `https://dadosabertos.rfb.gov.br/CNPJ/dados_abertos_cnpj/`). Atualização mensal.
- CSVs da RFB: `;`, latin-1, sem cabeçalho — lidos com colunas VARCHAR (zeros à esquerda).
- O e-mail do cadastro RFB pode ser do **contador** da empresa (por isso o score
  privilegia domínio próprio). Na vertical contabilidade isso é vantagem.
- CNPJ alfanumérico (jul/2026+): o CRM valida; a base da RFB os trará gradualmente.
- LGPD: dados públicos, contexto B2B (legítimo interesse). E-mails de prospecção
  devem ter remetente identificado e opt-out — ver `docs/comercial/prospeccao.md`.
- Python 3.14: exige `duckdb>=1.4.3` (bug de instalação em versões anteriores no Windows).
