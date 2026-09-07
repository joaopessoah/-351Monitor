using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F5 — CLASSIFICAÇÃO POR EQUIPE (spec seção 2.4: "o mesmo app pode ser produtivo no
    /// Marketing e improdutivo no Financeiro"). Sem regra por PESSOA nesta versão, por decisão
    /// da própria spec: classificação é sobre APLICATIVOS, e descer ao indivíduo transformaria
    /// a curadoria num julgamento de pessoa.
    ///
    /// POR QUE UMA TABELA NOVA E NÃO UMA COLUNA <c>team_id</c> EM <c>tenant_app_categories</c>:
    /// a regra da organização é (tenant, app) e é a chave primária de uma tabela que já está em
    /// produção, com <c>ON CONFLICT (tenant_id, app_id)</c> escrito em três caminhos de escrita
    /// (PUT individual, PUT em lote, DELETE de categoria) e lida pelo caminho MAIS QUENTE do
    /// produto (o LEFT JOIN da agregação diária). Enfiar o escopo lá dentro exigiria PK com
    /// coluna anulável (UNIQUE NULLS NOT DISTINCT), reescrita dos três upserts e um backfill —
    /// tudo para representar duas coisas diferentes na mesma linha. Aqui a regra GERAL fica
    /// intacta e a regra ESPECÍFICA mora ao lado, o que também torna a precedência legível numa
    /// linha de SQL (COALESCE(regra_da_equipe, regra_da_organizacao)).
    ///
    /// ORDEM DE PRECEDÊNCIA (a mesma em TODOS os lugares que resolvem a categoria de um app —
    /// agregação diária, listagem do catálogo e a tela de Classificação):
    ///   1. regra da EQUIPE da pessoa naquele dia  (tenant_app_team_categories)
    ///   2. regra da ORGANIZAÇÃO                   (tenant_app_categories)
    ///   3. nenhuma das duas → SEM CLASSIFICAÇÃO   (balde seconds_unclassified, jamais neutro)
    /// Ausência de regra de equipe é HERANÇA da regra geral, nunca "sem classificação": quem
    /// não declarou nada para a equipe continua com exatamente o comportamento de hoje.
    ///
    /// Toda escrita nesta tabela enfileira reagregação na MESMA transação (padrão atômico já
    /// usado pela curadoria da organização): trocar a regra da equipe muda os baldes do
    /// histórico recente exatamente como trocar a regra geral.
    /// </summary>
    public partial class ClassificacaoPorEquipeF5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE tenant_app_team_categories (
                    tenant_id   uuid NOT NULL,
                    team_id     uuid NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
                    app_id      uuid NOT NULL REFERENCES app_catalog(id),
                    category_id uuid NOT NULL REFERENCES categories(id),
                    created_at  timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT pk_tenant_app_team_categories PRIMARY KEY (tenant_id, team_id, app_id)
                );

                -- a agregação entra por (tenant, app) e filtra pela equipe da lane; o índice
                -- serve também a "quais equipes têm regra própria para este app?" da tela
                CREATE INDEX ix_tatc_tenant_app ON tenant_app_team_categories (tenant_id, app_id);
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP TABLE IF EXISTS tenant_app_team_categories;");
    }
}
