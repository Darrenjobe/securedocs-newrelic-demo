using SecureDocs.Shared;
using SecureDocs.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SecureDocs.Web.Pages;

/// <summary>Base page: knows the signed-in client and turns API failures into a friendly banner.</summary>
public abstract class PortalPageModel(SecureDocsApi api) : PageModel
{
    protected SecureDocsApi Api => api;
    public string ClientCode => Request.Cookies[PortalSession.ClientCookie] ?? "";
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    /// <summary>Runs an API call; on failure shows the error and reports it to New Relic (the trace links both tiers).</summary>
    protected async Task<T?> TryApi<T>(Func<Task<T>> call)
    {
        Telemetry.Client(ClientCode);
        try { return await call(); }
        catch (ApiException ex)
        {
            ErrorMessage = ex.UserMessage;
            Telemetry.NoticeError(ex);
            return default;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"The SecureDocs API is unreachable: {ex.Message}";
            Telemetry.NoticeError(ex);
            return default;
        }
        catch (TaskCanceledException ex)
        {
            ErrorMessage = "The SecureDocs API timed out.";
            Telemetry.NoticeError(ex);
            return default;
        }
    }
}

public static class Fmt
{
    public static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("MMM d, HH:mm") + " UTC";

    public static string StatusBadge(string status) => status switch
    {
        "Completed" or "Delivered" or "QaPassed" or "Processed" or "Pass" => "badge-ok",
        "CompletedWithExceptions" or "QaFailed" or "Failed" or "Fail" => "badge-bad",
        "Queued" or "Received" or "Pending" or "Processing" or "InProduction" => "badge-wait",
        _ => "badge-neutral"
    };
}
