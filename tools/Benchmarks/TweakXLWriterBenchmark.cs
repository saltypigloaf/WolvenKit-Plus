using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WolvenKit.App.Helpers;

// Standalone synthetic measurement harness; the launcher extracts both writer methods
// from the repository and compiles an exact copy of the committed atomic helper.
internal static class Program
{
    private const int RecordCount = 1000;
    private const int Repetitions = 3;
    private static readonly UTF8Encoding s_utf8 = new(false);
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private static readonly byte[] s_sentinel = s_utf8.GetBytes("Unrelated benchmark sentinel; preserve these bytes.\n");
    private static string s_root = "";
    private static string s_results = "";
    private static Payload[] s_payloads = [];

    private static async Task Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        s_root = Path.GetFullPath(args[0]);
        s_results = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(s_root, "BENCHMARK-OWNED.txt")))
        {
            throw new InvalidOperationException("Missing ownership marker.");
        }
        s_payloads = Enumerable.Range(0, RecordCount).Select(CreatePayload).ToArray();
        await Save("payload-manifest.json", s_payloads.Select(p => new
        {
            p.Name, Bytes = p.Bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(p.Bytes)).ToLowerInvariant(),
            OriginalSha256 = Convert.ToHexString(SHA256.HashData(p.OriginalBytes)).ToLowerInvariant()
        }));
        var rows = new List<Run>();
        foreach (var scenario in new[] { "create", "replace" })
        {
            foreach (var approach in new[] { "direct", "atomic" })
            {
                rows.Add(await Measure(scenario, approach, 0, warmup: true));
            }
        }
        for (var repetition = 1; repetition <= Repetitions; repetition++)
        {
            foreach (var scenario in new[] { "create", "replace" })
            {
                // Alternate order across repetitions, and reverse it for replacement.
                var atomicFirst = (repetition % 2 == 0) != (scenario == "replace");
                foreach (var approach in atomicFirst ? new[] { "atomic", "direct" } : new[] { "direct", "atomic" })
                {
                    rows.Add(await Measure(scenario, approach, repetition, warmup: false));
                    await Save("runs.json", rows);
                }
            }
        }
        var cancellations = new List<CancellationRun>();
        foreach (var scenario in new[] { "create", "replace" })
        {
            for (var repetition = 1; repetition <= Repetitions; repetition++)
            {
                foreach (var approach in new[] { "direct", "atomic" })
                {
                    cancellations.Add(await CheckBatchCancellation(scenario, approach, repetition));
                }
            }
        }
        var stagedCancellations = new List<StagedCancellation>();
        foreach (var scenario in new[] { "create", "replace" })
        {
            for (var repetition = 1; repetition <= Repetitions; repetition++)
            {
                stagedCancellations.Add(await CheckStagedCancellation(scenario, repetition));
            }
        }
        var summary = new[] { "create", "replace" }.Select(scenario =>
        {
            var direct = Median(rows.Where(r => !r.Warmup && r.Scenario == scenario && r.Approach == "direct").Select(r => r.ElapsedMs));
            var atomic = Median(rows.Where(r => !r.Warmup && r.Scenario == scenario && r.Approach == "atomic").Select(r => r.ElapsedMs));
            return new
            {
                Scenario = scenario, DirectMedianMs = direct, AtomicMedianMs = atomic,
                DirectRecordsPerSecond = RecordCount * 1000.0 / direct,
                AtomicRecordsPerSecond = RecordCount * 1000.0 / atomic,
                AtomicToDirectRatio = atomic / direct, AdditionalTimePercent = (atomic / direct - 1) * 100,
                AdditionalMsPerRecord = (atomic - direct) / RecordCount,
                Extrapolation193354RecordsOnly = new
                {
                    DirectSeconds = direct / 1000 * (193354.0 / RecordCount),
                    AtomicSeconds = atomic / 1000 * (193354.0 / RecordCount),
                    AdditionalSeconds = (atomic - direct) / 1000 * (193354.0 / RecordCount)
                }
            };
        }).ToArray();
        var result = new
        {
            CompletedAtUtc = DateTimeOffset.UtcNow, RecordCount, MeasuredRepetitions = Repetitions,
            Warmup = "One full 1000-record run per approach and scenario; not in medians.",
            Timing = "Sequential record-writer calls only; payload generation, directory creation, replacement seeding, verification and 250 ms settling pauses excluded.",
            CachePolicy = "Warm process/filesystem; no OS cache purge. Unique directory per run. No forced GC. Tiered compilation disabled for both approaches.",
            Scope = "Pre-serialized synthetic YAML only; no database, serializer, exporter planning, UI, or export-report timing.",
            DirectDurability = "Historical FileStream/StreamWriter disposal; no explicit disk flush. Measures its original completion contract.",
            AtomicDurability = "Unchanged helper and wrapper: encoding flush, Flush(true), close, token check and same-directory rename.",
            ReplacementPreparation = "1000 same-size old-state payloads seeded and disk-flushed before timing.",
            BenchmarkRoot = s_root, ResultsDirectory = s_results,
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
            Payloads = new
            {
                Distribution = s_payloads.GroupBy(p => p.Bytes.Length).Select(g => new { Bytes = g.Key, Count = g.Count() }).ToArray(),
                TotalBytes = s_payloads.Sum(p => (long)p.Bytes.Length), MeanBytes = s_payloads.Average(p => p.Bytes.Length),
                Encoding = "UTF-8 without BOM, LF newlines, includes accented text and an emoji; padded deterministic YAML. Not a sampled game corpus."
            },
            Summary = summary, Runs = rows, BatchCancellation = cancellations, InFlightStagingCancellation = stagedCancellations,
            AllChecksPassed = true, GeneratedDataRetained = true
        };
        await Save("results.json", result);
        foreach (var row in summary)
        {
            Console.WriteLine($"{row.Scenario}: direct {row.DirectMedianMs:F3} ms; atomic {row.AtomicMedianMs:F3} ms; {row.AtomicToDirectRatio:F3}x elapsed; atomic {row.AtomicRecordsPerSecond:F1} records/s");
        }
        Console.WriteLine($"PASS: {rows.Count} full runs, {cancellations.Count} asynchronous batch cancellation checks, {stagedCancellations.Count} guaranteed in-flight atomic cancellation checks.");
    }

    private static Payload CreatePayload(int index)
    {
        var size = index % 20 < 14 ? 1024 : index % 20 < 19 ? 4096 : 16384;
        var name = $"Synthetic.Record{index:D4}.yaml";
        string Make(string state)
        {
            var prefix = $"Synthetic.Record{index:D4}:\n  $type: gamedataItem_Record\n  displayName: 'Synthetic café 🚀'\n  state: {state}\n  enabled: true\n  value: {index}\n  tags: [synthetic, benchmark]\n  # padding: ";
            return prefix + new string('x', size - s_utf8.GetByteCount(prefix) - 1) + "\n";
        }
        var text = Make("new");
        return new Payload(name, text, s_utf8.GetBytes(text), s_utf8.GetBytes(Make("old")));
    }

    private static string NewDirectory(string name)
    {
        var path = Path.GetFullPath(Path.Combine(s_root, name));
        if (!path.StartsWith(s_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(path) || File.Exists(path))
        {
            throw new InvalidOperationException("Benchmark path must be new and below the owned root.");
        }
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "sentinel.keep"), s_sentinel);
        return path;
    }

    private static string[] Prepare(string directory, bool replace)
    {
        var paths = s_payloads.Select(p => Path.Combine(directory, p.Name)).ToArray();
        if (replace)
        {
            for (var i = 0; i < RecordCount; i++)
            {
                using var stream = new FileStream(paths[i], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(s_payloads[i].OriginalBytes);
                stream.Flush(flushToDisk: true);
            }
        }
        return paths;
    }

    private static Task Write(string approach, string path, string payload, bool replace, CancellationToken token) =>
        approach == "atomic" ? AtomicWriter.WriteAsync(path, payload, replace, token) : DirectWriter.WriteAsync(path, payload, replace, token);

    private static async Task<Run> Measure(string scenario, string approach, int repetition, bool warmup)
    {
        var directory = NewDirectory($"{(warmup ? "warmup" : "measured")}-{scenario}-{approach}-{repetition}");
        var replace = scenario == "replace";
        var paths = Prepare(directory, replace);
        await Task.Delay(250);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < RecordCount; i++)
        {
            await Write(approach, paths[i], s_payloads[i].Text, replace, CancellationToken.None);
        }
        clock.Stop();
        var inventory = Inspect(directory, paths);
        Require(inventory.Complete == RecordCount && inventory.Original == 0 && inventory.Partial == 0 && inventory.Missing == 0 && inventory.Temporary == 0, "Full-run byte/inventory validation failed.");
        var run = new Run(scenario, approach, repetition, warmup, clock.Elapsed.TotalMilliseconds, RecordCount * 1000.0 / clock.Elapsed.TotalMilliseconds, directory, inventory);
        Console.WriteLine($"{(warmup ? "warmup" : "measured")} {scenario} {approach} #{repetition}: {run.ElapsedMs:F3} ms; all 1000 payloads byte-verified; no temporaries");
        return run;
    }

    private static async Task<CancellationRun> CheckBatchCancellation(string scenario, string approach, int repetition)
    {
        var directory = NewDirectory($"cancel-batch-{scenario}-{approach}-{repetition}");
        var replace = scenario == "replace";
        var paths = Prepare(directory, replace);
        using var cancellation = new CancellationTokenSource();
        using var startCancellation = new ManualResetEventSlim();
        long requestTick = 0;
        var thread = new Thread(() =>
        {
            if (startCancellation.Wait(TimeSpan.FromSeconds(30)))
            {
                Thread.Sleep(1);
                Interlocked.Exchange(ref requestTick, Stopwatch.GetTimestamp());
                cancellation.Cancel();
            }
        }) { IsBackground = true, Name = "Synthetic export cancellation" };
        thread.Start();
        var completed = 0;
        var observed = false;
        long observationTick = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < RecordCount; i++)
            {
                if (i == 100)
                {
                    startCancellation.Set();
                }
                cancellation.Token.ThrowIfCancellationRequested();
                await Write(approach, paths[i], s_payloads[i].Text, replace, cancellation.Token);
                completed++;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            observationTick = Stopwatch.GetTimestamp();
            observed = true;
        }
        finally
        {
            clock.Stop();
            startCancellation.Set();
            thread.Join();
        }
        Require(observed && requestTick > 0, "Cancellation missed the bounded batch; do not report responsiveness for this run.");
        var inventory = Inspect(directory, paths);
        Require(inventory.Temporary == 0, "Handled cancellation left writer temporary files.");
        Require(inventory.Complete >= completed && inventory.Complete <= completed + 1, "Unexpected canceled-run completed file count.");
        if (approach == "atomic")
        {
            Require(inventory.Partial == 0 && inventory.Complete == completed, "Atomic cancellation changed an unpublished destination.");
        }
        var latency = Stopwatch.GetElapsedTime(requestTick, observationTick).TotalMilliseconds;
        var result = new CancellationRun(scenario, approach, repetition, completed, latency, clock.Elapsed.TotalMilliseconds, directory, inventory);
        Console.WriteLine($"cancel {scenario} {approach} #{repetition}: observed {latency:F3} ms after request; {completed} completed; {inventory.Partial} partial; {inventory.Temporary} temporaries");
        return result;
    }

    private static async Task<StagedCancellation> CheckStagedCancellation(string scenario, int repetition)
    {
        var directory = NewDirectory($"cancel-staged-{scenario}-{repetition}");
        var replace = scenario == "replace";
        var payload = s_payloads.Last(); // 16 KiB; first half is staged before canceling.
        var destination = Path.Combine(directory, payload.Name);
        if (replace)
        {
            await File.WriteAllBytesAsync(destination, payload.OriginalBytes);
        }
        using var cancellation = new CancellationTokenSource();
        long requestTick = 0;
        long observationTick = 0;
        var observed = false;
        var temporaryExisted = false;
        try
        {
            await TweakXLAtomicFileWriter.WriteAsync(destination, replace, async (stream, token) =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                var half = payload.Text.Length / 2;
                await writer.WriteAsync(payload.Text.AsMemory(0, half), token);
                await writer.FlushAsync(token);
                temporaryExisted = File.Exists(((FileStream)stream).Name) && stream.Length > 0;
                requestTick = Stopwatch.GetTimestamp();
                cancellation.Cancel();
                await writer.WriteAsync(payload.Text.AsMemory(half), token);
                await writer.FlushAsync(token);
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            observationTick = Stopwatch.GetTimestamp();
            observed = true;
        }
        Require(observed && temporaryExisted, "In-flight cancellation was not exercised.");
        var preserved = replace ? File.ReadAllBytes(destination).AsSpan().SequenceEqual(payload.OriginalBytes) : !File.Exists(destination);
        var temporaryCount = Directory.GetFiles(directory, ".wolvenkit-tweakxl-*.tmp").Length;
        Require(preserved && temporaryCount == 0, "Staged cancellation failed to preserve destination or clean temporary file.");
        Require(File.ReadAllBytes(Path.Combine(directory, "sentinel.keep")).AsSpan().SequenceEqual(s_sentinel), "Sentinel changed.");
        var latency = Stopwatch.GetElapsedTime(requestTick, observationTick).TotalMilliseconds;
        Console.WriteLine($"cancel staged {scenario} #{repetition}: {latency:F3} ms; temporary observed then cleaned; destination preserved");
        return new StagedCancellation(scenario, repetition, latency, temporaryExisted, preserved, temporaryCount, directory);
    }

    private static Inventory Inspect(string directory, string[] paths)
    {
        var complete = 0;
        var original = 0;
        var partial = 0;
        var missing = 0;
        for (var i = 0; i < paths.Length; i++)
        {
            if (!File.Exists(paths[i]))
            {
                missing++;
                continue;
            }
            var bytes = File.ReadAllBytes(paths[i]);
            if (bytes.AsSpan().SequenceEqual(s_payloads[i].Bytes)) { complete++; }
            else if (bytes.AsSpan().SequenceEqual(s_payloads[i].OriginalBytes)) { original++; }
            else { partial++; }
        }
        var temporary = Directory.GetFiles(directory, ".wolvenkit-tweakxl-*.tmp").Length;
        Require(File.ReadAllBytes(Path.Combine(directory, "sentinel.keep")).AsSpan().SequenceEqual(s_sentinel), "Sentinel changed.");
        Require(Directory.GetFiles(directory).Length == complete + original + partial + temporary + 1, "Unexpected file in benchmark directory.");
        return new Inventory(complete, original, partial, missing, temporary);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        Require(sorted.Length == Repetitions, "Median must use exactly three measured repetitions.");
        return sorted[sorted.Length / 2];
    }

    private static async Task Save<T>(string name, T value) =>
        await File.WriteAllTextAsync(Path.Combine(s_results, name), JsonSerializer.Serialize(value, s_json), s_utf8);

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed record Payload(string Name, string Text, byte[] Bytes, byte[] OriginalBytes);
    private sealed record Inventory(int Complete, int Original, int Partial, int Missing, int Temporary);
    private sealed record Run(string Scenario, string Approach, int Repetition, bool Warmup, double ElapsedMs, double RecordsPerSecond, string Directory, Inventory Inventory);
    private sealed record CancellationRun(string Scenario, string Approach, int Repetition, int Completed, double RequestToObservedMs, double TotalMs, string Directory, Inventory Inventory);
    private sealed record StagedCancellation(string Scenario, int Repetition, double RequestToObservedMs, bool TemporaryObserved, bool DestinationPreserved, int TemporaryFilesAfter, string Directory);
}
