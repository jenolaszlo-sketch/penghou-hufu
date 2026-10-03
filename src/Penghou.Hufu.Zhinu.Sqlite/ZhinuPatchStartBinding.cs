using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Zhinu;

namespace Penghou.Hufu.Zhinu.Sqlite;

/// <summary>Closed exact runtime facts for one locked-file patch start.</summary>
public sealed record ZhinuPatchStartBinding(
    Guid ExternalOperationId,
    Guid WorkflowRunId,
    Guid WorkflowGenerationId,
    long RunLeaseGeneration,
    string RunOwnerId,
    string WorkflowDefinitionFingerprint,
    string ExecutionFingerprint,
    string RunLeaseExpiresAt,
    Guid StepId,
    string StepKey,
    int StepRevision,
    int StepAttempt,
    long StepLeaseGeneration,
    string StepOwnerId,
    string StepLeaseExpiresAt,
    string Provider,
    string? ExternalId,
    string IdempotencyKey,
    string PlanRevisionId,
    string SubjectId,
    string EffectId,
    string InvocationAttemptId,
    string DocumentIdentity,
    string NodeIdentity,
    string PlanIdentity,
    string WorkspaceId,
    string RelativePath,
    string RequestIdentity,
    string WriterProfile,
    string NamespaceProfile,
    string ObjectIdentity,
    string OriginalVersion,
    string ProposedVersion,
    int OriginalByteLength,
    int ProposedByteLength);

