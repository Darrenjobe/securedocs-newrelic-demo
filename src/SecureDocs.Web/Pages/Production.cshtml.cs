using SecureDocs.Shared;
using SecureDocs.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace SecureDocs.Web.Pages;

public class ProductionModel(SecureDocsApi api) : PortalPageModel(api)
{
    [BindProperty(SupportsGet = true)] public bool FailedOnly { get; set; }
    [BindProperty] public int Max { get; set; } = 50;
    public List<QaInspectionDto> Inspections { get; set; } = [];
    public ProductionRunResult? Run { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostAsync()
    {
        Run = await TryApi(() => Api.RunProductionAsync(ClientCode, Max));
        if (Run is { Stalled: true })
            ErrorMessage = "The production floor is stalled (Fault Control → Stall production floor). Documents are waiting in the queue.";
        else if (Run is not null)
            SuccessMessage = $"Production run finished in {Run.ElapsedMs} ms: {Run.Printed} printed, {Run.Delivered} sent digitally, {Run.QaFailed} QA failures.";
        await LoadAsync();
    }

    async Task LoadAsync() => Inspections = await TryApi(() => Api.GetQaAsync(ClientCode, FailedOnly)) ?? [];
}
