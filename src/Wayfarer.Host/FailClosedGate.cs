namespace Wayfarer;

/// <summary>
/// Refuses to boot a path that would look like live remittance, soft unpark, or soft risk Pass.
/// Standalone skeleton boot is allowed; payout, Nova intake, and risk/compliance still fail closed.
/// </summary>
public static class FailClosedGate
{
    public static void EnsureCanBoot(IConfiguration configuration)
    {
        if (configuration.GetValue("Wayfarer:LivePayout", false))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: live payout is not implemented. "
                + RemittanceSystemOfRecord.Statement
                + " The remittance journey is not unblocked.");
        }

        if (configuration.GetValue("Wayfarer:AbsorbPulseRails", false))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: Pulse rail absorption is refused (T-11). "
                + RemittanceSystemOfRecord.Statement
                + " The remittance journey is not unblocked.");
        }

        if (configuration.GetValue("Wayfarer:RemittanceJourneyUnblocked", false))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: remittance journey soft-unpark is refused. "
                + "Wave D stays PARKED. Journey Accept is not claimed. "
                + RemittanceSystemOfRecord.Statement);
        }

        if (configuration.GetValue("Wayfarer:InventRiskAllow", false)
            || configuration.GetValue("Wayfarer:SoftCompliancePass", false))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: soft risk/compliance Pass invent is refused. "
                + "Unknown ≠ Allow. Unconfigured RiskPort/Aegis/Entitlement paths are Fail, not Pass. "
                + RemittanceSystemOfRecord.Statement);
        }

        if (configuration.GetValue("Wayfarer:ClaimMoneyPass", false))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: money_pass invent is refused. "
                + "money_pass stays false. No outbound PostingPort call. "
                + RemittanceSystemOfRecord.Statement
                + " The remittance journey is not unblocked.");
        }

        if (configuration.GetValue("Wayfarer:OwnLocalGl", false))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: local GL ownership is refused. "
                + "Balances change only via PostingPort. "
                + RemittanceSystemOfRecord.Statement);
        }

        if (configuration.GetValue("Wayfarer:AllowStubPosting", false))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: stub posting is not Allow. Unknown ≠ Allow. "
                + "No outbound PostingPort call. money_pass stays false. "
                + RemittanceSystemOfRecord.Statement);
        }

        var mode = configuration["Deployment:Mode"] ?? "Unlabeled";
        if (IsRegulatedMode(mode))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: " + mode
                + " is refused because live payout is not implemented and peer ports are not a payout. "
                + "RiskPort/Aegis/Entitlement remain NotConfigured (Fail-not-Pass). "
                + "PostingPort and CustomerDirectoryPort stay NotConfigured (Fail-not-Pass). "
                + "Local GL is refused. "
                + RemittanceSystemOfRecord.Statement
                + " The remittance journey is not unblocked.");
        }
    }

    private static bool IsRegulatedMode(string mode) =>
        mode.Equals("TrudiIntegrated", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("PlatformIntegrated", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("ForeignIntegrated", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("Production", StringComparison.OrdinalIgnoreCase);
}
