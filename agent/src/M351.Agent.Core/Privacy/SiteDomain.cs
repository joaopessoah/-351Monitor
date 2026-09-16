namespace M351.Agent.Core.Privacy;

/// <summary>
/// Redução de uma URL da barra de endereço ao DOMÍNIO REGISTRÁVEL — a única parte da navegação
/// que sai da máquina (Seção 6.3; design 04-lgpd §"Coleta de navegação: somente domínio, nunca
/// URL completa"). Puro, sem I/O: recebe o texto cru da omnibox e devolve `mercadolivre.com.br`,
/// `gov.br` ou null.
///
/// O QUE É DESCARTADO AQUI, NA MÁQUINA, ANTES DE QUALQUER FILA (nunca chega ao servidor):
///  - caminho, query string e fragmento (`/pedidos/123?cpf=...#x`) — é onde mora o conteúdo;
///  - credenciais embutidas (`usuario:senha@host`);
///  - porta, esquema e subdomínios (`docs.` de `docs.google.com` vira `google.com`).
///
/// FALHA FECHADA: qualquer entrada que não seja reconhecidamente um endereço — texto de busca
/// digitado na omnibox, `chrome://settings`, `file://`, string vazia — devolve null. É melhor
/// perder o domínio de uma navegação do que transformar o que o funcionário DIGITOU em dado
/// coletado. Por isso exigimos ponto + TLD plausível: "como fazer bolo" e "mercadoliv" (busca
/// pela metade) não passam.
///
/// SUBDOMÍNIO, decisão de produto: a unidade de classificação é o domínio registrável, porque é
/// a unidade que o cliente sabe julgar ("mercadolivre.com.br é improdutivo") e porque uma regra
/// por host obrigaria a classificar `www.`, `m.` e `produto.` separadamente. A ÚNICA exceção é a
/// lista curta de <see cref="ServiceSubdomainDomains"/>, onde serviços de natureza diferente
/// moram no mesmo domínio e um rótulo a mais evita jogar trabalho e lazer no mesmo balde.
/// </summary>
public static class SiteDomain
{
    /// <summary>Teto do domínio que viaja no evento (um domínio real não chega perto disso).</summary>
    public const int MaxLength = 128;

    /// <summary>Teto da entrada aceita: acima disso é texto colado na omnibox, não endereço.</summary>
    private const int MaxInputLength = 2_048;

    /// <summary>
    /// Esquemas que descrevem NAVEGAÇÃO. Tudo que não estiver aqui (`file:`, `chrome:`, `edge:`,
    /// `about:`, `data:`, `javascript:`, `view-source:`) devolve null: páginas internas do
    /// navegador e arquivos locais não são "site visitado" e `file://` carregaria caminho de
    /// arquivo pessoal.
    /// </summary>
    private static readonly string[] WebSchemes = ["http", "https"];

