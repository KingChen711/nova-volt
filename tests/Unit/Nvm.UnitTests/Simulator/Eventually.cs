using System.Diagnostics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Waits for the worker to catch up with a clock that was moved under it.</summary>
/// <remarks>
/// A fake clock advances on the calling thread and the tick it releases is continued on the thread
/// pool, so moving the clock is not the same as the worker having acted on it. Waiting on the
/// worker's own progress rather than on a sleep is what keeps these tests deterministic instead of
/// merely usually right.
/// </remarks>
internal static class Eventually
{
    /// <summary>Polls until the condition holds, or gives up loudly.</summary>
    /// <param name="condition">What is being waited for.</param>
    /// <param name="because">What it means when this never happens.</param>
    /// <exception cref="TimeoutException">The condition did not hold in time.</exception>
    public static async Task TrueAsync(Func<bool> condition, string because)
    {
        // Stopwatch rather than a clock. NVM001 refuses DateTime.UtcNow across the repository and is
        // right to: this is a deadline on a hung test, not a moment in the plant's day, and the two
        // must not be reachable through the same call.
        var started = Stopwatch.GetTimestamp();

        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(1);
        }
    }
}
