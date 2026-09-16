namespace M351.Agent.Core.Collectors;

/// <summary>
/// Amostra crua da janela em foco (antes do enforcement de privacidade).
///
/// Os três últimos campos nasceram com a coleta de sites e são preenchidos DEPOIS da amostra,
/// só quando o processo em foco é navegador (ver SessionCollectorEngine):
///  - <paramref name="Hwnd"/>: o handle que a leitura da barra de endereço precisa;
///  - <paramref name="BrowserUrl"/>: a url crua lida da barra (reduzida a domínio no TitleMasker);
///  - <paramref name="BrowserWindowNames"/>: os nomes ACESSÍVEIS da janela do navegador. É neles
///    — e não no título Win32 — que o Chrome atual marca a janela anônima ("(Modo anônimo)");
///    o Edge marca nos dois ("[InPrivate]" no título, "(InPrivate)" no nome acessível). Sem este
///    campo, o rebaixamento automático da navegação anônima não teria como acontecer no Chrome.
/// Todos opcionais para não mexer em nenhuma construção existente.
/// </summary>
public sealed record ForegroundSample(
    string ProcessName,
    string? ExePath,
    string? AppId,
    string? Title,
    IntPtr Hwnd = default,
    string? BrowserUrl = null,
    IReadOnlyList<string>? BrowserWindowNames = null);

/// <summary>Abstração da consulta Win32 de janela ativa (mockável em teste).</summary>
public interface IForegroundWindowQuery
{
    /// <summary>null quando não há janela em foco (trocas de foco, sessão sem desktop) — sem crash.</summary>
    ForegroundSample? GetForegroundWindowInfo();
}

/// <summary>Abstração de GetLastInputInfo (mockável em teste).</summary>
public interface IIdleTimeQuery
{
    /// <summary>Milissegundos desde o último input do usuário na sessão.</summary>
    long GetIdleMilliseconds();
}

/// <summary>
/// Leitura da barra de endereço do navegador em foco (Seção 6.2). Implementação real por UI
/// Automation (<c>Win32.UiaBrowserUrlQuery</c>); mockável em teste.
///
/// O QUE ESTA ABSTRAÇÃO PODE FAZER, e só: ler o TEXTO DA BARRA DE ENDEREÇO da janela em foco.
/// Não navega o conteúdo da página, não lê abas em segundo plano, não abre banco de histórico e
/// não injeta extensão. Quem reduz a url a domínio é o <c>Privacy.SiteDomain</c>, e quem decide
/// se ela pode sequer ser lida é o <c>Privacy.TitleMasker</c>.
/// </summary>
public interface IBrowserUrlQuery
{
    /// <summary>
    /// Lê a janela do navegador em foco. NUNCA lança: falha de leitura é ausência de dado, nunca
    /// queda da coleta — devolve null quando não dá para ler ou a leitura demora demais.
    ///
    /// <paramref name="readUrl"/> false lê SÓ os nomes acessíveis da janela (detecção de janela
    /// anônima) e nem chega perto da barra de endereço: é o modo usado quando a controladora
    /// desligou a coleta de sites, em que a leitura continua existindo para PROTEGER, não para
    /// coletar.
    /// </summary>
    BrowserReading? Read(IntPtr hwnd, string processName, bool readUrl);
}

/// <summary>
/// Resultado da leitura da janela do navegador.
/// <paramref name="Url"/> é a url CRUA da barra (null quando não foi pedida ou não foi lida);
/// <paramref name="WindowNames"/> são os nomes acessíveis da janela, onde mora a marca de
/// navegação anônima.
/// </summary>
public sealed record BrowserReading(string? Url, IReadOnlyList<string> WindowNames);

/// <summary>
/// Processos que têm barra de endereço para ler. Lista FECHADA e explícita: é ela que garante
/// que a leitura de UI Automation só acontece em navegador — nenhum outro aplicativo do
/// funcionário é inspecionado, nem por engano.
/// </summary>
public static class BrowserProcesses
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "brave.exe",
        "vivaldi.exe", "chromium.exe", "browser.exe", "yandex.exe", "iexplore.exe",
        "librewolf.exe", "waterfox.exe", "floorp.exe", "arc.exe", "whale.exe", "maxthon.exe",
    };

    public static bool IsBrowser(string? processName) =>
        processName is not null && Known.Contains(processName);
}
