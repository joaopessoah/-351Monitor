using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using M351.Domain;
using M351.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace M351.Api.Auth;

public class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public int AccessTokenSeconds => _options.AccessTokenMinutes * 60;
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    /// <summary>JWT de acesso (15 min, HS256) com claims user_id/tenant_id/role (+ sub/org_id/jti).</summary>
    public string CreateAccessToken(User user) =>
        CreateToken(user, AuthConstants.TokenUseAccess, TimeSpan.FromMinutes(_options.AccessTokenMinutes));

    /// <summary>Token temporário restrito ao fluxo de MFA (verify/setup).</summary>
    public string CreateMfaToken(User user) =>
        CreateToken(user, AuthConstants.TokenUseMfa, TimeSpan.FromMinutes(_options.MfaTokenMinutes));

    private string CreateToken(User user, string tokenUse, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(AuthConstants.ClaimSub, user.Id.ToString()),
            new(AuthConstants.ClaimJti, Uuid7.NewUuid7().ToString()),
            new(AuthConstants.ClaimUserId, user.Id.ToString()),
            new(AuthConstants.ClaimOrgId, user.TenantId.ToString()),
            new(AuthConstants.ClaimTenantId, user.TenantId.ToString()),
            new(AuthConstants.ClaimRole, user.Role.ToDbValue()),
            new(AuthConstants.ClaimEmail, user.Email),
            new(AuthConstants.ClaimTokenUse, tokenUse),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(lifetime),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
