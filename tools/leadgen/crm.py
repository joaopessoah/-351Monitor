"""Integração com o CRM (+351): dedupe via API e criação opcional de leads.

Token: variável de ambiente MAIS351_CRM_TOKEN ou arquivo .env nesta pasta
(linha MAIS351_CRM_TOKEN=...). Doc da API: crm/README.md no repositório.
"""

import os

import requests

import config

TIMEOUT = (10, 30)


def _token() -> str | None:
    t = os.environ.get("MAIS351_CRM_TOKEN")
    if t:
        return t.strip()
    env = config.RAIZ / ".env"
    if env.exists():
        for linha in env.read_text(encoding="utf-8").splitlines():
            if linha.startswith("MAIS351_CRM_TOKEN="):
                return linha.split("=", 1)[1].strip()
    return None


def carregar_existentes() -> tuple[set[str], set[str]]:
    """Percorre a API paginada e devolve (cnpjs, emails) já cadastrados."""
    token = _token()
    if not token:
        raise RuntimeError(
            "Token do CRM ausente. Defina MAIS351_CRM_TOKEN no ambiente ou "
            "crie tools/leadgen/.env (modelo em .env.example). "
            "Para rodar sem dedupe do CRM use --sem-crm."
        )
    headers = {"Authorization": f"Bearer {token}"}
    cnpjs: set[str] = set()
    emails: set[str] = set()
    pagina = 1
    while True:
        r = requests.get(
            config.CRM_API,
            params={"r": "leads", "page": pagina},
            headers=headers,
            timeout=TIMEOUT,
        )
        r.raise_for_status()
        dados = r.json()
        itens = dados.get("items", [])
        for item in itens:
            if item.get("cnpj"):
                cnpjs.add(str(item["cnpj"]))
            if item.get("email"):
                emails.add(str(item["email"]).strip().lower())
        if len(itens) < 25:
            break
        pagina += 1
        if pagina > 400:  # trava de segurança (10 mil leads)
            break
    return cnpjs, emails


def pool_upsert_lote(itens: list[dict]) -> dict:
    """POST ?r=pool-upsert com um lote de empresas para a fila de prospecção."""
    token = _token()
    if not token:
        raise RuntimeError("Token do CRM ausente (MAIS351_CRM_TOKEN).")
    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
    r = requests.post(
        config.CRM_API, params={"r": "pool-upsert"}, json={"items": itens},
        headers=headers, timeout=(10, 120),
    )
    r.raise_for_status()
    return r.json()


def pool_stats() -> dict:
    token = _token()
    headers = {"Authorization": f"Bearer {token}"}
    r = requests.get(config.CRM_API, params={"r": "pool-stats"}, headers=headers, timeout=TIMEOUT)
    r.raise_for_status()
    return r.json()


def criar_lead(linha: dict) -> dict:
    """POST ?r=leads — usado apenas com --enviar-crm (padrão é só gerar CSV)."""
    token = _token()
    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
    corpo = {
        "company": linha["empresa"],
        "cnpj": linha["cnpj"],
        "contact_name": linha["contato"],
        "email": linha["email"] or None,
        "whatsapp": linha["whatsapp"] or None,
        "estimated_devices": linha["estacoes"],
        "source": "prospeccao",
        "notes": linha["observacoes"],
    }
    r = requests.post(
        config.CRM_API, params={"r": "leads"}, json=corpo, headers=headers,
        timeout=TIMEOUT,
    )
    r.raise_for_status()
    return r.json()