    /// <summary>
    /// Sufixos públicos de DOIS rótulos: nestes, o domínio registrável tem TRÊS rótulos
    /// (`uol.com.br`, `fazenda.gov.br`, `bbc.co.uk`). Sem a lista, `empresa.eco.br` viraria
    /// "eco.br" e todo o .br cairia num punhado de baldes errados.
    ///
    /// Recorte deliberado da Public Suffix List: TODOS os genéricos de segundo nível do
    /// registro.br (é o mercado do produto) mais os internacionais que aparecem em PME
    /// brasileira. Não é a PSL inteira de propósito — ela tem ~10 mil linhas, muda toda semana e
    /// viaja dentro de um agente que roda offline. Sufixo ausente daqui degrada com elegância:
    /// o domínio fica um rótulo mais curto e o cliente reclassifica uma linha no portal.
    /// </summary>
    private static readonly HashSet<string> TwoLabelPublicSuffixes = new(StringComparer.Ordinal)
    {
        // --- .br (registro.br) ---
        "com.br", "net.br", "org.br", "gov.br", "edu.br", "mil.br", "art.br", "adv.br", "agr.br",
        "am.br", "arq.br", "ato.br", "bio.br", "blog.br", "bmd.br", "cim.br", "cng.br", "cnt.br",
        "coop.br", "ecn.br", "eco.br", "emp.br", "eng.br", "esp.br", "etc.br", "eti.br", "far.br",
        "flog.br", "fm.br", "fnd.br", "fot.br", "fst.br", "g12.br", "geo.br", "ggf.br", "imb.br",
        "ind.br", "inf.br", "jor.br", "jus.br", "leg.br", "lel.br", "mat.br", "med.br", "mus.br",
        "not.br", "ntr.br", "odo.br", "ppg.br", "pro.br", "psc.br", "psi.br", "qsl.br", "radio.br",
        "rec.br", "slg.br", "srv.br", "taxi.br", "teo.br", "tmp.br", "trd.br", "tur.br", "tv.br",
        "vet.br", "vlog.br", "wiki.br", "zlg.br", "b.br", "def.br", "app.br", "dev.br", "log.br",
        "seg.br", "tec.br",
        // --- internacionais com presença no dia a dia da PME brasileira ---
        "co.uk", "org.uk", "gov.uk", "ac.uk", "co.jp", "com.au", "net.au", "org.au", "com.ar",
        "com.mx", "com.co", "com.pt", "com.es", "com.cn", "co.in", "co.za", "co.nz", "com.uy",
        "com.py", "com.pe", "com.ve", "com.tr", "com.sg", "com.hk", "com.tw", "com.my",
    };

    /// <summary>
    /// Domínios que hospedam SERVIÇOS DIFERENTES em subdomínios diferentes. Neles, e SÓ neles, um
    /// rótulo a mais é preservado: é o que separa "docs.google.com" (documento de trabalho) da
    /// busca do Google, "console.aws.amazon.com" (infraestrutura) da loja da Amazon e
    /// "g1.globo.com" (notícia) de "globoplay.globo.com" (streaming).
    ///
    /// Sem esta lista, reduzir tudo ao domínio registrável jogaria trabalho e lazer no MESMO
    /// balde para os endereços mais usados de uma PME — e a classificação ficaria errada
    /// justamente onde mais importa. Com ela, o rótulo extra é a EXCEÇÃO explícita, não a regra:
    /// para todo o resto continua valendo "só o domínio registrável".
    ///
    /// É o mesmo conceito da seção PRIVADA da Public Suffix List, recortado ao que aparece no
    /// dia a dia brasileiro. Entrar aqui aumenta o que sai da máquina (um rótulo), então a lista
    /// é curta e cada linha se justifica por serviços de natureza claramente diferente.
    /// </summary>
    private static readonly HashSet<string> ServiceSubdomainDomains = new(StringComparer.Ordinal)
    {
        "google.com", "amazon.com", "microsoft.com", "live.com", "office.com", "sharepoint.com",
        "amazonaws.com", "atlassian.net", "zoho.com", "salesforce.com", "globo.com", "uol.com.br",
    };

    /// <summary>
    /// Domínio registrável da URL crua, ou null quando não é endereço navegável.
    /// Idempotente: aplicar de novo sobre a saída devolve a mesma coisa.
    /// </summary>
    public static string? FromAddressBar(string? raw)
    {
        var host = ExtractHost(raw);
        return host is null ? null : Registrable(host);
    }

