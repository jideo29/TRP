namespace Wayfarer;

/// <summary>
/// Fail-closed remittance boundary. This host is the remittance system of record.
/// It is not Pulse (T-11). Live payout is not implemented. Pulse rails are not absorbed.
/// </summary>
public static class RemittanceSystemOfRecord
{
    public const string Statement =
        "This host is the remittance system of record, not Pulse (T-11).";

    public const string FailClosedCode = "REMITTANCE_FAIL_CLOSED";

    public const string PulseRailAbsorptionCode = "T11_PULSE_RAIL_ABSORPTION_REFUSED";

    public const string NovaIntentIntakeCode = "NOVA_REMITTANCE_INTENT_FAIL_CLOSED";

    public const string RemittanceJourney = "Blocked";

    /// <summary>Pulse payment/collections rails that must never execute on this remittance SoR.</summary>
    public static readonly IReadOnlyList<string> ForbiddenPulseRails =
    [
        "INSTAPAY",
        "PESONET",
        "PDDTS",
        "SWIFT",
        "BILLS",
        "QR",
        "ECOMMERCE",
        "COLLECTIONS"
    ];

    public static HostIdentity Identity { get; } = new(
        Product: "Wayfarer",
        Repository: "jideo29/TRP",
        Role: "remittance system of record",
        NotPulse: true,
        Tension: "T-11",
        Statement: Statement,
        LivePayout: false,
        MoneyPass: false,
        RemittanceJourney: RemittanceJourney,
        RemittanceJourneyUnblocked: false,
        JourneyAcceptClaimed: false,
        PulseRailsAbsorbed: false,
        NovaRemittanceIntentsAbsorbedByPulse: false,
        ForbiddenPulseRails: ForbiddenPulseRails,
        WaveDParked: true,
        Equicom: "HOLD",
        OutboundEmail: false,
        SellOpen: "FROZEN");

    public static bool IsForbiddenPulseRail(string? railOrChannel)
    {
        if (string.IsNullOrWhiteSpace(railOrChannel))
        {
            return false;
        }

        var normalized = railOrChannel.Trim().ToUpperInvariant();
        return ForbiddenPulseRails.Any(r => r.Equals(normalized, StringComparison.Ordinal));
    }

    public static PayoutRefusal RefusePayout(string? requestedRail = null)
    {
        if (IsForbiddenPulseRail(requestedRail))
        {
            return RefusePulseRailAbsorption(requestedRail!);
        }

        return new(
            StatusCode: StatusCodes.Status503ServiceUnavailable,
            Code: FailClosedCode,
            Message: "Live payout is not implemented. Wayfarer fails closed. "
                + Statement
                + " The remittance journey is not unblocked.",
            PayoutExecuted: false,
            MoneyPass: false,
            PulseRailsAbsorbed: false,
            RemittanceJourney: RemittanceJourney,
            RequestedRail: requestedRail);
    }

    public static PayoutRefusal RefusePulseRailAbsorption(string requestedRail) => new(
        StatusCode: StatusCodes.Status503ServiceUnavailable,
        Code: PulseRailAbsorptionCode,
        Message: "Pulse rail absorption is refused (T-11). Requested rail '"
            + requestedRail.Trim().ToUpperInvariant()
            + "' belongs to Pulse, not Wayfarer remittance SoR. "
            + Statement
            + " The remittance journey is not unblocked.",
        PayoutExecuted: false,
        MoneyPass: false,
        PulseRailsAbsorbed: false,
        RemittanceJourney: RemittanceJourney,
        RequestedRail: requestedRail.Trim().ToUpperInvariant());

    /// <summary>
    /// Nova remittance intents land here (not on Pulse). Intake is recorded as fail-closed only —
    /// no payout, no soft Accept, journey stays Blocked.
    /// </summary>
    public static NovaIntentRefusal RefuseNovaIntent(NovaRemittanceIntentRequest? request)
    {
        var rail = request?.Rail ?? request?.Channel;
        if (IsForbiddenPulseRail(rail))
        {
            var absorption = RefusePulseRailAbsorption(rail!);
            return new(
                StatusCode: absorption.StatusCode,
                Code: absorption.Code,
                Message: absorption.Message,
                IntentAcceptedForProcessing: false,
                PayoutExecuted: false,
                MoneyPass: false,
                PulseRailsAbsorbed: false,
                AbsorbedByPulse: false,
                RemittanceJourney: RemittanceJourney,
                Owner: "wayfarer",
                RequestedRail: absorption.RequestedRail);
        }

        return new(
            StatusCode: StatusCodes.Status503ServiceUnavailable,
            Code: NovaIntentIntakeCode,
            Message: "Nova remittance intents must come to Wayfarer, not Pulse (T-11). "
                + "Intent intake is fail-closed: live payout is not implemented and the remittance journey stays Blocked. "
                + Statement,
            IntentAcceptedForProcessing: false,
            PayoutExecuted: false,
            MoneyPass: false,
            PulseRailsAbsorbed: false,
            AbsorbedByPulse: false,
            RemittanceJourney: RemittanceJourney,
            Owner: "wayfarer",
            RequestedRail: rail);
    }
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
    string RemittanceJourney,
    bool RemittanceJourneyUnblocked,
    bool JourneyAcceptClaimed,
    bool PulseRailsAbsorbed,
    bool NovaRemittanceIntentsAbsorbedByPulse,
    IReadOnlyList<string> ForbiddenPulseRails,
    bool WaveDParked,
    string Equicom,
    bool OutboundEmail,
    string SellOpen);

public sealed record PayoutRefusal(
    int StatusCode,
    string Code,
    string Message,
    bool PayoutExecuted,
    bool MoneyPass,
    bool PulseRailsAbsorbed,
    string RemittanceJourney,
    string? RequestedRail);

public sealed record NovaRemittanceIntentRequest(
    string? IntentId,
    string? Corridor,
    string? Rail,
    string? Channel,
    decimal? Amount);

public sealed record NovaIntentRefusal(
    int StatusCode,
    string Code,
    string Message,
    bool IntentAcceptedForProcessing,
    bool PayoutExecuted,
    bool MoneyPass,
    bool PulseRailsAbsorbed,
    bool AbsorbedByPulse,
    string RemittanceJourney,
    string Owner,
    string? RequestedRail);
