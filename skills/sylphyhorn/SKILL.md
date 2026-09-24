---
name: sylphyhorn
description: Operate Windows virtual desktops and application windows with the SylphyHornPlus agent CLI. Use for switching desktops, moving or pinning windows, managing persistent application assignments, and changing SylphyHornPlus settings through its JSON interface.
---

# SylphyHornPlus

Use `sylphyhorn-cli` for local Windows operations. If it is not on PATH, use the
full path to `sylphyhorn-cli.exe` in the application's folder. `SylphyHorn.exe`
is the GUI, not a substitute CLI executable. In PowerShell, invoke a quoted
executable path with `&` and pass arguments separately.

## Discover only what the task needs

1. Run `sylphyhorn-cli spec` when the command is unknown. It returns the available
   command names and summaries.
2. Read the selected command, for example `sylphyhorn-cli spec window move`.
   `data.specification` contains arguments, constraints, omission behavior,
   prerequisites, effects, examples, and read-only source queries.
3. When current IDs or settings are needed, use
   `sylphyhorn-cli spec window move --resolve`. Reuse its source responses instead
   of immediately running the same queries again. Use static specifications when
   live values are unnecessary. Do not fetch specifications for every command.
4. Execute the requested operation using real values and interpret its response.
   Re-read affected state when the outcome is uncertain or another step depends
   on a value that may have changed.

`spec` accepts a command name and optional trailing `--resolve`, not that
command's execution arguments. Examples are argument arrays; replace placeholders
with observed values. Boolean options take `true` or `false`; flags take no
value. Omitted configuration options preserve existing values.

Static specifications need no GUI. Operational commands and live resolution need
a compatible GUI host in the same Windows user session and at the same elevation
level. A connection failure is not an empty desktop or application list.

For `--resolve`, inspect `data.resolution.status` and every relevant
`sources[].response`. The outer `success: true` means the specification was
retrieved, even if live resolution is `partial` or `unavailable`.
`not-applicable` means this command has no live source queries. Sources are
sequential observations, not a reserved or atomic snapshot.

## Select the correct object and operation

- Desktop IDs come from `desktop list`. Numbers are current 1-based positions;
  names match exactly, with duplicate names resolving to the lowest number.
  Use the selector that matches the user's intent rather than treating a number
  as a permanent identity.
- Window IDs come from `window list`, not from a PID or native window handle.
  Re-list stale IDs. Inspect `complete`, `unavailableCount`, and `movable` before
  claiming all matching windows were found or can be moved.
- Use `window move` for an existing window. Add `--follow` when the task calls
  for switching the visible desktop along with it.
- Use `app assignment set` for a saved rule governing future windows. Inspect
  `app list` identity, executable path, `canAssign`, and `reason`; packaged
  applications use their application identity rather than a shared host path.
  Saved rule IDs come from `app assignment list` and are distinct from window IDs.
- Use `app assignment apply --dry-run` with a selector from its specification to
  inspect applying saved rules to open windows. Applying requires enabled,
  active monitoring; read `app assignment status`. Dry-run does not reserve the
  candidates, create missing desktops, or switch desktops. Actual apply also
  does not create or switch desktops. A one-time move does not require enabling
  persistent automatic assignment.

## Interpret results before retrying

Read `success`, `command`, and `error.code`, not English diagnostic text alone.
Use the selected specification's `resultSchema` and `errors` to interpret fields
and recovery guidance; `resultSchema` is a type description, not JSON Schema.

An unconfirmed or partially failed mutation may already have changed state.
Inspect the affected state and any `error.results` before retrying; `retryable`
does not make blind repetition safe. This matters especially for creation,
relative switching, toggles, and batch assignment. Report partial outcomes as
partial rather than treating the exit code as proof of rollback.

For settings tasks, distinguish a saved preference from its runtime effect:
inspect fields such as `assignmentStatus` and `restartRequired`. Import replaces
settings rather than merging; read its specification before selecting
`--apply-desktops`. Reset is a separate user-requested operation, not a recovery
step for a failed command.
