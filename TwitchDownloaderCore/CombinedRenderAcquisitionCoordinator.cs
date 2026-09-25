using System;
using System.Threading;
using System.Threading.Tasks;

namespace TwitchDownloaderCore
{
    internal static class CombinedRenderAcquisitionCoordinator
    {
        internal static async Task RunAsync(
            Func<CancellationToken, Task> downloadVod,
            Func<CancellationToken, Task> downloadAndRenderChat,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(downloadVod);
            ArgumentNullException.ThrowIfNull(downloadAndRenderChat);

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task vodTask = RunBranchAsync(downloadVod, linkedCancellation.Token);
            Task chatTask = RunBranchAsync(downloadAndRenderChat, linkedCancellation.Token);
            Task allTasks = Task.WhenAll(vodTask, chatTask);
            Task firstCompleted = await Task.WhenAny(vodTask, chatTask);

            if (!firstCompleted.IsCompletedSuccessfully)
                linkedCancellation.Cancel();

            try
            {
                await allTasks;
            }
            catch
            {
                linkedCancellation.Cancel();
                try
                {
                    await allTasks;
                }
                catch
                {
                    // Preserve the first acquisition failure after both branches stop.
                }

                if (!firstCompleted.IsCompletedSuccessfully)
                    await firstCompleted;
                throw;
            }
        }

        private static async Task RunBranchAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            await operation(cancellationToken);
        }
    }
}
