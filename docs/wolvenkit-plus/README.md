# WolvenKit Plus: TweakXL bulk exporter

Status: 2026-10-13. The published 2.31 milestone records a successful WolvenKit
9.1.0 desktop build and 52 passing focused tests. All 193,354 source records were
exported, including 121,037 inline records. Independent audits found no missing
record names, type mismatches, YAML syntax failures, array-length mismatches,
or source-value mismatches within the coverage described below. These are saved
milestone results; they do not establish in-game TweakXL compatibility.

## Source and branches

- Fork: https://github.com/saltypigloaf/WolvenKit-Plus
- Upstream: https://github.com/WolvenKit/WolvenKit
- Stable base: `9.1.0`, commit `4d1716b1755dcadead2815b3ec356fa61f30861c`.
- Development branch: `feature/tweakxl-bulk-export`.
- Original recovery branch: `recovery/tweakxl-exporter-20261013`, commit
  `359b8d448ebce9f2af188b7c3b1a0b626bcefff1`.

The recovery branch preserves the 17 locally changed or added exporter files
against their original May checkout base, `b75fde6a8f2d8dd1fddba06f1efd94ce35133610`.
The development branch applies those changes to 9.1.0. Upstream integration in
the pane factory and service registration merged without conflicts.

## Build and test

Use Windows x64 and the .NET 10 SDK. SDK `10.0.401` was verified; upstream's
`global.json` specifies `10.0.302` with `latestFeature` roll-forward.

Run these commands from the repository root:

```powershell
dotnet build .\WolvenKit\WolvenKit.csproj --configuration Debug --disable-build-servers -p:Platform=x64 -p:UseSharedCompilation=false -p:RestoreDisableParallel=true -m:1 -nr:false

dotnet test .\Tests\WolvenKit.UnitTests\WolvenKit.UnitTests.csproj --disable-build-servers --filter "FullyQualifiedName~TweakXL|FullyQualifiedName~TweakDB" -p:Platform=x64 -p:UseSharedCompilation=false -m:1 -nr:false
```

The desktop build output is under
`WolvenKit/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`.

During the earlier port and array-fix milestone, the first desktop build exposed
a newline in `InterceptorsPreviewNamespaces`
that broke compiler arguments and produced CS1504 errors. Keeping that property
on one line fixed the failure without changing its namespace entries or
disabling analyzers. The successful desktop rebuild reported zero warnings and
zero errors. The earlier test compilation emitted nullable warnings from
unchanged upstream RED4 sources; all 50 selected tests executed and passed at
that stage. The subsequent resource/rotation fix raised the saved total to 52.

## Current exporter behavior

- The Tweak Browser exports the full loaded record list, independently of its
  search and display filters.
- Each exported record gets a YAML file, grouped by its first namespace segment.
- Inline records are excluded by default and can be enabled with
  **Include inline records**.
- Records whose names cannot be resolved are skipped even when inline export
  is enabled. Skips and individual failures are included in `export_report.yaml`.
- Export supports progress, cancellation, overwrite control, and filename
  collision handling. A failed record does not abort the remaining export.
- Single-record and bulk export share the same record serializer.
- A missing localization text entry preserves the original numeric `LocKey#`
  reference instead of failing the entire record's export.
- Typed records with no properties retain their record name and `$type` mapping.
- Empty CName, TweakDBID, and localization entries retain their array positions.
- Record type lookup uses the existing ID index instead of scanning every record
  for each exported file.
- Serialization reports distinct fallback and unsupported CLR value types for
  each export attempt; see the diagnostic contract below.

This is a record exporter. It does not separately dump all standalone flats,
queries, or group tags, and it does not recursively embed referenced records
inside their parents. It should not yet be described as a complete database dump.

## Actual 2.31 database validation

The installed executable reported product version `2.31`. A read-only diagnostic
loaded `r6/cache/tweakdb_ep1.bin` and WolvenKit's bundled hash/name tables, then
called the actual bulk export service using the game's English localization
archives. Output went to a separate development directory.

- Database size: 48,192,743 bytes.
- Database SHA-256: `89c7ee678c1366d4c289edc78beaa60ce3d64bf44b300fc3902adc94f6ac14c5`.
- Records: 193,354; all record names resolved.
- Names matching the exporter's inline pattern: 121,037.
- Flats: 3,306,462; queries: 459; group tags: 5,862.

