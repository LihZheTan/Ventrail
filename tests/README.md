# Ventrail tests

Each folder is a small console app that drives the real Ventrail code and prints one line per
check (`PASS` / `FAIL`) and a final `N passed, M failed.` line. They are grouped by the
development week that introduced the feature.

| Suite | Checks | What it covers |
|---|---|---|
| Week07 | 24 | Discard changes (Recycle Bin safety copy), line-by-line diff |
| Week08 | 22 | ML.NET heavy/light classifier: model loading, verdicts, speed, ViewModel badges |
| Week09 | 39 | AI suggestions: commit message, storage advice, leave-out, Accept / Edit Manually |
| Week10 | 54 | Firewall audit rules, netsh scripts, listening on TCP/UDP, permission flow (Fig 3.5) |
| Week11 | 67 | Kademlia routing table, messages, discovery and DHT on 3 real nodes, provider records |
| Week12 | 48 | Version packages, block streaming, SHA-256 checks, resume/cancel, safe import |
| Week13 | 42 | Adaptive timeouts + **end-to-end**: two real nodes through the real ViewModel, slow/frozen/busy peers |

## Run

```
powershell -ExecutionPolicy Bypass -File tests\run-all.ps1              # everything (~3 min)
powershell -ExecutionPolicy Bypass -File tests\run-all.ps1 -Only 12,13  # some weeks
powershell -ExecutionPolicy Bypass -File tests\run-all.ps1 -Benchmark   # + classifier speed timings
```

Or run one suite on its own: `dotnet run --project tests\Ventrail.Tests.Week13`.

## Good to know

- **No admin rights and no firewall changes.** Network tests only use loopback (127.0.0.1) on
  ports 45100–48199, and the real Windows admin prompt is never triggered (the end-to-end suite
  replaces only the firewall check).
- **Temporary files** go to `%TEMP%` and are removed at the end.
- **Week07** tests "Discard", which by design moves a few tiny test files to the **Recycle Bin**.
- **Timing checks:** some network checks measure time, for example "a frozen peer is noticed
  within 15 s". On a heavily loaded PC they can occasionally fail; run them again.
