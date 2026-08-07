"""Transformação DuckDB: dumps da RFB → base_filtrada.parquet (só o alvo).

Filtros aplicados aqui (ver README): CNAE principal ∈ config.CNAES, situação
ATIVA (02), só matriz, idade ≥ 2 anos, exclui MEI e administração pública,
porte EPP/DEMAIS (ME/N.I. só com capital ≥ R$ 100 mil), tem e-mail ou fone.

A ingestão é ARQUIVO A ARQUIVO (CREATE + INSERTs): no modo econômico
(--economizar-disco, usado no GitHub Actions) cada zip é baixado, extraído,
ingerido e apagado na sequência — pico de disco ~11 GB em vez de ~30 GB.
"""

import duckdb

import config
import rfb


def _cols_sql(nomes: list[str]) -> str:
    """Espec de colunas do read_csv: tudo VARCHAR (preserva zeros à esquerda)."""
    return "{" + ", ".join(f"'{c}': 'VARCHAR'" for c in nomes) + "}"


def _read_csv(caminho: str, cols: list[str]) -> str:
    # CP1252 (não latin-1): os dumps da RFB trazem bytes 0x80-0x9F, que o
    # decodificador latin-1 estrito do DuckDB rejeita. Usa a extensão 'encodings'.
    return (
        f"read_csv('{caminho}', delim=';', quote='\"', header=false, "
        f"encoding='CP1252', ignore_errors=true, columns={_cols_sql(cols)})"
    )


def _ingerir_familia(con, mes: str, familia: str, cols: list[str],
                     tabela: str, corpo_select: str, economizar: bool) -> None:
    """Cria `tabela` a partir dos arquivos da família (ex.: Estabelecimentos0-9),
    um por vez. `corpo_select` usa o marcador {FONTE} no lugar do read_csv."""
    arquivos = [a for a in config.ARQUIVOS if a.startswith(familia)]
    for i, nome in enumerate(arquivos):
        csv = rfb.garantir_csv(mes, nome, economizar)
        select = corpo_select.replace("{FONTE}", _read_csv(csv.as_posix(), cols))
        if i == 0:
            con.execute(f"CREATE OR REPLACE TABLE {tabela} AS {select}")
        else:
            con.execute(f"INSERT INTO {tabela} {select}")
        if economizar:
            csv.unlink(missing_ok=True)
        print(f"      {nome} ok ({i + 1}/{len(arquivos)})")


