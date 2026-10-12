namespace Pulse.Core;

/// <summary>Time source that tests can replace so durations are measured without real sleeps.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
