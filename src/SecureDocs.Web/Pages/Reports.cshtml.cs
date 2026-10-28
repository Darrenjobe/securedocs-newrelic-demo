using SecureDocs.Shared;
using SecureDocs.Web.Services;

namespace SecureDocs.Web.Pages;

public class ReportsModel(SecureDocsApi api) : PortalPageModel(api)
{
    public List<VolumeReportRow> Rows { get; set; } = [];
    public async Task OnGetAsync() => Rows = await TryApi(() => Api.GetVolumeReportAsync(ClientCode)) ?? [];
}
