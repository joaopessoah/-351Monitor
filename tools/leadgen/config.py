"""Configuração do gerador de leads (dados abertos do CNPJ / Receita Federal).

Fundamentos no plano comercial: ICP = PME 10-200 funcionários, sweet spot
20-80 estações Windows (docs/design/05-produto-mvp.md). Pesos e heurísticas
documentados no README.md desta pasta — recalibrar mensalmente com as taxas
de conversão reais do CRM.
"""

from pathlib import Path

RAIZ = Path(__file__).resolve().parent
DIR_DATA = RAIZ / "data"
DIR_SAIDA = RAIZ / "saida"
ARQ_HISTORICO = DIR_DATA / "historico" / "exportados.csv"

# Espelho CDN dos dados abertos (Casa dos Dados) + oficial como plano B.
URL_ESPELHO = "https://dados-abertos-rf-cnpj.casadosdados.com.br/arquivos/"
URL_OFICIAL = "https://dadosabertos.rfb.gov.br/CNPJ/dados_abertos_cnpj/"

# CRM (dedupe e envio opcional). Token em MAIS351_CRM_TOKEN (.env ou ambiente).
CRM_API = "https://www.mais351monitor.com.br/crm/api/index.php"

# ---------------------------------------------------------------------------
# CNAEs alvo — onda 1 (código: (vertical, descrição curta, pontos, mult. estações))
# Formato na base RFB: 7 dígitos sem máscara.
# ---------------------------------------------------------------------------
CNAES = {
    "6920601": ("contabilidade", "Atividades de contabilidade", 25, 1.2),
    "6920602": ("contabilidade", "Consultoria e auditoria contabil", 25, 1.2),
    "6201501": ("software_ti", "Desenvolvimento de software sob encomenda", 20, 1.0),
    "6202300": ("software_ti", "Software customizavel", 20, 1.0),
    "6203100": ("software_ti", "Software nao-customizavel", 20, 1.0),
    "6204000": ("software_ti", "Consultoria em TI", 20, 1.0),
    "6209100": ("software_ti", "Suporte tecnico em TI (MSP)", 20, 1.0),
    "6911701": ("advocacia", "Servicos advocaticios", 18, 0.8),
    "8211300": ("bpo_agencias", "Servicos combinados de escritorio (BPO)", 22, 1.5),
    "8219999": ("bpo_agencias", "Apoio administrativo", 22, 1.5),
    "8220200": ("bpo_agencias", "Teleatendimento / call center", 22, 2.5),
    "7311400": ("bpo_agencias", "Agencia de publicidade", 15, 0.9),
}
# Extensões futuras (avaliar na calibração): "6311900" hosting/dados,
# "7020400" consultoria em gestão, "6822600" administração de imóveis.

VERTICAIS = ("contabilidade", "software_ti", "advocacia", "bpo_agencias")

# ---------------------------------------------------------------------------
# Filtros
# ---------------------------------------------------------------------------
IDADE_MINIMA_ANOS = 2
CAPITAL_MINIMO_ME = 100_000  # ME/porte não informado só entram acima disso

# ---------------------------------------------------------------------------
# Score (0-100): CNAE(25) + porte(20) + capital(15) + geografia(15)
#                + domínio de e-mail(10) + contatabilidade(10) + idade(5)
# ---------------------------------------------------------------------------
PONTOS_PORTE = {"03": 20, "05": 16, "01": 8, "00": 8}  # EPP, DEMAIS, ME, N/I

# (limite inferior do capital em R$, pontos)
PONTOS_CAPITAL = [(1_000_000, 15), (500_000, 12), (200_000, 9), (100_000, 6), (0, 3)]

UF_BOOST_PADRAO = "SP"
PONTOS_GEO_CAPITAL_BOOST = 15   # capital da UF em destaque (São Paulo capital)
PONTOS_GEO_UF_BOOST = 13        # interior da UF em destaque
PONTOS_GEO_SUDESTE_SUL = 10     # RJ, MG, ES, PR, SC, RS
PONTOS_GEO_DEMAIS = 5
UFS_SUDESTE_SUL = ("RJ", "MG", "ES", "PR", "SC", "RS")

