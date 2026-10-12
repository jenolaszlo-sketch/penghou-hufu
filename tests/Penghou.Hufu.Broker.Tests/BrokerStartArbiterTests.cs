using Penghou.Hufu.Broker;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

public sealed class BrokerStartArbiterTests
{
    private static readonly BrokerAuthorityBinding Binding = new("tenant", "subject", "delegation-1", "gen-1", "fence-1");

    [Fact]
    public async Task MutationFirst_BlocksStartUntilReleased()
    {
        using var arbiter = new BrokerStartArbiter();
        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startRan = false;

        var mutation = arbiter.RunMutationAsync(Binding, async _ =>
        {
            mutationEntered.SetResult();
            await releaseMutation.Task;
        });
        await mutationEntered.Task;

        var start = arbiter.RunStartAsync(Binding, _ => { startRan = true; return ValueTask.CompletedTask; });
        await Task.Delay(50);
        Assert.False(startRan);

        releaseMutation.SetResult();
        await mutation;
        await start;
        Assert.True(startRan);
    }

    [Fact]
    public async Task StartFirst_BlocksMutationUntilReleased()
    {
        using var arbiter = new BrokerStartArbiter();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationRan = false;

        var start = arbiter.RunStartAsync(Binding, async _ =>
        {
            startEntered.SetResult();
            await releaseStart.Task;
        });
        await startEntered.Task;

        var mutation = arbiter.RunMutationAsync(Binding, _ => { mutationRan = true; return ValueTask.CompletedTask; });
        await Task.Delay(50);
        Assert.False(mutationRan);

        releaseStart.SetResult();
        await start;
        await mutation;
        Assert.True(mutationRan);
    }

    [Fact]
    public async Task DifferentBindings_DoNotBlockEachOther()
    {
        using var arbiter = new BrokerStartArbiter();
        var other = new BrokerAuthorityBinding("tenant", "subject", "delegation-2", "gen-1", "fence-1");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = arbiter.RunStartAsync(Binding, async _ => { held.SetResult(); await release.Task; });
        await held.Task;

        var second = arbiter.RunStartAsync(other, _ => ValueTask.CompletedTask);
        await second.AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task SameKey_IsMutuallyExclusiveUnderContention()
    {
        using var arbiter = new BrokerStartArbiter();
        var concurrent = 0;
        var maximum = 0;
        var tasks = Enumerable.Range(0, 64).Select(_ => arbiter.RunStartAsync(Binding, async _ =>
        {
            var current = Interlocked.Increment(ref concurrent);
            UpdateMax(ref maximum, current);
            await Task.Yield();
            Interlocked.Decrement(ref concurrent);
        }).AsTask()).ToArray();

        await Task.WhenAll(tasks);

        // Cleanup must never remove/recreate a keyed arbiter while another waiter
        // still depends on that ordering domain.
        Assert.Equal(1, maximum);

        static void UpdateMax(ref int target, int value)
        {
            int observed;
            while ((observed = Volatile.Read(ref target)) < value)
                if (Interlocked.CompareExchange(ref target, value, observed) == observed) break;
        }
    }

    [Fact]
    public async Task DifferentKeys_RunConcurrently()
    {
        using var arbiter = new BrokerStartArbiter();
        var other = new BrokerAuthorityBinding("tenant", "subject", "delegation-2", "gen-1", "fence-1");
        using var barrier = new Barrier(2);

        var first = arbiter.RunStartAsync(Binding, _ =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        }).AsTask();
        var second = arbiter.RunStartAsync(other, _ =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        }).AsTask();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
