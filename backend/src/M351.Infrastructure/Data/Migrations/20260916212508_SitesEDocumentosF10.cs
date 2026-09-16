using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// SITES E DOCUMENTOS — o agente passa a reportar o DOMÍNIO do site em foco no navegador e o
    /// NOME do arquivo aberto, e o produto passa a classificar tempo de navegação por SITE.
    ///
    /// Era o último item "a avaliar" da tabela canônica de decisões (CONSIDERACOES-E-DECISOES.md,
    /// linha 117: "Coleta de URLs/domínios de navegação — avaliar, somente domínio, nunca URL
    /// completa") e já estava escrito no compromisso público (design 04-lgpd §"Coleta de
    /// navegação: somente domínio, com categorização — não conteúdo"). Esta migration é a parte
    /// de banco disso.
    ///
    /// AS QUATRO TABELAS SÃO ESPELHOS DELIBERADOS DAS DE APPS. site_catalog está para app_catalog
    /// assim como tenant_site_categories está para tenant_app_categories: mesma forma, mesmas
    /// garantias, mesma precedência de escopo. Não é acaso — é o que permite reaproveitar
    /// `categories` (o cliente classifica site e app no MESMO vocabulário, com as MESMAS
    /// categorias) e escrever a resolução inteira num COALESCE de quatro termos.
    ///
    /// PRECEDÊNCIA DA CLASSIFICAÇÃO, agora com quatro degraus (DailyAggregationService é quem
    /// manda; catálogo e telas repetem a mesma ordem):
    ///   1. regra de SITE da EQUIPE       (tenant_site_team_categories)
    ///   2. regra de SITE da ORGANIZAÇÃO  (tenant_site_categories)
    ///   3. regra de APP da EQUIPE        (tenant_app_team_categories)
    ///   4. regra de APP da ORGANIZAÇÃO   (tenant_app_categories)
    ///   5. nenhuma → SEM CLASSIFICAÇÃO   (jamais neutro)
    /// Site vence app de propósito: é a regra MAIS ESPECÍFICA. Sem isso, marcar
    /// "mercadolivre.com.br" como improdutivo não teria efeito nenhum enquanto "chrome.exe"
    /// estivesse classificado como Navegação — que é exatamente o buraco que esta leva fecha.
    ///
    /// COLUNAS EM TABELA PARTICIONADA: os quatro ADD COLUMN de raw_events e activity_intervals
    /// são todos nulos por padrão, então o PostgreSQL 11+ os aplica sem reescrever tabela; a
    /// propagação para as partições existentes é automática.
    ///
    /// DUAS CHAVES NOVAS NA CONFIG (site_capture e document_capture) NASCEM LIGADAS, INCLUSIVE
    /// PARA QUEM JÁ EXISTE — e é por isso que o UPDATE no fim sobe também notice_version: escopo
    /// de coleta que aumenta sem o funcionário ser avisado de novo contradiz o produto inteiro. O
    /// bump reexibe o aviso de ciência na frota e gera NOTICE_ACK novo; config_version propaga as
    /// chaves no próximo ack de cada device. Uma controladora que não queira a coleta desliga as
    /// duas em Configurações › Coleta, e a página pública de transparência passa a dizer isso.
    /// </summary>
    public partial class SitesEDocumentosF10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----- chaves de coleta na config do tenant -----
            migrationBuilder.AddColumn<bool>(
                name: "document_capture",
                table: "tenant_agent_configs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "site_capture",
                table: "tenant_agent_configs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // ----- campos novos do ACTIVE_WINDOW_CHANGED -----
            migrationBuilder.Sql("""
                -- Colunas dedicadas (e não só o payload jsonb) pelo mesmo motivo de window_title:
                -- é o que a intervalização lê, e o que a ingestão SANEIA antes de gravar. O payload
                -- cru continua intacto ao lado, como informação.
                ALTER TABLE raw_events ADD COLUMN site_domain text;
                ALTER TABLE raw_events ADD COLUMN document_name text;

                -- site_id (e não o texto do domínio) no intervalo: mesma escolha do app_id — a
                -- identidade mora no catálogo global, e trocar o display_name de um site não
                -- reescreve doze meses de intervalos.
                ALTER TABLE activity_intervals ADD COLUMN site_id uuid;
                ALTER TABLE activity_intervals ADD COLUMN document_name text;

                -- Relatório de documentos: varre por (tenant, dia) só as linhas que TÊM arquivo.
                -- Índice parcial porque a esmagadora maioria dos intervalos não tem documento.
                CREATE INDEX ix_intervals_document ON activity_intervals (tenant_id, source_day)
                    WHERE document_name IS NOT NULL;
                """);

            // ----- catálogo de sites e regras do tenant -----
            migrationBuilder.Sql("""
                CREATE TABLE site_catalog (
                  id uuid PRIMARY KEY,
                  domain text UNIQUE NOT NULL,
                  display_name text NOT NULL,
                  default_category text,
                  curated boolean NOT NULL DEFAULT false
                );

                CREATE TABLE tenant_site_categories (
                  tenant_id uuid NOT NULL,
                  site_id uuid NOT NULL REFERENCES site_catalog(id),
                  category_id uuid NOT NULL REFERENCES categories(id),
                  custom_display_name text,
                  PRIMARY KEY (tenant_id, site_id)
                );

                CREATE TABLE tenant_site_team_categories (
                  tenant_id   uuid NOT NULL,
                  team_id     uuid NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
                  site_id     uuid NOT NULL REFERENCES site_catalog(id),
                  category_id uuid NOT NULL REFERENCES categories(id),
                  created_at  timestamptz NOT NULL DEFAULT now(),
                  CONSTRAINT pk_tenant_site_team_categories PRIMARY KEY (tenant_id, team_id, site_id)
                );
                CREATE INDEX ix_tstc_tenant_site ON tenant_site_team_categories (tenant_id, site_id);
                """);

            // ----- agregado diário por site -----
            migrationBuilder.Sql("""
                CREATE TABLE daily_site_usage (
                  tenant_id uuid NOT NULL, summary_date date NOT NULL,
                  device_id uuid NOT NULL,
                  device_user_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                  site_id uuid NOT NULL,
                  seconds_active int NOT NULL, visit_count int NOT NULL,
                  PRIMARY KEY (tenant_id, summary_date, device_id, device_user_id, site_id)
                );
                CREATE INDEX ix_dsu_tenant_date_site ON daily_site_usage (tenant_id, summary_date, site_id);
                """);

            // ----- categorias novas do vocabulário, para QUEM JÁ EXISTE -----
            // App e site compartilham a tabela `categories`, mas o mundo dos sites tem baldes
            // que o dos executáveis não tinha. Sem semear em quem já existe, o dicionário
            // brasileiro de sites sugeriria categorias que o tenant não tem — e a tela de
            // "aplicar sugestões" não teria para onde apontar. Mesma lista, mesmas cores e
            // mesmas classificações do CreateOrgCommand.SeedCategoriesAsync (são espelhos).
            // ON CONFLICT DO NOTHING: tenant que já criou uma categoria com esse nome mantém a dele.
            migrationBuilder.Sql("""
                INSERT INTO categories (id, tenant_id, name, classification, color)
                SELECT gen_random_uuid(), o.id, v.name, v.classification, v.color
                FROM organizations o
                CROSS JOIN (VALUES
                    ('Governo/Fisco',     1::smallint, '#0284c7'),
                    ('Bancos/Financeiro', 0::smallint, '#16a34a'),
                    ('Busca e portais',   0::smallint, '#94a3b8'),
                    ('Notícias',          0::smallint, '#0ea5e9'),
                    ('Compras',          -1::smallint, '#f59e0b')
                ) AS v(name, classification, color)
                ON CONFLICT (tenant_id, name) DO NOTHING;
                """);

            // ----- a frota precisa saber, e o funcionário precisa ser avisado -----
            migrationBuilder.Sql("""
                UPDATE tenant_agent_configs
                SET config_version = config_version + 1,
                    notice_version = notice_version + 1,
                    updated_at = now();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS daily_site_usage;
                DROP TABLE IF EXISTS tenant_site_team_categories;
                DROP TABLE IF EXISTS tenant_site_categories;
                DROP TABLE IF EXISTS site_catalog;
                DROP INDEX IF EXISTS ix_intervals_document;
                ALTER TABLE activity_intervals DROP COLUMN IF EXISTS document_name;
                ALTER TABLE activity_intervals DROP COLUMN IF EXISTS site_id;
                ALTER TABLE raw_events DROP COLUMN IF EXISTS document_name;
                ALTER TABLE raw_events DROP COLUMN IF EXISTS site_domain;
                """);

            migrationBuilder.DropColumn(name: "document_capture", table: "tenant_agent_configs");
            migrationBuilder.DropColumn(name: "site_capture", table: "tenant_agent_configs");
        }
    }
}
