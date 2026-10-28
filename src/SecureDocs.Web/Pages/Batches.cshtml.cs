using SecureDocs.Shared;
using SecureDocs.Web.Services;

namespace SecureDocs.Web.Pages;

public class BatchesModel(SecureDocsApi api) : PortalPageModel(api)
{
    public List<BatchSummaryDto> Batches { get; set; } = [];
    public async Task OnGetAsync() => Batches = await TryApi(() => Api.GetBatchesAsync(ClientCode, 100)) ?? [];
}