def transformar(mes: str, refazer: bool = False, economizar: bool = False) -> str:
    """Gera data/<mes>/base_filtrada.parquet e devolve o caminho."""
    dir_mes = config.DIR_DATA / mes
    parquet = dir_mes / "base_filtrada.parquet"
    if parquet.exists() and not refazer:
        print(f"  [cache] {parquet.name} ja existe (use --refazer para reprocessar)")
        return str(parquet)

    dir_tmp = dir_mes / "tmp"
    dir_tmp.mkdir(parents=True, exist_ok=True)

    cnaes = ", ".join(f"'{c}'" for c in config.CNAES)
    quals = ", ".join(f"'{q}'" for q in config.QUALIFICACOES_CONTATO)
    prioridade_qual = " ".join(
        f"WHEN '{q}' THEN {i + 1}" for i, q in enumerate(config.QUALIFICACOES_CONTATO)
    )

    con = duckdb.connect()
    con.execute("SET memory_limit='4GB'")
    con.execute(f"SET temp_directory='{dir_tmp.as_posix()}'")
    con.execute("SET preserve_insertion_order=false")

    print("  [1/5] Estabelecimentos (filtro precoce na tabela grande)...")
    _ingerir_familia(con, mes, "Estabelecimentos", config.COLS_ESTABELECIMENTOS,
                     "est_alvo", f"""
        SELECT cnpj_basico,
               lpad(cnpj_basico, 8, '0') || lpad(cnpj_ordem, 4, '0')
                 || lpad(cnpj_dv, 2, '0')                          AS cnpj14,
               nome_fantasia,
               cnae_fiscal_principal                               AS cnae,
               try_strptime(data_inicio_atividade, '%Y%m%d')::DATE AS inicio,
               uf,
               municipio                                           AS cod_municipio,
               regexp_replace(coalesce(ddd_1, '') || coalesce(telefone_1, ''),
                              '[^0-9]', '', 'g')                   AS fone1,
               regexp_replace(coalesce(ddd_2, '') || coalesce(telefone_2, ''),
                              '[^0-9]', '', 'g')                   AS fone2,
               lower(trim(split_part(coalesce(correio_eletronico, ''), ';', 1)))
                                                                   AS email
        FROM {{FONTE}}
        WHERE situacao_cadastral = '02'
          AND identificador_matriz_filial = '1'
          AND cnae_fiscal_principal IN ({cnaes})
          AND try_strptime(data_inicio_atividade, '%Y%m%d')
                <= now() - INTERVAL {config.IDADE_MINIMA_ANOS} YEAR
          AND (coalesce(correio_eletronico, '') LIKE '%@%'
               OR length(regexp_replace(coalesce(telefone_1, ''), '[^0-9]', '', 'g')) >= 8)
    """, economizar)

    print("  [2/5] Empresas (porte, capital, natureza)...")
    _ingerir_familia(con, mes, "Empresas", config.COLS_EMPRESAS, "emp_alvo", f"""
        SELECT cnpj_basico, razao_social, natureza_juridica,
               CAST(replace(coalesce(capital_social, '0'), ',', '.') AS DOUBLE) AS capital,
               coalesce(porte_empresa, '00') AS porte
        FROM {{FONTE}}
        WHERE cnpj_basico IN (SELECT cnpj_basico FROM est_alvo)
          AND natureza_juridica NOT LIKE '1%'
          AND (coalesce(porte_empresa, '00') IN ('03', '05')
               OR (coalesce(porte_empresa, '00') IN ('01', '00')
                   AND CAST(replace(coalesce(capital_social, '0'), ',', '.') AS DOUBLE)
                         >= {config.CAPITAL_MINIMO_ME}))
    """, economizar)

    print("  [3/5] Simples (excluir MEI)...")
    _ingerir_familia(con, mes, "Simples", config.COLS_SIMPLES, "mei", """
        SELECT cnpj_basico
        FROM {FONTE}
        WHERE opcao_mei = 'S'
          AND cnpj_basico IN (SELECT cnpj_basico FROM est_alvo)
    """, economizar)

    print("  [4/5] Socios (contato = 1o socio-administrador PF, com qualificacao)...")
    _ingerir_familia(con, mes, "Socios", config.COLS_SOCIOS, "socios_alvo", f"""
        SELECT cnpj_basico, nome_socio_razao_social, qualificacao_socio,
               data_entrada_sociedade
        FROM {{FONTE}}
        WHERE identificador_socio = '2'
          AND qualificacao_socio IN ({quals})
          AND cnpj_basico IN (SELECT cnpj_basico FROM est_alvo)
    """, economizar)
    con.execute(f"""
        CREATE OR REPLACE TABLE contato AS
        SELECT cnpj_basico, nome_socio_razao_social AS contato,
               qualificacao_socio AS contato_qual
        FROM (
            SELECT s.*, row_number() OVER (
                       PARTITION BY cnpj_basico
                       ORDER BY CASE qualificacao_socio {prioridade_qual} ELSE 9 END,
                                data_entrada_sociedade
                   ) AS rn
            FROM socios_alvo s
        ) WHERE rn = 1
    """)

    print("  [5/5] Referencias e base final -> parquet...")
    _ingerir_familia(con, mes, "Municipios", config.COLS_REFERENCIA, "munic",
                     "SELECT codigo, descricao FROM {FONTE}", economizar)
    con.execute(f"""
        COPY (
            SELECT e.cnpj14, e.cnpj_basico, e.nome_fantasia, e.cnae, e.inicio,
                   e.uf, e.fone1, e.fone2, e.email,
                   emp.razao_social, emp.capital, emp.porte,
                   c.contato, c.contato_qual,
                   coalesce(m.descricao, '') AS municipio_nome
            FROM est_alvo e
            JOIN emp_alvo emp USING (cnpj_basico)
            ANTI JOIN mei USING (cnpj_basico)
            LEFT JOIN contato c USING (cnpj_basico)
            LEFT JOIN munic m ON m.codigo = e.cod_municipio
        ) TO '{parquet.as_posix()}' (FORMAT parquet)
    """)
    total = con.execute(f"SELECT count(*) FROM '{parquet.as_posix()}'").fetchone()[0]
    con.close()
    print(f"  [ok] base_filtrada.parquet: {total:,} empresas alvo".replace(",", "."))
    return str(parquet)
