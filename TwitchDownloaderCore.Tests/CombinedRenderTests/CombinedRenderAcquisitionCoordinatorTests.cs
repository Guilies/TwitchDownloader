using TwitchDownloaderCore;

namespace TwitchDownloaderCore.Tests.CombinedRenderTests
{
    public sealed class CombinedRenderAcquisitionCoordinatorTests
    {
        [Fact]
        public async Task StartsVodAndChatBranchesBeforeAwaitingEither()
        {
            var bothStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int startedCount = 0;

            async Task Branch(CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                    bothStarted.SetResult(true);

                await bothStarted.Task.WaitAsync(cancellationToken);
            }

            await CombinedRenderAcquisitionCoordinator.RunAsync(
                Branch,
                Branch,
                CancellationToken.None);

            Assert.Equal(2, startedCount);
        }

        [Fact]
        public async Task CancelsSiblingAndWaitsForItWhenOneBranchFails()
        {
            var siblingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task FailingBranch(CancellationToken cancellationToken)
            {
                await siblingStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("fixture failure");
            }

            async Task CancellableBranch(CancellationToken cancellationToken)
            {
                siblingStarted.SetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    siblingStopped.SetResult(true);
                }
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => CombinedRenderAcquisitionCoordinator.RunAsync(
                    FailingBranch,
                    CancellableBranch,
                    CancellationToken.None));

            Assert.Equal("fixture failure", exception.Message);
            Assert.True(siblingStopped.Task.IsCompletedSuccessfully);
        }
    }
}
