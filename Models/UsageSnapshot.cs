namespace TokenPulse.Models;

public sealed record UsageBucket(
    double UsedPercent,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record UsageSnapshot(
    string Plan,
    UsageBucket? Primary,
    UsageBucket? Secondary,
    string? Error = null)
{
    public bool IsAvailable => Primary is not null;
    public string PrimaryLabel { get; init; } = "현재 5시간 세션";
    public string SecondaryLabel { get; init; } = "주간 남은 사용량";

    public static UsageSnapshot Unavailable(string plan, string error) =>
        new(plan, null, null, error);
}
