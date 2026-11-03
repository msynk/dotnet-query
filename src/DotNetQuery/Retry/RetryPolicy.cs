namespace DotNetQuery;

/// <summary>
/// Computes the delay before the next retry attempt for a failed query or mutation.
/// </summary>
/// <param name="attempt">The zero-based index of the retry about to be scheduled (0 = first retry).</param>
/// <param name="error">The exception that caused the most recent failure.</param>
/// <returns>The delay to wait before the next attempt.</returns>
public delegate TimeSpan RetryDelayStrategy(int attempt, Exception error);

/// <summary>
/// Decides whether a failed query or mutation attempt should be retried. Consulted only while the retry
/// budget (<c>RetryCount</c>) is not exhausted, so it can veto retries but never extend them.
/// </summary>
/// <param name="failureCount">The number of consecutive failures so far, including this one (1 = first failure).</param>
/// <param name="error">The exception that caused the failure.</param>
/// <returns><see langword="true"/> to retry; <see langword="false"/> to fail immediately with <paramref name="error"/>.</returns>
public delegate bool RetryPredicate(int failureCount, Exception error);

/// <summary>
/// Factory helpers for common retry back-off strategies.
/// </summary>
public static class RetryPolicy
{
    /// <summary>A constant delay between every attempt.</summary>
    public static RetryDelayStrategy Constant(TimeSpan delay) => (_, _) => delay;

    /// <summary>
    /// Exponential back-off: <paramref name="baseDelay"/> × 2^attempt, capped at <paramref name="maxDelay"/>.
    /// </summary>
    public static RetryDelayStrategy Exponential(TimeSpan baseDelay, TimeSpan? maxDelay = null)
    {
        var cap = maxDelay ?? TimeSpan.FromSeconds(30);
        return (attempt, _) =>
        {
            var factor = Math.Pow(2, Math.Min(attempt, 30));
            var ticks = baseDelay.Ticks * factor;
            var capped = Math.Min(ticks, cap.Ticks);
            return TimeSpan.FromTicks((long)capped);
        };
    }

    /// <summary>
    /// Exponential back-off with full jitter, capped at <paramref name="maxDelay"/>.
    /// Jitter spreads retries to avoid thundering-herd load on the origin.
    /// </summary>
    public static RetryDelayStrategy ExponentialWithJitter(TimeSpan baseDelay, TimeSpan? maxDelay = null)
    {
        var cap = maxDelay ?? TimeSpan.FromSeconds(30);
        return (attempt, _) =>
        {
            var factor = Math.Pow(2, Math.Min(attempt, 30));
            var ticks = Math.Min(baseDelay.Ticks * factor, cap.Ticks);
            var jittered = ticks * Random.Shared.NextDouble();
            return TimeSpan.FromTicks((long)jittered);
        };
    }

    /// <summary>The default strategy: exponential back-off with jitter starting at one second.</summary>
    public static RetryDelayStrategy Default { get; } = ExponentialWithJitter(TimeSpan.FromSeconds(1));
}
