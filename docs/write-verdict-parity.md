# Write/verdict parity — D-01 / D-02 / D-03 (lane G5)

Status: **implemented** — branch `feat/write-verdict-fixes`, base `2ade54b` = v2.4.14.

Lane check-list (each section was written to disk as it was finished):

- DONE-1 document skeleton → this file
- DONE-2 fix D-02 (`applied` comes from the device answer) → `Cli/Commands/DeviceWriteCommands.cs`, `Cli/Commands/WriteCommands.cs`, `tests/WriteGates`
- DONE-3 fix D-03 (CLI `write-sn` reads back) → `Cli/Commands/DeviceWriteCommands.cs`, `tests/WriteGates`
- DONE-4 fix D-01 (oeminfo split + GUI through the gate) → `Services/Capabilities/CapabilityRegistry.cs`, `Services/WriteOperations/DeviceWriteOps.cs`, `Cli/Commands/DeviceWriteCommands.cs`, `Cli/CliRunner.cs`, `MainWindow.axaml.cs`
- DONE-5 full build + every suite green, parity gate green (369 → 386 checks), `StableParity` hash refreshed
- DONE-6 docs (`docs/cli-mcp.md`, `README.md`, `README.zh-CN.md`, `docs/INDEX.md`; `docs/debt-register.md` untouched)

This file records three real defects found while building the debt register, the code fix for
each of them, and what "the three faces agree" means once the fix is in. It is intentionally
written as an audit artefact: every claim below names the file and line it was checked against.

## 0. Honest position on v2.4.14

All three statements below were **false in the released v2.4.14**. They are not documentation
gaps, they are behavioural gaps:

| Claim the project made in v2.4.14 | Reality in v2.4.14 |
| :--- | :--- |
| "every device write goes through the approval gate and one shared service entry" | the GUI flashed `oeminfo` straight from `OemInfoService`, and the registry advertised `oeminfo` as `PlanOnly` |
| "the JSON a client reads is the real outcome" | several write commands emitted a literal `applied: true` regardless of what the device answered |
| "GUI and CLI agree on the NVE serial number verdict" | the GUI compared a read-back, the CLI printed "rewritten" off the exit code alone |

v2.4.14 therefore stays as released; the corrections land in the next release and this file is
the reference for them.

## 1. D-01 (high) — GUI wrote oeminfo directly, and the registry lied about the write kind

**Evidence (v2.4.14)**

- `MainWindow.axaml.cs:2613` `_oemInfoService.ConvertOemInfo(...)` and `:2693`
  `_oemInfoService.FlashConvertedOemInfo(filePath)` — the flash button path reached
  `FastbootClient.FlashOemInfo` without ever producing a `WriteTicket`.
- `Services/Capabilities/CapabilityRegistry.cs:105`: one `oeminfo` row with
  `CapabilityWriteKind.PlanOnly`, no `Approval`, no `ServiceMethod`.

**Root cause.** The row was registered when `oeminfo` really was plan-only (the CLI can only
build the image locally). The GUI later gained a *flash* button, but it was described as the
same capability, so `tests/SurfaceParity` compared `PlanOnly` against a `PlanOnly` CLI command
and passed. `oeminfo` is a redline partition (its HMAC is keyed by device secrets) yet the one
face that could actually write it was the unregistered one.

**Fix.**

1. `oeminfo` is split into two capabilities:
   - `oeminfo-convert` — `PlanOnly`, CLI `oeminfo` / MCP `oeminfo`, GUI `BtnConvertOem_Click`;
     writes a file on the host, nothing on the device.
   - `oeminfo-flash` — `WriteKind=Write`, `Approval=RedlineTypedConfirm`,
     `Reversibility=Irreversible`, `ServiceMethod=DeviceWriteOps.FlashOemInfoAsync`, with all
     three faces (CLI `oeminfo-flash` / MCP `oeminfo_flash` / GUI `HandleOemInfoFlash`).
2. `DeviceWriteOps.FlashOemInfoAsync(WriteTicket, path)` is the only device entry for an
   oeminfo write; `OemInfoService.FlashConvertedOemInfo` is gone so there is nothing left to
   call around the ticket.
