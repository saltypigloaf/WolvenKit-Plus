# TweakXL writer performance check — 2026-10-13

**Verdict: the measured overhead is reasonable for occasional bulk exports on this machine.** Keeping the existing durability guarantees adds about 0.57–0.64 ms per record in this workload. Atomic publication took under 0.9 seconds for 1,000 records at the median, and handled cancellation left no atomic temporary or partial files. This check found no concrete performance issue that warrants changing production behavior or weakening disk flushing.

During measurement, the working checkout stayed on `feature/tweakxl-bulk-export` at `4dd33be7802e805b71ad9bd4e4150515eac9e520`, and the benchmark, results, and handoff update were left uncommitted. The user subsequently authorized committing and pushing these artifacts. Production code was not changed.

## Results

Each cell is based on exactly three measured repetitions after one complete 1,000-record warmup for each approach/scenario. Throughput is 1,000 divided by median elapsed seconds; the ratio is atomic median divided by direct median.

| Operation | Direct median | Atomic median | Direct records/s | Atomic records/s | Elapsed-time ratio | Additional time |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Create 1,000 new files | 282.03 ms | 855.14 ms | 3,545.7 | 1,169.4 | 3.03× | +203.2%; +573.10 ms |
| Replace 1,000 existing files | 241.45 ms | 884.41 ms | 4,141.7 | 1,130.7 | 3.66× | +266.3%; +642.97 ms |

Measured elapsed times, in repetition order:

| Operation / approach | Repetition 1 | Repetition 2 | Repetition 3 |
| --- | ---: | ---: | ---: |
| Create / direct | 282.0345 ms | 291.5312 ms | 277.8969 ms |
| Create / atomic | 880.5421 ms | 854.8820 ms | 855.1371 ms |
| Replace / direct | 238.1037 ms | 241.4469 ms | 258.0911 ms |
| Replace / atomic | 877.4274 ms | 958.7000 ms | 884.4137 ms |

Use the machine-readable result for full precision. This is a small comparison with three samples per case, not a latency distribution or worst-case guarantee.

## Payloads, environment, and timing boundaries

- Exactly **1,000 pre-generated synthetic YAML strings**, reused byte-for-byte for both approaches: **700 × 1,024 bytes, 250 × 4,096 bytes, 50 × 16,384 bytes**. Total **2,560,000 bytes (2.4414 MiB)** per full run; mean **2,560 bytes (2.5 KiB)**. Sizes cycle in a fixed 20-record pattern.
- UTF-8 without BOM, LF newlines, deterministic record names and fields, accented text and an emoji, and a padding comment to reach exact byte lengths. These are generated records, not samples from the game corpus. Replacements start with distinct `old`-state payloads of the same byte length and finish with the same `new` payloads used in creation runs.
- Windows build 26300, .NET SDK **10.0.401**, runtime **.NET 10.0.12**, Release x64 harness with tiered compilation disabled for both approaches. AMD Ryzen 7 7700X, 16 logical processors, local **C: NTFS** on a **WD_BLACK SN770 2TB** drive. This was outside OneDrive.
- The launcher reads the current atomic helper without modifying it, copies its exact source, and extracts the current record-write wrapper. It extracts the earlier direct-write method from commit `3db99b10b079ba507b08ad664b3e1b0c7a5e4606` using `git show`; it never checks out that commit. Only visibility/method names and containing wrapper classes change in the extracted methods. No application or database service is loaded.
- The direct path retains its historical `FileMode.CreateNew`/`Create`, asynchronous `FileStream`, and `StreamWriter` disposal behavior. It does **not** explicitly flush data to disk. The atomic path retains its encoding flush, `Flush(true)`, close, cancellation check, and same-directory rename. The comparison measures their different completion contracts; the atomic cost is not just rename overhead.
- A stopwatch covers only the sequential 1,000 writer calls, including their open/write/dispose/flush/rename work. Payload generation, path construction, directory creation, replacement seeding, byte checks, and 250 ms settling pauses are outside timing. Replacement seed files are disk-flushed before timing.
- One full warmup for each of the four cases, then three measured repetitions with approach order alternated and reversed between create/replace cases. Each run has a fresh directory. No OS cache purge, forced GC, or background-process suppression was performed. This is a warm local-filesystem measurement, not a cold-drive or isolated storage benchmark.

## Cancellation and cleanup

Asynchronous batch checks use the exact extracted writer methods. After 100 completed records, a dedicated thread is signaled, waits 1 ms, records a monotonic timestamp immediately before calling `Cancel()`, and requests cancellation while the batch continues. Latency ends when the outer loop catches `OperationCanceledException`, after the writer has unwound and attempted cleanup. The thread's initial wait/scheduling delay is outside this latency. Cancellation is cooperative and is not guaranteed to occur inside a specific system call.

