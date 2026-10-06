using System.Text.Json;
using Gagamba.Execution;

namespace Penghou.Hufu.Zhinu;

/// <summary>
/// Durable correlation for one sandbox attempt, persisted as the external
/// operation payload. It records the durable workflow identity, the Hufu
/// authority identity and the trusted profile revision, and the negotiated
/// guarantees actually granted for this execution. It deliberately contains no
/// provider handle (no Gagamba execution handle, PID, job object, cgroup path
/// or launchd label): those are ephemeral provider state, not durable authority.
/// </summary>
public sealed record SandboxExecutionCorrelation
{
    public required Guid WorkflowRunId { get; init; }
    public required Guid StepExecutionId { get; init; }
    public required string StepKey { get; init; }
    public required int Attempt { get; init; }
    public required int Revision { get; init; }
    public required string AuthorizationRequestId { get; init; }
    public required string TenantId { get; init; }
    public required string SubjectId { get; init; }
    public required string AuthorityRunId { get; init; }
    public required string AuthorityRevision { get; init; }
    public required string AuthorityFence { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileRevision { get; init; }
    public required IReadOnlyList<string> RequestedGuarantees { get; init; }
    public required IReadOnlyList<string> NegotiatedGuarantees { get; init; }
    /// <summary>Whether the negotiated capability set included a native Full
    /// OwnerDeathCleanup guarantee, which decides automatic crash retry.</summary>
    public required bool OwnerDeathCleanupSufficient { get; init; }
    public Guid? ExternalOperationId { get; init; }
    public string? SandboxExecutionId { get; init; }
    public string Outcome { get; init; } = "Requested";
    public string? TerminationReason { get; init; }
}

internal static class SandboxCorrelationJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static string Serialize(SandboxExecutionCorrelation correlation) =>
        JsonSerializer.Serialize(correlation, Options);

    internal static SandboxExecutionCorrelation? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SandboxExecutionCorrelation>(json, Options);

    internal static IReadOnlyList<string> Describe(IEnumerable<ExecutionRequirement>? requirements) =>
        requirements is null
            ? Array.Empty<string>()
            : requirements.Select(r => $"{r.Capability}:{r.Minimum}:{r.AllowConstructed}").ToArray();
}