3. The GUI handler asks `RequestGuiApprovalAsync(ApprovalRequests.For("oeminfo-flash", ...))`
   and then calls `_deviceWriteOps.FlashOemInfoAsync(admission.RequireTicket(), path)`.

**Verification.** `tests/SurfaceParity` now sees a `Write` capability named `oeminfo-flash` and
applies H1/H2/H3 to it (three faces, approval kind, reachable gate, present service method).
Nothing was added to `gui-ui-only.txt`.

## 2. D-02 (high) — `applied: true` was a constant in the write JSON

**Evidence (v2.4.14)**

- `Cli/Commands/DeviceWriteCommands.cs:118-135`: `WriteResult` hard-coded
  `applied = true, gate = "applied", exitCode = ExitCodes.Ok`.
- `Cli/Commands/WriteCommands.cs:310`: flash-ota wrote `applied = true` even when `ok == 0`.

**Root cause.** The JSON was written next to the human line, not next to the device answer, so
the helper took the happy path as a default. A failed `flash` still exited non-zero, but the
machine-readable field said "applied".

**Fix.** `WriteResult` now takes a `WriteReport` that only the call site can construct:

| device answer | applied | gate | exitCode |
| :--- | :--- | :--- | :--- |
| command ran and the device accepted it | `true` | `applied` | `0` |
| command failed / device rejected it | `false` | `failed` | 3 or 5 (existing convention) |
| some of several writes succeeded (flash-ota / flash-xml / frp steps) | `true` (count > 0) | `partial` | `3` |
| command accepted but no `OKAY` evidence (oeminfo write) | `false` | `unconfirmed` | `3` |

There is no default value and no overload that omits the verdict. Every call site
(`reboot`, `reboot-usb-update`, `write-sn`, `frp-remove`, `unlock-bootloader`, `usb-testpoint`,
`oeminfo-flash`, plus the two flash paths) passes what it observed.

**Verification.** `tests/WriteGates` runs the commands against a stub fastboot that answers
`FAILED` and asserts `applied == false` and `gate == "failed"`; the success case asserts
`applied == true` with `gate == "applied"`. No hardware is involved.

## 3. D-03 (medium) — CLI `write-sn` had no read-back

**Evidence (v2.4.14)**

- `Cli/Commands/DeviceWriteCommands.cs:200-213`: the human line was
  "serial number rewritten (no read-back check is available on this path)".
- `MainWindow.axaml.cs` (v2.4.14) already used `Services/Verdict/NveWriteVerdict.cs`.

**Root cause.** The verdict layer was written for the GUI only; the CLI command predates it and
was never revisited. Two faces, two conclusions for one capability.

**Fix.** The CLI command now uses the same `NveWriteCheck.Compare` the GUI uses:

- reads `nve:SN` back after the write, through the same client the write used;
- JSON gains `verified`, `reason`, `readBack`, `expected`;
- exit 0 **only** when `verified == true`; otherwise `exit 3` with the existing reason tokens
  (`command_failed` / `readback_unavailable` / `readback_mismatch:<value>`);
- the human line says "Write Not Confirmed" plus the read-back value unless the verdict is
  verified.

**Verification.** `tests/WriteGates` drives the command in-process against a stub fastboot:
write-fails, read-back-mismatch, read-back-unavailable and verified cases each assert their JSON
fields and exit code.

## 4. Three-face consistency: what the gate now enforces

| capability | GUI | CLI | MCP | gate |
| :--- | :--- | :--- | :--- | :--- |
| `oeminfo-convert` | `BtnConvertOem_Click` (convert only) | `oeminfo` | `oeminfo` | plan-only, exit 4 |
| `oeminfo-flash` | `HandleOemInfoFlash` | `oeminfo-flash --yes --allow-redline` + typed phrase | `oeminfo_flash` with `confirm: true` + out-of-band GUI approval | `RedlineTypedConfirm` / irreversible |
| `write-sn` | read-back verdict | read-back verdict | same command table | `RedlineTypedConfirm` |

