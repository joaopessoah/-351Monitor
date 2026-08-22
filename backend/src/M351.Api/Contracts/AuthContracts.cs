namespace M351.Api.Contracts;

public record LoginRequest(string? Email, string? Password);

/// <summary>status: ok | mfa_required | mfa_setup_required.</summary>
public record AuthResponse(string Status, string? AccessToken = null, string? TokenType = null, int? ExpiresIn = null, string? MfaToken = null);

public record MfaVerifyRequest(string? Code);

public record MfaSetupResponse(string Secret, string OtpauthUri);

public record InviteAcceptRequest(string? Token, string? Password, string? DisplayName);

/// <summary>Preview público do convite (tela /convite/:token — Seção 8.2).</summary>
public record InvitePreviewResponse(string Email, string Role, string OrganizationName, bool MfaRequired);

/// <summary>POST /auth/forgot-password (Seção 7.4): resposta SEMPRE genérica (202).</summary>
public record ForgotPasswordRequest(string? Email);

/// <summary>POST /auth/reset-password (Seção 7.4): token 1 h; invalida todas as sessões.</summary>
public record ResetPasswordRequest(string? Token, string? Password);

/// <summary>POST /auth/mfa/recovery-codes (Seção 7.5): exibidos UMA única vez.</summary>
public record RecoveryCodesResponse(IReadOnlyList<string> Codes);
