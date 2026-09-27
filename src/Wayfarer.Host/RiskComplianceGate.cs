namespace Wayfarer;

/// <summary>
/// Claim-honest risk / compliance fail-closed. Unconfigured RiskPort (Sentinel)
/// and Aegis IdentityPort are Fail, not Pass. Unknown ≠ Allow.
/// </summary>
public static class RiskComplianceGate
{
    public const string RiskPortNotConfiguredCode = "RISKPORT_NOT_CONFIGURED";
    public const string AegisNotConfiguredCode = "AEGIS_IDENTITY_NOT_CONFIGURED";
    public const string EntitlementNotConfiguredCode = "ENTITLEMENT_NOT_CONFIGURED";
    public const string ComplianceUnknownCode = "COMPLIANCE_UNKNOWN_NOT_ALLOW";
    public const string ComplianceDenyCode = "COMPLIANCE_DENY_NOT_ALLOW";
    public const string SoftPassInventCode = "RISK_COMPLIANCE_SOFT_PASS_REFUSED";

    public const string NotConfigured = "NotConfigured";
    public const string DecisionUnknown = "Unknown";
    public const string DecisionDeny = "Deny";
    public const string DecisionAllow = "Allow";

    public static PeerHonesty Honesty { get; } = new(
        RiskPortStatus: NotConfigured,
        AegisIdentityStatus: NotConfigured,
        EntitlementStatus: NotConfigured,
        RiskPortIsAllow: false,
        ComplianceUnknownIsAllow: false,
        SoftPassInvented: false,
        JourneyAcceptClaimed: false,
        MoneyPass: false,
        Equicom: "HOLD",
        SellOpen: "FROZEN");

    public static bool IsConfigured(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && !status.Equals(NotConfigured, StringComparison.OrdinalIgnoreCase)
        && !status.Equals("Unconfigured", StringComparison.OrdinalIgnoreCase)
        && !status.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    public static RiskDecideResult Decide(RiskDecideRequest? request, IConfiguration configuration)
    {
        var riskStatus = configuration["Ports:RiskPort:Status"] ?? NotConfigured;
        if (!IsConfigured(riskStatus))
        {
            return Fail(
                RiskPortNotConfiguredCode,
                "RiskPort is NotConfigured. Unconfigured risk path is Fail, not Pass. "
                + "Unknown ≠ Allow. Sentinel RiskPort is not wired. "
                + RemittanceSystemOfRecord.Statement);
        }

        var aegisStatus = configuration["Ports:Identity:Status"] ?? NotConfigured;
        if (!IsConfigured(aegisStatus))
        {
            return Fail(
                AegisNotConfiguredCode,
                "Aegis IdentityPort is NotConfigured on the remittance risk path. "
                + "Unconfigured identity is Fail, not Pass. "
                + RemittanceSystemOfRecord.Statement);
        }

        // A configured RiskPort is still not claimed live on this scaffold.
        // Soft Allow invent is refused until Journey Accept.
        return Fail(
            SoftPassInventCode,
            "RiskPort peer label is present but live decide is not implemented. "
                + "Soft Pass / Allow invent is refused. Journey Accept is not claimed. "
                + RemittanceSystemOfRecord.Statement,
            requestedOutcome: request?.RequestedOutcome);
    }

    public static ComplianceConsultResult Consult(ComplianceConsultRequest? request, IConfiguration configuration)
    {
        var entitlementStatus = configuration["Ports:Entitlement:Status"] ?? NotConfigured;
        if (!IsConfigured(entitlementStatus))
        {
            return ComplianceFail(
                EntitlementNotConfiguredCode,
                DecisionUnknown,
                "Entitlement/compliance peer is NotConfigured. "
                + "Unconfigured compliance path is Fail, not Pass. Unknown ≠ Allow.");
        }

        var decision = NormalizeDecision(request?.Decision);
        if (decision.Equals(DecisionUnknown, StringComparison.OrdinalIgnoreCase))
        {
            return ComplianceFail(
                ComplianceUnknownCode,
                DecisionUnknown,
                "Compliance decision Unknown is not Allow. Unknown ≠ Allow.");
        }

        if (decision.Equals(DecisionDeny, StringComparison.OrdinalIgnoreCase))
        {
            return ComplianceFail(
                ComplianceDenyCode,
                DecisionDeny,
                "Compliance decision Deny is not Allow.");
        }

        if (decision.Equals(DecisionAllow, StringComparison.OrdinalIgnoreCase))
        {
            // Soft Allow invent refused on this fail-closed scaffold.
            return ComplianceFail(
                SoftPassInventCode,
                DecisionUnknown,
                "Soft Allow invent on compliance consult is refused. "
                + "Journey Accept is not claimed. money_pass stays false.");
        }

        return ComplianceFail(
            ComplianceUnknownCode,
            DecisionUnknown,
            "Unrecognized compliance decision is treated as Unknown, not Allow. Unknown ≠ Allow.");
    }

    public static PayoutRefusal RefusePayoutForRiskCompliance(
        IConfiguration configuration,
        string? requestedRail = null)
    {
        var decide = Decide(null, configuration);
        return new(
            StatusCode: decide.StatusCode,
            Code: decide.Code,
            Message: "Remittance payout blocked by risk/compliance fail-closed. " + decide.Message,
            PayoutExecuted: false,
            MoneyPass: false,
            PulseRailsAbsorbed: false,
            RemittanceJourney: RemittanceSystemOfRecord.RemittanceJourney,
            RequestedRail: requestedRail);
    }

    private static string NormalizeDecision(string? decision) =>
        string.IsNullOrWhiteSpace(decision) ? DecisionUnknown : decision.Trim();

    private static RiskDecideResult Fail(string code, string message, string? requestedOutcome = null) => new(
        StatusCode: StatusCodes.Status503ServiceUnavailable,
        Code: code,
        Decision: DecisionUnknown,
        IsAllowed: false,
        Result: "Fail",
        SoftPass: false,
        MoneyPass: false,
        JourneyAcceptClaimed: false,
        Message: message,
        RequestedOutcome: requestedOutcome);

    private static ComplianceConsultResult ComplianceFail(string code, string decision, string message) => new(
        StatusCode: StatusCodes.Status403Forbidden,
        Code: code,
        Decision: decision,
        IsAllowed: false,
        Result: "Fail",
        SoftPass: false,
        MoneyPass: false,
        JourneyAcceptClaimed: false,
        Message: message);
}

public sealed record PeerHonesty(
    string RiskPortStatus,
    string AegisIdentityStatus,
    string EntitlementStatus,
    bool RiskPortIsAllow,
    bool ComplianceUnknownIsAllow,
    bool SoftPassInvented,
    bool JourneyAcceptClaimed,
    bool MoneyPass,
    string Equicom,
    string SellOpen);

public sealed record RiskDecideRequest(string? RequestedOutcome);

public sealed record RiskDecideResult(
    int StatusCode,
    string Code,
    string Decision,
    bool IsAllowed,
    string Result,
    bool SoftPass,
    bool MoneyPass,
    bool JourneyAcceptClaimed,
    string Message,
    string? RequestedOutcome);

public sealed record ComplianceConsultRequest(string? Capability, string? Decision);

public sealed record ComplianceConsultResult(
    int StatusCode,
    string Code,
    string Decision,
    bool IsAllowed,
    string Result,
    bool SoftPass,
    bool MoneyPass,
    bool JourneyAcceptClaimed,
    string Message);
