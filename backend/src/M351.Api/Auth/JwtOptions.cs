namespace M351.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Chave HS256 (≥ 32 bytes). Em produção vem de env var (Jwt__SigningKey).</summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "m351-monitor";
    public string Audience { get; set; } = "m351-portal";

    /// <summary>JWT de acesso: 15 min (N23).</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Token temporário de MFA (login em duas etapas / setup obrigatório).</summary>
    public int MfaTokenMinutes { get; set; } = 10;

    /// <summary>Refresh token opaco: 30 dias.</summary>
    public int RefreshTokenDays { get; set; } = 30;
}
