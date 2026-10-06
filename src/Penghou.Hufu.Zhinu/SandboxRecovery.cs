using Penghou.Zhinu;

namespace Penghou.Hufu.Zhinu;

public enum SandboxRecoveryAction
{
    /// <summary>Nothing to do (terminal operation).</summary>
    None,
    /// <summary>The prior domain is guaranteed gone after owner death; a fresh
    /// attempt with fresh authorization is safe.</summary>
    RetryFreshAttempt,
    /// <summary>The prior domain may still be alive; do not relaunch; reconcile.</summary>
    Abandon,
}

public sealed record SandboxRecoveryDecision(Guid OperationId, SandboxRecoveryAction Action, string Reason);

/// <summary>
/// Capability-driven recovery for durable sandbox operations. Whether an
/// interrupted execution may be safely retried is decided by the negotiated
/// <c>OwnerDeathCleanup</c> recorded for that execution, never by the OS name.
/// </summary>
public static class SandboxRecovery
{
    public static IReadOnlyList<SandboxRecoveryDecision> Plan(IReadOnlyList<WorkflowExternalOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var decisions = new List<SandboxRecoveryDecision>();
        foreach (var operation in operations)
        {
            if (operation.Status is ExternalOperationStatus.Completed
                or ExternalOperationStatus.Failed or ExternalOperationStatus.Cancelled)
                continue;
            var correlation = SandboxCorrelationJson.Deserialize(operation.PayloadJson);
            bool sufficient = correlation?.OwnerDeathCleanupSufficient == true;
            decisions.Add(sufficient
                ? new(operation.OperationId, SandboxRecoveryAction.RetryFreshAttempt,
                    "OwnerDeathCleanup sufficient: the prior domain is guaranteed gone; retry with fresh authority")
                : new(operation.OperationId, SandboxRecoveryAction.Abandon,
                    "OwnerDeathCleanup insufficient: the prior domain may be alive; reconcile, do not relaunch"));
        }
        return decisions;
    }
}
