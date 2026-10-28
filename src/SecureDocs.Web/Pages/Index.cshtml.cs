using SecureDocs.Shared;
using SecureDocs.Web.Services;

namespace SecureDocs.Web.Pages;

public class IndexModel(SecureDocsApi api) : PortalPageModel(api)
{
    public DashboardDto? Dashboard { get; set; }

    public async Task OnGetAsync() => Dashboard = await TryApi(() => Api.GetDashboardAsync(ClientCode));

    public int Count(string status) => Dashboard?.ByStatus.FirstOrDefault(s => s.Label == status)?.Count ?? 0;
}
