using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — pessoas. A identidade canônica de uma pessoa no tenant é o windows_sid, que
    /// device_users JÁ grava; o que faltava era (a) um lugar para o apelido dado pelo admin e
    /// (b) mesclagem quando a MESMA pessoa aparece com dois SIDs (conta local numa máquina fora
    /// do domínio, por exemplo).
    ///
    /// Por que NÃO uma tabela people com id próprio e person_id em device_users: exigiria mexer
    /// no caminho quente da ingestão (IngestService.UpsertDeviceUsers) e um backfill por tenant.
    /// Chavear people pelo próprio SID dá identidade cross-device de graça — a lista de
    /// colaboradores agrupa daily_device_summaries por (tenant, SID resolvido) — sem tocar na
    /// ingestão e sem migração de dado. Resolve a limitação registrada no DeviceUsersController
    /// ("a mesma pessoa em duas máquinas tem dois registros distintos") na camada de leitura.
    ///
    /// merged_into_sid aponta para o SID que absorve este; a leitura resolve com COALESCE e UM
    /// salto (mesclagem em cadeia é impedida na aplicação: o alvo precisa estar sem
    /// merged_into_sid). Linha em people existe SÓ quando houve apelido ou mesclagem — a lista
    /// de colaboradores funciona com a tabela vazia.
    ///
    /// Sem FK para device_users de propósito: o SID sobrevive à exclusão de um dispositivo, e o
    /// DSR anonimiza device_users preservando o agregado. A limpeza de people entra no DSR de
    /// titular junto da anonimização (tratado no serviço, não por cascade).
    /// </summary>
    public partial class PessoasF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE people (
                    tenant_id       uuid NOT NULL,
                    windows_sid     text NOT NULL,
                    display_name    text NULL,
                    merged_into_sid text NULL,
                    created_at      timestamptz NOT NULL DEFAULT now(),
                    updated_at      timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT pk_people PRIMARY KEY (tenant_id, windows_sid),
                    CONSTRAINT ck_people_merge_nao_aponta_para_si
                        CHECK (merged_into_sid IS NULL OR merged_into_sid <> windows_sid)
                );
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP TABLE people;");
    }
}
