namespace SecureDocs.Shared;

public static class DocumentTypes
{
    public const string Check = "Check";
    public const string CreditCard = "CreditCard";
    public const string MedicalStatement = "MedicalStatement";
    public const string ExplanationOfBenefits = "EOB";
    public const string MemberIdCard = "MemberIdCard";
    public static readonly string[] All = [Check, CreditCard, MedicalStatement, ExplanationOfBenefits, MemberIdCard];
}

public static class Channels
{
    public const string Print = "Print";
    public const string Email = "Email";
    public const string Sms = "Sms";
    public static readonly string[] All = [Print, Email, Sms];
}

public static class DocumentStatus
{
    public const string Queued = "Queued";
    public const string Composed = "Composed";
    public const string QaPassed = "QaPassed";
    public const string QaFailed = "QaFailed";
    public const string Delivered = "Delivered";
}

public static class BatchStatus
{
    public const string Received = "Received";
    public const string InProduction = "InProduction";
    public const string Completed = "Completed";
    public const string CompletedWithExceptions = "CompletedWithExceptions";
}

public static class IngestionSources
{
    public const string Api = "API";
    public const string Ftp = "FTP";
    public const string Portal = "Portal";
    /// <summary>Test traffic from New Relic synthetic monitors (header X-Ingestion-Source: Synthetic).</summary>
    public const string Synthetic = "Synthetic";
}

public record ClientDto(int Id, string Code, string Name, string Industry, string ApiKey);

public record CountItem(string Label, int Count);

public record BatchSummaryDto(int Id, string Reference, string Source, string Status, int DocumentCount,
    DateTime ReceivedUtc, DateTime? CompletedUtc);

public record DashboardDto(
    string ClientCode, string ClientName,
    int TotalBatches, int TotalDocuments, int DocumentsLast24h, int QueuedDocuments,
    int QaInspections, int QaFailures, double QaPassRate,
    List<CountItem> ByType, List<CountItem> ByChannel, List<CountItem> ByStatus,
    List<BatchSummaryDto> RecentBatches);

public record DocumentDto(long Id, int BatchId, string DocumentType, string Channel, string RecipientName,
    string MaskedMemberId, string City, string State, string PostalCode, string Status, string? QaResult,
    DateTime CreatedUtc);

public record BatchDetailDto(BatchSummaryDto Batch, List<DocumentDto> Documents);

public class DocumentRequest
{
    public string DocumentType { get; set; } = "";
    public string Channel { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public decimal? Amount { get; set; }
}

public class SubmitBatchRequest
{
    public string ClientCode { get; set; } = "";
    public string? Reference { get; set; }
    public List<DocumentRequest> Documents { get; set; } = [];
}

public record SubmitBatchResponse(int BatchId, string Reference, int Accepted, int Rejected, string Status,
    List<string> Warnings);

public record FtpTransferDto(int Id, string FileName, long SizeBytes, string Origin, string Status, int RecordCount,
    int? BatchId, DateTime UploadedUtc, DateTime? ProcessedUtc, string? Error);

public record QaInspectionDto(long Id, long DocumentId, string CameraId, string ExpectedName, string ScannedName,
    string ExpectedPostal, string ScannedPostal, bool Passed, DateTime InspectedUtc);

public record ProductionRunRequest(string? ClientCode, int MaxDocuments = 25);

public record ProductionRunResult(int Processed, int Printed, int Delivered, int QaPassed, int QaFailed, long ElapsedMs,
    bool Stalled = false);

public record VolumeReportRow(string DocumentType, string Channel, int Documents);

public record AddressVerifyRequest(List<AddressLine> Addresses);
public record AddressLine(string AddressLine1, string City, string State, string PostalCode);
public record AddressVerifyResponse(int Checked, int Deliverable, int Undeliverable, long ElapsedMs);

/// <summary>Faults injected inside the API tier (shared by every API caller).</summary>
public class ApiFaultState
{
    /// <summary>0-100: chance that ingestion, dashboard and production calls throw.</summary>
    public int ErrorRatePercent { get; set; }
    /// <summary>Ingestion and dashboard run a query against a table that doesn't exist.</summary>
    public bool DatabaseErrors { get; set; }
    /// <summary>The downstream address-verification call stalls.</summary>
    public bool SlowTraces { get; set; }
    public int SlowTraceMs { get; set; } = 3000;
    /// <summary>Search, dashboard and reports use a slow, non-sargable SQL path.</summary>
    public bool SlowQueries { get; set; }
    public int SlowQueryMs { get; set; } = 2500;
    /// <summary>Batch detail loads QA results one query per document.</summary>
    public bool NPlusOneQueries { get; set; }
    /// <summary>QA cameras misread ~35% of printed documents.</summary>
    public bool QaMismatches { get; set; }
    /// <summary>The production floor stops processing, so the queue backs up and SLAs are put at risk.</summary>
    public bool ProductionStalled { get; set; }
}

/// <summary>Faults injected inside the portal (web) tier.</summary>
public class PortalFaultState
{
    public bool PortalErrors { get; set; }
    public bool AutoTraffic { get; set; }
    public int PendingTrafficRequests { get; set; }
}

public class FaultPanelState
{
    public ApiFaultState Api { get; set; } = new();
    public PortalFaultState Portal { get; set; } = new();
    public string? ApiError { get; set; }
}

public static class Phi
{
    /// <summary>Masks an identifier so only the last 4 characters show (e.g. ****4821).</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= 4 ? "****" : new string('*', 4) + value[^4..];
}
