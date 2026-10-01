namespace Farol.Testing;

/// <summary>Polls until a condition holds: file-system events arrive asynchronously.</summary>
internal static class Eventually
{
    public static async Task<T> MatchesAsync<T>(Func<Task<T>> probe, Func<T, bool> condition, CancellationToken cancellationToken, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var value = await probe();
            if (condition(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(100, cancellationToken);
        }
    }
}
