# Native subtitle-policy verification

This tool compares Babelfin's Video default subtitle policy with Jellyfin's actual server implementation. It also checks Jellyfin's language normalization and measures the cached selection path. It loads the runtime assemblies without starting a server.

## Run

Use the .NET 10 SDK. Provide an official Jellyfin 12.2 runtime folder for Windows or Linux, then run these commands from the repository root. Replace `/path/to/jellyfin-runtime` with that folder:

```powershell
dotnet test plugin/tests/Babelfin.Engine.Tests/Babelfin.Engine.Tests.csproj --configuration Release
dotnet run --project tools/native-policy-check/NativePolicyCheck.csproj --configuration Release -- --runtime /path/to/jellyfin-runtime --parity-only
dotnet run --project tools/native-policy-check/NativePolicyCheck.csproj --configuration Release -- --runtime /path/to/jellyfin-runtime --inspect-normalization
```

Parity exits nonzero on a mismatch. The tool prints the loaded assembly identity and SHA256 so a result can be tied to its oracle. The runtime, generated output and captured server data are excluded from source control.

## Policy parity and normalization

The parity check compares Babelfin's subtitle policy with Jellyfin's `Emby.Server.Implementations` assembly, including the actual localization table and `MediaSourceManager.NormalizeLanguage`. Each run prints the loaded assembly identity, MVID and SHA256. Record these with the source revision and runtime being evaluated so the check can be repeated.

Cases cover all five subtitle modes, stream ordering, default/forced/external flags, text/image codecs, undefined tags, mixed language codes, regional preferences and missing preferences.

The normalization contract is deliberately separate from Babelfin's language matching:

| Preference | Native normalization |
| --- | --- |
| Missing/empty | Empty list |
| en / eng | eng |
| de / deu / ger | deu, ger in that order |
| pt-BR / pt-br | pt-br |
| pt-PT | pt-pt |
| Unknown spelling | Original spelling |

Regional culture names containing a hyphen remain regional. Other known cultures contribute all three-letter spellings in the native order. Unknown values and whitespace are not silently changed.

`VideoSubtitleSelector.Choose` accepts an optional normalized preference list. An explicit empty list means no language preference; null preserves its raw four-argument contract.

The checker prints example decisions for each subtitle mode with a forced/default Signs track and with only an unflagged Full track. These show the native policy's choices; Full subtitles are a separate explicit Babelfin choice.

## Regression coverage

The related engine regressions cover:

- Subtitle recalculation for the selected audio, neutral choices and Always show subtitles.
- Same-language alternatives to avoided default audio, with script/variety/region boundaries.
- Every stream recognition input, external subtitle additions, keyword/alias generation changes, source identity and date-only reuse.
- Cache expiry, the 512-entry bound and parallel cold/hot access.
- Deferred explanations that snapshot mutable caller collections and retain their original decision.
- The lock-free current-engine read and the adapter's actual normalized language list.

These policy comparisons do not measure application-wide test coverage. Re-run the checker against the source revision and Jellyfin runtime being evaluated.

## Benchmark measurements

Compare the same source revision and Jellyfin runtime when measuring changes. From the repository root, run the Release benchmark with your current tiering setting, then temporarily disable tiered compilation for comparison. The previous environment setting is restored afterward:

```powershell
dotnet run --project tools/native-policy-check/NativePolicyCheck.csproj --configuration Release -- --runtime /path/to/jellyfin-runtime --benchmark-only

$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet run --project tools/native-policy-check/NativePolicyCheck.csproj --configuration Release -- --runtime /path/to/jellyfin-runtime --benchmark-only
}
finally {
    if ($null -eq $previousTiering) {
        Remove-Item Env:DOTNET_TieredCompilation -ErrorAction SilentlyContinue
    }
    else {
        $env:DOTNET_TieredCompilation = $previousTiering
    }
}
```

These synthetic in-process workloads use 15 tracks. Each case warms for at least 500 ms, followed by seven samples. Most samples contain 20,000 decisions; Explain and uncached cases contain 5,000.

The cases compare deferred and eager trace creation, Explain generation, a fresh uncached selector, a cached decider with a fixed resolver, normalized native policy and the adapter using real localization. Times are medians of sample means, not individual-request percentiles. Allocations are measured on the current thread.

The adapter includes stream mapping and decision construction with a precomputed resolver result. It excludes resolver cache misses, server/network I/O and full playback workload. Results depend on the source revision, runtime and host; they are not a production playback latency guarantee.

Benchmark output includes runtime and host metadata. Keep raw output in private artifacts and remove machine details before sharing excerpts.
