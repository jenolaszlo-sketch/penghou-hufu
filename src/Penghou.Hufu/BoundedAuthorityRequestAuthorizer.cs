namespace Penghou.Hufu;

/// <summary>Bounds active authority evaluations and their FIFO admission queue.</summary>
/// <remarks>
/// Share one instance for a common evaluator/store owner. Admission timeout applies only
/// to queued calls. Cancellation stops a caller's wait but active work retains its slot
/// until the inner task completes. The inner provider receives no caller cancellation;
/// it must await the actual work it owns rather than detach it internally.
/// </remarks>
public sealed class BoundedAuthorityRequestAuthorizer : IAuthorityRequestAuthorizer
{
    public const int MaximumExecuting = 256;
    public const int MaximumQueued = 4_096;
    public static readonly TimeSpan MaximumAdmissionTimeout = TimeSpan.FromDays(1);
    private readonly IAuthorityRequestAuthorizer inner;
    private readonly int maxExecuting;
    private readonly int maxQueued;
    private readonly TimeSpan admissionTimeout;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly LinkedList<Waiter> queue = new();
    private int executing;

    public BoundedAuthorityRequestAuthorizer(IAuthorityRequestAuthorizer inner, int maxExecuting,
        int maxQueued, TimeSpan admissionTimeout, TimeProvider? timeProvider = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (maxExecuting is < 1 or > MaximumExecuting) throw new ArgumentOutOfRangeException(nameof(maxExecuting));
        if (maxQueued is < 0 or > MaximumQueued) throw new ArgumentOutOfRangeException(nameof(maxQueued));
        if (admissionTimeout <= TimeSpan.Zero || admissionTimeout > MaximumAdmissionTimeout)
            throw new ArgumentOutOfRangeException(nameof(admissionTimeout));
        this.maxExecuting = maxExecuting;
        this.maxQueued = maxQueued;
        this.admissionTimeout = admissionTimeout;
        clock = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AuthorityValidation.IsValidRequest(request)) return new(request, AuthorityStatus.Deny);
        if (!await AcquireAsync(cancellationToken).ConfigureAwait(false))
            return new(request, AuthorityStatus.Unavailable);
        if (cancellationToken.IsCancellationRequested)
        {
            ReleaseExecuting();
            cancellationToken.ThrowIfCancellationRequested();
        }
        // ExecuteAsync owns the slot independently of the caller's cancellable wait.
        var result = await ExecuteAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<AuthorityRequestAuthorization> ExecuteAsync(AuthorityRequest request)
    {
        try
        {
            var result = await inner.AuthorizeAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (result is null || result.Request != request || !Enum.IsDefined(result.Status) ||
                (result.Decision is not null && !Enum.IsDefined(result.Decision.Status)) ||
                (result.Status == AuthorityStatus.Deny && result.Decision?.Status == AuthorityStatus.Permit) ||
                (result.Status == AuthorityStatus.Permit && !result.IsAuthorized))
                return new(request, AuthorityStatus.Unavailable);
            return result;
        }
        catch { return new(request, AuthorityStatus.Unavailable); }
        finally { ReleaseExecuting(); }
    }

    private ValueTask<bool> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (executing < maxExecuting && queue.Count == 0)
            {
                executing++;
                return ValueTask.FromResult(true);
            }
            if (queue.Count >= maxQueued) return ValueTask.FromResult(false);
            var waiter = new Waiter();
            waiter.Node = queue.AddLast(waiter);
            return new(WaitAsync(waiter, cancellationToken));
        }
    }

    private async Task<bool> WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        try
        {
            await waiter.Ready.Task.WaitAsync(admissionTimeout, clock, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            RemoveWaiter(waiter);
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch
        {
            RemoveWaiter(waiter);
            throw;
        }
    }

    private void RemoveWaiter(Waiter waiter)
    {
        lock (gate)
        {
            if (waiter.State == Waiter.Queued)
            {
                if (waiter.Node is not null) queue.Remove(waiter.Node);
                waiter.Node = null;
                waiter.State = Waiter.Removed;
            }
            else if (waiter.State == Waiter.Granted)
            {
                waiter.State = Waiter.Removed;
                executing--;
                GrantNext();
            }
        }
    }

    private void ReleaseExecuting()
    {
        lock (gate)
        {
            executing--;
            GrantNext();
        }
    }

    private void GrantNext()
    {
        while (executing < maxExecuting && queue.First is { } first)
        {
            queue.RemoveFirst();
            var waiter = first.Value;
            waiter.Node = null;
            if (waiter.State != Waiter.Queued) continue;
            waiter.State = Waiter.Granted;
            executing++;
            waiter.Ready.TrySetResult();
        }
    }

    private sealed class Waiter
    {
        internal const int Queued = 0, Granted = 1, Removed = 2;
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
        internal int State = Queued;
    }
}
