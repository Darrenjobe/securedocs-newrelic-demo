using SecureDocs.Shared;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SecureDocs.Web.Services;

public class PortalRenderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Fault switches that live in the portal (web) tier.</summary>
public class PortalFaults
{
    readonly Lock _lock = new();
    readonly PortalFaultState _state = new();

    public PortalFaultState Snapshot()
    {
        lock (_lock) return new PortalFaultState
        {
            PortalErrors = _state.PortalErrors, AutoTraffic = _state.AutoTraffic,
            PendingTrafficRequests = _state.PendingTrafficRequests
        };
    }

    public void Set(PortalFaultState s)
    {
        lock (_lock) { _state.PortalErrors = s.PortalErrors; _state.AutoTraffic = s.AutoTraffic; }
    }

    public void QueueTraffic(int requests)
    {
        lock (_lock) _state.PendingTrafficRequests = Math.Min(_state.PendingTrafficRequests + requests, 1000);
    }

    /// <summary>True if the generator should send a request now (auto mode, or a queued burst).</summary>
    public bool TakeTrafficSlot()
    {
        lock (_lock)
        {
            if (_state.PendingTrafficRequests > 0) { _state.PendingTrafficRequests--; return true; }
            return _state.AutoTraffic;
        }
    }
}

/// <summary>When "Portal errors" is on, about half of page requests blow up while rendering.</summary>
public class PortalFaultPageFilter(PortalFaults faults) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var page = context.ActionDescriptor.ViewEnginePath;
        if (faults.Snapshot().PortalErrors && page is not "/Login" and not "/Error" && Random.Shared.Next(100) < 50)
        {
            Telemetry.Attr("fault.portalErrors", true);
            throw new PortalRenderException($"Failed to render page '{page}': member widget model was null",
                new NullReferenceException("Object reference not set to an instance of an object (MemberSummaryWidget.Model)"));
        }
        return next();
    }
}
