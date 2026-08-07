"""Histórico de CNPJs já exportados — nunca sugerir a mesma empresa duas vezes.

O arquivo data/historico/exportados.csv é COMMITADO no git (ao contrário do
resto de data/): é a memória de quem já foi prospectado.
"""

import csv
import datetime as dt

import config


def carregar() -> set[str]:
    if not config.ARQ_HISTORICO.exists():
        return set()
    with open(config.ARQ_HISTORICO, encoding="utf-8") as f:
        return {linha["cnpj"] for linha in csv.DictReader(f, delimiter=";")}


def registrar(cnpjs: list[str], mes: str) -> None:
    config.ARQ_HISTORICO.parent.mkdir(parents=True, exist_ok=True)
    novo = not config.ARQ_HISTORICO.exists()
    hoje = dt.date.today().isoformat()
    with open(config.ARQ_HISTORICO, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f, delimiter=";")
        if novo:
            w.writerow(["cnpj", "mes_referencia", "data_exportacao"])
        for c in cnpjs:
            w.writerow([c, mes, hoje])
