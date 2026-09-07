using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F6 — ANOTAÇÃO DE PERÍODO e CONTESTAÇÃO DE CLASSIFICAÇÃO com revisão do gestor
    /// (decisão 6 do spec de 07/09/2026: "página do colaborador entra, com os próprios dados,
    /// anotação de período e contestação de classificação com revisão do gestor").
    ///
    /// POR QUE ESTA TABELA EXISTE: a medição sabe QUANTO tempo a máquina ficou ativa, nunca
    /// POR QUE. Reunião presencial, treinamento, visita a cliente, máquina em manutenção — tudo
    /// isso aparece como ausência de atividade e o número, sozinho, mente por omissão. A
    /// anotação é o CONTEXTO que a própria pessoa (ou o gestor) escreve sobre um período; a
    /// contestação é a discordância registrada sobre COMO um aplicativo foi classificado.
    ///
    /// <c>kind</c> separa as duas coisas de propósito, na mesma tabela: o ciclo de vida é
    /// idêntico (nasce aberta, o gestor aceita ou recusa com uma resposta) e a tela é a mesma
    /// lista do período. Duas tabelas duplicariam o CRUD, a auditoria e o WHERE de tenant.
    ///
    /// <c>status</c> nasce 'aberta' e só o gestor (Admin+) move para 'aceita' ou 'recusada'.
    /// CONTESTAÇÃO ACEITA NÃO ALTERA AGREGADO: nada aqui reescreve daily_device_summaries. O
    /// registro é insumo da CURADORIA (o gestor decide remapear a categoria em
    /// Configurações › Classificação, e é a reagregação que muda número). Se aceitar mudasse o
    /// agregado, o histórico deixaria de ser reprodutível a partir dos eventos e o dado perderia
    /// valor probatório — exatamente o que a trilha de auditoria existe para preservar.
    ///
    /// <c>app_id</c> é opcional e só faz sentido na contestação (o aplicativo cuja classificação
    /// se contesta). FK para app_catalog porque o catálogo é GLOBAL e imutável na prática; o
    /// ON DELETE é omitido de propósito — app do catálogo não é apagado.
    ///
    /// Sem entidade EF (como daily_*, people e management_alerts): DDL cru, e o
    /// <c>tenant_id</c> vai MANUSCRITO em todo WHERE do controller — não há filtro global aqui.
    /// A referência à pessoa é o <c>windows_sid</c> (a mesma identidade de /people, já resolvida
    /// pela mesclagem), não uma FK: SID é texto do tenant e uma pessoa pode não ter linha em
    /// people. Uma exclusão de titular (DSR) remove as anotações pelo par (tenant, sid).
    /// </summary>
    public partial class AnotacoesEContestacoesF6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE person_notes (
                    id                   uuid        PRIMARY KEY,
                    tenant_id            uuid        NOT NULL,
                    windows_sid          text        NOT NULL,
                    kind                 text        NOT NULL,
                    started_at           timestamptz NOT NULL,
                    ended_at             timestamptz NOT NULL,
                    app_id               uuid        NULL REFERENCES app_catalog(id),
                    body                 text        NOT NULL,
                    status               text        NOT NULL DEFAULT 'aberta',
                    created_by_user_id   uuid        NOT NULL,
                    created_at           timestamptz NOT NULL DEFAULT now(),
                    reviewed_by_user_id  uuid        NULL,
                    reviewed_at          timestamptz NULL,
                    review_note          text        NULL,
                    CONSTRAINT ck_person_notes_kind
                        CHECK (kind IN ('anotacao','contestacao')),
                    CONSTRAINT ck_person_notes_status
                        CHECK (status IN ('aberta','aceita','recusada')),
                    -- período de UM instante é válido (marco pontual); invertido não é
                    CONSTRAINT ck_person_notes_periodo
                        CHECK (ended_at >= started_at),
                    -- revisão é atômica: ou os três campos estão nulos (aberta) ou quem
                    -- revisou e quando estão preenchidos. Impede "aceita por ninguém".
                    CONSTRAINT ck_person_notes_revisao
                        CHECK ((status = 'aberta'  AND reviewed_by_user_id IS NULL AND reviewed_at IS NULL)
                            OR (status <> 'aberta' AND reviewed_by_user_id IS NOT NULL AND reviewed_at IS NOT NULL))
                );

                -- a consulta do GET é sempre "as anotações desta pessoa cujo período cruza o
                -- recorte da tela, mais recentes primeiro"
                CREATE INDEX ix_person_notes_pessoa_periodo
                    ON person_notes (tenant_id, windows_sid, started_at DESC);

                -- a fila de revisão do gestor: as abertas do tenant, mais antigas primeiro
                CREATE INDEX ix_person_notes_abertas
                    ON person_notes (tenant_id, created_at)
                    WHERE status = 'aberta';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE person_notes;");
        }
    }
}
