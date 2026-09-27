namespace Wayfarer;

/// <summary>
/// ADR-004 fail-closed money ports. Wayfarer emits no live PostingInstruction and owns no GL.
/// Unconfigured PostingPort, CustomerDirectoryPort, and DocumentPort are Fail, not Allow.
/// Unknown ≠ Allow. No outbound peer call. money_pass stays false. Journey stays Blocked.
/// </summary>
public static class MoneyPortsGate
{
    public const string PostingPortNotConfiguredCode = "POSTINGPORT_NOT_CONFIGURED";
    public const string StubPostingNotAllowCode = "STUB_POSTING_NOT_ALLOW";
    public const string PostingSubmitNotImplementedCode = "POSTING_SUBMIT_NOT_IMPLEMENTED";
    public const string LocalGlRefusedCode = "LOCAL_GL_REFUSED";
    public const string CustomerDirectoryNotConfiguredCode = "CUSTOMER_DIRECTORY_NOT_CONFIGURED";
    public const string CustomerDirectoryLocalNotAllowCode = "CUSTOMER_DIRECTORY_LOCAL_NOT_ALLOW";
    public const string CustomerDirectoryNotImplementedCode = "CUSTOMER_DIRECTORY_NOT_IMPLEMENTED";
    public const string LocalCifRefusedCode = "LOCAL_CIF_REFUSED";
    public const string DocumentPortNotConfiguredCode = "DOCUMENTPORT_NOT_CONFIGURED";
    public const string DocumentEvidenceNotImplementedCode = "DOCUMENT_EVIDENCE_NOT_IMPLEMENTED";

    public const string NotConfigured = "NotConfigured";

    public static MoneyPortsHonesty Honesty { get; } = new(
        PostingPortStatus: NotConfigured,
        CustomerDirectoryStatus: NotConfigured,
        DocumentPortStatus: NotConfigured,
        PostingPortIsAllow: false,
        CustomerDirectoryIsAllow: false,
        DocumentPortIsAllow: false,
        LocalGlOwned: false,
        PostingSubmitted: false,
        OutboundCall: false,
        MoneyPass: false,
        JourneyAcceptClaimed: false,
        RemittanceJourney: RemittanceSystemOfRecord.RemittanceJourney,
        WaveDParked: true,
        Equicom: "HOLD",
        SellOpen: "FROZEN",
        AdminGlassShipped: false);

    public static bool IsConfigured(string? status) =>
        RiskComplianceGate.IsConfigured(status);

    public static PostingSubmitResult Submit(PostingSubmitRequest? request, IConfiguration configuration)
    {
        var status = configuration["Ports:Posting:Status"] ?? NotConfigured;
        if (IsStubOrLocal(status))
        {
            return PostingFail(
                StubPostingNotAllowCode,
                StatusCodes.Status403Forbidden,
                "Stub or local posting is not Allow. Unknown ≠ Allow. "
                + "No outbound PostingPort call. Local GL is refused. money_pass stays false. "
                + RemittanceSystemOfRecord.Statement);
        }

        if (!IsConfigured(status))
        {
            return PostingFail(
                PostingPortNotConfiguredCode,
                StatusCodes.Status503ServiceUnavailable,
                "PostingPort is NotConfigured. Unconfigured posting is Fail, not Pass. "
                + "Unknown ≠ Allow. No outbound PostingPort call. Wayfarer does not own GL. "
                + RemittanceSystemOfRecord.Statement);
        }

        return PostingFail(
            PostingSubmitNotImplementedCode,
            StatusCodes.Status503ServiceUnavailable,
            "PostingPort peer label is present but live submit is not implemented. "
            + "No outbound PostingPort call. Soft money Pass invent is refused. "
            + "money_pass stays false. Journey Accept is not claimed. "
            + RemittanceSystemOfRecord.Statement,
            requestedOutcome: request?.RequestedOutcome);
    }

    public static CustomerPortResult LookupCustomer(CustomerLookupRequest? request, IConfiguration configuration)
    {
        var status = configuration["Ports:CustomerDirectory:Status"] ?? NotConfigured;
        if (IsStubOrLocal(status))
        {
            return CustomerFail(
                CustomerDirectoryLocalNotAllowCode,
                StatusCodes.Status403Forbidden,
                "Local or stub CustomerDirectory is not Titan CIF and is not Allow. Unknown ≠ Allow. "
                + "No outbound customer lookup. No local CIF was created.");
        }

        if (!IsConfigured(status))
        {
            return CustomerFail(
                CustomerDirectoryNotConfiguredCode,
                StatusCodes.Status503ServiceUnavailable,
                "CustomerDirectoryPort is NotConfigured. Unconfigured customer lookup is Fail, not Pass. "
                + "Unknown ≠ Allow. No outbound customer lookup. No local CIF was created.");
        }

        return CustomerFail(
            CustomerDirectoryNotImplementedCode,
            StatusCodes.Status503ServiceUnavailable,
            "CustomerDirectoryPort peer label is present but live lookup is not implemented. "
            + "No outbound customer lookup. No local CIF was created. Journey Accept is not claimed.",
            requestedReference: request?.CustomerRef);
    }

    public static CustomerPortResult RefuseLocalCif(CustomerLookupRequest? request) =>
        CustomerFail(
            LocalCifRefusedCode,
            StatusCodes.Status403Forbidden,
            "Wayfarer does not own CIF. Local customer create is refused. "
            + "Customer gold stays on Titan or a foreign CIF behind CustomerDirectoryPort. "
            + "No local CIF was created.",
            requestedReference: request?.CustomerRef);

