# Hufu.SandboxRunner

A cross-platform governed-execution sample: the first runnable consumer of the
frozen agent-to-effect chain (`Fuwen → Zhinu → Hufu → Gagamba`).

It admits one fixed Fuwen plan — a single read-only activity carrying the
neutral execution intent `diagnostic.whoami` (requires
`execution.unit-termination` at Partial, the level all three native providers
satisfy) — then drives it with a real Zhinu engine over SQLite, resolves the
intent through `Penghou.Hufu.Fuwen` to one host-registered trusted invocation
(`whoami`, pinned executable/arguments/working directory/ceiling), authorizes
each attempt against a pinned workspace/executable pair, and launches it in a
real Gagamba execution domain via `ExecutionRuntime.Create()`.

```powershell
dotnet run --project samples/Hufu.SandboxRunner/Hufu.SandboxRunner.csproj -c Release -- run
```

`run` prints one JSON audit record: status, exit code, invocation, run id,
authority request id, profile id/revision, and platform. Exit `0` on success,
`2` otherwise. Pass a `WORKSPACE` path to keep the SQLite database for
inspection; otherwise a temporary workspace is used and removed.

What this proves: an admitted plan executes end to end through durable
orchestration, per-attempt authority, and a negotiated native domain, with the
plan contributing only intent. What it does not claim: filesystem brokering,
network controls, quotas, richer isolation, or a production host, approval
surface, or evidence store. The child performs no I/O beyond its own stdout;
`whoami` is a containment diagnostic, not a workload.

This sample is not packed or published and takes no dependency on
Penghou.Luban. See [the FZ-1 composition](../../docs/sandbox-execution-fuwen.md)
for the architecture and its frozen boundaries.
