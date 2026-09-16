using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace M351.Infrastructure.Data.Migrations
{
    /// <summary>
    /// SITE E ARQUIVO NA PRESENÇA "AGORA" — complemento da SitesEDocumentosF10.
    ///
    /// device_current_state é a projeção do instante (a faixa "Equipe agora" da Visão Geral) e já
    /// carregava `foreground_process` e `foreground_title`. Sem estas duas colunas, o gestor via
    /// "chrome.exe" em tempo real e precisava esperar o pipeline de intervalização para descobrir
    /// QUAL site — o que, para a pergunta que a faixa responde ("o que está acontecendo agora?"),
    /// é chegar tarde.
    ///
    /// Migration separada da anterior de propósito: aquela já foi para produção, e migration
    /// publicada não se reescreve.
    ///
    /// As duas colunas seguem exatamente a disciplina das que já existiam ali: nascem nulas, são
    /// preenchidas pelo CurrentStateProjector a partir do ACTIVE_WINDOW_CHANGED (que já chega do
    /// agente com política de títulos, janela anônima e chaves de coleta aplicadas), e são ZERADAS
    /// junto com processo/título em SESSION_END, AGENT_STOP, SYSTEM_SUSPEND e quando o gestor
    /// pausa o dispositivo. Nenhum histórico: a linha é o agora, e o passado vive nos intervalos.
    /// </summary>
    public partial class SiteEDocumentoNaPresencaF10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                ALTER TABLE device_current_state ADD COLUMN foreground_site text;
                ALTER TABLE device_current_state ADD COLUMN foreground_document text;
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                ALTER TABLE device_current_state DROP COLUMN IF EXISTS foreground_document;
                ALTER TABLE device_current_state DROP COLUMN IF EXISTS foreground_site;
                """);
    }
}
