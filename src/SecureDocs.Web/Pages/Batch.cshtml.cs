using SecureDocs.Shared;
using SecureDocs.Web.Services;

namespace SecureDocs.Web.Pages;

public class BatchModel(SecureDocsApi api) : PortalPageModel(api)
{
    public BatchDetailDto? Detail { get; set; }
    public async Task OnGetAsync(int id) => Detail = await TryApi(() => Api.GetBatchAsync(id));
}
