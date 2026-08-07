"""Score, heurística de estações, dedupe e geração dos CSVs de saída."""

import csv
import datetime as dt
from pathlib import Path

import duckdb

import config

# Tokens que permanecem em caixa alta ao formatar nomes
_SIGLAS = {"LTDA", "ME", "EPP", "SA", "S/A", "S.A.", "EIRELI", "SS", "TI",
           "RH", "BPO", "CRM", "ERP", "SC", "ADV", "II", "III", "IV"}
_MINUSCULAS = {"de", "da", "do", "das", "dos", "e", "em", "para"}


def nome_titulo(s: str | None) -> str:
    if not s:
        return ""
    palavras = []
    for i, p in enumerate(str(s).strip().split()):
        alto = p.upper()
        if alto in _SIGLAS:
            palavras.append(alto)
        elif i > 0 and p.lower() in _MINUSCULAS:
            palavras.append(p.lower())
        else:
            palavras.append(p.capitalize())
    return " ".join(palavras)


def _pontos_faixa(valor: float, faixas: list[tuple[float, int]]) -> int:
    for minimo, pontos in faixas:
        if valor >= minimo:
            return pontos
    return 0


def _fator_faixa(valor: float, faixas: list[tuple[float, float]]) -> float:
    for minimo, fator in faixas:
        if valor >= minimo:
            return fator
    return faixas[-1][1]


def _idade_anos(inicio) -> float:
    if inicio is None:
        return 0.0
    if isinstance(inicio, dt.datetime):
        inicio = inicio.date()
    return (dt.date.today() - inicio).days / 365.25


def pontuar_linha(r: dict, uf_boost: str) -> dict:
    """Recebe uma linha da base filtrada, devolve score + componentes + estações."""
    vertical, cnae_desc, pts_cnae, mult_cnae = config.CNAES[r["cnae"]]

    pts_porte = config.PONTOS_PORTE.get(r["porte"], 8)
    pts_capital = _pontos_faixa(r["capital"] or 0, config.PONTOS_CAPITAL)

    municipio = (r["municipio_nome"] or "").upper()
    if r["uf"] == uf_boost and municipio == "SAO PAULO":
        pts_geo = config.PONTOS_GEO_CAPITAL_BOOST
    elif r["uf"] == uf_boost:
        pts_geo = config.PONTOS_GEO_UF_BOOST
    elif r["uf"] in config.UFS_SUDESTE_SUL:
        pts_geo = config.PONTOS_GEO_SUDESTE_SUL
    else:
        pts_geo = config.PONTOS_GEO_DEMAIS

    email = (r["email"] or "").strip().lower()
    email_valido = "@" in email and "." in email.rsplit("@", 1)[-1]
    dominio = email.rsplit("@", 1)[-1] if email_valido else ""
    if not email_valido:
        pts_email = 0
    elif dominio in config.DOMINIOS_GRATIS:
        pts_email = config.PONTOS_EMAIL_GRATIS
    else:
        pts_email = config.PONTOS_EMAIL_PROPRIO

    tem_fone = bool(r["fone1"] and len(r["fone1"]) >= 10)
    if email_valido and tem_fone:
        pts_contato = config.PONTOS_CONTATO_AMBOS
    elif email_valido:
        pts_contato = config.PONTOS_CONTATO_SO_EMAIL
    elif tem_fone:
        pts_contato = config.PONTOS_CONTATO_SO_FONE
    else:
        pts_contato = 0

    idade = _idade_anos(r["inicio"])
    pts_idade = 0
    if idade >= 15:
        pts_idade = 4
    elif idade >= 5:
        pts_idade = 5
    elif idade >= config.IDADE_MINIMA_ANOS:
        pts_idade = 3

    base = config.ESTACOES_BASE_PORTE.get(r["porte"], 8)
    fator_cap = _fator_faixa(r["capital"] or 0, config.AJUSTE_CAPITAL)
    estacoes = round(base * mult_cnae * fator_cap)
    estacoes = max(config.ESTACOES_MIN, min(config.ESTACOES_MAX, estacoes))

    return {
        "vertical": vertical,
        "cnae_desc": cnae_desc,
        "score": pts_cnae + pts_porte + pts_capital + pts_geo + pts_email + pts_contato + pts_idade,
        "estacoes": estacoes,
        "email_valido": email_valido,
        "idade_anos": round(idade, 1),
    }


def _fone_formatado(d: str) -> str:
    if len(d) == 11:
        return f"({d[:2]}) {d[2:7]}-{d[7:]}"
    if len(d) == 10:
        return f"({d[:2]}) {d[2:6]}-{d[6:]}"
    return d


def _whatsapp_provavel(d: str) -> bool:
    return len(d) == 11 and d[2] == "9"


