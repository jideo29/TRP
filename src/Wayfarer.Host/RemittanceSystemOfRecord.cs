namespace Wayfarer;

/// <summary>
/// Fail-closed remittance boundary. This host is the remittance system of record.
/// It is not Pulse (T-11). Live payout is not implemented.
/// </summary>
public static class RemittanceSystemOfRecord
{
    public const string Statement =
        "This host is the remittance system of record, not Pulse (T-11).";

    public const string FailClosedCode = "REMITTANCE_FAIL_CLOSED";

    public static HostIdentity Identity { get; } = new(
        Product: "Wayfarer",
        Repository: "jideo29/TRP",
        Role: "remittance system of record",
        NotPulse: true,
        Tension: "T-11",
        Statement: Statement,
        LivePayout: false,
        MoneyPass: false,
        RemittanceJourneyUnblocked: false,
        JourneyAcceptClaimed: false,
        NovaRemittanceIntentsAbsorbedByPulse: false,
        Equicom: "HOLD",
        OutboundEmail: false,
        SellOpen: "FROZEN");

    public static PayoutRefusal RefusePayout() => new(
        StatusCode: StatusCodes.Status503ServiceUnavailable,
        Code: FailClosedCode,
        Message: "Live payout is not implemented. Wayfarer fails closed. "
            + Statement
            + " The remittance journey is not unblocked.",
        PayoutExecuted: false,
        MoneyPass: false);
}

public sealed record HostIdentity(
    string Product,
    string Repository,
    string Role,
    bool NotPulse,
    string Tension,
    string Statement,
    bool LivePayout,
    bool MoneyPass,
    bool RemittanceJourneyUnblocked,
    bool JourneyAcceptClaimed,
    bool NovaRemittanceIntentsAbsorbedByPulse,
    string Equicom,
    bool OutboundEmail,
    string SellOpen);

public sealed record PayoutRefusal(
    int StatusCode,
    string Code,
    string Message,
    bool PayoutExecuted,
    bool MoneyPass);