| Case | Request-to-observation median | Maximum of 3 | Partial files | Temporary leftovers |
| --- | ---: | ---: | ---: | ---: |
| Create / direct | 0.224 ms | 1.333 ms | 3 total, one per run | 0 |
| Create / atomic | 0.344 ms | 0.431 ms | 0 | 0 |
| Replace / direct | 0.078 ms | 0.113 ms | 0 | 0 |
| Replace / atomic | 0.361 ms | 0.380 ms | 0 | 0 |

Six additional atomic-helper probes (three create, three replace) cancel after half of a 16 KiB payload has been written and text-buffer-flushed into an observed temporary file. These controlled probes use the unchanged helper with a split-write callback, not the ordinary full-string callback. All six removed their owned temporary, preserved the original destination or its absence, and preserved a sentinel file. Observation took **0.175–0.819 ms**, including the first controlled probe; medians were **0.215 ms** for create and **0.180 ms** for replace.

All **16 full runs** (four warmups plus twelve measured runs) byte-verified all 1,000 destinations and found zero temporary files. All **12 asynchronous cancellation runs** verified every planned destination as complete, old, partial, or absent, and checked directory inventories. No atomic partial file was found. Every sentinel remained unchanged. The three direct-write partial files are deliberately retained inside this benchmark's owned directory as evidence of the historical behavior.

These probes do not measure cancellation while an uninterruptible disk flush stalls, power loss, forced process termination, disk exhaustion, or a cleanup-permission/lock failure. Prior fixtures covered handled cleanup failure separately. Network/cloud filesystem behavior remains untested.

## Extrapolation only: 193,354 records

Multiplying these medians by **193.354** gives the following **linear extrapolation of writer time only**, assuming the same payload mix, hardware, cache behavior, and per-file cost:

| Operation | Direct extrapolation | Atomic extrapolation | Added writer time |
| --- | ---: | ---: | ---: |
| Create | 54.5 s | 165.3 s (2.76 min) | 110.8 s (1.85 min) |
| Replace | 46.7 s | 171.0 s (2.85 min) | 124.3 s (2.07 min) |

**These are not measured full-export durations.** Real payload sizes, directory counts, serialization/planning, database access, report writing, filesystem scaling, background activity, and storage stalls can change the result. The existing 193,354-record snapshot was neither read as benchmark input nor regenerated.

## Reproduction and saved locations

Run from PowerShell with the current committed writer sources:

```powershell
& 'C:\workspace\WolvenKit-Plus\tools\Benchmarks\TweakXLWriterBenchmark.ps1' `
  -RepositoryRoot 'C:\workspace\WolvenKit-Plus' `
  -ExpectedCommit (git -C 'C:\workspace\WolvenKit-Plus' rev-parse HEAD) `
  -ResultsDirectory 'C:\path\to\a-new-results-directory'
```

The script's default expected commit is the measured `4dd33be78` baseline. The explicit `-ExpectedCommit` above permits running from the subsequent artifact-only commit, whose production writer sources are unchanged. On any later production revision, the script measures that selected revision and records its source hashes; its new results are a separate experiment. The historical direct-write comparison remains pinned to `3db99b10`.

The result directory must not already exist. The script creates a fresh GUID-named directory below the OS temporary directory and retains it for inspection; it does not scan/delete prior benchmark directories. No Git ref, production behavior, game data, or published snapshot is changed.

This run's complete synthetic files and compiled harness are retained at:

`C:\Users\hanlo\AppData\Local\Temp\wolvenkit-tweakxl-writer-benchmark-b43bd18275fa4bd58c660aecf0192501`

Detailed results, environment/source hashes, payload manifest, logs, script/harness copies, verification, and the updated handoff copy are at:

`C:\Users\hanlo\Documents\Codex\2026-10-13\continue-my-wolvenkit-plus-project-read\outputs\performance-check`

Repository review files are `Tools/Benchmarks/TweakXLWriterBenchmark.ps1`, its adjacent `.cs` harness, this report, `atomic-writer-2026-10-13.json` (environment plus measurements), and `HANDOFF.md`. `harness/Program.cs` in the retained temporary root is the exact compiled harness; repository scripts were subsequently normalized to CRLF without changing their text/behavior.

Measurement validation consists of this standalone harness build (**zero warnings/errors**) and the measurements/assertions described above. The earlier **102-test suite and desktop build are saved prior results**, not rerun by the measurement task. No source/value audit, in-game test, connector upload, commit, push, or OneDrive synchronization occurred during measurement. The user subsequently authorized publication of these artifacts; existing validation is reused because scripts, results, and production code are unchanged. No production performance change is indicated by this check.

The final checkout audit found OneDrive WolvenKit already clean at `4dd33be78`, newer than the earlier publication receipt's `3db99b10`. That stale comparison initially stopped the audit; read-only reinspection established the current state without changing it. This task did not establish when that checkout advanced or perform synchronization. Both documentation checkouts remain clean at their published `5d23c544` import commit.
