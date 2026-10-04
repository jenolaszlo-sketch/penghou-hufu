using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Luban.Sqlite;
using Penghou.Hufu.Sqlite;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Changes;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;

namespace Hufu.LocalHost;

internal sealed record LocalHostState(string OperatorSid, string TenantId, string RunId,
    string RevisionId, string FenceId, string WorkspaceId, string Target,
    DateTimeOffset NotBefore, DateTimeOffset AuthorityExpiry, DateTimeOffset CeilingExpiry,
    string PublicationCommandId, DateTimeOffset PublicationApprovalExpiry, bool PublicationApproved,
    string? BootstrapSeedBase64 = null)
{
    internal AuthenticatedAuthorityContext Context => new(TenantId, OperatorSid, RunId, RevisionId, FenceId);
    internal AuthoritySnapshot Proposal => Snapshot("local-exact-policy-v1", AuthorityExpiry);
    internal AuthoritySnapshot Ceiling => Snapshot("local-operator-ceiling-v1", CeilingExpiry);

    private AuthoritySnapshot Snapshot(string version, DateTimeOffset expiry)
    {
        var grants = new List<AuthorityGrant>
        {
            new("exact-content", [AuthorityAction.ReadFile, AuthorityAction.PatchFile,
                AuthorityAction.WriteFile, AuthorityAction.Release],
                new(WorkspaceId, Target, AuthorityScopeKind.Exact), [], NotBefore, expiry)
        };
        var pieces = Target.Split('/');
        for (var i = 0; i <= pieces.Length; i++)
            grants.Add(new("metadata-" + i, [AuthorityAction.ReadMetadata, AuthorityAction.Release],
                new(WorkspaceId, string.Join('/', pieces.Take(i)), AuthorityScopeKind.Exact), [], NotBefore, expiry));
        return new(Context, version, [new("local-operator", grants)], [], expiry);
    }

    internal void Validate(string currentSid)
    {
        if (OperatorSid != currentSid || !AuthorityValidation.ValidContext(Context) ||
            !AuthorityValidation.ValidToken(WorkspaceId) || !AuthorityValidation.ValidToken(PublicationCommandId) ||
            !LocalHostApplication.ValidTarget(Target) || NotBefore >= AuthorityExpiry ||
            AuthorityExpiry >= CeilingExpiry || CeilingExpiry - NotBefore > TimeSpan.FromHours(2) ||
            PublicationApprovalExpiry > AuthorityExpiry)
            throw new InvalidDataException("Invalid protected host configuration.");
        _ = Proposal; _ = Ceiling;
    }
}

internal sealed record LocalPatchDraft(string OperationId, int Offset, int DeleteLength,
    string ReplacementBase64, string AdmissionIdentity, bool Reviewed = false,
    string? ApprovalCommandId = null, DateTimeOffset? ApprovalExpiresAt = null);
internal sealed record LocalHostResult(string Status, string? OperationId = null,
    string? AdmissionIdentity = null, string? Target = null, string? OriginalVersion = null,
    string? ProposedVersion = null, int? OriginalBytes = null, int? ProposedBytes = null);
internal sealed record LocalPatchReview(LocalHostResult Facts, string? BeforeUtf8 = null, string? AfterUtf8 = null);

