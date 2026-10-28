using SecureDocs.Shared;
using SecureDocs.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace SecureDocs.Web.Pages;

public class DocumentsModel(SecureDocsApi api) : PortalPageModel(api)
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public List<DocumentDto>? Results { get; set; }
    public long ElapsedMs { get; set; }

    public async Task OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(Q)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Results = await TryApi(() => Api.SearchAsync(ClientCode, Q)) ?? [];
        ElapsedMs = sw.ElapsedMilliseconds;
    }
}
