namespace Whatsapp.IntegrationTests.Support;

internal static class Eventually
{
    /// <summary>Polls until the condition holds. Used where a background dispatcher does the work.</summary>
    public static async Task<T> Async<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var value = await read();
        while (!done(value))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The condition was not met in time. Last value: {value}");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
            value = await read();
        }

        return value;
    }
}
