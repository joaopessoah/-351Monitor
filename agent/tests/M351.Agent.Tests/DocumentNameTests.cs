using M351.Agent.Core.Privacy;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Extração do nome do arquivo a partir do título da janela. Os títulos abaixo são os formatos
/// reais que Word, Excel, PowerPoint, Acrobat, visualizadores e navegadores escrevem.
/// </summary>
public class DocumentNameTests
{
    [Theory]
    [InlineData("Contrato de locação.docx - Word", "Contrato de locação.docx")]
    [InlineData("Orçamento 2026.xlsx - Excel", "Orçamento 2026.xlsx")]
    [InlineData("Apresentação comercial.pptx - PowerPoint", "Apresentação comercial.pptx")]
    [InlineData("nota-fiscal-1234.pdf - Adobe Acrobat Reader (64-bit)", "nota-fiscal-1234.pdf")]
    [InlineData("balancete.pdf", "balancete.pdf")] // PDF aberto no navegador: título é só o arquivo
    [InlineData("anotações.txt - Bloco de Notas", "anotações.txt")]
    [InlineData("clientes.csv - LibreOffice Calc", "clientes.csv")]
    [InlineData("Proposta.docx [Modo de Compatibilidade] - Word", "Proposta.docx")]
    [InlineData("• Relatório mensal.docx - Word", "Relatório mensal.docx")]
    [InlineData("Planilha de custos.xlsx (Somente leitura) - Excel", "Planilha de custos.xlsx")]
    [InlineData("curriculo.pdf - Google Drive - Google Chrome", "curriculo.pdf")]
    public void Extrai_nome_do_arquivo_do_titulo(string titulo, string esperado) =>
        Assert.Equal(esperado, DocumentName.FromWindowTitle(titulo));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Documento1 - Word")]                       // documento novo, nunca salvo
    [InlineData("Notebook | Mercado Livre - Google Chrome")] // navegação comum
    [InlineData("Caixa de Entrada - Outlook")]
    [InlineData("setup.exe - Instalador")]                   // extensão fora da lista fechada
    [InlineData("Como abrir .pdf no Windows - Google Chrome")] // menção a extensão no meio do título
    public void Titulo_sem_documento_nao_inventa_nome(string? titulo) =>
        Assert.Null(DocumentName.FromWindowTitle(titulo));

    [Fact]
    public void Caminho_no_titulo_vira_so_o_nome_do_arquivo()
    {
        // Notepad++ e afins escrevem o caminho inteiro — que carrega o nome civil na pasta do
        // usuário. Só o último segmento pode sair da máquina.
        var nome = DocumentName.FromWindowTitle(@"C:\Users\joao.pessoa\Documentos\rescisão.docx - Notepad++");
        Assert.Equal("rescisão.docx", nome);
    }

    [Fact]
    public void Nome_atingido_pelo_mascaramento_e_descartado()
    {
        // O título chega aqui JÁ mascarado: "Senha do banco.docx" virou "*** do ***.docx".
        // Publicar esse nome seria publicar o rastro do que foi mascarado.
        Assert.Null(DocumentName.FromWindowTitle("*** do ***.docx - Word"));
    }

    [Fact]
    public void Nome_gigante_e_truncado_no_teto()
    {
        var titulo = new string('a', 300) + ".pdf - Acrobat";
        var nome = DocumentName.FromWindowTitle(titulo);
        Assert.NotNull(nome);
        Assert.Equal(DocumentName.MaxLength, nome!.Length);
    }

    [Theory]
    [InlineData("Contrato.docx", "docx")]
    [InlineData("balancete.PDF", "pdf")]
    [InlineData("sem-extensao", null)]
    [InlineData(null, null)]
    public void Extensao_sai_em_minusculas_e_so_da_lista_fechada(string? nome, string? esperado) =>
        Assert.Equal(esperado, DocumentName.ExtensionOf(nome));
}
