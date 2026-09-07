using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// F7 — EQUIPES DE VERDADE, feriados e jornada por equipe (spec seção 2.3 e seção 6).
    ///
    /// Até aqui "equipe" era a etiqueta livre de <c>devices.tags</c>: um rótulo por
    /// DISPOSITIVO, sem nome canônico, sem jornada e sem vínculo com a pessoa. A spec 2.3 pede
    /// a entidade <c>teams</c> vinculada a PESSOA (windows_sid), não a máquina — porque a
    /// pessoa que troca de notebook não pode trocar de equipe, e porque a classificação por
    /// equipe (F5) precisa de um dono estável da regra.
    ///
    /// AS DUAS COEXISTEM, de propósito:
    ///  - <c>devices.tags</c> CONTINUA existindo e continua sendo o filtro <c>?tag</c> de todos
    ///    os dashboards, relatórios e exports (nada foi removido, nenhum contrato mudou);
    ///  - <c>teams.tag</c> é a ponte: a equipe DECLARA qual etiqueta legada ela representa, e
    ///    quem ainda não vinculou pessoa nenhuma continua funcionando pelo caminho antigo;
    ///  - onde as duas respondem, a EQUIPE DA PESSOA VENCE (team_members), e a etiqueta do
    ///    dispositivo é só o atalho de migração. A ordem está escrita no
    ///    DailyAggregationService, que é quem resolve a equipe da lane.
    ///
    /// Decisões registradas nesta migration:
    ///  - <c>team_members</c> tem PK (tenant_id, windows_sid) e NÃO (tenant_id, team_id,
    ///    windows_sid): UMA pessoa em UMA equipe. Sem isso a classificação por equipe seria
    ///    ambígua (dois times classificando o mesmo app de formas opostas para a mesma pessoa)
    ///    e a agregação teria de escolher arbitrariamente — pior, duplicaria linhas no JOIN e
    ///    inflaria os segundos. Uma matriz pessoa × equipe pode voltar depois, mas exigiria uma
    ///    regra de desempate explícita antes.
    ///  - sem FK de <c>team_members.windows_sid</c> para <c>people</c>: a linha em people só
    ///    existe quando houve apelido ou mesclagem (ver PessoasF6), então exigir a FK obrigaria
    ///    criar people vazio a cada vínculo. O SID é a identidade canônica por si.
    ///  - <c>teams.work_hours</c> guarda a jornada declarada DA EQUIPE no mesmo formato do
    ///    <c>organizations.business_hours</c> ({"days":[1..7 ISO],"start":"08:00","end":"18:00"}),
    ///    para o parser ser um só (BusinessHoursWindow). NULL = a equipe herda a jornada da
    ///    organização — ausência é herança, nunca "sem jornada".
    ///  - <c>organization_holidays</c> é por ORGANIZAÇÃO e não por equipe: feriado nacional é do
    ///    calendário, não do time. Feriado municipal por equipe seria outra tabela; não há
    ///    pedido. Entra no cálculo de CAPACIDADE UTILIZADA saindo do denominador (o dia não
    ///    existiu), e NÃO afeta agregado nenhum — por isso nenhuma reagregação é enfileirada
    ///    quando um feriado é criado ou removido.
    /// </summary>
    public partial class EquipesFeriadosF7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE teams (
                    id         uuid PRIMARY KEY,
                    tenant_id  uuid NOT NULL REFERENCES organizations(id),
                    name       text NOT NULL,
                    -- etiqueta legada de devices.tags que ESTA equipe representa (atalho de
                    -- migração). NULL = equipe só por vínculo de pessoa.
                    tag        text,
                    -- jornada declarada da equipe, mesmo jsonb de organizations.business_hours;
                    -- NULL = herda a jornada da organização
                    work_hours jsonb,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT uq_teams_tenant_name UNIQUE (tenant_id, name)
                );

                -- uma etiqueta legada não pode ser reivindicada por duas equipes: o índice
                -- parcial deixa vários NULL conviverem e barra o tag repetido
                CREATE UNIQUE INDEX ux_teams_tenant_tag ON teams (tenant_id, tag) WHERE tag IS NOT NULL;

                CREATE TABLE team_members (
                    tenant_id   uuid NOT NULL,
                    team_id     uuid NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
                    windows_sid text NOT NULL,
                    created_at  timestamptz NOT NULL DEFAULT now(),
                    -- UMA pessoa em UMA equipe (ver o cabeçalho): a PK é o SID, não o par
                    CONSTRAINT pk_team_members PRIMARY KEY (tenant_id, windows_sid)
                );
                CREATE INDEX ix_team_members_team ON team_members (tenant_id, team_id);

                CREATE TABLE organization_holidays (
                    tenant_id    uuid NOT NULL REFERENCES organizations(id),
                    holiday_date date NOT NULL,
                    name         text NOT NULL,
                    created_at   timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT pk_organization_holidays PRIMARY KEY (tenant_id, holiday_date)
                );
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS organization_holidays;
                DROP TABLE IF EXISTS team_members;
                DROP TABLE IF EXISTS teams;
                """);
    }
}
