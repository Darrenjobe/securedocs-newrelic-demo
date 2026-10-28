using SecureDocs.Shared;

namespace SecureDocs.Api.Infrastructure;

/// <summary>
/// In-memory fault switches for this API instance (set from the portal's Fault Control panel).
/// Note: with more than one App Service instance, each instance keeps its own state.
/// </summary>
public class FaultState
{
    volatile ApiFaultState _current = new();

    public ApiFaultState Current => _current;

    public void Set(ApiFaultState s)
    {
        s.ErrorRatePercent = Math.Clamp(s.ErrorRatePercent, 0, 100);
        s.SlowTraceMs = Math.Clamp(s.SlowTraceMs, 250, 20000);
        s.SlowQueryMs = Math.Clamp(s.SlowQueryMs, 250, 20000);
        _current = s;
    }

    /// <summary>Rolls the dice against the configured error rate.</summary>
    public bool RollError() => _current.ErrorRatePercent > 0 && Random.Shared.Next(100) < _current.ErrorRatePercent;
}

// Distinct exception types so they show up as separate groups in New Relic Errors Inbox.
public class PhiValidationException(string message) : Exception(message);
public class CompositionEngineException(string message) : Exception(message);
public class QaMismatchException(string message) : Exception(message);
public class SlaBreachException(string message) : Exception(message);
public class DashboardAggregationException(string message, Exception? inner = null) : Exception(message, inner);
public class AddressServiceException(string message, Exception? inner = null) : Exception(message, inner);
