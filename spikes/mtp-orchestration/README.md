# MTP orchestration spike (issue #43)

**Throwaway spike, not production code.** It exists to answer one question: can a Bobcat
supervisor process drive Microsoft.Testing.Platform test hosts and collect per-test results
cleanly enough to build [#41](https://github.com/JasperFx/bobcat/issues/41) on top of?

**→ Read [findings.md](findings.md) for the answer (GO) and the evidence.**

Deliberately **not** part of `bobcat.sln`, so the main build and CI never touch it.

## Run it

```bash
dotnet build MtpSpike.sln

cd Spike.Orchestrator
dotnet run -- \
  ../Spike.XunitV3Host/bin/Debug/net10.0/Spike.XunitV3Host \
  ../Spike.TUnitHost/bin/Debug/net10.0/Spike.TUnitHost \
  ../Spike.BobcatHost/bin/Debug/net10.0/Spike.BobcatHost
```

Add `--verbose` to echo every JSON-RPC frame, or set `SPIKE_DUMP=<file>` to write the full
protocol log.

## Layout

| Project | Role |
|---|---|
| `Spike.Orchestrator` | The parent: JSON-RPC client (`JsonRpcConnection`), session driver (`TestHostSession`), and the experiment battery (`Experiments`). |
| `Spike.XunitV3Host` | xUnit v3 tests — one per outcome shape the supervisor must classify. |
| `Spike.TUnitHost` | The same shapes in TUnit, to check two independent frameworks agree on the wire. |
| `Spike.BobcatHost` | A Bobcat-owned MTP host, proving Bobcat can *expose* itself as well as drive others. |

The test hosts read three environment variables so the parent can arm specific behaviour:
`SPIKE_FLAKY_PASSES` (make the flaky test pass), `SPIKE_CRASH` (kill the process mid-run),
`SPIKE_SLOW` (sleep 30s so cancellation has something to interrupt).
