namespace Wayfarer;

/// <summary>
/// Refuses to boot a path that would look like live remittance.
/// Standalone skeleton boot is allowed; payout still fails closed.
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
