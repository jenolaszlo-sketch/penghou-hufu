using Penghou.Hufu;
using Penghou.Hufu.Biscuit;
using System.Text;

namespace Penghou.Hufu.Biscuit.Sqlite;

/// <summary>Records Biscuit verification evidence in both Hufu's decision log and its credential registry.</summary>
public sealed class BiscuitSqliteDecisionRecorder : IBiscuitDecisionRecorder
{
    private readonly IAuthorityStore _store;
    private readonly SqliteBiscuitCredentialRegistry _registry;

    public BiscuitSqliteDecisionRecorder(IAuthorityStore store, SqliteBiscuitCredentialRegistry registry)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async ValueTask<bool> RecordAsync(BiscuitDecisionEvidence evidence, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(evidence);

            var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(BiscuitCodec.Encode(evidence));
            var record = new AuthorityDecisionRecord(evidence.Id, evidence.Request, evidence.Decision,
                evidence.EvaluatedAt, "hufu-biscuit-evidence-v1", json);
            var coreWrite = await _store.RecordDecisionAsync(evidence.Actor, record, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (coreWrite.Status is not (AuthorityEvidenceStatus.Recorded or AuthorityEvidenceStatus.Replayed))
                return false;

            var registryWrite = await _registry.RecordVerificationAsync(evidence, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return registryWrite is BiscuitRegistryStatus.Recorded or BiscuitRegistryStatus.Replayed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