/// <summary>
/// A closed, single-operator local application. The management CLI is a trusted
/// operator channel; give agents only the bounded preview/apply data surface.
/// It does not contain arbitrary code running as the operator account.
/// </summary>
internal sealed class LocalHostApplication : IDisposable
{
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumControlBytes = 2_097_152;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 16
    };
    private readonly LocalHostCustody custody;
    private readonly FileStream lease;
    private readonly LocalHostState state;
    private readonly LocalHostServiceConfiguration configuration;
    private readonly LocalHostPatchJournalAuthorizer journalPolicy;
    private readonly LocalHostMode mode;
    internal SqlitePatchOutcomeJournal Journal { get; }
    internal SqliteAuthorityStore Store { get; }
    internal HufuSinglePatchHost Host { get; }
    internal LocalWorkspaceProvider Provider { get; }
    internal AuthorityStoreActor Actor => configuration.Actor;
    internal AuthenticatedAuthorityContext Context => state.Context;

    private LocalHostApplication(LocalHostCustody custody, FileStream lease,
        LocalHostState state, LocalHostMode mode, string? operationId)
    {
        this.custody = custody; this.lease = lease; this.state = state; this.mode = mode;
        configuration = LocalHostServiceConfiguration.Create(state.TenantId, state.OperatorSid,
            state.Context, state.OperatorSid, mode, operationId);
        var identity = new CustodiedIdentitySource(custody);
        var policy = new LocalHostStoreAuthorizer(configuration, identity);
        var issuance = new LocalHostIssuanceTrustSource(configuration, identity, new ProtectedIssuanceSource(custody));
        journalPolicy = new(configuration, identity);
        Journal = new(Path.Combine(custody.ControlRoot, "authority.db"), journalPolicy);
        Store = new(Journal, new BoundedAuthorityIssuanceAuthorizer(issuance, policy));
        Host = new(Actor, Context, Store, new CedarAuthorityEvaluator(), new LocalHostDecisionEvidenceFactory(), Journal);
        Provider = new(new WorkspaceId(state.WorkspaceId), custody.WorkspaceRoot, LocalPatchNamespace.HostControlled);
    }

    internal static async Task<LocalHostResult> InitializeAsync(string root, string target, string initialBase64)
    {
        if (!ValidTarget(target)) throw new ArgumentException("Use a canonical lowercase exact target, at most eight components.");
        _ = Decode(initialBase64);
        var sid = CurrentSid();
        var custody = LocalHostCustody.CreateNew(root, new SecurityIdentifier(sid));
        var now = DateTimeOffset.UtcNow;
        var state = new LocalHostState(sid, "local-" + Guid.NewGuid().ToString("N"),
            "run-" + Guid.NewGuid().ToString("N"), "revision-1", "fence-" + Guid.NewGuid().ToString("N"),
            "workspace-" + Guid.NewGuid().ToString("N"), target, now.AddMinutes(-1), now.AddMinutes(15),
            now.AddHours(1), "publish-" + Guid.NewGuid().ToString("N"), now.AddMinutes(5), true, initialBase64);
        state.Validate(sid);
        WriteControl(custody, "host.json", state, replace: false);
        using var app = Open(root, LocalHostMode.Operator);
        return await app.ActivateAsync();
    }

    internal async Task<LocalHostResult> ActivateAsync()
    {
        RequireOperator();
        var current = await Store.ReadCurrentAsync(Actor, Context);
        if (current.Status == AuthorityReadStatus.Active && current.Record is { } existing &&
            existing.CommandId == state.PublicationCommandId && existing.Sequence == 1 &&
            existing.Snapshot?.Identity == state.Proposal.Identity)
        {
            // A lost publication/finalization response can be finalized from the
            // exact current record without treating history as new authority.
            WriteControl(custody, "host.json", state with { PublicationApproved = false, BootstrapSeedBase64 = null }, true);
            return new("Initialized", Target: state.Target);
        }
        if (current.Status != AuthorityReadStatus.NotFound || !state.PublicationApproved ||
            state.BootstrapSeedBase64 is null || state.PublicationApprovalExpiry <= DateTimeOffset.UtcNow)
            return new("ActivationDeniedOrUnavailable");
        var initial = Decode(state.BootstrapSeedBase64);
        var file = Path.Combine(custody.WorkspaceRoot, state.Target.Replace('/', Path.DirectorySeparatorChar));
        // Explicit operator bootstrap, limited to the newly allocated namespace.
        // A partially written or changed seed is never silently overwritten.
        if (File.Exists(file))
        {
            custody.Validate();
            if (new FileInfo(file).Length != initial.Length || !File.ReadAllBytes(file).AsSpan().SequenceEqual(initial))
                return new("BootstrapSeedChanged");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var seed = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            seed.Write(initial); seed.Flush(flushToDisk: true);
        }
        custody.Validate();
        var publication = await Store.PublishAsync(new(state.PublicationCommandId, Actor, state.Proposal, 0));
        if (publication.Status is not (AuthorityMutationStatus.Applied or AuthorityMutationStatus.Replayed) ||
            publication.Record?.Snapshot?.Identity != state.Proposal.Identity)
            return new("ActivationDeniedOrUnavailable");
        WriteControl(custody, "host.json", state with { PublicationApproved = false, BootstrapSeedBase64 = null }, true);
        return new("Initialized", Target: state.Target);
    }

    internal static LocalHostApplication Open(string root, LocalHostMode mode, string? operationId = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown host mode.");
        if (operationId is not null && !AuthorityValidation.ValidToken(operationId))
            throw new ArgumentException("A bounded operation identity is required.");
        var sid = CurrentSid();
        var custody = LocalHostCustody.OpenExisting(root, new SecurityIdentifier(sid));
        var lockPath = Path.Combine(custody.ControlRoot, "host.lock");
        if (File.Exists(lockPath)) custody.ValidateControlFile("host.lock");
        // One command owns the application namespace. A busy host fails closed;
        // human approval never waits while holding a SQLite writer transaction.
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            custody.Validate();
            var state = ReadControl<LocalHostState>(custody, "host.json");
            state.Validate(sid);
            return new(custody, lease, state, mode, operationId);
        }
        catch { lease.Dispose(); throw; }
    }

    internal async Task<LocalHostResult> PreviewAsync(string operationId, int offset, int deleteLength, string replacementBase64)
    {
        RequireActiveBootstrap();
        if (configuration.BoundOperationId != operationId) throw new UnauthorizedAccessException();
        var name = DraftName(operationId);
        if (File.Exists(Path.Combine(custody.ControlRoot, name))) return new("OperationAlreadyExists", operationId);
        if (Directory.EnumerateFiles(custody.ControlRoot, "patch-*.json").Take(65).Count() >= 64)
            return new("DraftCapacityExceeded", operationId);
        var draft = new LocalPatchDraft(operationId, offset, deleteLength, replacementBase64, "");
        var capture = await CaptureAsync(draft);
        if (capture is null) return new("PreviewDeniedOrUnavailable", operationId);
        var identity = HufuSinglePatchHost.AdmissionIdentity(Context, capture.Admission);
        WriteControl(custody, name, draft with { AdmissionIdentity = identity }, replace: false);
        return Summary("Prepared", capture.Admission, identity);
    }

    internal async Task<LocalHostResult> ApproveAsync(string operationId, string confirmedIdentity, int lifetimeSeconds = 300)
    {
        RequireOperator();
        RequireActiveBootstrap();
        if (lifetimeSeconds is < 1 or > 300) throw new ArgumentException("Approval lifetime must be 1 to 300 seconds.");
        var draft = ReadDraft(operationId);
        if (!string.Equals(confirmedIdentity, draft.AdmissionIdentity, StringComparison.Ordinal)) return new("ConfirmationMismatch", operationId);
        var capture = await CaptureAsync(draft);
        if (capture is null || HufuSinglePatchHost.AdmissionIdentity(Context, capture.Admission) != draft.AdmissionIdentity)
            return new("PreviewChangedOrUnavailable", operationId);
        if (!draft.Reviewed) return new("OperatorReviewRequired", operationId);
        if (draft.ApprovalExpiresAt is null)
        {
            var expiry = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds);
            if (expiry > state.AuthorityExpiry) expiry = state.AuthorityExpiry;
            draft = draft with { ApprovalCommandId = "approve-" + Hash(operationId), ApprovalExpiresAt = expiry };
            // Persist the exact command intent before journal append. A retry
            // after response loss reuses this expiry, never silently extends it.
            WriteControl(custody, DraftName(operationId), draft, true);
        }
        if (draft.ApprovalExpiresAt <= DateTimeOffset.UtcNow) return new("Expired", operationId);
        var command = new PatchApprovalCommand(draft.ApprovalCommandId!, Actor, Context, capture.Admission, draft.ApprovalExpiresAt.Value);
        using var approval = journalPolicy.BeginApproval(command);
        var result = await Journal.ApproveAsync(command);
        return Summary(result.Status.ToString(), capture.Admission, draft.AdmissionIdentity);
    }

    internal async Task<LocalPatchReview> ReviewAsync(string operationId)
    {
        RequireOperator(); RequireActiveBootstrap();
        var draft = ReadDraft(operationId);
        var capture = await CaptureAsync(draft);
        if (capture is null || HufuSinglePatchHost.AdmissionIdentity(Context, capture.Admission) != draft.AdmissionIdentity)
            return new(new("PreviewChangedOrUnavailable", operationId));
        var proposal = capture.Admission.Plan.Nodes[0].Proposals[0];
        // This console profile requires complete, bounded human review. Large
        // patches need another review UI; truncated output never enables approval.
        if (proposal.OriginalByteLength > 8192 || proposal.ProposedByteLength > 8192)
            return new(new("ReviewTooLarge", operationId));
        WriteControl(custody, DraftName(operationId), draft with { Reviewed = true }, true);
        var utf8 = new UTF8Encoding(false, true);
        return new(Summary("Reviewed", capture.Admission, draft.AdmissionIdentity),
            utf8.GetString(proposal.OriginalContent.ToArray()), utf8.GetString(proposal.ProposedContent.ToArray()));
    }

    internal async Task<LocalHostResult> ApplyAsync(string operationId)
    {
        RequireActiveBootstrap();
        if (mode != LocalHostMode.Worker || configuration.BoundOperationId != operationId)
            throw new UnauthorizedAccessException();
        var draft = ReadDraft(operationId);
        var prior = await Journal.InspectAsync(Actor, Context, operationId, draft.AdmissionIdentity);
        if (prior.State != PatchRecoveryState.NotFound)
            return new(prior.State == PatchRecoveryState.Unavailable ? "Unavailable" : "AlreadyRecorded", operationId);
        var capture = await CaptureAsync(draft);
        if (capture is null || HufuSinglePatchHost.AdmissionIdentity(Context, capture.Admission) != draft.AdmissionIdentity)
            return new("PreviewChangedOrUnavailable", operationId);
        var result = await new SinglePatchExecutor(new WorkspaceReference(state.WorkspaceId), Provider, Host)
            .ExecuteAsync(capture.Document, capture.Plan, operationId);
        return new(result.Succeeded ? "Succeeded" : result.Failure?.ToString() ?? "Unavailable", operationId);
    }

    internal async Task<LocalHostResult> InspectAsync(string operationId)
    {
        RequireOperator();
        var draft = ReadDraft(operationId);
        var result = await Journal.InspectAsync(Actor, Context, operationId, draft.AdmissionIdentity);
        return new(result.State.ToString(), operationId);
    }

    internal async Task<LocalHostResult> WithdrawAsync(string operationId)
    {
        RequireOperator();
        var draft = ReadDraft(operationId);
        var command = "withdraw-" + Hash(operationId);
        using var withdrawal = journalPolicy.BeginApprovalRevocation(Context, operationId, draft.AdmissionIdentity, command);
        var result = await Journal.RevokeApprovalAsync(Actor, Context, operationId, draft.AdmissionIdentity, command);
        return new(result.Status.ToString(), operationId);
    }

    internal async Task<LocalHostResult> RevokeAsync()
    {
        RequireOperator();
        var current = await Store.ReadCurrentAsync(Actor, Context);
        if (current.Status == AuthorityReadStatus.Revoked) return new("Revoked");
        if (current.Record is null) return new("Unavailable");
        var result = await Store.RevokeAsync(new("revoke-local-authority", Actor, Context, current.Record.Sequence, "operator-revoked"));
        return new(result.Status.ToString());
    }

    private async Task<Captured?> CaptureAsync(LocalPatchDraft draft)
    {
        if (draft.Offset < 0 || draft.DeleteLength < 0 || draft.Offset > MaximumFileBytes || draft.DeleteLength > MaximumFileBytes)
            throw new ArgumentException("Patch offsets must fit the bounded file profile.");
        var workspace = new WorkspaceId(state.WorkspaceId);
        var compilation = PreviewCompiler.Compile([new FilePatchStage(state.Target,
            [new TextPatch(draft.Offset, draft.DeleteLength, Decode(draft.ReplacementBase64))])], workspace);
        if (compilation.Document is not { } document) return null;
        var invocation = new EffectInvocation(Context.SubjectId, "local-patch-v1", "attempt-" + Hash(draft.OperationId));
        var preview = await new PreviewRuntime(new WorkspaceReference(state.WorkspaceId), Provider,
            Host.CreatePreviewAuthorizer(invocation, document)).WhatIfAsync(invocation, document);
        return preview.Status == PreviewRunStatus.Succeeded && preview.Plan is { } plan
            ? new(document, plan, HufuSinglePatchHost.PrepareAdmission(Context, document, plan, draft.OperationId)) : null;
    }

    private LocalPatchDraft ReadDraft(string operationId)
    {
        if (!LocalHostAuthentication.IsBoundOperation(configuration, operationId)) throw new UnauthorizedAccessException();
        var draft = ReadControl<LocalPatchDraft>(custody, DraftName(operationId));
        if (draft.OperationId != operationId || draft.AdmissionIdentity.Length != 64 ||
            draft.AdmissionIdentity.Any(c => !char.IsAsciiHexDigitLower(c))) throw new InvalidDataException("Invalid protected patch draft.");
        return draft;
    }

    private void RequireOperator()
    {
        if (mode != LocalHostMode.Operator) throw new UnauthorizedAccessException("This is an operator-only command.");
    }

    private void RequireActiveBootstrap()
    {
        if (state.PublicationApproved) throw new InvalidOperationException("Finalize the explicit operator bootstrap before using work commands.");
    }

    private static LocalHostResult Summary(string status, PatchAdmissionRequest admission, string identity)
    {
        var proposal = admission.Plan.Nodes[0].Proposals[0];
        return new(status, admission.OperationId, identity, proposal.RelativePath, proposal.OriginalVersion.Value,
            "local-read-v1:sha256:" + proposal.ProposedSha256, proposal.OriginalContent.Length, proposal.ProposedContent.Length);
    }

    internal static bool ValidTarget(string? target) => target is { Length: > 0 and <= 256 } &&
        target == target.ToLowerInvariant() && target.Split('/').Length <= 8 &&
        target.Split('/').All(part => part is not ("" or "." or "..") && !part.EndsWith('.') &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
            !new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(part.Split('.')[0]));

    private static byte[] Decode(string input)
    {
        if (input.Length > 1_398_104) throw new ArgumentException("UTF-8 payload exceeds 1 MiB.");
        var bytes = Convert.FromBase64String(input);
        if (bytes.Length > MaximumFileBytes) throw new ArgumentException("UTF-8 payload exceeds 1 MiB.");
        _ = new UTF8Encoding(false, true).GetString(bytes);
        return bytes;
    }

    private static string CurrentSid()
    {
        var identity = new WindowsLocalHostIdentitySource().Read();
        if (!identity.IsWindows || identity.IsImpersonating || identity.ProcessSid is null || identity.CurrentSid != identity.ProcessSid)
            throw new UnauthorizedAccessException("A non-impersonated Windows operator is required.");
        return identity.ProcessSid;
    }

    private static string DraftName(string operation)
    {
        if (!AuthorityValidation.ValidToken(operation)) throw new ArgumentException("Invalid operation identity.");
        return "patch-" + Hash(operation) + ".json";
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static T ReadControl<T>(LocalHostCustody custody, string name)
    {
        var path = custody.ValidateControlFile(name);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumControlBytes) throw new InvalidDataException("Control record exceeds its bound.");
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("Missing control record.");
    }

    private static void WriteControl<T>(LocalHostCustody custody, string name, T value, bool replace)
    {
        custody.Validate();
        if (Path.GetFileName(name) != name) throw new ArgumentException("Invalid control filename.");
        var destination = Path.Combine(custody.ControlRoot, name);
        if (File.Exists(destination)) custody.ValidateControlFile(name);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumControlBytes) throw new InvalidDataException("Control record exceeds its bound.");
        var temporary = Path.Combine(custody.ControlRoot, "pending-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            custody.ValidateControlFile(Path.GetFileName(temporary));
            File.Move(temporary, destination, overwrite: replace);
            custody.ValidateControlFile(name);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => lease.Dispose();
    private sealed record Captured(CompiledPreviewDocument Document, ResolvedEffectPlan Plan, PatchAdmissionRequest Admission);
    private sealed class CustodiedIdentitySource(LocalHostCustody custody) : ILocalHostIdentitySource
    {
        public LocalHostIdentityObservation Read() { custody.Validate(); return new WindowsLocalHostIdentitySource().Read(); }
    }
    private sealed class ProtectedIssuanceSource(LocalHostCustody custody) : ILocalHostIssuanceStateSource
    {
        public ValueTask<LocalHostIssuanceApproval?> ResolveCurrentAsync(AuthorityStoreActor actor,
            AuthenticatedAuthorityContext context, AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadControl<LocalHostState>(custody, "host.json");
            current.Validate(CurrentSid());
            return ValueTask.FromResult(current.PublicationApproved && current.Context == context && actor.ActorId == current.OperatorSid
                ? new LocalHostIssuanceApproval(current.Ceiling, current.Proposal.Identity,
                    current.PublicationCommandId, 0, current.PublicationApprovalExpiry) : null);
        }
    }
}
