namespace M351.Api.Auth;

/// <summary>
/// Cookie httpOnly/SameSite=Strict do refresh token (Seção 7.4). Secure acompanha o esquema da
/// requisição para funcionar em dev/testes sobre http; em produção (TLS via Caddy) é sempre Secure.
/// </summary>
public static class RefreshCookie
{
    private const string Path = "/api/v1/auth";

    public static void Set(HttpResponse response, string token, TimeSpan lifetime)
    {
        response.Cookies.Append(AuthConstants.RefreshCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = response.HttpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = Path,
            MaxAge = lifetime,
        });
    }

    public static void Delete(HttpResponse response)
    {
        response.Cookies.Delete(AuthConstants.RefreshCookieName, new CookieOptions { Path = Path });
    }

    public static string? Get(HttpRequest request) =>
        request.Cookies.TryGetValue(AuthConstants.RefreshCookieName, out var value) ? value : null;
}
