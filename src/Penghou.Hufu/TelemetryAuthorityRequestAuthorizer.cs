using System.Diagnostics;

namespace Penghou.Hufu;

/// <summary>Observes completed preflight calls without changing authorization, evidence or cancellation.</summary>
/// <remarks>
/// Wrap the shared admitted authorizer to measure caller-visible latency, including queue waits.
/// Active cancellation is an observation of the caller's wait, not proof that native work stopped.
/// Only closed action/outcome categories and capped elapsed timing enter optional telemetry.
/// </remarks>
public sealed class TelemetryAuthorityRequestAuthorizer : IAuthorityRequestAuthorizer
{
    private readonly IAuthorityRequestAuthorizer inner;
    private readonly AuthorityTelemetry telemetry;

    public TelemetryAuthorityRequestAuthorizer(IAuthorityRequestAuthorizer inner, AuthorityTelemetry telemetry)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        var outcome = AuthorityTelemetry.Outcome.Faulted;
        try
        {
            var result = await inner.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
            try { outcome = Classify(request, result); }
            catch { outcome = AuthorityTelemetry.Outcome.Invalid; }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = AuthorityTelemetry.Outcome.Cancelled;
            throw;
        }
        finally
        {
            // Observation failure must never replace the result or exception from the owned authorizer.
            try
            {
                telemetry.Record(request?.Action, outcome, startedAt,
                    Stopwatch.GetElapsedTime(timestamp).TotalSeconds);
            }
            catch { }
        }
    }

    private static AuthorityTelemetry.Outcome Classify(AuthorityRequest request, AuthorityRequestAuthorization? result)
    {
        if (result is null || result.Request != request || !Enum.IsDefined(result.Status) ||
            (result.Decision is not null && !Enum.IsDefined(result.Decision.Status)) ||
            (result.Status == AuthorityStatus.Deny && result.Decision?.Status == AuthorityStatus.Permit))
            return AuthorityTelemetry.Outcome.Invalid;
        return result.Status switch
        {
            AuthorityStatus.Permit when result.IsAuthorized => AuthorityTelemetry.Outcome.Permit,
            AuthorityStatus.Deny => AuthorityTelemetry.Outcome.Deny,
            AuthorityStatus.Unavailable => AuthorityTelemetry.Outcome.Unavailable,
            _ => AuthorityTelemetry.Outcome.Invalid
        };
    }
}
