namespace M351.Domain;

/// <summary>RBAC do MVP: Owner ⊃ Admin ⊃ Viewer (3 papéis; enum extensível).</summary>
public enum UserRole
{
    Owner,
    Admin,
    Viewer,
}

public static class UserRoleExtensions
{
    public const string OwnerValue = "owner";
    public const string AdminValue = "admin";
    public const string ViewerValue = "viewer";

    public static string ToDbValue(this UserRole role) => role switch
    {
        UserRole.Owner => OwnerValue,
        UserRole.Admin => AdminValue,
        UserRole.Viewer => ViewerValue,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Papel desconhecido."),
    };

    public static UserRole FromDbValue(string value) => value switch
    {
        OwnerValue => UserRole.Owner,
        AdminValue => UserRole.Admin,
        ViewerValue => UserRole.Viewer,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Papel desconhecido."),
    };

    public static bool TryFromDbValue(string? value, out UserRole role)
    {
        switch (value)
        {
            case OwnerValue: role = UserRole.Owner; return true;
            case AdminValue: role = UserRole.Admin; return true;
            case ViewerValue: role = UserRole.Viewer; return true;
            default: role = UserRole.Viewer; return false;
        }
    }

    /// <summary>MFA TOTP é obrigatória para Owner e Admin (Seção 7.5).</summary>
    public static bool RequiresMfa(this UserRole role) => role is UserRole.Owner or UserRole.Admin;
}
