# Screenshots

Screenshots live in **docs/images/**; the README references them by relative path (`docs/images/<file>`). Do not embed here or in the README until the F10 capture pass produces the files.

Requirements for all captures:

- **Real v2.4.13 UI** — capture from a build of this fork at tag `v2.4.13`. Do not reuse upstream or older-port screenshots; the UI text and dialogs differ (battery banner, backup line, gate wording).
- **Size 1600x1000** (device pixel ratio 1, or crop/scale to that aspect). Keep text legible after GitHub's downscaling.
- **Font**: the app uses its default Avalonia font; in terminal captures use a plain mono font (e.g. DejaVu Sans Mono) at ≥14 px on a dark background.
- No personal data: redact serial numbers, PSIDs, IMEIs and account names before publishing.

| # | File | Content | Capture notes |
| :-- | :--- | :--- | :--- |
| 1 | `screenshot-main.png` | Main window, light theme, device connected in fastboot mode. | Device page visible with model/lock-state info; if the low-battery banner appears, prefer ≥50% SoC so the default (hidden) state is shown. |
| 2 | `screenshot-dark.png` | Main window, dark theme, same layout as #1. | Same device state as #1 so the pair is comparable; pick whichever theme the README leads with for #1. |
| 3 | `screenshot-extractor.png` | Extractor Tools with a loaded UPDATE.APP and per-partition progress (green/red rows). | Use a real Base firmware; capture mid-extract so progress colors are visible; no serial numbers in the title/info area. |
| 4 | `screenshot-flash-progress.png` | Flash progress dialog with per-partition status, including the pre-op backup line. | Must show the v2.4.13 backup line in the progress dialog; real write or a faithful staged run on the verified P40 Pro setup. |
| 5 | `screenshot-redline-confirm.png` | Redline confirmation dialog listing the refused partitions. | Trigger a plan containing redline partitions so the dialog lists exact names; capture the dialog, not the refusal log. |
| 6 | `screenshot-cli-doctor.png` | Terminal with `kirin-tool-cli doctor` (and optionally `flash-ota --json` plan output). | 1600x1000 terminal window, dark background, mono font; JSON must show the `gate` field; redact serials. |

Status: **not captured yet** — this file defines the set; images arrive via docs/images/ in the F10 pass.