PORTE_LABEL = {"01": "ME", "03": "EPP", "05": "Demais", "00": "N/I"}


def gerar_saida(
    parquet: str,
    mes: str,
    limite: int,
    uf_boost: str,
    por_vertical: int | None,
    excluir_cnpjs: set[str],
    excluir_emails: set[str],
    simular: bool,
) -> tuple[Path, list[dict], dict]:
    """Pontua a base, aplica dedupe/limites e escreve os CSVs. Devolve
    (caminho_csv_crm, linhas_exportadas, resumo)."""
    con = duckdb.connect()
    linhas = con.execute(f"SELECT * FROM '{Path(parquet).as_posix()}'").fetchall()
    colunas = [d[0] for d in con.description]
    con.close()

    candidatos = []
    resumo = {"base": len(linhas), "dedupe_historico_crm": 0}
    for valores in linhas:
        r = dict(zip(colunas, valores))
        if r["cnpj14"] in excluir_cnpjs:
            resumo["dedupe_historico_crm"] += 1
            continue
        email = (r["email"] or "").strip().lower()
        if email and email in excluir_emails:
            resumo["dedupe_historico_crm"] += 1
            continue
        r.update(pontuar_linha(r, uf_boost))
        candidatos.append(r)

    candidatos.sort(key=lambda x: (-x["score"], -(x["capital"] or 0)))

    selecionados: list[dict] = []
    if por_vertical:
        conta = {v: 0 for v in config.VERTICAIS}
        for r in candidatos:
            if conta[r["vertical"]] < por_vertical and len(selecionados) < limite:
                selecionados.append(r)
                conta[r["vertical"]] += 1
        # completa com os melhores restantes se alguma vertical não encheu
        if len(selecionados) < limite:
            ja = {r["cnpj14"] for r in selecionados}
            for r in candidatos:
                if len(selecionados) >= limite:
                    break
                if r["cnpj14"] not in ja:
                    selecionados.append(r)
    else:
        selecionados = candidatos[:limite]

    config.DIR_SAIDA.mkdir(parents=True, exist_ok=True)
    sufixo = "-simulacao" if simular else ""
    mes_curto = mes[:7]
    arq_crm = config.DIR_SAIDA / f"leads-{mes_curto}{sufixo}.csv"
    arq_completo = config.DIR_SAIDA / f"leads-{mes_curto}{sufixo}-completo.csv"

    with open(arq_crm, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f, delimiter=";")
        w.writerow(["empresa", "contato", "email", "whatsapp", "estacoes",
                    "origem", "observacoes", "cnpj"])
        for r in selecionados:
            fones = [x for x in (r["fone1"], r["fone2"]) if x and len(x) >= 10]
            whatsapp = fones[0] if fones and _whatsapp_provavel(fones[0]) else ""
            obs_fones = [f"Fone: {_fone_formatado(x)}" for x in fones if x != whatsapp]
            fantasia = nome_titulo(r["nome_fantasia"])
            obs = " | ".join(filter(None, [
                f"CNAE {r['cnae']} {r['cnae_desc']}",
                f"Porte {PORTE_LABEL.get(r['porte'], r['porte'])}",
                f"Capital R$ {int(r['capital'] or 0):,}".replace(",", "."),
                f"Fantasia: {fantasia}" if fantasia else "",
                *obs_fones,
                f"{nome_titulo(r['municipio_nome'])}/{r['uf']}",
                f"Fundada ha {r['idade_anos']:.0f} anos",
                f"Score {r['score']}",
            ]))
            w.writerow([
                nome_titulo(r["razao_social"]),
                nome_titulo(r["contato"]),
                r["email"] if r["email_valido"] else "",
                whatsapp,
                r["estacoes"],
                "prospeccao",
                obs,
                r["cnpj14"],
            ])

    with open(arq_completo, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f, delimiter=";")
        cols = ["cnpj14", "razao_social", "nome_fantasia", "contato", "email",
                "fone1", "fone2", "vertical", "cnae", "cnae_desc", "porte",
                "capital", "uf", "municipio_nome", "inicio", "idade_anos",
                "estacoes", "score"]
        w.writerow(cols)
        for r in selecionados:
            w.writerow([r.get(c, "") for c in cols])

    resumo["candidatos"] = len(candidatos)
    resumo["exportados"] = len(selecionados)
    resumo["por_vertical"] = {
        v: sum(1 for r in selecionados if r["vertical"] == v) for v in config.VERTICAIS
    }
    ufs: dict[str, int] = {}
    for r in selecionados:
        ufs[r["uf"]] = ufs.get(r["uf"], 0) + 1
    resumo["por_uf"] = dict(sorted(ufs.items(), key=lambda kv: -kv[1])[:10])
    return arq_crm, selecionados, resumo
