using SecureDocs.Shared;
using SecureDocs.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace SecureDocs.Web.Pages;

public class SubmitModel(SecureDocsApi api) : PortalPageModel(api)
{
    [BindProperty] public string? Reference { get; set; }
    [BindProperty] public int Count { get; set; } = 25;
    [BindProperty] public string DocumentType { get; set; } = "Mixed";
    [BindProperty] public string Channel { get; set; } = "Mixed";
    public SubmitBatchResponse? Result { get; set; }
    public ClientDto? Client { get; set; }

    public async Task OnGetAsync() => Client = await TryApi(() => Api.GetClientAsync(ClientCode));

    public async Task OnPostAsync()
    {
        Client = await TryApi(() => Api.GetClientAsync(ClientCode));
        var docs = SyntheticData.GenerateDocuments(Math.Clamp(Count, 1, 1000));
        foreach (var d in docs)
        {
            if (DocumentType != "Mixed") d.DocumentType = DocumentType;
            if (Channel != "Mixed") d.Channel = Channel;
        }
        Result = await TryApi(() => Api.SubmitBatchAsync(ClientCode, Reference, docs));
        if (Result is not null)
            SuccessMessage = $"Batch #{Result.BatchId} ({Result.Reference}) accepted: {Result.Accepted} documents queued for production.";
    }
}
