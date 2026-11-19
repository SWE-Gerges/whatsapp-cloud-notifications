using Microsoft.Extensions.Time.Testing;

namespace Whatsapp.Tests.Support;

internal static class TimeExtensions
{
    /// <summary>
    /// Completes a task that waits on a fake clock by advancing the clock in small steps. No real time passes
    /// beyond a few scheduler yields.
    /// </summary>
    public static async Task<T> RunWithTimeAsync<T>(this FakeTimeProvider time, Task<T> task, TimeSpan? step = null)
    {
        var tick = step ?? TimeSpan.FromMilliseconds(250);
        var guard = 0;
        while (!task.IsCompleted)
        {
            time.Advance(tick);
            await Task.Delay(1, TestContext.Current.CancellationToken);
            if (++guard > 20_000)
            {
                throw new TimeoutException("The task did not complete while the fake clock advanced.");
            }
        }

        return await task;
    }

    public static async Task RunWithTimeAsync(this FakeTimeProvider time, Task task, TimeSpan? step = null)
    {
        await time.RunWithTimeAsync(WrapAsync(task), step);
    }

    private static async Task<bool> WrapAsync(Task task)
    {
        await task;
        return true;
    }
}
