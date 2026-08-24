#nullable enable

sealed class BurstInspectorOptionsFixture
{
    public bool EnableBurstSafetyChecks { get; set; } = true;
    public bool ForceEnableBurstSafetyChecks { get; set; } = true;
    public bool EnableBurstDebug { get; set; } = true;
}