DOMINIOS_GRATIS = (
    "gmail.com", "hotmail.com", "outlook.com", "yahoo.com", "yahoo.com.br",
    "bol.com.br", "uol.com.br", "terra.com.br", "ig.com.br", "live.com",
    "msn.com", "icloud.com", "globo.com", "oi.com.br", "zipmail.com.br",
)
PONTOS_EMAIL_PROPRIO = 10
PONTOS_EMAIL_GRATIS = 3
PONTOS_CONTATO_AMBOS = 10   # e-mail E telefone
PONTOS_CONTATO_SO_EMAIL = 6
PONTOS_CONTATO_SO_FONE = 4

# (idade mínima em anos, pontos) — avaliadas em ordem
PONTOS_IDADE = [(15, 4), (5, 5), (2, 3)]

# ---------------------------------------------------------------------------
# Heurística de estações Windows (proxy do ICP 20-80)
# estacoes = base_porte * mult_cnae * ajuste_capital, limitado a [5, 200]
# ---------------------------------------------------------------------------
ESTACOES_BASE_PORTE = {"01": 8, "00": 8, "03": 25, "05": 60}
AJUSTE_CAPITAL = [(1_000_000, 1.3), (500_000, 1.15), (100_000, 1.0), (0, 0.7)]
ESTACOES_MIN, ESTACOES_MAX = 5, 200

# ---------------------------------------------------------------------------
# Layouts dos CSVs da RFB (ordem exata das colunas; tudo VARCHAR)
# ---------------------------------------------------------------------------
COLS_EMPRESAS = [
    "cnpj_basico", "razao_social", "natureza_juridica",
    "qualificacao_responsavel", "capital_social", "porte_empresa",
    "ente_federativo_responsavel",
]
COLS_ESTABELECIMENTOS = [
    "cnpj_basico", "cnpj_ordem", "cnpj_dv", "identificador_matriz_filial",
    "nome_fantasia", "situacao_cadastral", "data_situacao_cadastral",
    "motivo_situacao_cadastral", "nome_cidade_exterior", "pais",
    "data_inicio_atividade", "cnae_fiscal_principal", "cnae_fiscal_secundaria",
    "tipo_logradouro", "logradouro", "numero", "complemento", "bairro", "cep",
    "uf", "municipio", "ddd_1", "telefone_1", "ddd_2", "telefone_2",
    "ddd_fax", "fax", "correio_eletronico", "situacao_especial",
    "data_situacao_especial",
]
COLS_SOCIOS = [
    "cnpj_basico", "identificador_socio", "nome_socio_razao_social",
    "cpf_cnpj_socio", "qualificacao_socio", "data_entrada_sociedade", "pais",
    "representante_legal", "nome_do_representante",
    "qualificacao_representante_legal", "faixa_etaria",
]
COLS_SIMPLES = [
    "cnpj_basico", "opcao_pelo_simples", "data_opcao_simples",
    "data_exclusao_simples", "opcao_mei", "data_opcao_mei",
    "data_exclusao_mei",
]
COLS_REFERENCIA = ["codigo", "descricao"]  # Cnaes.zip e Municipios.zip

# Famílias de arquivos usadas (Socios: contato; demais: filtros/dados)
ARQUIVOS = (
    [f"Empresas{i}.zip" for i in range(10)]
    + [f"Estabelecimentos{i}.zip" for i in range(10)]
    + [f"Socios{i}.zip" for i in range(10)]
    + ["Simples.zip", "Cnaes.zip", "Municipios.zip"]
)

# Qualificação de sócio aceita como contato (ordem de prioridade)
QUALIFICACOES_CONTATO = ("49", "05", "16", "22")  # sócio-adm, adm, presidente, sócio
