using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace M351.Domain.Privacy;

/// <summary>
/// Regras do texto do aviso de ciência escrito pela CONTROLADORA (coluna notice_text de
/// TenantAgentConfig, editada no portal e entregue à frota no ack do batch).
///
/// O tenant escreve só o CORPO. O enquadramento jurídico (<see cref="FixedFraming"/>) é
/// concatenado pelo AGENTE, no NoticeTextComposer, e não passa por aqui: nenhum texto salvo
/// nesta coluna consegue removê-lo, truncá-lo ou desativá-lo. Esta classe existe para o
/// servidor recusar, ANTES de a config chegar à frota, três coisas que quebrariam o aviso:
///
/// 1. Texto que não cabe na janela do aviso. O agente truncaria em silêncio um texto grande
///    demais, e um aviso truncado é um aviso que informa pela metade. O limite daqui é
///    deliberadamente MENOR que o do agente: aqui recusamos, lá nunca chega a truncar.
/// 2. HTML e qualquer marcação. O aviso é um rótulo de texto puro do Windows Forms: marcação
///    não é interpretada, apareceria crua para o funcionário e só serviria para tentar
///    disfarçar o texto (por exemplo, empurrar o enquadramento para fora da vista).
/// 3. Texto que imita pedido de consentimento. O NOTICE_ACK é registro de CIÊNCIA, e é isso
///    que sustenta a base legal do controlador. Um corpo escrito como "ao clicar você
///    consente" transformaria o aviso em algo que ele não é, ainda que o enquadramento fixo
///    venha logo abaixo desmentindo.
///
/// ATENÇÃO: <see cref="DefaultBody"/> e <see cref="FixedFraming"/> são cópias literais de
/// M351.Agent.Core.Privacy.NoticeTextComposer (soluções separadas, sem referência entre elas).
/// Ao mexer lá, mexa aqui: o portal usa estas constantes para mostrar o preview do texto final.
/// </summary>
public static class NoticeTextPolicy
{
    /// <summary>Corpo padrão do agente, usado quando o tenant não escreveu texto próprio.</summary>
    public const string DefaultBody =
        "Esta máquina é monitorada pela sua empresa.\n\n" +
        "São coletados: aplicativo e título da janela em foco (conforme a política da " +
        "empresa), eventos de sessão (logon/bloqueio), ociosidade e saúde do agente.";

    /// <summary>Enquadramento fixo, sempre concatenado pelo agente e não editável pelo tenant.</summary>
    public const string FixedFraming =
        "Este aviso registra a sua ciência. Não é um pedido de consentimento.\n" +
        "Você pode ver o que está sendo coletado agora a qualquer momento, pelo ícone " +
        "de monitoramento na área de notificação.";

    /// <summary>Separador entre o corpo e o enquadramento (o mesmo do NoticeTextComposer).</summary>
    private const string Separator = "\n\n";

    /// <summary>
    /// Orçamento da janela do aviso (NoticeForm, 480x300 com rótulo em AutoEllipsis): tamanho
    /// máximo do texto FINAL, já com o enquadramento fixo. Acima disso o aviso deixa de ser
    /// legível de uma vez só, que é o que dá sentido a ele.
    /// </summary>
    public const int MaxComposedLength = 1_200;

    /// <summary>
    /// Quanto sobra para o corpo do tenant depois de reservar o enquadramento fixo. É derivado,
    /// nunca fixado à mão: se o enquadramento crescer, o espaço do tenant encolhe sozinho.
    /// </summary>
    public static int MaxBodyLength => MaxComposedLength - Separator.Length - FixedFraming.Length;

    /// <summary>
    /// Texto final que o funcionário vê: corpo do tenant (ou o padrão) + enquadramento fixo.
    /// Mesma montagem do NoticeTextComposer do agente, para o preview do portal bater com a
    /// janela de verdade.
    /// </summary>
    public static string Compose(string? tenantBody)
    {
        var body = string.IsNullOrWhiteSpace(tenantBody) ? DefaultBody : tenantBody.Trim();
        return $"{body}{Separator}{FixedFraming}";
    }

    /// <summary>
    /// Normaliza o texto recebido do portal: quebras de linha do Windows viram \n (o rótulo do
    /// agente só entende \n) e sobras de espaço nas pontas caem fora. Texto só com espaço vira
    /// string vazia, que o chamador trata como "voltar ao aviso padrão".
    /// </summary>
    public static string Normalize(string raw) =>
        raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>Motivo da recusa: `Code` vai na extensão `code` do ProblemDetails.</summary>
    public sealed record Rejection(string Code, string Message);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Marcação de qualquer tipo: tag, entidade HTML, link/ênfase/código de Markdown.</summary>
    private static readonly Regex[] MarkupPatterns =
    [
        new("[<>]", RegexOptions.None, RegexTimeout),
        new(@"&(#[0-9]{1,6}|#x[0-9a-fA-F]{1,6}|[a-zA-Z][a-zA-Z0-9]{1,10});", RegexOptions.None, RegexTimeout),
        new(@"\[[^\]]*\]\([^)]*\)", RegexOptions.None, RegexTimeout),
        new(@"(\*\*|__|~~|`)", RegexOptions.None, RegexTimeout),
    ];

