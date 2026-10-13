# WolvenKit Plus: TweakXL bulk exporter

Status: 2026-10-13. The recovered exporter compiles on WolvenKit 9.1.0, and its
35 focused tests pass. A 1,009-record sample from an actual Cyberpunk 2077 2.31
database exports successfully, including 334 inline records. Full database
completeness and in-game loading have not yet been verified.

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

dotnet test .\Tests\WolvenKit.UnitTests\WolvenKit.UnitTests.csproj --disable-build-servers --filter "FullyQualifiedName~TweakXL|FullyQualifiedName~TweakDBServiceTests" -p:Platform=x64 -p:UseSharedCompilation=false -m:1 -nr:false
```

The desktop build output is under
`WolvenKit/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`.

The first desktop build exposed a newline in `InterceptorsPreviewNamespaces`
that broke compiler arguments and produced CS1504 errors. Keeping that property
on one line fixed the failure without changing its namespace entries or
disabling analyzers. The successful desktop rebuild reported zero warnings and
zero errors. The earlier test compilation emitted nullable warnings from
unchanged upstream RED4 sources; all 35 selected tests executed and passed.

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

## Next validation

1. Export the full 2.31 record set with inline records enabled and retain the
   report. The successful sample does not prove that every record exports.
2. Compare actual flat values and cross-record references against the YAML,
   especially for the previously reported missing inline items.
3. Validate a small exported set through TweakXL in game before treating the
   output as runtime-tested.
4. Resolve unsupported value handling and populate the currently unfinished
   fallback/unsupported-type diagnostics before treating the report as a
   completeness audit.

No in-game load test or full 2.31 export has been performed.