The sample selected the ordinally first record from each `(record type, inline
status)` group: 1,009 records covering 843 record types, including 334 inline
records. With inline export enabled, all 1,009 exported with no failures or
skips, and all generated record files parsed successfully with YamlDotNet.
This verifies YAML syntax, not TweakXL runtime compatibility or value completeness.

Before the localization fix, the same sample failed on
`scanning.sample_reveal_item`: its localization key had no matching text entry.
Preserving the numeric key fixed that failure. Regression tests cover scalar
and array localization keys with and without text, plus explicitly enabled
inline export, including nested inline names.

### Full record export

The first full audit exposed two errors that the exporter's successful-file
count did not detect:

- Eleven records (eight inline) had no properties, so their YAML contained only
  a top-level `$type` and lost the record name. Typed empty records now retain
  the enclosing named mapping.
- In 408 arrays, empty CName or TweakDBID entries were omitted, shifting later
  entries. Empty array positions are now written explicitly; zero localization
  keys are preserved too. The representation follows the upstream
  [TweakXL value reader](https://github.com/psiberx/cp2077-tweak-xl/blob/f8da6be4fb7b8340d5744d822a85de1400f2cafb/src/App/Tweaks/Declarative/Yaml/YamlReader.Values.cpp).

The full run also exposed repeated linear scans in `TweakDB.GetRecordType`.
Using the existing record-ID dictionary removes those scans. A fresh complete
export after all fixes took about 2 minutes 19 seconds on the validation machine.

The final independent audit checked every generated record file:

- 193,354 requested, exported, and parsed records; zero failures or skips.
- 121,037 inline records present; 843 record types represented.
- Zero missing, duplicate, or unknown record names, and zero type mismatches.
- 854,451 array-valued properties checked; zero array-length mismatches.
- Zero unresolved `<TDBID:...>` reference placeholders observed.
- 155,220,546 bytes of record YAML, excluding the report, after the value-fidelity fixes below.

### Value and coverage audit

A subsequent independent comparison against the source flat pool found two
additional converter problems. Resource references whose path was not in the
bundled name tables became empty strings, losing their numeric hash. They now
use TweakXL's `ResRef(hash)` representation; empty references use `ResRef(0)`.
Euler angles used a display string; they now use a mapping with `roll`, `pitch`,
and `yaw`. Float scalars and array entries now use invariant-culture formatting.
The representation follows the same upstream TweakXL value reader linked above.

After re-exporting, the audit matched all 193,354 record identities and examined
all 3,230,398 resolved flat properties associated with existing records. It made
4,441,024 value comparisons, including array entries and vector/rotation
components, with zero mismatches. All 47,493 resource references were compared by
their numeric hash. Floats were compared by their 32-bit representation.

Coverage details matter when using this as reference material:

- The converter intentionally omits 190,184 empty CName scalars, 385,498 empty
  TweakDBID scalars, and 40,548 zero localization-key scalars. Empty entries in
  arrays are retained. The audit accounts for these omissions explicitly.
- There are 3,218 extra YAML properties supplied by WolvenKit's record model;
  they do not have a corresponding stored source flat and are outside the
  source-value comparison.
- 76,046 resolved flats are not associated with an existing record. Another
  18 flat names are unresolved. These, 459 queries, and 5,862 group tags are
  outside this record export.

The saved published milestone reports 52 passing focused TweakXL/TweakDB tests,
including unknown/empty resource hashes, Euler-angle mappings, and decimal
formatting under `de-DE`, plus a successful desktop build. Machine-readable
results are in
[validation-2.31.json](validation-2.31.json).

## Serializer diagnostics

The bulk report's existing `fallbackValueTypes` and `unsupportedValueTypes`
fields now contain sorted, distinct CLR full type names observed during that
export. They are type inventories, not occurrence counts or a completeness
audit. Null values are identified as `<null>`.

- **Supported:** explicit writers for CName, TweakDBID, resource references,
  localization keys, vectors, Euler angles, floats, integers, strings, booleans,
  and arrays of supported elements. Their existing YAML representation is
  preserved; intentionally omitted empty scalars are not diagnostics.
- **Fallback:** a value without an explicit writer whose override of `object.ToString()`
  supplies non-null scalar text. The text is preserved and its type reported.
  This does not prove that TweakXL accepts that representation or that the text
  preserves every component of a structured value.
- **Unsupported:** a null value, a value with only the default object/value-type
  string representation, a `ToString()` that returns null or throws, or a
  top-level TweakXL item with no writer. Serialization fails with its type in
  the error. Bulk export records that failure and continues with later records;
  the failed record's destination is not opened or overwritten. For example,
  a `CVariant` is now rejected instead of being written as its CLR type name.
  Hidden or generic methods named `ToString` do not count as overrides; the
  converter checks the virtual method it actually calls, including inherited overrides.

Scalar properties, standalone flat values, and array elements use the same
fallback classification. Each export owns its collector; the report receives
detached snapshots. Observations made before a record fails or the export is
canceled remain in the report, including fallbacks from an ultimately failed
record. Skipped records, records after cancellation, and properties after a
serialization failure have not been examined. A type can appear in both lists
if different instances produce different conversion outcomes.

Empty diagnostics do not establish complete source coverage or runtime
compatibility. The published 2.31 validation report predates this diagnostic
implementation; its historical unfinished-diagnostics limitation is retained.
The record snapshot and source-value audit have not been regenerated for this
change. Local synthetic-fixture validation is recorded separately in `HANDOFF.md`.

Local diagnostic validation on 2026-10-13 passed all 69 focused TweakXL/TweakDB
tests (the existing 52 plus 17 new diagnostic cases). The desktop Debug x64 build
also passed with zero warnings and errors using SDK `10.0.401`. These checks used
synthetic fixtures; the full source export and value audit were not rerun.

## Completed-file publication

Record YAML and `export_report.yaml` now write to exclusively created sibling
`.wolvenkit-tweakxl-<id>.tmp` files. Text/encoding buffers finish first, then the
file is flushed to disk and closed. A same-directory rename publishes the
completed file, enforcing the requested overwrite policy at that final step.
The existing destination is not truncated while new content is being written.
The temporary filename does not extend an already long record filename.

Cancellation and handled write/finalization/publication errors remove only the
temporary file owned by that attempt. A racing destination is preserved when
overwrite is disabled. If cleanup itself fails, the exception identifies the
leftover path and retains both errors. Record failures remain per-record;
failure to publish the final report propagates and preserves the previous
report. Canceled runs still write their report with a non-canceled token.
If cancellation coincides with another failure (including temporary cleanup),
the report retains the failure and marks the run canceled, even on the last record.

Cancellation is checked immediately before publication. A rename that already
succeeds is not rolled back if cancellation arrives afterward. This protects
individual files, not an entire export as one transaction. Abrupt process exit
can leave a `.tmp` file; this change does not scan for or delete older leftovers.
Power-loss durability and network/cloud filesystem semantics were not tested.
The per-file disk flush adds I/O cost; full-corpus performance was not remeasured.

The implementation uses the .NET [move/overwrite API](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0)
and [disk-flush API](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0).
Keeping the temporary file beside the destination avoids a cross-volume move.

Local write-safety validation on 2026-10-13 passed all 89 focused TweakXL/TweakDB
tests: the previous 69 plus 20 new cases. Fixtures cover complete-byte
publication, cancellation, partial-write and finalization faults, competing
destinations, locked record/report replacement, cleanup errors, and canceled
report publication. The desktop Debug x64 build passed with zero warnings and
errors. The full source export and in-game validation were not rerun.

The subsequent combined review corrected virtual-method fallback detection and
cancellation reporting when another error occurs on the final record. Its local
fixtures passed all 102 focused tests (89 previous cases plus 13 new cases).
The desktop Debug x64 build also passed with zero warnings and errors.
Earlier 52/69/89-test results above remain historical milestone evidence.

## Further validation

1. Add standalone-flat and separate queries/group-tag exports
   if a complete database reference is required.
2. Validate a small exported set through TweakXL in game before treating the
   output as runtime-tested.
3. Review any reported fallback types before relying on their YAML semantics;
   implement explicit representations only with focused fixtures and evidence
   for the intended TweakXL format. Diagnostics alone are not a completeness audit.

The full 2.31 record export is complete. No in-game load test has been performed.
