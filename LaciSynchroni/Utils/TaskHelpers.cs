using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace LaciSynchroni.Utils
{
    /// <summary>
    /// Helper methods for working with tasks.
    /// </summary>
    public static class TaskHelpers
    {
        /// <summary>
        /// Run a task in the background, catching and logging any unhandled exceptions.
        /// </summary>
        public static void FireAndForget(Func<Task> taskFunc, ILogger? logger = null, CancellationToken? token = null, [CallerMemberName] string callerName = "")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (token.HasValue)
                        await taskFunc().WaitAsync(token.Value).ConfigureAwait(false);
                    else
                        await taskFunc().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected cancellation, ignore
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Unhandled exception in fire-and-forget task called by ({CallerName}): ", callerName);
                }
            });
        }
    }
}
