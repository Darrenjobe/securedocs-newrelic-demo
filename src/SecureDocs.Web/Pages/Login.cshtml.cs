using SecureDocs.Shared;
using SecureDocs.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace SecureDocs.Web.Pages;

[IgnoreAntiforgeryToken]
public class LoginModel(SecureDocsApi api) : PortalPageModel(api)
{
    public List<ClientDto> Clients { get; set; } = [];
    [BindProperty] public string SelectedClient { get; set; } = "";
    [BindProperty] public string UserName { get; set; } = "jordan.ops";

    public async Task OnGetAsync() => Clients = await TryApi(() => Api.GetClientsAsync()) ?? [];

    public async Task<IActionResult> OnPostAsync()
    {
        var client = await TryApi(() => Api.GetClientAsync(SelectedClient));
        if (client is null)
        {
            ErrorMessage ??= "Please choose an organization.";
            Clients = await TryApi(() => Api.GetClientsAsync()) ?? [];
            return Page();
        }
        var opts = new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true };
        Response.Cookies.Append(PortalSession.ClientCookie, client.Code, opts);
        Response.Cookies.Append("sd_client_name", client.Name, opts);
        Response.Cookies.Append(PortalSession.UserCookie, string.IsNullOrWhiteSpace(UserName) ? "portal.user" : UserName.Trim(), opts);
        return Redirect("/");
    }

    public IActionResult OnGetSignOut()
    {
        foreach (var c in new[] { PortalSession.ClientCookie, PortalSession.UserCookie, "sd_client_name" }) Response.Cookies.Delete(c);
        return Redirect("/Login");
    }
}
