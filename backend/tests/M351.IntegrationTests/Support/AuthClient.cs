using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OtpNet;

namespace M351.IntegrationTests.Support;

/// <summary>Helpers de autenticação contra a API real (login, MFA, cookies de refresh).</summary>
public static class AuthClient
{
    public static string ComputeTotp(string secretBase32) =>
        new Totp(Base32Encoding.ToBytes(secretBase32)).ComputeTotp();

    /// <summary>Login completo (com MFA quando habilitada). Retorna o access token.</summary>
    public static async Task<string> LoginAsync(HttpClient client, TestUser user)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = user.Password });
        login.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var status = body.RootElement.GetProperty("status").GetString();

        if (status == "ok")
        {
            return body.RootElement.GetProperty("access_token").GetString()!;
        }

        if (status != "mfa_required" || user.MfaSecretBase32 is null)
        {
            throw new InvalidOperationException($"Login inesperado: status={status}");
        }

        var mfaToken = body.RootElement.GetProperty("mfa_token").GetString()!;
        using var verifyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/mfa/verify")
        {
            Content = JsonContent.Create(new { code = ComputeTotp(user.MfaSecretBase32) }),
        };
        verifyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mfaToken);
        var verify = await client.SendAsync(verifyRequest);
        verify.EnsureSuccessStatusCode();
        using var verifyBody = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        return verifyBody.RootElement.GetProperty("access_token").GetString()!;
    }

    public static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>Extrai o valor do cookie m351_refresh de uma resposta (ou null).</summary>
    public static string? ExtractRefreshCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            if (cookie.StartsWith("m351_refresh=", StringComparison.Ordinal))
            {
                var value = cookie["m351_refresh=".Length..];
                var end = value.IndexOf(';');
                return end >= 0 ? value[..end] : value;
            }
        }

        return null;
    }

    public static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshCookieValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"m351_refresh={refreshCookieValue}");
        return await client.SendAsync(request);
    }
}
