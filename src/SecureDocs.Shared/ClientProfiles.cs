namespace SecureDocs.Shared;

/// <summary>
/// Business context for each client: service tier and SLA target. Sent with telemetry so performance data can be
/// sliced by business impact (e.g. "p95 intake time for Enterprise clients").
/// SLA targets are demo-speed (minutes) so SLA risk can be shown live; a real print plant measures hours or days.
/// </summary>
public static class ClientProfiles
{
    public const string Enterprise = "Enterprise";
    public const string Standard = "Standard";

    static readonly Dictionary<string, string> Tiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PCHP"] = Enterprise,   // Pacific Coast Health Plan
        ["SMG"] = Enterprise,    // Summit Medical Group
        ["EBA"] = Standard,      // Evergreen Benefits Administrators
    };

    /// <summary>Demo SLA, in minutes from batch received to batch completed. Override with Sla:EnterpriseMinutes / Sla:StandardMinutes.</summary>
    public static int EnterpriseSlaMinutes { get; set; } = 15;
    public static int StandardSlaMinutes { get; set; } = 30;

    /// <summary>A queued document is "at risk" once it has used this share of its SLA.</summary>
    public const double AtRiskShareOfSla = 0.5;

    public static string TierOf(string? clientCode) =>
        clientCode is not null && Tiers.TryGetValue(clientCode, out var t) ? t : Standard;

    public static int SlaMinutesOf(string? clientCode) =>
        TierOf(clientCode) == Enterprise ? EnterpriseSlaMinutes : StandardSlaMinutes;
}
