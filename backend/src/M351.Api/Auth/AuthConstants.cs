namespace M351.Api.Auth;

public static class AuthConstants
{
    // claims
    public const string ClaimSub = "sub";
    public const string ClaimJti = "jti";
    public const string ClaimOrgId = "org_id";
    public const string ClaimTenantId = "tenant_id";
    public const string ClaimUserId = "user_id";
    public const string ClaimRole = "role";
    public const string ClaimEmail = "email";
    public const string ClaimTokenUse = "token_use";

    public const string TokenUseAccess = "access";
    public const string TokenUseMfa = "mfa";
    public const string TokenUseDevice = "device";

    public const string ClaimDeviceId = "device_id";

    // policies
    public const string PolicyAccess = "Access";
    public const string PolicyAdminPlus = "AdminPlus";
    public const string PolicyOwnerOnly = "OwnerOnly";
    public const string PolicyMfaToken = "MfaToken";

    /// <summary>Scheme/policy do device token (escopo exclusivo /api/v1/agent/* e /api/v1/ingest/*).</summary>
    public const string SchemeDevice = "Device";
    public const string PolicyDevice = "Device";

    /// <summary>Prefixo do device token opaco (Seção 5.7): dt_ + 256 bits base64url.</summary>
    public const string DeviceTokenPrefix = "dt_";

    public const string RefreshCookieName = "m351_refresh";
}
