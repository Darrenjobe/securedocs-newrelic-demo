using System.Diagnostics;
using SecureDocs.Shared;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SecureDocs.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }

    public void OnGet() => Capture();
    public void OnPost() => Capture();

    void Capture()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        var ex = HttpContext.Features.Get<IExceptionHandlerPathFeature>()?.Error;
        if (ex is null) return;
        ExceptionType = ex.GetType().Name;
        ExceptionMessage = ex.Message;
        // Report the original exception (with stack trace) to New Relic Errors Inbox.
        Telemetry.NoticeError(ex);
        Response.StatusCode = 500;
    }
}
