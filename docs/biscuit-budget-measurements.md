# Registered Biscuit policy and host-budget measurements

Measured 2026-10-03 on Windows x64, .NET 8.0.31 and .NET 10.0.12.
The executable probe reuses the production fixed-authorizer construction; it
does not copy the policy or infer runtime failures from exception messages.

## Workload and results

Each framework executes 960 fixed-policy evaluations: registered 1/8/32-block
credentials, 1/4 workers, time limits 1/5/25/100 ms, 2000 facts and 50 iterations.
Three warm-ups per native scenario are excluded. Another 120 complete preflights
per framework include token verification, current real Cedar, registry/lineage/
revocation reads and both required disk SQLite evidence writes.

| Framework | Maximum fixed-policy wall time | Maximum full-preflight wall time | Full-preflight outcomes | Sampled process peak |
| --- | ---: | ---: | --- | ---: |
| net8.0 | 4.526 ms | 1356.390 ms | 119 permits; 1 AuthorizationUnavailable | 88,481,792 bytes |
| net10.0 | 4.653 ms | 1210.957 ms | 120 permits | 98,213,888 bytes |

All measured native evaluations authorized, including the maximum supported
32-block chain at four workers. The wall time includes constructing trusted facts,
bridge work and native evaluation; the native MaxTime budget is not a deadline
for that entire interval. Full preflight includes additional Cedar and database
work. No failure was retried and no authorization increased its budget.

The net8.0 unavailable result occurred at 32 blocks/four workers with the
fixture's one-second SQLite busy timeout. Earlier probing incorrectly classified
an unavailable ancestor lookup as InvalidCredential; typed chain failure
propagation was corrected and covered by deterministic regressions before the
final measurements above. The original observations remain under ignored
artifacts for local investigation.

Memory is sampled every 20 ms for the entire process: managed runtime, Cedar,
SQLite, test-host dependencies and Biscuit native library. It is an observed
sampled peak, not a per-request measurement or a hard memory cap. Shorter peaks
may be missed. Eight full evaluations per sequential scenario and 32 per
concurrent scenario are local evidence, not a production latency SLA.

## Local host decision and remaining capacity gate

Retain the explicit local read-test limits of 2000 facts, 50 iterations and
100 ms. These passed the measured supported chain depths on both frameworks.
Keep complete preflights sequential per shared database in this locally
qualified consumer profile. The existing Luban read runtime performs its
checks sequentially; it does not promise database-wide admission across
independently constructed hosts.

Four-worker native policy execution is measured, but four-worker complete-host
availability is not qualified. A production host must measure its actual
database owner, busy timeout, workload and concurrency admission. Do not add
automatic larger-budget retries, a permissive cache or an unbounded global queue
to hide contention. A native evaluation budget does not cancel synchronous
native work or cover authentication/evidence-store latency. An independently
chosen cancellable host deadline remains necessary.

This is a synthetic registered local host with fixed test identity/time,
not production authentication, maximum-length identity-field qualification,
key custody, tenant load, backup protection, AOT or cross-platform evidence.

## Reproduction and exact artifacts

~~~powershell
./eng/Measure-BiscuitBudget.ps1 -Framework net8.0
./eng/Measure-BiscuitBudget.ps1 -Framework net10.0
~~~

Restore the reviewed Biscuit and corrected IO candidate packages first.
The [source inventory](biscuit-integration-source-manifest.json) identifies
the current source and package archives. IO and Luban are now published and adopted; see
[the public-feed qualification](qualification/public-resource-packages.json).
The historical probe artifact inventory remains unchanged. The probe is an explicit non-packable
tool referencing the test host; ordinary tests do not run the measurement loop.

Reports retain configuration, mapping/engine identities, outcome counts and
latency samples summarized per scenario:

- [net8.0 report](qualification/biscuit-budget-net8.0.json)
- [net10.0 report](qualification/biscuit-budget-net10.0.json)
