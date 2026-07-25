# Project-specific Rules for ActualLab.Kvasar

ActualLab.Kvasar is a pure-managed, encrypted, file-system-based key-value store for .NET
(Bitcask model: an in-RAM hash index over an append-only, encrypted, paged log). It is meant
to replace SQLite + SQLCipher as ActualChat's client-side cache, so **speed and a small
dependency footprint are the whole point** — see "Zero dependencies" below.

**YOU MUST READ [CODING_STYLE.md](CODING_STYLE.md) before writing or
modifying any C# code.** It's not optional. This project
**deviates from standard .NET conventions** on several points (notably:
no `Async` suffix on async methods; no XML docs on members; mixed brace
style; explicit accessibility modifiers everywhere). Default instincts from
elsewhere will produce code that gets rejected. If you haven't opened that
file yet in this session, stop and read it now.

**You MUST NOT write a single comment, docstring, or XML doc** without
first reading [CODING_STYLE.md → "Regular comments, docstrings, XML
documentation comments"](CODING_STYLE.md#regular-comments-docstrings-xml-documentation-comments).
You have a strong tendency to over-comment and to restate what the code
already says; that section explains exactly when a comment is justified
and when it isn't. Re-read it any time you're tempted to add a `//` or `///`.
In particular: **type-level `/// <summary>` is allowed (≤5 lines) when the name
isn't self-explanatory; member-level `///` docs are banned** — use a short `//`
comment at the top of the body only when the signature can't carry the meaning.

# Zero dependencies (CRITICAL)

The core library `src/ActualLab.Kvasar` depends on **exactly one** package:
`System.IO.Hashing`. Keep it that way. Kvasar's value proposition is being a
lean, fast, self-contained SQLite replacement, so **do not add package or
project references to the core library** — most emphatically not `ActualLab.Core`,
which drags in a large transitive graph (MemoryPack, MessagePack, Newtonsoft.Json,
System.Reactive, Ulid, ZString, the whole `Microsoft.Extensions.*` stack).

When you need a helper that already exists in `ActualLab.Core`, **port the minimal
implementation** into Kvasar rather than taking the dependency. Precedent:
`Internal/Varint.cs` ports Core's SIMD/branchless `SpanExt.WriteVarUInt64` /
`ReadVarUInt64` fast paths (keeping a non-throwing, torn-tail-safe `TryRead`).
Because of this carve-out, the Fusion-only helpers named in CODING_STYLE.md —
`FilePath`, `TaskCompletionSourceExt`, `SilentAwait`/`ResultAwait`, `LogFor` —
do **not** apply here; use BCL equivalents.

Benchmark/test projects may reference whatever they need (the benchmark uses
`sqlite-net-sqlcipher` as the baseline) — the rule is about the shipped library only.

# Git workflow — don't branch unless asked

Commit your changes directly to the default branch (`main`). **You typically
should NOT create a feature branch in this repo unless the user explicitly asks
for one.** Small, self-contained changes (docs, fixes, tweaks) belong on
`main`; a needless branch only adds a merge step later. Do not commit or push
unless the user asks.

# Design docs — read before changing internals

This repo is young but its internals are contract-driven. Before touching a
module, read:

- [`docs/SPEC.md`](docs/SPEC.md) — the product spec (public API, `KvasarOptions`,
  storage architecture, on-disk formats, §14 test requirements).
- [`docs/DESIGN.md`](docs/DESIGN.md) — frozen internal module contracts
  (M1 Crypto, M2 Paging, M3 Log, M4 Index, M5 IndexFile), key invariants, and
  known limitations.
- [`docs/BENCHMARKS.md`](docs/BENCHMARKS.md) — methodology and current numbers
  vs SQLCipher. If you change a hot path, re-run and update this.

**Reuse before adding.** A new helper that duplicates an existing internal one is
a defect. Look through `src/ActualLab.Kvasar` (Crypto/, Paging/, Log/, Index/,
Internal/) first. If you introduce something broadly useful, note whether it
belongs in a shared `Internal/` location rather than a single call site.

# Multi-targeting

The library multi-targets via the `UseMultitargeting` MSBuild property (defined in
the root `Directory.Build.props`):

- **Default** (`UseMultitargeting` unset/false): single-target **net10.0**.
- **`-p:UseMultitargeting=true`**: multi-target **net10.0;net9.0**.

net10.0 is the default/primary target; net9.0 is the floor. Any code you add must
compile on both — guard newer-BCL usage with `#if NET10_0_OR_GREATER` when needed,
following Fusion's conditional-compilation patterns.

# Building & testing

```bash
# Default: single-target net10.0
dotnet build ActualLab.Kvasar.slnx -c Release
dotnet test  ActualLab.Kvasar.slnx -c Release

# Validate both target frameworks
dotnet build ActualLab.Kvasar.slnx -c Release -p:UseMultitargeting=true
dotnet test  ActualLab.Kvasar.slnx -c Release -p:UseMultitargeting=true
```

Keep the build **warning-free** (`AnalysisMode=AllEnabledByDefault`; the curated
`<NoWarn>` list lives in `Directory.Build.props`). Add a suppression only with a
one-line comment explaining why, matching the existing entries.

# Async I/O

SQLite is synchronous for historical reasons; Kvasar should **not** be. Prefer
async, cancellable I/O (`FileStream` opened with `useAsync: true`,
`ReadAsync`/`WriteAsync`) on the storage paths, and flow `CancellationToken`
through. Follow the CODING_STYLE.md rules for async: no `Async` suffix,
`.ConfigureAwait(false)` in library code.

# Temporary files

Do not create temporary files in the project root. Use a `tmp/` folder (gitignored)
or the session scratchpad for scripts, debug output, and throwaway artifacts.

# ActualLab.Fusion source & docs (reference only)

ActualLab.Kvasar lives in the ActualLab family and follows its conventions, but
does not depend on Fusion at runtime. Fusion is still the best reference for
idiomatic ActualLab code and for helpers worth porting:

- **`fusion-docs` MCP** — when wired up (look for `mcp__fusion-docs__*` tools),
  it searches/reads the Fusion **docs** and **source**. Prefer it over guessing.
- **Sibling checkout** — the full Fusion source is available as a sibling project
  (e.g. `D:\Projects\ActualLab.Fusion` on Windows). Read the real source when you
  need to port a helper or match a pattern.