    /// <summary>
    /// Host normalizado (minúsculo, sem esquema, sem credencial, sem porta, sem caminho, sem
    /// `www.` e sem ponto final), ou null quando a entrada não descreve um host plausível.
    /// </summary>
    internal static string? ExtractHost(string? raw)
    {
        if (raw is null) return null;
        var text = raw.Trim();
        if (text.Length is 0 or > MaxInputLength) return null;

        // Espaço em branco = o funcionário está DIGITANDO uma busca, não um endereço.
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) return null;
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var scheme = text[..schemeEnd].ToLowerInvariant();
            if (!WebSchemes.Contains(scheme)) return null; // file:, view-source:, ftp:…
            text = text[(schemeEnd + 3)..];
        }
        else if (IsSchemeLike(text))
        {
            return null; // chrome://, edge://, about:blank, data:, javascript:
        }

        // Autoridade = tudo antes do primeiro delimitador de caminho/query/fragmento.
        var cut = text.AsSpan().IndexOfAny('/', '?', '#');
        var authority = cut >= 0 ? text[..cut] : text;

        // Credencial embutida: some inteira (é segredo, não é identificação de site).
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];

        // Porta (mas preservando IPv6 entre colchetes).
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return null;
            authority = authority[1..close]; // IPv6 sem colchetes
        }
        else
        {
            var colon = authority.IndexOf(':');
            if (colon >= 0) authority = authority[..colon];
        }

        authority = authority.Trim().TrimEnd('.').ToLowerInvariant();
        if (authority.StartsWith("www.", StringComparison.Ordinal)) authority = authority[4..];

        return IsPlausibleHost(authority) ? authority : null;
    }

    /// <summary>
    /// "algo:coisa" onde "algo" é um esquema (só letras) — `chrome:`, `about:`, `mailto:`.
    /// Endereço com porta ("localhost:8080") não cai aqui porque tem host antes dos dois pontos.
    /// </summary>
    private static bool IsSchemeLike(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0) return false;
        for (var i = 0; i < colon; i++)
        {
            if (!char.IsAsciiLetter(text[i])) return false;
        }
        // "host:8080" tem dígitos depois dos dois pontos e nada de esquema conhecido.
        return colon + 1 >= text.Length || !char.IsAsciiDigit(text[colon + 1]);
    }

    /// <summary>
    /// Regra de "isto é um host": ponto obrigatório, rótulos não vazios com caracteres de
    /// domínio, e último rótulo ou letra (TLD) ou IPv4. É o filtro que impede busca digitada de
    /// virar dado coletado.
    /// </summary>
    private static bool IsPlausibleHost(string host)
    {
        if (host.Length is 0 or > MaxLength) return false;
        if (IsIpv6(host)) return true;

        var labels = host.Split('.');
        if (labels.Length < 2) return false; // "intranet" sozinho não é endereço reconhecível

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63) return false;
            foreach (var c in label)
            {
                // letras (inclusive acentuadas de IDN), dígitos e hífen
                if (!char.IsLetterOrDigit(c) && c != '-') return false;
            }
        }

        var tld = labels[^1];
        if (tld.Length < 2) return false;

        // IPv4 (ex.: 192.168.0.10 do ERP interno) é host legítimo; senão, TLD tem de ser letras.
        var allNumeric = true;
        foreach (var label in labels)
        {
            foreach (var c in label)
            {
                if (!char.IsAsciiDigit(c)) { allNumeric = false; break; }
            }
            if (!allNumeric) break;
        }
        if (allNumeric) return labels.Length == 4;

        foreach (var c in tld)
        {
            if (!char.IsLetter(c)) return false;
        }
        return true;
    }

    private static bool IsIpv6(string host)
    {
        if (!host.Contains(':')) return false;
        foreach (var c in host)
        {
            if (!char.IsAsciiHexDigit(c) && c != ':') return false;
        }
        return true;
    }

    /// <summary>
    /// Domínio registrável do host: último rótulo + sufixo público
    /// (`produto.mercadolivre.com.br` → `mercadolivre.com.br`; `docs.google.com` → `google.com`).
    /// IP e host de até 2 rótulos voltam inteiros.
    /// </summary>
    internal static string Registrable(string host)
    {
        if (IsIpv6(host)) return host;

        var labels = host.Split('.');
        if (labels.Length <= 2) return host;

        // IPv4 não tem domínio registrável.
        var allNumeric = true;
        foreach (var label in labels)
        {
            foreach (var c in label)
            {
                if (!char.IsAsciiDigit(c)) { allNumeric = false; break; }
            }
            if (!allNumeric) break;
        }
        if (allNumeric) return host;

        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        var take = TwoLabelPublicSuffixes.Contains(lastTwo) ? 3 : 2;
        if (labels.Length <= take) return host;

        var registrable = string.Join('.', labels[^take..]);

        // Um rótulo a mais SÓ nos domínios de serviços compartilhados (ver o campo).
        if (ServiceSubdomainDomains.Contains(registrable) && labels.Length > take)
            return string.Join('.', labels[^(take + 1)..]);

        return registrable;
    }
}
