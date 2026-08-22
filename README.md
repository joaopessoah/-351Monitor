# +351 Monitor

SaaS B2B brasileiro de **monitoramento transparente de estações Windows** para PMEs. Agente coleta uso de aplicativos, sessões e ociosidade; portal multi-tenant exibe dashboards, timeline e relatórios.

> **Fonte única de verdade:** `docs/PROMPT-DESENVOLVIMENTO.md`. Em qualquer conflito, ele vence. Decisões e porquês: `docs/CONSIDERACOES-E-DECISOES.md`.

## Estrutura do monorepo

| Pasta | Conteúdo |
|---|---|
| `backend/` | API ASP.NET Core 8 + worker + testes (PostgreSQL 16, multi-tenant) |
| `portal/` | SPA React + TypeScript + Vite (Tailwind, shadcn/ui, TanStack Query) |
| `agent/` | Agente Windows .NET 8 (serviço + helper de sessão por usuário, instalador WiX) |
| `infra/` | Docker Compose (caddy, api, worker, seq, postgres), scripts de deploy, backup e disco |
| `crm/` | CRM de leads interno (PHP + MySQL na hospedagem do site) — fora do spec do produto |
| `docs/` | Especificação canônica, análises de design e runbooks de operação |
| `.github/workflows/` | CI: build, testes (backend e agente), gate de isolamento multi-tenant, MSI, deploy, teste mensal de restore, smoke E2E semanal |

## Desenvolvimento local

Pré-requisitos: .NET 8 SDK, Node 20+, PostgreSQL 16 local (porta 5432, usuário `postgres`/`postgres`).

```powershell
# Backend (API em http://localhost:5080)
cd backend
dotnet run --project src/M351.Api

# Portal (em http://localhost:5173, proxy para a API)
cd portal
npm install
npm run dev

# Testes (inclui o gate de isolamento multi-tenant — precisa do Postgres local)
cd backend
dotnet test
```

Banco de dev: `m351_dev` (criado automaticamente pelas migrations no primeiro run). E-mails de dev (convites) são gravados em `./.dev-mail/` em vez de enviados.

## Fase atual

**F5 — Piloto** (Seção 10 do spec). O MVP está completo: F0 (fundação multi-tenant com gate de isolamento no CI), F1 (ingestão fim a fim, aceite formal em VM limpa), F2 (pipeline de intervalos + timeline), F3 (dashboard, categorias, exports CSV, seed do tenant demo), F4 (MSI, auto-update, DSR, retenção, auditoria, transparência pública, painel de saúde).

Em cima disso, a leva de melhorias da F5: recuperação de senha e recovery codes de MFA, política de coleta editável pela controladora, resumo semanal e alertas de frota por e-mail, saúde de frota server-side, cobrança mensal congelada, telas de chaves e usuários com checklist de ativação, demo pública permanente, backup off-site verificável e observabilidade (Sentry, `/readyz`, dead-man switches).

Pendências externas antes do primeiro cliente real: certificado de code signing (comprar com a data do piloto marcada, lead time de 1 a 3 semanas), revisão jurídica do kit LGPD/DPA e decisão da cloud gerenciada de produção. Staging: VPS Hostinger com deploy automático no push para `main`.