    public static LocalGlRefusal RefuseLocalGl() => new(
        StatusCode: StatusCodes.Status403Forbidden,
        Code: LocalGlRefusedCode,
        Result: "Fail",
        IsAllowed: false,
        LocalGlOwned: false,
        BalanceChanged: false,
        MoneyPass: false,
        JourneyAcceptClaimed: false,
        RemittanceJourney: RemittanceSystemOfRecord.RemittanceJourney,
        Message: "Local GL balance mutation is refused. Wayfarer does not own ledger balances. "
            + "Money effects require PostingPort (Titan or foreign CBS). "
            + "money_pass stays false. The remittance journey is not unblocked.");

    public static DocumentPortResult ClaimEvidence(EvidenceClaimRequest? request, IConfiguration configuration)
    {
        var status = configuration["Ports:Document:Status"] ?? NotConfigured;
        if (!IsConfigured(status) || IsStubOrLocal(status))
        {
            return DocumentFail(
                DocumentPortNotConfiguredCode,
                "DocumentPort is NotConfigured. An evidence claim is Fail, not Allow. "
                + "Unknown ≠ Allow. Scribe is not wired. No outbound document call.");
        }

        return DocumentFail(
            DocumentEvidenceNotImplementedCode,
            "DocumentPort peer label is present but a live Scribe evidence write is not implemented. "
            + "No outbound document call. Journey Accept is not claimed.",
            requestedPack: request?.Pack);
    }

    private static bool IsStubOrLocal(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.Equals("Stub", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Demo", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Local", StringComparison.OrdinalIgnoreCase)
            || status.Equals("StubPostingAdapter", StringComparison.OrdinalIgnoreCase);
    }

    private static PostingSubmitResult PostingFail(
        string code,
        int statusCode,
        string message,
        string? requestedOutcome = null) => new(
        StatusCode: statusCode,
        Code: code,
        Result: "Fail",
        IsAllowed: false,
        PostingSubmitted: false,
        OutboundCall: false,
        LocalGlOwned: false,
        MoneyPass: false,
        JourneyAcceptClaimed: false,
        RemittanceJourney: RemittanceSystemOfRecord.RemittanceJourney,
        WaveDParked: true,
        Message: message,
        RequestedOutcome: requestedOutcome);

    private static CustomerPortResult CustomerFail(
        string code,
        int statusCode,
        string message,
        string? requestedReference = null) => new(
        StatusCode: statusCode,
        Code: code,
        Result: "Fail",
        IsAllowed: false,
        LocalCifCreated: false,
        OutboundCall: false,
        MoneyPass: false,
        JourneyAcceptClaimed: false,
        RemittanceJourney: RemittanceSystemOfRecord.RemittanceJourney,
        Message: message,
        RequestedReference: requestedReference);

    private static DocumentPortResult DocumentFail(string code, string message, string? requestedPack = null) => new(
        StatusCode: StatusCodes.Status503ServiceUnavailable,
        Code: code,
        Result: "Fail",
        IsAllowed: false,
        EvidenceWritten: false,
        OutboundCall: false,
        MoneyPass: false,
        JourneyAcceptClaimed: false,
        Message: message,
        RequestedPack: requestedPack);
}

public sealed record MoneyPortsHonesty(
    string PostingPortStatus,
    string CustomerDirectoryStatus,
    string DocumentPortStatus,
    bool PostingPortIsAllow,
    bool CustomerDirectoryIsAllow,
    bool DocumentPortIsAllow,
    bool LocalGlOwned,
    bool PostingSubmitted,
    bool OutboundCall,
    bool MoneyPass,
    bool JourneyAcceptClaimed,
    string RemittanceJourney,
    bool WaveDParked,
    string Equicom,
    string SellOpen,
    bool AdminGlassShipped);

public sealed record PostingSubmitRequest(string? InstructionId, string? RequestedOutcome);

public sealed record PostingSubmitResult(
    int StatusCode,
    string Code,
    string Result,
    bool IsAllowed,
    bool PostingSubmitted,
    bool OutboundCall,
    bool LocalGlOwned,
    bool MoneyPass,
    bool JourneyAcceptClaimed,
    string RemittanceJourney,
    bool WaveDParked,
    string Message,
    string? RequestedOutcome);

public sealed record CustomerLookupRequest(string? CustomerRef);

public sealed record CustomerPortResult(
    int StatusCode,
    string Code,
    string Result,
    bool IsAllowed,
    bool LocalCifCreated,
    bool OutboundCall,
    bool MoneyPass,
    bool JourneyAcceptClaimed,
    string RemittanceJourney,
    string Message,
    string? RequestedReference);

public sealed record LocalGlRefusal(
    int StatusCode,
    string Code,
    string Result,
    bool IsAllowed,
    bool LocalGlOwned,
    bool BalanceChanged,
    bool MoneyPass,
    bool JourneyAcceptClaimed,
    string RemittanceJourney,
    string Message);

public sealed record EvidenceClaimRequest(string? Pack);

public sealed record DocumentPortResult(
    int StatusCode,
    string Code,
    string Result,
    bool IsAllowed,
    bool EvidenceWritten,
    bool OutboundCall,
    bool MoneyPass,
    bool JourneyAcceptClaimed,
    string Message,
    string? RequestedPack);