/// <summary>Canonical JSON codec. Unknown fields, duplicates, alternate spellings and noncanonical JSON are rejected.</summary>
public static class ZhinuPatchStartBindingCodec
{
    public const string ProfileIdentity = "Penghou.Hufu.Zhinu.Sqlite.PatchStart.v1";
    public const string WriterProfile = "local-windows-ntfs-controlled-patch-v1";
    public const string NamespaceProfile = "HostControlled";
    public const int MaximumUtf8Bytes = 24 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Encode(ZhinuPatchStartBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!Valid(binding)) throw new ArgumentException("The patch start binding is invalid.", nameof(binding));
        var bytes = Write(binding);
        if (bytes.Length > MaximumUtf8Bytes) throw new ArgumentException("The binding exceeds its encoded size bound.", nameof(binding));
        return StrictUtf8.GetString(bytes);
    }

    public static string Identity(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        var bytes = StrictUtf8.GetBytes(canonicalJson);
        if (bytes.Length > MaximumUtf8Bytes) throw new ArgumentException("The binding exceeds its encoded size bound.", nameof(canonicalJson));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static bool TryDecode(AuthorityOperationStartCommand command, out ZhinuPatchStartBinding binding)
    {
        binding = null!;
        if (command is null || command.BindingJson is null || command.BindingJson.Length > MaximumUtf8Bytes) return false;
        try
        {
            var input = StrictUtf8.GetBytes(command.BindingJson);
            if (input.Length > MaximumUtf8Bytes || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != command.BindingIdentity)
                return false;
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(input)) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) names.Add(property.Name);
            if (!names.SetEquals(PropertyNames)) return false;
            binding = new ZhinuPatchStartBinding(
                GuidValue(root, "externalOperationId"), GuidValue(root, "workflowRunId"), GuidValue(root, "workflowGenerationId"),
                LongValue(root, "runLeaseGeneration"), StringValue(root, "runOwnerId"),
                StringValue(root, "workflowDefinitionFingerprint"), StringValue(root, "executionFingerprint"), StringValue(root, "runLeaseExpiresAt"),
                GuidValue(root, "stepId"), StringValue(root, "stepKey"), IntValue(root, "stepRevision"),
                IntValue(root, "stepAttempt"), LongValue(root, "stepLeaseGeneration"), StringValue(root, "stepOwnerId"),
                StringValue(root, "stepLeaseExpiresAt"), StringValue(root, "provider"), NullableStringValue(root, "externalId"),
                StringValue(root, "idempotencyKey"), StringValue(root, "planRevisionId"), StringValue(root, "subjectId"),
                StringValue(root, "effectId"), StringValue(root, "invocationAttemptId"), StringValue(root, "documentIdentity"),
                StringValue(root, "nodeIdentity"), StringValue(root, "planIdentity"), StringValue(root, "workspaceId"),
                StringValue(root, "relativePath"), StringValue(root, "requestIdentity"), StringValue(root, "writerProfile"),
                StringValue(root, "namespaceProfile"), StringValue(root, "objectIdentity"), StringValue(root, "originalVersion"),
                StringValue(root, "proposedVersion"), IntValue(root, "originalByteLength"), IntValue(root, "proposedByteLength"));
            if (!Valid(binding) || !Write(binding).AsSpan().SequenceEqual(input)) { binding = null!; return false; }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or EncoderFallbackException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            binding = null!;
            return false;
        }
    }

    internal static bool MatchesCommand(ZhinuPatchStartBinding b, AuthorityOperationStartCommand command)
    {
        var context = command.Request.Context;
        return command.OperationId == b.ExternalOperationId.ToString("D") &&
            command.Request.Action == AuthorityAction.PatchFile &&
            context.RunId == b.WorkflowRunId.ToString("D") && context.RevisionId == b.PlanRevisionId &&
            context.FenceId == b.RunLeaseGeneration.ToString(CultureInfo.InvariantCulture) &&
            context.SubjectId == b.SubjectId && command.Request.WorkspaceId == b.WorkspaceId &&
            command.Request.RelativePath == b.RelativePath && command.Request.RequestIdentity == b.RequestIdentity;
    }

    private static readonly string[] PropertyNames =
    [
        "schema", "externalOperationId", "workflowRunId", "workflowGenerationId", "runLeaseGeneration", "runOwnerId", "workflowDefinitionFingerprint", "executionFingerprint",
        "runLeaseExpiresAt", "stepId", "stepKey", "stepRevision", "stepAttempt", "stepLeaseGeneration", "stepOwnerId",
        "stepLeaseExpiresAt", "provider", "externalId", "idempotencyKey", "planRevisionId", "subjectId", "effectId",
        "invocationAttemptId", "documentIdentity", "nodeIdentity", "planIdentity", "workspaceId", "relativePath",
        "requestIdentity", "writerProfile", "namespaceProfile", "objectIdentity", "originalVersion", "proposedVersion",
        "originalByteLength", "proposedByteLength"
    ];

    private static byte[] Write(ZhinuPatchStartBinding b)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "penghou.zhinu.patch-start-binding.v1");
            writer.WriteString("externalOperationId", b.ExternalOperationId.ToString("D"));
            writer.WriteString("workflowRunId", b.WorkflowRunId.ToString("D"));
            writer.WriteString("workflowGenerationId", b.WorkflowGenerationId.ToString("D"));
            writer.WriteNumber("runLeaseGeneration", b.RunLeaseGeneration);
            writer.WriteString("runOwnerId", b.RunOwnerId);
            writer.WriteString("workflowDefinitionFingerprint", b.WorkflowDefinitionFingerprint);
            writer.WriteString("executionFingerprint", b.ExecutionFingerprint);
            writer.WriteString("runLeaseExpiresAt", b.RunLeaseExpiresAt);
            writer.WriteString("stepId", b.StepId.ToString("D"));
            writer.WriteString("stepKey", b.StepKey);
            writer.WriteNumber("stepRevision", b.StepRevision);
            writer.WriteNumber("stepAttempt", b.StepAttempt);
            writer.WriteNumber("stepLeaseGeneration", b.StepLeaseGeneration);
            writer.WriteString("stepOwnerId", b.StepOwnerId);
            writer.WriteString("stepLeaseExpiresAt", b.StepLeaseExpiresAt);
            writer.WriteString("provider", b.Provider);
            if (b.ExternalId is null) writer.WriteNull("externalId"); else writer.WriteString("externalId", b.ExternalId);
            writer.WriteString("idempotencyKey", b.IdempotencyKey);
            writer.WriteString("planRevisionId", b.PlanRevisionId);
            writer.WriteString("subjectId", b.SubjectId);
            writer.WriteString("effectId", b.EffectId);
            writer.WriteString("invocationAttemptId", b.InvocationAttemptId);
            writer.WriteString("documentIdentity", b.DocumentIdentity);
            writer.WriteString("nodeIdentity", b.NodeIdentity);
            writer.WriteString("planIdentity", b.PlanIdentity);
            writer.WriteString("workspaceId", b.WorkspaceId);
            writer.WriteString("relativePath", b.RelativePath);
            writer.WriteString("requestIdentity", b.RequestIdentity);
            writer.WriteString("writerProfile", b.WriterProfile);
            writer.WriteString("namespaceProfile", b.NamespaceProfile);
            writer.WriteString("objectIdentity", b.ObjectIdentity);
            writer.WriteString("originalVersion", b.OriginalVersion);
            writer.WriteString("proposedVersion", b.ProposedVersion);
            writer.WriteNumber("originalByteLength", b.OriginalByteLength);
            writer.WriteNumber("proposedByteLength", b.ProposedByteLength);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool Valid(ZhinuPatchStartBinding? b)
    {
        if (b is null || b.ExternalOperationId == Guid.Empty || b.WorkflowRunId == Guid.Empty || b.WorkflowGenerationId == Guid.Empty || b.StepId == Guid.Empty ||
            b.RunLeaseGeneration < 1 || b.StepLeaseGeneration != b.RunLeaseGeneration || b.StepRevision < 1 || b.StepAttempt < 1 ||
            b.OriginalByteLength < 0 || b.ProposedByteLength < 0 || b.WriterProfile != WriterProfile || b.NamespaceProfile != NamespaceProfile ||
            !Token(b.RunOwnerId) || !Token(b.WorkflowDefinitionFingerprint) || !Token(b.ExecutionFingerprint) || !Token(b.StepOwnerId) || !Token(b.StepKey) ||
            b.Provider != WriterProfile || !Token(b.IdempotencyKey) || !Token(b.PlanRevisionId) || !Token(b.SubjectId) || !Token(b.EffectId) ||
            !Token(b.InvocationAttemptId) || !Digest(b.DocumentIdentity) || !Digest(b.NodeIdentity) || !Digest(b.PlanIdentity) ||
            !Token(b.WorkspaceId) || !CanonicalPath(b.RelativePath) || !Digest(b.RequestIdentity) || !Token(b.ObjectIdentity) ||
            !Token(b.OriginalVersion) || !Token(b.ProposedVersion) || b.ExternalId is not null && !Token(b.ExternalId)) return false;
        return Timestamp(b.RunLeaseExpiresAt, out _) && Timestamp(b.StepLeaseExpiresAt, out _);
    }

    private static bool Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; } catch (EncoderFallbackException) { return false; }
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CanonicalPath(string? value)
    {
        if (value is null || value.Length is < 1 or > 512) return false;
        try { return AuthorityValidation.NormalizePath(value) == value; } catch { return false; }
    }

    private static bool Timestamp(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant) &&
        instant.Offset == TimeSpan.Zero && instant.ToString("O", CultureInfo.InvariantCulture) == value;

    private static bool HasDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 2, CommentHandling = JsonCommentHandling.Disallow });
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && !names.Add(reader.GetString()!)) return true;
        }
        return false;
    }

    private static string StringValue(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw new JsonException();
    private static string? NullableStringValue(JsonElement root, string name) => root.GetProperty(name).ValueKind switch
    { JsonValueKind.Null => null, JsonValueKind.String => root.GetProperty(name).GetString(), _ => throw new JsonException() };
    private static Guid GuidValue(JsonElement root, string name) => Guid.TryParseExact(StringValue(root, name), "D", out var value) &&
        value.ToString("D") == StringValue(root, name) ? value : throw new JsonException();
    private static long LongValue(JsonElement root, string name) => root.GetProperty(name).TryGetInt64(out var value) ? value : throw new JsonException();
    private static int IntValue(JsonElement root, string name) => root.GetProperty(name).TryGetInt32(out var value) ? value : throw new JsonException();
}

