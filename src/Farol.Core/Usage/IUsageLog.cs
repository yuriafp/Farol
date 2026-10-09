namespace Farol.Core.Usage;

/// <summary>Where usage events go. Off unless the host turns it on, and writing to it never fails a request.</summary>
public interface IUsageLog
{
    bool Enabled { get; }

    void Write(UsageEvent usageEvent);
}

/// <summary>The usage log when it is off.</summary>
public sealed class NullUsageLog : IUsageLog
{
    private NullUsageLog()
    {
    }

    public static NullUsageLog Instance { get; } = new();

    public bool Enabled => false;

    public void Write(UsageEvent usageEvent)
    {
    }
}
