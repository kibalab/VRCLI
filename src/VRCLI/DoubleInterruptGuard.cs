namespace KibaLab.WorldDeployment;

internal sealed class DoubleInterruptGuard(TimeSpan window)
{
    private readonly object gate = new();
    private DateTimeOffset? firstInterrupt;

    public bool Register(DateTimeOffset now)
    {
        lock (gate)
        {
            if (firstInterrupt.HasValue && now - firstInterrupt.Value <= window)
            {
                firstInterrupt = null;
                return true;
            }

            firstInterrupt = now;
            return false;
        }
    }
}
