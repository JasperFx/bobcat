# Spike: Bobcat on an inherited compliance battery (bobcat#564)

A broker-free reproduction of Wolverine's `TransportCompliance<T>` shape: an abstract generic base of
`[BobcatSpec]` tests in a class library (`Compliance`), and a test host (`Transports`) of bodyless
subclasses, one per transport. It measures how Bobcat identifies, lists, previews and renders inherited
specifications. **Read `findings.md`**; it holds the evaluation and the recommendation.

Not in `bobcat.slnx` and not built by CI, like `spikes/mtp-orchestration`. It takes its package versions
from `src/Directory.Packages.props` and references Bobcat by project, so it measures the Bobcat in this tree.
One test fails on purpose (`DurableTcpTransportCompliance.can_schedule_retry`): the fake transport can't
schedule a retry, so one transport is red and the output can show whether a reader can tell which.

```bash
dotnet build spikes/transport-compliance/TransportComplianceSpike.slnx
BOBCAT_MONITOR=0 spikes/transport-compliance/Transports/bin/Debug/net10.0/Transports
```
