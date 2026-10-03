using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BiscuitSharp;
using static Penghou.Hufu.Biscuit.Tests.BiscuitIntegrationFixture;

namespace Penghou.Hufu.Biscuit.Tests;

/// <summary>Explicit local measurement harness; not a production host or an automatic limit selector.</summary>
public static class BiscuitBudgetProbe
{
    public static async Task RunAsync(string outputPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This host qualification targets Windows x64.");
        var observations = new List<object>();
        using var process = Process.GetCurrentProcess();
        long peak = process.WorkingSet64;
        int samples = 0;
        using var stopSampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    process.Refresh();
                    peak = Math.Max(peak, process.WorkingSet64);
                    samples++;
                    await Task.Delay(20, stopSampling.Token);
                }
            }
            catch (OperationCanceledException) when (stopSampling.IsCancellationRequested) { }
        });
        string? engine = null;
        try
        {
            foreach (var depth in new[] { 1, 8, BiscuitProfile.MaximumBlocks })
            {
                using var fixture = new BiscuitIntegrationFixture();
                await fixture.PublishAsync();
                var envelope = await fixture.IssueAsync();
                var chain = new List<BiscuitCredentialRegistration>();
                chain.Add(await Registration(fixture, envelope));
                while (chain.Count < depth)
                {
                    var grant = chain[^1].EffectiveGrant;
                    var child = await fixture.Service.AttenuateAsync(Context, envelope,
                        new(grant.Actions, grant.Scope, grant.Exclusions, grant.NotBefore, grant.ExpiresAt));
                    if (!child.IsSuccess) throw new InvalidOperationException("Probe derivation failed: " + child.FailureCode);
                    envelope = child.Envelope!;
                    chain.Add(await Registration(fixture, envelope));
                }
                var leaf = chain[^1];
                var ancestry = chain.AsEnumerable().Reverse().ToArray();
                var key = await fixture.Keys.FindVerificationKeyAsync(Realm, envelope.RootKeyId);
                var token = BiscuitToken.Parse(envelope.GetTokenBytes(), key!);
                engine = fixture.Service.EngineIdentity;
                foreach (var milliseconds in new[] { 1, 5, 25, 100 })
                {
                    var limits = new BiscuitAuthorizerLimits(2000, 50, TimeSpan.FromMilliseconds(milliseconds));
                    foreach (var workers in new[] { 1, 4 })
                    {
                        // Warm-up is outside samples. No measured failure is retried.
                        for (var warm = 0; warm < 3; warm++)
                            _ = BiscuitProfile.CreateAuthorizer(token, leaf, ancestry, fixture.Request(), Now).WithLimits(limits).Authorize();
                        var row = await Measure("fixed-native-policy", workers, 16, depth, milliseconds,
                            () => Task.FromResult(BiscuitProfile.Classify(
                                BiscuitProfile.CreateAuthorizer(token, leaf, ancestry, fixture.Request(), Now).WithLimits(limits).Authorize())));
                        observations.Add(row);
                    }
                }
                // Separate complete host preflight: native token verification, current Cedar,
                // registered ancestry/revocation reads and both required SQLite evidence writes.
                var service = fixture.CreateService(limits: new(2000, 50, TimeSpan.FromMilliseconds(100)));
                foreach (var workers in new[] { 1, 4 })
                    observations.Add(await Measure("registered-preflight-with-durable-evidence", workers, 8, depth, 100,
                        async () => (await service.VerifyAsync(envelope, fixture.Request())).FailureCode));
            }
        }
        finally
        {
            stopSampling.Cancel();
            await sampler;
        }
        var report = new
        {
            format = "hufu-biscuit-budget-probe-v1",
            capturedAt = DateTimeOffset.UtcNow,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            runtimeIdentifier = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            logicalProcessorCount = Environment.ProcessorCount,
            profile = BiscuitProfile.Identity,
            mapping = BiscuitProfile.MappingIdentity,
            engine,
            package = "BiscuitSharp/0.1.0-preview.2",
            packageSha256 = "c5f0c94aa14cf82b68239ed49314a3cf468780337432cdff89975beed6ead3b1",
            sampledPeakWorkingSetBytes = peak,
            sampleCount = samples,
            sampleIntervalMilliseconds = 20,
            memoryScope = "Whole probe process including managed runtime, Cedar, SQLite, tests and native bridge. Sampled observation, not a hard memory cap.",
            fixtureScope = "Synthetic local host and fixed clock; protected authentication/custody, real tenant traffic and production concurrency are not qualified.",
            observations
        };
        var absolute = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Biscuit budget report: " + absolute);
    }

    private static async Task<BiscuitCredentialRegistration> Registration(BiscuitIntegrationFixture fixture, BiscuitEnvelope envelope)
    {
        var record = await fixture.Registry.FindAsync(Actor, Realm, Context, Fingerprint(envelope));
        return record.Registration ?? throw new InvalidOperationException("Probe registration missing.");
    }

    private static async Task<object> Measure(string workload, int workers, int perWorker, int blocks, int milliseconds,
        Func<Task<BiscuitFailureCode>> evaluate)
    {
        var durations = new ConcurrentBag<double>();
        var outcomes = new ConcurrentDictionary<string, int>();
        var whole = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var outcome = await evaluate();
                durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                outcomes.AddOrUpdate(outcome.ToString(), 1, static (_, count) => count + 1);
            }
        })));
        whole.Stop();
        var ordered = durations.Order().ToArray();
        return new
        {
            workload, blocks, workers, evaluations = ordered.Length,
            maxFacts = 2000, maxIterations = 50, maxTimeMilliseconds = milliseconds,
            p50Milliseconds = Percentile(ordered, .50),
            p95Milliseconds = Percentile(ordered, .95),
            maximumMilliseconds = ordered[^1],
            totalElapsedMilliseconds = whole.Elapsed.TotalMilliseconds,
            outcomes = outcomes.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }
    private static double Percentile(double[] values, double fraction) => values[(int)Math.Ceiling(fraction * values.Length) - 1];
}
