# Hufu.PlanHost

A trusted host that starts an **admitted** Fuwen plan as a durable Zhinu run.
It is the runnable form of the consumer #9 start operation: the missing
operator step between "inspect a plan" and "operate the run".

The host owns a trusted catalogue and capability policy. A submitted plan
contributes only intent and descriptor references; it never supplies
executable definitions or authority. The host verifies the plan's canonical
bytes, re-admits it against its own catalogue and policy, and starts a run —
refusing fail-closed on any identity mismatch or unknown descriptor.

```powershell
dotnet run --project samples/Hufu.PlanHost/Hufu.PlanHost.csproj -c Release -- catalogue
dotnet run --project samples/Hufu.PlanHost/Hufu.PlanHost.csproj -c Release -- start
```

`start` prints one JSON record: the durable `RunId`, the plan's execution
fingerprint and revision, and the admission/catalogue/policy/grant identities
that bind the run. It is **start-only** — it creates a `Pending` run and never
executes; a worker owns provider composition. The run is then inspectable with
the existing operator surfaces:

```powershell
zhinu --db <workspace>/plan-host.db runs show <run-id>
zhinu --db <workspace>/plan-host.db runs wait <run-id>
zhinu --db <workspace>/plan-host.db runs evidence <run-id>
```

## Trust boundary

```text
plan                 declares intent and descriptor references
admission            proves that exact plan was accepted against trusted revisions
this host's catalogue supplies the actual trusted activity definitions
Zhinu                receives the resulting executable workflow definition
```

A plan whose descriptors are not in the host catalogue is refused before any
run is created, and the plan can never widen the host's policy. This sample is
not packed or published.
