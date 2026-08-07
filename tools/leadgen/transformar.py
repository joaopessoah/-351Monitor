"""Transformação DuckDB: dumps da RFB → base_filtrada.parquet (só o alvo).

Filtros aplicados aqui (ver README): CNAE principal ∈ config.CNAES, situação
ATIVA (02), só matriz, idade ≥ 2 anos, exclui MEI e administração pública,
porte EPP/DEMAIS (ME/N.I. só com capital ≥ R$ 100 mil), tem e-mail ou fone.
"""

import duckdb

import config


def _cols_sql(nomes: list[str]) -> str:
    """Espec de colunas do read_csv: tudo VARCHAR (preserva zeros à esquerda)."""
    return "{" + ", ".join(f"'{c}': 'VARCHAR'" for c in nomes) + "}"


def _read_csv(caminho_glob: str, cols: list[str]) -> str:
    # CP1252 (não latin-1): os dumps da RFB trazem bytes 0x80-0x9F, que o
    # decodificador latin-1 estrito do DuckDB rejeita. Usa a extensão 'encodings'.
    return (
        f"read_csv('{caminho_glob}', delim=';', quote='\"', header=false, "
        f"encoding='CP1252', ignore_errors=true, columns={_cols_sql(cols)})"
    )


def transformar(mes: str, refazer: bool = False) -> str:
    """Gera data/<mes>/base_filtrada.parquet e devolve o caminho."""
    dir_mes = config.DIR_DATA / mes
    parquet = dir_mes / "base_filtrada.parquet"
    if parquet.exists() and not refazer:
        print(f"  [cache] {parquet.name} ja existe (use --refazer para reprocessar)")
        return str(parquet)

    dir_csv = (dir_mes / "csv").as_posix()
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
    con.execute(f"""
        CREATE TABLE est_alvo AS
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
        FROM {_read_csv(dir_csv + '/Estabelecimentos*.csv', config.COLS_ESTABELECIMENTOS)}
        WHERE situacao_cadastral = '02'
          AND identificador_matriz_filial = '1'
          AND cnae_fiscal_principal IN ({cnaes})
          AND try_strptime(data_inicio_atividade, '%Y%m%d')
                <= now() - INTERVAL {config.IDADE_MINIMA_ANOS} YEAR
          AND (coalesce(correio_eletronico, '') LIKE '%@%'
               OR length(regexp_replace(coalesce(telefone_1, ''), '[^0-9]', '', 'g')) >= 8)
    """)

    print("  [2/5] Empresas (porte, capital, natureza)...")
    con.execute(f"""
        CREATE TABLE emp_alvo AS
        SELECT cnpj_basico, razao_social, natureza_juridica,
               CAST(replace(coalesce(capital_social, '0'), ',', '.') AS DOUBLE) AS capital,
               coalesce(porte_empresa, '00') AS porte
        FROM {_read_csv(dir_csv + '/Empresas*.csv', config.COLS_EMPRESAS)}
        WHERE cnpj_basico IN (SELECT cnpj_basico FROM est_alvo)
          AND natureza_juridica NOT LIKE '1%'
          AND (coalesce(porte_empresa, '00') IN ('03', '05')
               OR (coalesce(porte_empresa, '00') IN ('01', '00')
                   AND CAST(replace(coalesce(capital_social, '0'), ',', '.') AS DOUBLE)
                         >= {config.CAPITAL_MINIMO_ME}))
    """)

    print("  [3/5] Simples (excluir MEI)...")
    con.execute(f"""
        CREATE TABLE mei AS
        SELECT cnpj_basico
        FROM {_read_csv(dir_csv + '/Simples.csv', config.COLS_SIMPLES)}
        WHERE opcao_mei = 'S'
          AND cnpj_basico IN (SELECT cnpj_basico FROM est_alvo)
    """)

    print("  [4/5] Socios (contato = 1o socio-administrador PF)...")
    con.execute(f"""
        CREATE TABLE contato AS
        SELECT cnpj_basico, nome_socio_razao_social AS contato
        FROM (
            SELECT s.cnpj_basico, s.nome_socio_razao_social,
                   row_number() OVER (
                       PARTITION BY s.cnpj_basico
                       ORDER BY CASE s.qualificacao_socio {prioridade_qual} ELSE 9 END,
                                s.data_entrada_sociedade
                   ) AS rn
            FROM {_read_csv(dir_csv + '/Socios*.csv', config.COLS_SOCIOS)} s
            WHERE s.identificador_socio = '2'
              AND s.qualificacao_socio IN ({quals})
              AND s.cnpj_basico IN (SELECT cnpj_basico FROM est_alvo)
        ) WHERE rn = 1
    """)

    print("  [5/5] Base final -> parquet...")
    con.execute(f"""
        CREATE TABLE munic AS
        SELECT codigo, descricao FROM {_read_csv(dir_csv + '/Municipios.csv', config.COLS_REFERENCIA)}
    """)
    con.execute(f"""
        COPY (
            SELECT e.cnpj14, e.cnpj_basico, e.nome_fantasia, e.cnae, e.inicio,
                   e.uf, e.fone1, e.fone2, e.email,
                   emp.razao_social, emp.capital, emp.porte,
                   c.contato,
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
