using System.Text;
using SecureDocs.Shared;
using SecureDocs.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace SecureDocs.Web.Pages;

public class TransferModel(SecureDocsApi api, IConfiguration config) : PortalPageModel(api)
{
    public List<FtpTransferDto> Transfers { get; set; } = [];
    public string FtpHost => config["Portal:FtpHost"] ?? "ftp.securedocs.example";
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public int SampleRows { get; set; } = 50;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostUploadAsync()
    {
        if (Upload is null || Upload.Length == 0) ErrorMessage = "Choose a CSV file to transfer.";
        else
        {
            await using var stream = Upload.OpenReadStream();
            var t = await TryApi(() => Api.UploadFileAsync(ClientCode, Upload.FileName, stream));
            if (t is not null) SuccessMessage = $"{t.FileName} received ({t.SizeBytes:N0} bytes). Processing starts within a few seconds.";
        }
        await LoadAsync();
    }

    public async Task OnPostSampleAsync()
    {
        var csv = SyntheticData.ToCsv(SyntheticData.GenerateDocuments(Math.Clamp(SampleRows, 1, 2000)));
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var name = $"{ClientCode}_members_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
        var t = await TryApi(() => Api.UploadFileAsync(ClientCode, name, ms));
        if (t is not null) SuccessMessage = $"{t.FileName} transferred ({t.SizeBytes:N0} bytes). Processing starts within a few seconds.";
        await LoadAsync();
    }

    public IActionResult OnGetSampleCsv() =>
        File(Encoding.UTF8.GetBytes(SyntheticData.ToCsv(SyntheticData.GenerateDocuments(25))), "text/csv", "securedocs_sample.csv");

    async Task LoadAsync() => Transfers = await TryApi(() => Api.GetTransfersAsync(ClientCode)) ?? [];
}
