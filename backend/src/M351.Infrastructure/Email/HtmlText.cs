using System.Text;

namespace M351.Infrastructure.Email;

/// <summary>
/// Escape de texto para os corpos HTML dos e-mails (digest semanal, alertas de frota).
/// Escapa SOMENTE o que é perigoso em conteúdo HTML (&amp; &lt; &gt; " '), preservando os
/// acentos: o WebUtility.HtmlEncode transformaria "sem comunicação" em entidades numéricas,
/// e o e-mail já é enviado em UTF-8, então acento entra literal e legível.
/// </summary>
public static class HtmlText
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }
}
