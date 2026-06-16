# Revisão jurídica — Regra de exclusão de titular (DSR)

> Para o advogado/DPO do cliente (ou o jurídico da operadora) revisar **antes do primeiro
> cliente real**. Descreve exatamente o que o sistema apaga, anonimiza e mantém quando uma
> empresa-cliente exerce a exclusão de dados de um titular (LGPD art. 18, V — eliminação).
> Implementado na F4.5 (`DELETE /api/v1/privacy/subjects/{deviceUserId}/data`).

## Contexto de papéis

- **Empresa-cliente = controladora** dos dados. Nós (+351 Monitor) = **operadora**.
- A exclusão é disparada pela controladora (papel **Owner** no portal), com **confirmação dupla**
  (digitar o nome do titular/hostname) e **motivo obrigatório** registrado.

## O que o sistema faz na exclusão de um titular

| Dado | Ação | Justificativa |
|---|---|---|
| `raw_events` do titular (eventos brutos: app, **título de janela**, sessão, SID Windows) | **Apagado (hard delete)** | Conteúdo pessoal identificável |
| `activity_intervals` do titular (intervalos com **título de janela** dominante) | **Apagado (hard delete)** | Conteúdo pessoal identificável |
| `device_users` (cadastro: usuário Windows, nome de exibição) | **Anonimizado** (nome → "Usuário removido (DSR)"; SID → marcador) | Remove a identificação, preserva a chave técnica |
| `daily_device_summaries` / `daily_app_usage` do titular (agregados: **segundos por categoria/estado**, sem título nem nome) | **Mantido** | Agregado de equipe já computado, sem dado pessoal direto |
| `audit_log` (trilha, incl. o próprio registro `dsr_delete`) | **Mantido (imutável)** | Evidência de compliance; append-only por design |

**Recibo:** ao concluir, o sistema retorna e registra contagens (quantos eventos/intervalos
apagados, cadastros anonimizados, agregados mantidos) + o motivo, numa linha `dsr_delete`
imutável na auditoria.

## Racional

A spec do produto determina (Seção 9.3): *"exclusão de titular NÃO apaga agregados de equipe já
computados"*. A regra acima implementa essa fronteira: tudo que **identifica a pessoa** ou contém
**conteúdo** (títulos de janela, usuário) é apagado/anonimizado; o que sobra são **somatórios
estatísticos** (ex.: "X segundos em apps de comunicação no dia Y" ligados a um identificador
técnico já sem nome) que sustentam os relatórios gerenciais da equipe.

## Perguntas para o jurídico decidir

1. **Anonimização suficiente?** Os agregados mantidos (`daily_*`) referenciam um `device_user_id`
   (UUID técnico) cujo cadastro foi anonimizado. Isso configura **anonimização** (irreversível,
   fora do escopo da LGPD) ou **pseudonimização** (ainda dado pessoal)? Se o entendimento for que
   é pseudonimização, talvez seja preciso também apagar/agregar mais grosso os `daily_*` do titular.
2. **Direito de eliminação vs. obrigação de retenção:** manter os agregados atende ao legítimo
   interesse de gestão da controladora sem reter dado pessoal? Documentar no DPA.
3. **Texto do recibo e do motivo:** o recibo de contagens + motivo é prova adequada de atendimento
   ao pedido do titular (art. 19, resposta em 15 dias)?
4. **Export antes de excluir:** o titular pode pedir o **export** (pacote JSON+CSV de todos os
   seus dados, link 72h) antes da exclusão — o fluxo cobre os dois direitos.
5. **Backups:** dado apagado ainda pode residir em backup por até **35 dias** (declarado no DPA).
   Aceitável?

## O que ajustar se o jurídico pedir

Tudo acima é configurável em código (`DsrService` na camada de Infrastructure). Se a orientação
for apagar também os `daily_*` do titular, ou anonimizar de forma diferente, é uma mudança
localizada — me passe a decisão e eu ajusto + adiciono teste.
