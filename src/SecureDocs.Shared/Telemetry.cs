using NR = NewRelic.Api.Agent.NewRelic;

namespace SecureDocs.Shared;

/// <summary>
/// Thin wrapper over the New Relic agent API. Calls are no-ops when the agent isn't attached
/// (e.g. running locally on a Mac), so the app works with or without New Relic.
/// Never put PHI (names, addresses, member IDs) in attributes or events.
/// </summary>
public static class Telemetry
{
    public static void Attr(string key, object? value)
    {
        if (value is null) return;
        try { NR.GetAgent().CurrentTransaction.AddCustomAttribute(key, value); } catch { /* agent not attached */ }
    }

    /// <summary>Tags the current transaction with the client and its service tier.</summary>
    public static void Client(string? clientCode)
    {
        if (string.IsNullOrEmpty(clientCode)) return;
        Attr("client.code", clientCode);
        Attr("client.tier", ClientProfiles.TierOf(clientCode));
    }

    /// <summary>
    /// Identifies the end user of the current request (sets enduser.id), so Errors Inbox can show users impacted.
    /// Use a stable, non-PHI identifier such as "SMG:jordan.ops"; never a member's name or ID.
    /// </summary>
    public static void User(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        try { NR.GetAgent().CurrentTransaction.SetUserId(userId); } catch { /* agent not attached */ }
    }

    public const string UserHeader = "X-SecureDocs-User";

    public static void NoticeError(Exception ex, IDictionary<string, object>? attributes = null)
    {
        try { NR.NoticeError(ex, attributes ?? new Dictionary<string, object>()); } catch { }
    }

    /// <summary>Set SECUREDOCS_TELEMETRY_LOG=1 to also print custom events to the console (handy locally, where the agent can't run).</summary>
    static readonly bool LogEvents = Environment.GetEnvironmentVariable("SECUREDOCS_TELEMETRY_LOG") == "1";

    public static void Event(string eventType, IDictionary<string, object> attributes)
    {
        if (LogEvents) Console.WriteLine($"[telemetry] {eventType} {System.Text.Json.JsonSerializer.Serialize(attributes)}");
        try { NR.RecordCustomEvent(eventType, attributes); } catch { }
    }

    public static void Metric(string name, float value)
    {
        try { NR.RecordMetric(name, value); } catch { }
    }
}