    /// <summary>
    /// Construções que pedem consentimento ao funcionário. Casadas sobre o texto sem acento e
    /// em minúsculas, para "consentimento" e "consinto" não escaparem por acentuação.
    /// A lista é deliberadamente conservadora: na dúvida o texto é recusado, com uma mensagem
    /// que explica o porquê e o que escrever no lugar.
    /// </summary>
    private static readonly Regex[] ConsentPatterns =
    [
        new(@"\b(consinto|autorizo|concordo)\b", RegexOptions.None, RegexTimeout),
        new(@"\bvoce\s+(consente|autoriza|concorda|aceita)\b", RegexOptions.None, RegexTimeout),
        new(@"\bao\s+(clicar|continuar|prosseguir|confirmar|fechar)\b[^.]{0,120}\b(consent|autoriz|concord|aceit)",
            RegexOptions.None, RegexTimeout),
        new(@"\baceit\w*\s+(os\s+|estes\s+|esses\s+)?termos\b", RegexOptions.None, RegexTimeout),
        new(@"\btermo\s+de\s+consentimento\b", RegexOptions.None, RegexTimeout),
        new(@"\bconsentimento\s+(livre|informado|expresso|inequivoco|previo|explicito|do\s+titular)\b",
            RegexOptions.None, RegexTimeout),
        new(@"\b(solicit\w*|pedimos|peco|pedido|coletamos|coletar|obter|obtemos|dar|dou|conced\w*|manifest\w*|assinar|assino|recusar|revogar)\b[^.]{0,60}\bconsentimento\b",
            RegexOptions.None, RegexTimeout),
        new(@"\bopt[-\s]?in\b", RegexOptions.None, RegexTimeout),
        new(@"\b(li\s+e\s+(aceito|concordo)|estou\s+de\s+acordo)\b", RegexOptions.None, RegexTimeout),
        new(@"\b(declaro|manifesto)\b[^.]{0,60}\b(consent|concord|aceit|autoriz)", RegexOptions.None, RegexTimeout),
    ];

    /// <summary>
    /// Valida o corpo já normalizado. Devolve null quando o texto pode ser salvo, ou o motivo
    /// da recusa (código + mensagem em PT-BR, pronta para virar 400 ProblemDetails).
    /// </summary>
    public static Rejection? Validate(string body)
    {
        if (TemCaractereDeControle(body))
        {
            return new Rejection("notice_text_markup",
                "O aviso é exibido como texto simples na janela do agente. Remova os caracteres de controle.");
        }

        foreach (var pattern in MarkupPatterns)
        {
            if (pattern.IsMatch(body))
            {
                return new Rejection("notice_text_markup",
                    "O aviso é exibido como texto simples na janela do agente, sem HTML e sem nenhuma " +
                    "marcação: escreva apenas texto e quebras de linha. Marcação não é interpretada, " +
                    "apareceria crua para o funcionário e serviria para disfarçar o conteúdo do aviso.");
            }
        }

        var semAcento = StripDiacritics(body).ToLowerInvariant();
        foreach (var pattern in ConsentPatterns)
        {
            if (pattern.IsMatch(semAcento))
            {
                return new Rejection("notice_text_consent",
                    "O aviso registra a CIÊNCIA do funcionário, não é um pedido de consentimento, e é " +
                    "isso que sustenta a base legal do tratamento. Reescreva sem pedir consentimento, " +
                    "autorização ou aceite. O enquadramento fixo do aviso já esclarece esse ponto, você " +
                    "não precisa repeti-lo nem contradizê-lo.");
            }
        }

        var composed = Compose(body);
        if (composed.Length > MaxComposedLength)
        {
            return new Rejection("notice_text_too_long",
                $"O texto do aviso tem {body.Length} caracteres e o limite é {MaxBodyLength}. A janela do " +
                $"aviso comporta {MaxComposedLength} caracteres e o enquadramento fixo, que não pode ser " +
                $"removido, ocupa {FixedFraming.Length + Separator.Length} deles.");
        }

        return null;
    }

    /// <summary>Caracteres de controle não fazem sentido num rótulo: só quebra de linha e tabulação.</summary>
    private static bool TemCaractereDeControle(string value)
    {
        foreach (var ch in value)
        {
            if (ch is '\n' or '\t')
            {
                continue;
            }

            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Remove acentuação para a comparação (a análise nunca altera o texto salvo).</summary>
    private static string StripDiacritics(string value)
    {
        var decomposto = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposto.Length);
        foreach (var ch in decomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
