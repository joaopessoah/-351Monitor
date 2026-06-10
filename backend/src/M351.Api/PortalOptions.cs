namespace M351.Api;

public class PortalOptions
{
    public const string SectionName = "Portal";

    /// <summary>URL base do portal (links de convite). Dev: http://localhost:5173.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5173";
}
