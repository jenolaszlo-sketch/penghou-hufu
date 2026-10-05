# Authority-mediated execution (HG-1)

Status: implementation slice, 2026-10-05. HG-1 is frozen (tag `arch-hg-1`);
`Penghou.Hufu.Sandbox` joins Hufu authority to an external execution provider
(Gagamba). Not qualified, not published, and not a security boundary. This
names the seam and its limits; the authority profile and validation baseline
remain separate review gates.

## What was added to Hufu core

- `AuthorityAction.ExecuteProcess` (appended; existing numeric values unchanged).
  It authorizes starting a process whose executable is the request's scoped
  resource (workspace + canonical relative path). The action alone contains
  nothing; the host selects and records an external execution provider.
- The `process.execute` capability mapping in `Penghou.Hufu.Workflow`
  (`WorkflowAuthorityRequirements`), so a neutral workflow declaration can
  request it through the existing authorizer.
- Cedar schema/action mapping, Biscuit capability, telemetry label and the
  per-grant action bound (which now tracks the enum) were updated together.
- `AuthorityAction` remains a closed vocabulary; there is still no arbitrary
  shell escape hatch. `ExecuteProcess` is the reserved external-provider seam
  named by [the behavioral specification](workflow-authority-spec.md).

## Dependency direction

```text
Hufu authority (IAuthorityRequestAuthorizer)
        |
        v
Penghou.Hufu.Sandbox  (this adapter: profile + host)
        |
        v
Gagamba.Execution  (IExecutionProvider / ExecutionRuntime)
        |
        v
native provider (Job Objects / cgroup v2 / launchd)
```

Hufu core does not reference Gagamba. Gagamba does not reference Hufu. Only the
adapter references both.

## The trusted execution profile

An activity may request an execution only through a host-registered
`SandboxApprovedInvocation`, which fixes the executable, arguments, working
directory and permitted environment names, and caps the guarantees that may be
requested (`GuaranteeCeiling`). The profile is the host's "approved tools"
registry; registration is not permission. A request may select an invocation and
supply values only for allowed environment names; it can never widen the shape.

The profile also declares platform-owned environment entries (for example
`SystemRoot`/`SystemDrive` on Windows) that are always present. Caller ambient
environment is never inherited: the child environment is exactly the
platform-owned entries plus the authorized caller entries.

### Profile identity and revision

`SandboxExecutionProfile` carries a stable `ProfileId` and `ProfileRevision`.
The host captures the invocation and revision once per start and uses that
immutable snapshot for the whole operation, so a later profile change cannot
silently alter an in-flight authorization. The exact revision is bound into the
authority request identity and recorded in a `SandboxExecutionBinding` for
evidence; a launch is always attributed to the revision that passed validation.

### Invocation authority is an intersection

`ExecuteProcess` authority over an executable does **not** imply arbitrary
invocation authority. Effective invocation authority is the **intersection** of
the Hufu grant (this executable in this workspace) and the trusted execution
profile (arguments, working directory, permitted environment names, guarantee
ceiling). This matters most for interpreters and shells, whose executable
identity is otherwise extremely broad.

## Lifecycle

1. Activity requests execution (`SandboxExecutionRequest`: authority context,
   activity/grant revision, invocation id, environment, guarantees, fresh
   authorization request id).
2. The adapter validates the request against the profile (unknown invocation,
   unallowed environment name, or a guarantee outside the ceiling denies before
   any provider call).
3. Hufu authority is evaluated for the `ExecuteProcess` resource.
4. Gagamba negotiation proves the platform can provide the required guarantees;
   a gap is refused, never weakened.
5. Gagamba `Prepare` proves a usable domain (delegation/placement) without
   launching.
6. **Hufu authority is re-evaluated immediately before launch** (fresh request
   identity) — authority must still be valid where effects become possible.
7. Gagamba `Launch`; the opaque handle is associated with the exact
   activity/grant revision.
8. Grant revocation calls the adapter's `TerminateAsync`, which terminates the
   associated execution domain.
9. Completion disposes the handle; host disposal tears down remaining domains.

### Prepared-domain reclamation (GP-3 amendment)

Because `Prepare` may allocate provider resources (a Linux cgroup, a Windows
job handle, a macOS job directory), every path that prepares but does not
launch must reclaim the domain. Gagamba's SPI therefore gained
`IExecutionProvider.Discard(PreparedExecution)`: provider-owned, single-use,
safe before launch, idempotent, and fail-closed for a foreign preparation. The
host invokes it in a `finally` whenever a start does not reach a successful
launch — a pre-launch authority denial, cancellation between `Prepare` and
`Launch`, or any adapter fault. Provider disposal remains a final safety net,
not the normal cleanup mechanism.

### Failure classes (kept distinct)

- `RequirementNotAuthorized` — the profile/policy does not permit the requested
  guarantee (or the invocation/environment is outside the profile).
- `GuaranteeUnavailable` — the guarantee is permitted, but this host cannot
  provide it.

plus `AuthorityDenied`, `PreparationFailed`, `LaunchFailed` (and
`Terminated`/`UnknownActivity` for termination). Authority decisions, profile
limits, platform guarantee gaps and provider faults remain separately
attributable, and the distinction is preserved into the recorded evidence.

## Explicit non-claims

- An execution domain is **not** a security sandbox, and this adapter does not
  claim confinement, an IAM boundary, or a filesystem/VFS broker.
- No filesystem brokering, quotas, networking, output capture, or owner-death
  composition is added here.
- Guarantee gating lives in the trusted profile (host) for this slice; lifting
  it into Hufu's Cedar evaluation context is deferred.
- Not packable yet: the public API inventory, allow-list entry and package
  validation baseline are a later, reviewed step.

## Verification

- `Penghou.Hufu.Sandbox.Tests`: profile/ceiling/revision/discard/cancellation
  cases, plus two real-provider E2E cases over `ExecutionRuntime.Create()` — an
  authorized launch runs a real process and revocation terminates it; and a
  pre-launch revocation runs nothing while the prepared domain is discarded.
- Gagamba: SPI contract tests cover discard idempotence and foreign-preparation
  fail-closed; the Linux provider test proves a prepared cgroup directory is
  created at `Prepare` and removed at `Discard`.

The adapter references the amended `Gagamba.Runtime` preview that carries
`Discard`; the first published preview predates the amendment.

## Next

**HZ-1 (workflow-authorized execution)** is the next pressure: a durable
workflow activity consumes this adapter so activity identity, authority
revision and profile revision survive orchestration, retries, cancellation and
recovery — proving fresh authority per retry, workflow-cancellation and
independent-revocation termination, and recovery that neither resurrects an
authorization nor reuses a consumed preparation. It adds no sandbox capability.

HG-2 (filesystem authority broker), HG-3 (process/tool authority), HG-4
(network/outbound HTTP authority) follow only when a real consumer needs them.
