using SecureDocs.Shared;
using SecureDocs.Web.Services;

namespace SecureDocs.Web.Pages;

public class DevelopersModel(SecureDocsApi api, IConfiguration config) : PortalPageModel(api)
{
    public ClientDto? Client { get; set; }
    public string ApiBase => config["Api:PublicUrl"] is { Length: > 0 } u ? u : config["Api:BaseUrl"] ?? "";
    public async Task OnGetAsync() => Client = await TryApi(() => Api.GetClientAsync(ClientCode));
}
