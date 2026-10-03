Hufu’s biggest opportunity is to make authority understandable, enforceable and recoverable throughout an agent’s work.

Cedar provides the authorization decision engine. Hufu should provide the surrounding authority runtime: persistent grants, delegation, approvals, execution requirements, enforcement brokers, revocation, evidence and understandable explanations.

**Authority analysis and explanation**

Hufu should have an authority debugger that can explain not only whether something is allowed, but why.

The authoritative part should remain deterministic. For every request, Hufu should be able to reconstruct the relevant grant lineage, exclusions, resource relationships, policy versions and requirements.

It should answer questions such as:

“Why was this denied?”

“What authority would be sufficient?”

“What exactly becomes possible if I approve this?”

“What changed between these two authority states?”

For example, Hufu should be able to explain that a file was covered by a repository grant but removed by a narrower exclusion.

Counterfactual simulation should also be supported. Hufu can apply a proposed grant or policy change to a temporary authority state and calculate what additional actions and resources would become reachable.

An optional AI explainer can sit on top of this deterministic analysis. Its job is to explain the impact in understandable language, summarize large authority changes and highlight surprising consequences. The AI explanation is informational only. Authorization and scope calculation remain deterministic.

**Persistent authority state**

Hufu needs its own persistent authority store around Cedar.

The stored model includes principals, resources, grants, parent-child delegation, exclusions, approvals, operation requirements, policy revisions, revocations, decisions and broker receipts.

This is naturally graph-shaped, but Hufu does not need to require a graph database initially.

The local-first implementation can use SQLite behind an `IAuthorityStore` abstraction. Graph-backed implementations can be added later if traversal complexity or scale justifies them.

Cedar remains the policy evaluator. Hufu owns the lifecycle and provenance of the authority Cedar evaluates.

**Execution contracts**

Authorization should not always end with a Boolean allow or deny.

An approved operation may also carry requirements such as:

“This export is allowed after redaction, for at most 500 rows, to this destination, using this credential.”

Hufu should represent this as an execution contract containing structured requirements.

Every requirement must have a registered enforcer or verifier. An unsupported requirement blocks execution rather than being silently ignored.

This makes authorization composable with limits such as destination restrictions, artifact versions, budget reservations, credentials, redaction requirements and approval conditions.

The broker executing the operation is responsible for enforcing these requirements and returning evidence of what happened.

**Capability brokers**

Penghou libraries should perform sensitive external operations through small replaceable abstractions rather than directly binding themselves to Hufu.

A lightweight capability abstraction package can provide filesystem, HTTP, process and credential interfaces together with normal local implementations.

When running under a Hufu-enabled host, dependency injection replaces those implementations with Hufu-governed brokers.

This lets libraries remain independently usable while allowing Marang, Guyabano and other higher-level hosts to place their operations under Hufu authority.

The filesystem broker should handle canonical paths, aliases, symlinks and junctions.

The HTTP broker should govern the actual destination, including redirects, and should be able to attach approved credentials without exposing them to the agent.

The process broker should govern process admission, arguments, working directories and environment exposure.

Initially this provides mediated execution rather than complete containment. Arbitrary child processes can still exercise authority available to the host operating-system identity.

Full process, filesystem and network isolation should therefore be a later pluggable capability behind an `IExecutionIsolationProvider`, rather than a prerequisite for Hufu.

**Credential brokering**

Credential handling is useful even for local-first Hufu.

An agent should be able to request an operation using a named credential without receiving the underlying secret.

The local implementation can use opaque credential handles resolved by the trusted host. Credentials should not normally appear in workflow state, prompts, logs or Hufu's durable authority database.

Later, remote Hufu deployments can add workload identity systems such as SPIFFE and secret systems such as Vault without changing the core authority model.

Identity, credential lifetime and Hufu authority lifetime remain separate concepts and must be coordinated explicitly.

**Approval governance**

The initial local-first version should support exact-effect approvals, temporary elevation, expiry and approval bound to specific resources, artifacts or destinations.

Multi-person approvals, separation of duties and organizational approval policies should remain planned extensions rather than core requirements.

The underlying approval model should leave room for those features without requiring them in the first implementation.

**Decision evidence and observability**

Every authorization and governed operation should produce durable decision evidence connecting the request, grant lineage, policy version, approval state, execution requirements, broker action and eventual outcome.

OpenTelemetry should provide operational traces and metrics around authorization latency, broker execution, approval waits, revocation propagation and failures.

Durable authority records remain separate from telemetry because telemetry may be sampled or discarded.

Tamper-evident or externally anchored records can be added later without changing the basic evidence model.

**Enforcement conformance**

Hufu should ship conformance tests for its own brokers and for third-party implementations.

These should test resource normalization, aliases, filesystem links, redirects, changed resources, delegation attenuation, revocation races, retries, crashes and duplicate execution.

A broker should be able to demonstrate which Hufu guarantees it actually enforces.

This distinction is particularly important for process execution. Authorizing a command is not equivalent to isolating everything the resulting process can do.

**Portable delegation**

Biscuit is interesting as a future transport for Hufu authority, not as a replacement authorization system.

Its offline attenuation model maps well to Hufu delegation between independently executing workers.

If adopted, Hufu should issue Biscuit tokens from existing Hufu grants using a restricted Hufu-defined token profile. Biscuit attenuation could reduce authority further, but Biscuit logic would never be allowed to create authority that Hufu did not issue.

Cedar and Hufu remain the source of authorization truth.

Because offline capability tokens complicate immediate revocation and introduce bearer-token concerns, Biscuit should remain an optional remote-delegation adapter rather than part of the local-first core.

The first major Hufu investments should therefore be the authority store and lineage model, deterministic authority analysis, execution contracts, Hufu-bound capability brokers, decision evidence and a broker conformance suite.

A complete sandbox can come later.

This gives Hufu something more useful than another permission-prompt system: a runtime where authority can be explained, delegated, changed, revoked, enforced and proven throughout an agent’s work.