# Compiler & CI warning policy

Measured on the `cli-mcp-ci` run of 2026-11-13 (`main` @ v2.4.16, run 43443391616):
279 `##[warning]` annotations in the log, **130 unique warning sites** (119 in the app
project, 11 in `tests/`). The Actions summary page shows far fewer because GitHub caps the
number of annotations it renders per step, and because the app project is compiled twice
(`Kirin-Tool.csproj` directly and again as a project reference of `KirinTool.Cli`), which
duplicates every line in the raw log.

Root cause is not new code going bad: the project turned on `<Nullable>enable</Nullable>`
after most of the code was written against non-annotated APIs, so the compiler started
reporting pre-existing assumptions in bulk. Nothing here gates a build — the project does
not set `TreatWarningsAsErrors` globally.

## Batches

| Batch | Scope | Status |
| :--- | :--- | :--- |
| **A** | Real defects + one-line hygiene: `CS4014` (unawaited `Dispatcher.UIThread.InvokeAsync`), `CS0168` (unused `ex`), `CS0618` (`OpenFolderDialog`, `ToggleButton.Checked/Unchecked`, `IClipboard.GetTextAsync`), `CA2022` (inexact `Stream.ReadAsync` in `VcomFlasher`), `CA1416` (`File.SetUnixFileMode` in tests), the `PropertyChanged` family (`CS0108`/`CS8612`/`CS8618`/`CS8622`: hand-written `INotifyPropertyChanged` declared non-nullable and shadowing `AvaloniaObject.PropertyChanged`) | done in 2.4.17 |
| **B** | Nullable-reference leftovers on signatures: `CS8625` (`string op = null`, `IProgress<string> progress = null` …), `CS8618` non-nullable fields/properties set in `InitializeComponent`-style paths, `CS8600/CS8602/CS8603/CS8604/CS8620`. Touches `FastbootClient`, `ProcessRunner`, `VcomFlasher`, `UsbDownloadService`, `FirmwareUnlocker` — the flashing hot paths, hence its own commit and a full `tests/suites.txt` run | done: 76 unique sites → 0 (app project; tests were already 0). Baseline measured locally as 76 unique sites / 160 raw compiler lines; the raw count is higher than the site count because some lines carry two arguments (e.g. `EditEntries(model, regionVendor)`). Nullable tuple/delegate payloads (`string? Source`, `IReadOnlyList<string?>? argv`) were widened end-to-end instead of `!`-suppressing; `!` is only used where an invariant is genuinely invisible to the compiler (ctor-ordered init, success-gated payloads) and is always commented. **Done in 2.4.18** — the cleared nullable codes are gated from there on. |
| **B+** | Drop the four hand-written `INotifyPropertyChanged` implementations in `MainWindow` / `ProcessDialogUapp` / `PartitionSelectorDialog` / `FastbootPartitionSelector` and use `AvaloniaObject.RaisePropertyChanged` instead of carrying `new event …?` forever | open (refactor, needs headless UI suites) |

Batch A deliberately used the smallest correct change (`public new event
PropertyChangedEventHandler? PropertyChanged;` on `AvaloniaObject` subclasses, plain
nullable event on the POCO models) so behaviour is untouched; suppressing the warning
with `#pragma` was not used anywhere.

## Gate

`Directory.Build.props` (tree-wide, so the app, `KirinTool.Cli` and every `tests/*` harness are
covered — a gate in one csproj would leave the rest free to accumulate the same warnings) sets

```xml
<WarningsAsErrors>CS4014;CS0168;CA2022;CA1416;CS8600;CS8602;CS8603;CS8604;CS8612;CS8618;CS8620;CS8622;CS8625</WarningsAsErrors>
```

Codes that batch A brought to zero may not regress; the nullable set (`CS86xx`) joined the gate in
2.4.18, only once batch B had cleared its last site — gating a half-cleared category only teaches
people to add `!`. Batch B's own residue lived in the test harnesses (two `CS8620` tuple-nullability
mismatches in `tests/UsbRedlineGate` and `tests/UsbUpdateUi`), which is why the gate moved out of a
single csproj: the app project was clean while the suites were not.

`AVLN3001` is deliberately not gated: it is Avalonia's "won't be reachable via the runtime loader"
notice for the three dialogs that are constructed in code with parameterized constructors. It is not
a defect, so it is not gated — but CI *does* print it (3 sites, 6 lines because the app builds twice),
so an annotation-free run is not achievable without lying to the XAML compiler.

## How to measure locally

```sh
dotnet build Kirin-Tool.csproj -c Release -p:NuGetAudit=false 2>&1 \
  | grep -oE 'warning (CS|CA)[0-9]+' | sort | uniq -c | sort -rn
```

`dotnet build` prints every site once; CI logs print them twice (see above), so compare
counts from a local build, not from a workflow log.

Baseline after batch B (2.4.18): **0 CS/CA warnings anywhere in the tree**; the only annotations left
are `AVLN3001 ×3`. `KirinTool.Cli` compiles the app sources through a project reference, so a clean app
build implies a clean CLI build; the `tests/*` harnesses are separate projects and are verified by
running `tests/suites.txt` (or by reading the CI log, which builds every one of them).

## Platform noise (not ours, still worth clearing)

* `Node.js 20 is deprecated … forced to run on Node.js 24` — action majors were bumped
  instead (`actions/checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`,
  `download-artifact@v8`, all `node24`). Requires a runner >= 2.327.1; GitHub-hosted is fine.
* `ubuntu-latest will migrate to Ubuntu 26 (Oct 19, 2026)` — every job now pins an explicit
  image (`ubuntu-24.04`, or the `ubuntu-24.04`/`ubuntu-26.04` matrix for the deb gate).
  A silent migration would change what the packaging gate proves without a commit saying so.
* The AppImage/release container stays `ubuntu:20.04` on purpose — see
  [portable-appimage.md](portable-appimage.md).
