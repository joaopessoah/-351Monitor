# +351 Monitor

SaaS B2B brasileiro de **monitoramento transparente de estações Windows** para PMEs. Agente coleta uso de aplicativos, sessões e ociosidade; portal multi-tenant exibe dashboards, timeline e relatórios.

> **Fonte única de verdade:** `docs/PROMPT-DESENVOLVIMENTO.md`. Em qualquer conflito, ele vence. Decisões e porquês: `docs/CONSIDERACOES-E-DECISOES.md`.

## Estrutura do monorepo

| Pasta | Conteúdo |
|---|---|
| `backend/` | API ASP.NET Core 8 + worker + testes (PostgreSQL 16, multi-tenant) |
| `portal/` | SPA React + TypeScript + Vite (Tailwind, shadcn/ui, TanStack Query) |
| `agent/` | Agente Windows .NET 8 (serviço + helper de sessão) — implementação na F1 |
| `infra/` | Docker Compose (caddy, api, worker, seq), scripts de deploy/backup |
| `docs/` | Especificação canônica e análises de design |
| `.github/workflows/` | CI: build + testes + gate de isolamento multi-tenant |

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

**F0 — Fundação** (Seção 10 do spec): esqueleto multi-tenant com `tenant_id` desde a primeira migration, auth completa (Argon2id, JWT + refresh, MFA TOTP para Owner/Admin, lockout, convites), RBAC Owner/Admin/Viewer, criação de org via backoffice, esqueleto do portal, **teste de isolamento entre tenants como gate de CI**.

Deploy em staging: aguardando provisionamento do VPS (Hostinger, datacenter São Paulo). O workflow de CI já contém o job de deploy, ativado quando os secrets `STAGING_*` existirem.
