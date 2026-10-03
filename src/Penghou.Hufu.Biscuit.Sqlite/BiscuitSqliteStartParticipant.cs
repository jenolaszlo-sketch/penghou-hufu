using Penghou.Hufu;
using Microsoft.Data.Sqlite;
using Penghou.Hufu.Biscuit;
using Penghou.Hufu.Sqlite;

namespace Penghou.Hufu.Biscuit.Sqlite;

/// <summary>Composes current Biscuit credential validation with a trusted SQLite runtime start.</summary>
public sealed class BiscuitSqliteStartParticipant : IAuthoritySqliteStartParticipant
{
    private readonly SqliteBiscuitCredentialRegistry _registry;
    private readonly IAuthoritySqliteStartParticipant _inner;
    private readonly string _engineIdentity;
    private readonly string _mappingIdentity;
    private readonly string _evaluatorIdentity;

    public BiscuitSqliteStartParticipant(SqliteBiscuitCredentialRegistry registry,
        IAuthoritySqliteStartParticipant inner, string engineIdentity, string mappingIdentity, string evaluatorIdentity)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ValidateIdentity(inner.ProfileIdentity, nameof(inner));
        ValidateIdentity(engineIdentity, nameof(engineIdentity));
        ValidateIdentity(mappingIdentity, nameof(mappingIdentity));
        ValidateIdentity(evaluatorIdentity, nameof(evaluatorIdentity));
        _engineIdentity = engineIdentity;
        _mappingIdentity = mappingIdentity;
        _evaluatorIdentity = evaluatorIdentity;
        ProfileIdentity = "hufu-biscuit-sqlite-start-v1:" + BiscuitProfile.Hash(BiscuitProfile.Utf8.GetBytes(
            string.Join("\n", "Penghou.Hufu.Biscuit.Sqlite.StartParticipant.v1", inner.ProfileIdentity,
                engineIdentity, mappingIdentity, evaluatorIdentity)));
    }

    public string ProfileIdentity { get; }

    public async ValueTask<SqliteRuntimeStartResult> TryStartAsync(SqliteConnection connection,
        SqliteTransaction transaction, AuthorityOperationStartCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var biscuit = await _registry.CheckStartAsync(connection, transaction, command,
                _engineIdentity, _mappingIdentity, _evaluatorIdentity, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (biscuit is null || !Enum.IsDefined(biscuit.Status))
                return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue);
            if (biscuit.Status != AuthorityStatus.Permit)
                return biscuit;
            if (biscuit.ValidUntil.Offset != TimeSpan.Zero)
                return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue);

            var runtime = await _inner.TryStartAsync(connection, transaction, command, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (runtime is null || !Enum.IsDefined(runtime.Status))
                return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue);
            if (runtime.Status != AuthorityStatus.Permit)
                return runtime;
            if (runtime.ValidUntil.Offset != TimeSpan.Zero)
                return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue);

            return new(AuthorityStatus.Permit,
                biscuit.ValidUntil <= runtime.ValidUntil ? biscuit.ValidUntil : runtime.ValidUntil);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(AuthorityStatus.Unavailable, DateTimeOffset.MinValue);
        }
    }

    private static void ValidateIdentity(string? value, string parameterName)
    {
        if (!AuthorityValidation.ValidToken(value))
            throw new ArgumentException("A bounded trusted identity is required.", parameterName);
    }
}
