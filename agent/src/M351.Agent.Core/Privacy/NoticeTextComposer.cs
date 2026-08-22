namespace M351.Agent.Core.Privacy;

/// <summary>
/// Texto do aviso de ciência exibido no primeiro logon (Seções 6.5 e 9.4).
///
/// O tenant pode escrever o CORPO do aviso (config `notice_text`, gerenciada no portal) para falar
/// a linguagem da empresa dele. Mas o enquadramento jurídico NÃO é editável: o aviso é registro de
/// CIÊNCIA, não pedido de consentimento, e é isso que sustenta a base legal do controlador. Por
/// isso os trechos fixos ficam AQUI, no agente, e são sempre concatenados ao corpo — um tenant não
/// consegue publicar um aviso que transforme o NOTICE_ACK em "consentimento" nem que esconda o
/// caminho para ver a coleta em tempo real.
/// </summary>
public static class NoticeTextComposer
{
    /// <summary>Corpo padrão, usado quando o tenant não definiu texto próprio.</summary>
    public const string DefaultBody =
        "Esta máquina é monitorada pela sua empresa.\n\n" +
        "São coletados: aplicativo e título da janela em foco (conforme a política da " +
        "empresa), eventos de sessão (logon/bloqueio), ociosidade e saúde do agente.";

    /// <summary>
    /// Enquadramento FIXO, sempre concatenado (editável só por quem mexe no código do agente):
    /// deixa explícito que isto é ciência e não consentimento, e lembra o caminho da transparência.
    /// </summary>
    public const string FixedFraming =
        "Este aviso registra a sua ciência. Não é um pedido de consentimento.\n" +
        "Você pode ver o que está sendo coletado agora a qualquer momento, pelo ícone " +
        "de monitoramento na área de notificação.";

    /// <summary>Limite do corpo vindo do tenant, para o aviso continuar caber na janela.</summary>
    public const int MaxBodyLength = 1_200;

    /// <summary>
    /// Monta o texto final: corpo do tenant (ou o padrão) + enquadramento fixo. Corpo em branco,
    /// só espaços ou ausente cai no padrão; corpo longo demais é truncado (o enquadramento fixo
    /// NUNCA é truncado nem removido).
    /// </summary>
    public static string Compose(string? tenantBody)
    {
        var body = string.IsNullOrWhiteSpace(tenantBody) ? DefaultBody : tenantBody.Trim();
        if (body.Length > MaxBodyLength) body = body[..MaxBodyLength];
        return $"{body}\n\n{FixedFraming}";
    }
}

/// <summary>
/// Decisão de exibir o aviso (Seção 6.5): o helper persiste localmente a versão confirmada e só
/// reexibe quando a versão vigente da config é MAIOR. Sem flag legível (primeiro logon, arquivo
/// corrompido) o aviso é exibido — na dúvida, informar o funcionário.
/// </summary>
public static class NoticeGate
{
    public static bool ShouldShow(int? acknowledgedVersion, int currentVersion) =>
        acknowledgedVersion is null || acknowledgedVersion.Value < currentVersion;
}
