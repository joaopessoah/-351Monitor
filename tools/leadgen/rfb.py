"""Acesso aos dados abertos do CNPJ: descoberta do mês, download com resume, extração."""

import re
import zipfile
from pathlib import Path

import requests

import config

TIMEOUT = (10, 120)  # (conexão, leitura)


def descobrir_mes_mais_recente() -> str:
    """Lê o índice do espelho e devolve a pasta mensal mais recente (AAAA-MM-DD)."""
    r = requests.get(config.URL_ESPELHO, timeout=TIMEOUT)
    r.raise_for_status()
    pastas = sorted(set(re.findall(r"(\d{4}-\d{2}-\d{2})/", r.text)))
    if not pastas:
        raise RuntimeError(
            "Nenhuma pasta mensal encontrada no espelho. "
            f"Confira {config.URL_ESPELHO} (plano B: {config.URL_OFICIAL})."
        )
    return pastas[-1]


def _tamanho_remoto(url: str) -> int | None:
    try:
        r = requests.head(url, timeout=TIMEOUT, allow_redirects=True)
        n = r.headers.get("Content-Length")
        return int(n) if n and n.isdigit() else None
    except requests.RequestException:
        return None


def baixar_arquivo(url: str, destino: Path) -> None:
    """Download com retomada via Range e validação de tamanho. Idempotente."""
    remoto = _tamanho_remoto(url)
    if destino.exists():
        if remoto is None or destino.stat().st_size == remoto:
            return  # já completo (ou sem como validar — confia no arquivo local)
        destino.unlink()  # tamanho diferente: recomeça limpo

    parcial = destino.with_suffix(destino.suffix + ".part")
    for tentativa in range(1, 4):
        headers = {}
        modo = "wb"
        if parcial.exists() and remoto is not None:
            headers["Range"] = f"bytes={parcial.stat().st_size}-"
            modo = "ab"
        try:
            with requests.get(url, stream=True, timeout=TIMEOUT, headers=headers) as r:
                if r.status_code == 416:  # range além do fim: já temos tudo
                    break
                r.raise_for_status()
                if headers and r.status_code != 206:  # servidor ignorou o Range
                    modo = "wb"
                with open(parcial, modo) as f:
                    for pedaco in r.iter_content(chunk_size=1024 * 1024):
                        f.write(pedaco)
            if remoto is None or parcial.stat().st_size == remoto:
                break
        except requests.RequestException as e:
            if tentativa == 3:
                raise RuntimeError(f"Falha ao baixar {url}: {e}") from e
    if remoto is not None and parcial.stat().st_size != remoto:
        raise RuntimeError(
            f"{destino.name}: tamanho baixado ({parcial.stat().st_size}) "
            f"difere do remoto ({remoto}). Rode de novo para retomar."
        )
    parcial.replace(destino)


def baixar_mes(mes: str, apenas_faltantes: bool = True) -> Path:
    """Garante todos os zips do mês em data/<mes>/zips/. Devolve o diretório."""
    dir_zips = config.DIR_DATA / mes / "zips"
    dir_zips.mkdir(parents=True, exist_ok=True)
    base = config.URL_ESPELHO.rstrip("/") + f"/{mes}/"
    for nome in config.ARQUIVOS:
        destino = dir_zips / nome
        if apenas_faltantes and destino.exists() and destino.stat().st_size > 0:
            # valida contra o remoto só quando o HEAD responde
            remoto = _tamanho_remoto(base + nome)
            if remoto is None or destino.stat().st_size == remoto:
                print(f"  [ok] {nome} (ja baixado)")
                continue
        print(f"  [baixando] {nome} ...")
        baixar_arquivo(base + nome, destino)
        print(f"  [ok] {nome} ({destino.stat().st_size // (1024 * 1024)} MB)")
    return dir_zips


def extrair_mes(mes: str) -> Path:
    """Extrai cada zip renomeando o membro interno (nome críptico da RFB)
    para um nome previsível: Empresas0.csv, Estabelecimentos3.csv, ...
    """
    dir_zips = config.DIR_DATA / mes / "zips"
    dir_csv = config.DIR_DATA / mes / "csv"
    dir_csv.mkdir(parents=True, exist_ok=True)
    for nome in config.ARQUIVOS:
        alvo = dir_csv / (Path(nome).stem + ".csv")
        if alvo.exists() and alvo.stat().st_size > 0:
            continue
        caminho_zip = dir_zips / nome
        with zipfile.ZipFile(caminho_zip) as z:
            membros = z.namelist()
            if not membros:
                raise RuntimeError(f"{nome}: zip vazio.")
            with z.open(membros[0]) as origem, open(alvo, "wb") as destino:
                while True:
                    pedaco = origem.read(1024 * 1024 * 8)
                    if not pedaco:
                        break
                    destino.write(pedaco)
        print(f"  [extraido] {alvo.name}")
    return dir_csv