/// <summary>Bounded SQLite participant that claims one exact Zhinu external handle in Hufu's transaction.</summary>
public sealed class ZhinuSqlitePatchStartParticipant(ISqliteAuthorityDatabase database) : IAuthoritySqliteStartParticipant
{
    private readonly ISqliteAuthorityDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    public string ProfileIdentity => ZhinuPatchStartBindingCodec.ProfileIdentity;

    public async ValueTask<SqliteRuntimeStartResult> TryStartAsync(SqliteConnection connection, SqliteTransaction transaction,
        AuthorityOperationStartCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _database.TimeProvider.GetUtcNow();
        if (connection is null || transaction is null || command is null || transaction.Connection != connection ||
            !ZhinuPatchStartBindingCodec.TryDecode(command, out var binding) ||
            !ZhinuPatchStartBindingCodec.MatchesCommand(binding, command)) return new(AuthorityStatus.Deny, now);
        if (!TryTimestamp(binding.RunLeaseExpiresAt, out var runLeaseUntil) ||
            !TryTimestamp(binding.StepLeaseExpiresAt, out var stepLeaseUntil) || runLeaseUntil <= now || stepLeaseUntil <= now)
            return new(AuthorityStatus.Deny, now);

        if (!await RunIsCurrentAsync(connection, transaction, binding, now, cancellationToken).ConfigureAwait(false) ||
            !await GenerationIsCurrentAsync(connection, transaction, binding, cancellationToken).ConfigureAwait(false) ||
            !await StepIsCurrentAsync(connection, transaction, binding, now, cancellationToken).ConfigureAwait(false) ||
            !await HandleIsCurrentAsync(connection, transaction, binding, cancellationToken).ConfigureAwait(false))
            return new(AuthorityStatus.Deny, now);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE main.workflow_external_operations
            SET status=$running, owner=$owner, updated_at=$now
            WHERE operation_id=$operation AND workflow_run_id=$run AND step_id=$step
              AND step_key=$stepKey AND step_revision=$stepRevision AND attempt=$attempt
              AND idempotency_key=$idempotencyKey AND provider=$provider
              AND lease_generation=$generation AND status=$requested AND recovery_intent=$abandon
              AND owner IS NULL;
            """;
        Add(update, "$running", (int)ExternalOperationStatus.Running);
        Add(update, "$owner", binding.StepOwnerId);
        Add(update, "$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(update, "$operation", binding.ExternalOperationId.ToString("D"));
        Add(update, "$run", binding.WorkflowRunId.ToString("D"));
        Add(update, "$step", binding.StepId.ToString("D"));
        Add(update, "$stepKey", binding.StepKey);
        Add(update, "$stepRevision", binding.StepRevision);
        Add(update, "$attempt", binding.StepAttempt);
        Add(update, "$idempotencyKey", binding.IdempotencyKey);
        Add(update, "$provider", binding.Provider);
        Add(update, "$generation", binding.RunLeaseGeneration);
        Add(update, "$requested", (int)ExternalOperationStatus.Requested);
        Add(update, "$abandon", ExternalOperationRecoveryIntent.Abandon.ToString());
        cancellationToken.ThrowIfCancellationRequested();
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return new(AuthorityStatus.Deny, now);
        cancellationToken.ThrowIfCancellationRequested();
        var runDeadline = await RunDeadlineAsync(connection, transaction, binding.WorkflowRunId, now, cancellationToken).ConfigureAwait(false);
        var validUntil = new[] { runLeaseUntil, stepLeaseUntil, runDeadline }.Where(x => x.HasValue).Select(x => x!.Value).Min();
        return new(AuthorityStatus.Permit, validUntil);
    }

    private static async Task<DateTimeOffset?> RunDeadlineAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT deadline FROM main.workflow_runs WHERE id=$run";
        Add(command, "$run", runId.ToString("D"));
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null || value is DBNull) return null;
        if (value is not string timestamp || !TryParseTimestamp(timestamp, out var deadline) || deadline <= now)
            return now;
        return deadline;
    }

    private static async Task<bool> RunIsCurrentAsync(SqliteConnection connection, SqliteTransaction transaction,
        ZhinuPatchStartBinding binding, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status,lease_owner,lease_expires_at,lease_generation,definition_fingerprint,deadline FROM main.workflow_runs WHERE id=$run";
        Add(command, "$run", binding.WorkflowRunId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt32(0) != (int)WorkflowStatus.Running ||
            reader.IsDBNull(1) || reader.GetString(1) != binding.RunOwnerId || reader.IsDBNull(2) ||
            reader.GetInt64(3) != binding.RunLeaseGeneration || reader.IsDBNull(4) ||
            reader.GetString(4) != binding.WorkflowDefinitionFingerprint ||
            !SameTimestamp(reader.GetString(2), binding.RunLeaseExpiresAt, now)) return false;
        if (!reader.IsDBNull(5) && (!TryParseTimestamp(reader.GetString(5), out var deadline) || deadline <= now)) return false;
        return true;
    }

    private static async Task<bool> StepIsCurrentAsync(SqliteConnection connection, SqliteTransaction transaction,
        ZhinuPatchStartBinding binding, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT workflow_run_id,step_key,status,attempt,lease_owner,lease_expires_at,revision,lease_generation
            FROM main.workflow_steps WHERE id=$step AND revision=(
                SELECT MAX(revision) FROM main.workflow_steps WHERE workflow_run_id=$run AND step_key=$stepKey);
            """;
        Add(command, "$step", binding.StepId.ToString("D"));
        Add(command, "$run", binding.WorkflowRunId.ToString("D"));
        Add(command, "$stepKey", binding.StepKey);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) &&
            reader.GetString(0) == binding.WorkflowRunId.ToString("D") && reader.GetString(1) == binding.StepKey &&
            reader.GetInt32(2) == (int)StepStatus.Running && reader.GetInt32(3) == binding.StepAttempt &&
            !reader.IsDBNull(4) && reader.GetString(4) == binding.StepOwnerId && !reader.IsDBNull(5) &&
            reader.GetInt32(6) == binding.StepRevision && reader.GetInt64(7) == binding.StepLeaseGeneration &&
            SameTimestamp(reader.GetString(5), binding.StepLeaseExpiresAt, now);
    }

    private static async Task<bool> GenerationIsCurrentAsync(SqliteConnection connection, SqliteTransaction transaction,
        ZhinuPatchStartBinding binding, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT workflow_run_id,plan_revision,execution_fingerprint,status,
                   (SELECT COUNT(*) FROM main.workflow_generations AS owner
                    WHERE owner.workflow_run_id=$run AND owner.status IN ($active,$quiescing))
            FROM main.workflow_generations WHERE generation_id=$generation;
            """;
        Add(command, "$generation", binding.WorkflowGenerationId.ToString("D"));
        Add(command, "$run", binding.WorkflowRunId.ToString("D"));
        Add(command, "$active", (int)WorkflowGenerationStatus.Active);
        Add(command, "$quiescing", (int)WorkflowGenerationStatus.Quiescing);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) &&
            reader.GetString(0) == binding.WorkflowRunId.ToString("D") && !reader.IsDBNull(1) &&
            reader.GetString(1) == binding.PlanRevisionId && !reader.IsDBNull(2) &&
            reader.GetString(2) == binding.ExecutionFingerprint && reader.GetInt32(3) == (int)WorkflowGenerationStatus.Active &&
            reader.GetInt64(4) == 1;
    }

    private static async Task<bool> HandleIsCurrentAsync(SqliteConnection connection, SqliteTransaction transaction,
        ZhinuPatchStartBinding binding, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT workflow_run_id,step_id,step_key,step_revision,attempt,idempotency_key,provider,external_id,
                   lease_generation,status,recovery_intent,owner
            FROM main.workflow_external_operations WHERE operation_id=$operation;
            """;
        Add(command, "$operation", binding.ExternalOperationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) &&
            reader.GetString(0) == binding.WorkflowRunId.ToString("D") && !reader.IsDBNull(1) && reader.GetString(1) == binding.StepId.ToString("D") &&
            !reader.IsDBNull(2) && reader.GetString(2) == binding.StepKey && !reader.IsDBNull(3) && reader.GetInt32(3) == binding.StepRevision &&
            !reader.IsDBNull(4) && reader.GetInt32(4) == binding.StepAttempt && !reader.IsDBNull(5) && reader.GetString(5) == binding.IdempotencyKey &&
            reader.GetString(6) == binding.Provider && (reader.IsDBNull(7) ? binding.ExternalId is null : reader.GetString(7) == binding.ExternalId) &&
            reader.GetInt64(8) == binding.RunLeaseGeneration && reader.GetInt32(9) == (int)ExternalOperationStatus.Requested &&
            reader.GetString(10) == ExternalOperationRecoveryIntent.Abandon.ToString() && reader.IsDBNull(11);
    }

    private static bool SameTimestamp(string persisted, string expected, DateTimeOffset now) =>
        TryParseTimestamp(persisted, out var instant) && instant > now && persisted == expected;
    private static bool TryTimestamp(string text, out DateTimeOffset instant) => TryParseTimestamp(text, out instant) && instant.Offset == TimeSpan.Zero;
    private static bool TryParseTimestamp(string text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant) &&
        instant.ToString("O", CultureInfo.InvariantCulture) == text;
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
}
