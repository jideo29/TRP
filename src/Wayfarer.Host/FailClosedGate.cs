namespace Wayfarer;

/// <summary>
/// Refuses to boot a path that would look like live remittance or a soft unpark.
/// Standalone skeleton boot is allowed; payout and Nova intake still fail closed.
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

        var mode = configuration["Deployment:Mode"] ?? "Unlabeled";
        if (IsRegulatedMode(mode))
        {
            throw new InvalidOperationException(
                "Wayfarer fails closed: " + mode
                + " is refused because live payout is not implemented and peer ports are not a payout. "
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