Rules the harness now pins (no document can substitute for them):

1. `tests/SurfaceParity` — every declared capability exists on the face it claims, write
   capabilities have all three faces, name their shared service method, and reach the gate from
   the GUI handler (H1/H3), plus `DeviceWriteOps` methods all demand a `WriteTicket` (H7).
2. `tests/WriteGates` — a device failure can never produce `applied: true`.
3. `tests/VerdictHonesty` — the read-back comparison itself (unchanged, still the only thing
   that may call an NVE write verified).

## 5. Test plan

| harness | what it proves for this lane |
| :--- | :--- |
| `SurfaceParity` | `oeminfo-flash` has GUI+CLI+MCP, `ApprovalKind`, `ServiceMethod`, no whitelist entry, GUI handler reaches the gate |
| `WriteGates` | `applied/gate/exitCode` follow the device answer for every write command; `write-sn` read-back verdict |
| `VerdictHonesty` | `NveWriteCheck.Compare` semantics unchanged (verified only on identical read-back) |
| `ApprovalGate` | registry shape for the new write capability; typed-confirm phrase |
| `StableParity` | `MainWindow.axaml.cs` change is approval-wiring only, no wire command/timing change |
| `McpProtocol` | `oeminfo_flash` requires `confirm:true` and refuses without a GUI to ask |

## 6. Blast radius / notes for the merge

- `docs/debt-register.md` is **not** touched here (another lane owns it); D-01/D-02/D-03 are
  closed by this branch and should be struck there when the lanes merge.
- `docs/cli-mcp.md`, `README.md` and `README.zh-CN.md` gain the `oeminfo-flash` command/tool and
  the new JSON fields.
- `tests/StableParity/boundary.json` hash is refreshed with the reason "approval wiring only,
  no wire command/timing/retry change".
- Files touched (only these): `Cli/CliRunner.cs`, `Cli/Commands/DeviceWriteCommands.cs`,
  `Cli/Commands/WriteCommands.cs`, `MainWindow.axaml.cs`, `Services/Capabilities/CapabilityRegistry.cs`,
  `Services/OemInfoService.cs`, `Services/WriteOperations/DeviceWriteOps.cs`, `tests/WriteGates/Program.cs`,
  `tests/StableParity/boundary.json`, plus this file and the doc/README edits above.

## 7. Result (measured on this branch)

| check | before (v2.4.14) | after |
| :--- | :--- | :--- |
| `tests/SurfaceParity` | 369 checks, `oeminfo` declared PlanOnly while the GUI wrote it | **386 checks green**, 23 capabilities / 22 CLI commands / 22 MCP tools, 0 GUI-only |
| `tests/WriteGates` | no assertion on `applied` for a failing device | **59 checks green** (reboot fail/ok, write-sn x4, oeminfo-flash gate refusal, plus the pre-existing gate/plan checks) |
| `tests/StableParity` | boundary hash of `MainWindow.axaml.cs` | green, hash recomputed (approval wiring only) |
| every other suite in `tests/suites.txt` | — | green (`dotnet build`: 0 errors) |

Concretely, the machine-readable contract for a failed write is now:

```json
{ "command": "reboot", "capability": "reboot", "dryRun": false,
  "applied": false, "gate": "failed",
  "reason": "fastboot reboot failed: FAILED (remote: reboot not allowed)",
  "result": { "success": false, "output": "FAILED (remote: reboot not allowed)" },
  "exitCode": 3 }
```

and for the NVE serial number (read-back mismatch — the case the GUI already rejected):

```json
{ "command": "write-sn", "capability": "write-sn", "applied": false, "gate": "failed",
  "reason": "readback_mismatch:OLDSERIAL7",
  "verified": false, "expected": "NEWSERIAL9", "readBack": "OLDSERIAL7",
  "exitCode": 3 }
```

`exit 0` for `write-sn` now requires `verified: true`, i.e. an identical read-back — the same evidence
the GUI requires, produced by the same `NveWriteCheck.Compare`.
