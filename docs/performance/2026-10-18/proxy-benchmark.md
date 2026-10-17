# Proxy task benchmark and verification

All inputs are synthetic; no production process was terminated or restarted and no chat/account data was read. Binaries were compiled with the same swiftc -O and a fake CLI executing /bin/cat. The benchmark uses CODEX_PROVIDER_REAL_CLI, so the production default-CLI resolver is outside the measured route. The after benchmark main uses an unused fallback path solely to avoid linking profile resolution into this standalone compilation.

## Actual pump, 100 × 1 MiB non-target JSONL

Before/after were alternated for 3 trials; values below are medians. Source input file was identical. stdout went to /dev/null. /usr/bin/time -l measured the Swift proxy, not the official Codex CLI.

| Metric | Before | After |
|---|---:|---:|
| Wall time | 2.84 s | 0.50 s |
| Maximum RSS | 100,745,216 B | 12,566,528 B |
| Peak physical footprint | 117,523,008 B | 8,094,152 B |

Raw three-trial output: [proxy-benchmark-trials.json](proxy-benchmark-trials.json).

## Core buffer, 64 KiB chunks, two complete lines per run

Single paired measurements; elapsed excludes process startup.

| Payload per line | Before | After |
|---|---:|---:|
| 8 MiB | 3681.261 ms | 50.226 ms |
| 16 MiB | 17910.448 ms | 94.114 ms |

The after elapsed approximately doubles as input doubles, rather than repeating full-prefix scans. This workload is an optimization for scanning CPU, not a guarantee of lower peak memory: the standalone 16 MiB run showed RSS 27,230,208 B before versus 43,417,600 B after. Large legal lines intentionally remain resident, without hard truncation.

## Core rewrite, 1 MiB × 100, a local autoreleasepool per request

Single paired measurements. Before includes the previous proxy Data → String → rewriter Data → String → Data route; after uses Data directly.

| Method | Before | After |
|---|---:|---:|
| Non-target thread/get | 307.033 ms | 169.147 ms |
| Target thread/list | 285.780 ms | 312.974 ms |

The target request's allocation-free strict UTF-8 validation adds about 0.27 ms/request in this sample, preserving the original encoding acceptance boundary. Non-target requests avoid that validation and return the original Data.

## Verification

- Red: new Data API XCTest source typechecked against the original module and failed in five places because only the String API existed.
- Green: 10 AppServerLineBufferTests + 12 AppServerRequestRewriterTests + 4 CodexSharedHistoryProxyProcessTests actually executed via XCTest, 0 failures. The isolated package copied the repository test sources; process tests used a symlink to the new real debug product in /tmp/codex-switch-proxy-build/debug/CodexSharedHistoryProxy.
- Actual new debug proxy handled >2 MiB mixed stdin, CRLF normalization, escaped method, existing provider filter, Unicode, invalid bytes, blank lines, and an unterminated trailing CR successfully.
- Product CodexSharedHistoryProxy built successfully in /tmp/codex-switch-proxy-build.
- git diff --check passed for all four modified files.

## Remaining bounds

Foundation still parses each complete request, and legal large requests are not capped. CRLF normalization to LF is retained from the original buffer; byte-for-byte preservation applies to the line contents that are not rewritten. Newly measured resource reductions concern only the Swift proxy under the stated synthetic load. The official Codex CLI is outside this measurement.
