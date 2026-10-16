# Spike: agent-assignment telemetry on failure (bobcat#563)

A broker-free, database-free reproduction of Wolverine's leadership-election compliance suite
(`LeadershipElectionCompliance`, run by every `*Tests.LeaderElection` project), used to build and prove
a producer that turns the cluster's agent-assignment history into Bobcat's model **when a specification
fails**: a scenario report, step logs and a failure attachment. **Read `findings.md`**: it holds the
analysis of the instrumentation Wolverine's suite has today, what was built, what it showed about
Bobcat, and the port to Wolverine.

- `Cluster`: stands in for the Wolverine runtime and node storage. Nodes, a leader, twelve agents, an
  assignment table, and an event tracker that calls observers on each node's background loop. No Bobcat.
- `Compliance`: stands in for `Wolverine.ComplianceTests`. The abstract base keeps Wolverine's test names;
  `ClusterSteps` is the step vocabulary; **`Telemetry/` is the producer**, the part that would move.
- `Providers`: stands in for a `*Tests.LeaderElection` project, with bodyless subclasses. One provider's
  leader forgets the stop when it moves an agent between live nodes, so its two rebalancing
  specifications fail **on purpose**, and their output is the worked example.

Not in `bobcat.slnx` and not built by CI, like the other spikes. It takes its package versions from
`src/Directory.Packages.props` and references Bobcat by project.

```bash
dotnet build spikes/leadership-telemetry/LeadershipTelemetrySpike.slnx
BOBCAT_MONITOR=0 spikes/leadership-telemetry/Providers/bin/Debug/net10.0/Providers
```

Expect 10 specifications, 8 passing and 2 failing, in about 6 seconds. Add `BOBCAT_RESULTS=<dir>` to get
the JSON results file and the transcripts under `<dir>/attachments/`.
