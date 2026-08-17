# NAG Import (Migrate NAG Database)

Documentation for importing a NAG database folder into EQLP: what is read, how fields map to EQLP triggers/overlays, what is deliberately dropped and how that is reported, and the design decisions behind the import.

Verified against **NAG v0.2.16 engine source** (`src/electron/threads/log-watcher.js`, `renderer.js`, `overlay.js`, `data/models/trigger.js`) and a real-world database (2,425 triggers, 16 overlays, 4 characters).

---

## 1. Design Principles

1. **Import what EQLP can express, faithfully.** Triggers are converted to behave as close to NAG as the EQLP runtime allows.
2. **Enablement and organization belong to the user.** Imported triggers **start disabled**. No per-character enable state is imported (see §7). The user enables and organizes triggers in the Triggers view after import.
3. **The report must be honest.** Anything not representable in EQLP is never silently lost — it either causes a skip with a reason, or is listed in the trigger's `DroppedFeatures` (visible in the trigger's Comments as `EQLP Import Notes: …` and in the HTML report).

---

## 2. Entry Point & Flow

Menu: **Migrate NAG _Database** (main window) → folder browser → progress window.

Implementation: `MainWindow.MigrateNagDbClick()` → `TriggerUtil.SelectNagDatabaseDirectory()` → `TriggerUtil.ImportNagTriggers(directory)`.

Steps in `ImportNagTriggers`:

1. Read `<dir>\trigger-database.json` (missing → error dialog).
2. Convert triggers off the UI thread: `Task.Run(() => NagUtil.ConvertTriggers(json, dir))` — returns `(nodes, results, metadata)`.
3. Import overlays first (so `ValidateOverlays()` can resolve trigger→overlay references): `<dir>\overlays-database.json` via `NagUtil.ConvertOverlays(json, out skippedFct)`. Audio file names are resolved through `<dir>\files-database.json` during step 2.
4. Create a **new timestamped root folder** `"NAG Ingest - yyyy-MM-dd HH:mm"` under the `Triggers` root and import all trigger nodes into it.
   - If earlier `NAG Ingest - *` roots already exist, the summary dialog warns that this import adds a **separate copy** (re-imports never update earlier imports; deleting old roots is up to the user).
5. Write an HTML report to `<EQLP log dir>\nag-import.html` and show the completion dialog (counts + warning notes + "Open Report" button).

Files in a NAG folder and their use:

| File | Use |
|---|---|
| `trigger-database.json` | Triggers, folders, tags — the main import source |
| `overlays-database.json` | Overlays (Timer/Alert imported; FCT skipped + counted) |
| `files-database.json` | Maps NAG `audioFileId` → file name for audio actions and missing-audio checks |
| `characters-database.json` | **Not read** — per-character enable state is intentionally not imported (§7) |
| `user-preferences.json` | Not needed |

---

## 3. Trigger Import

### 3.1 Phrases → Nodes

Each NAG **capture phrase becomes its own EQLP trigger node** (no regex-alternation combining, which caused named-group collisions). A trigger with *N* phrases produces *N* sibling nodes named `Name`, or `Name #1` … `Name #N` when multi-phrase. Every node stores the NAG `triggerId` in `TriggerNode.OriginalId`.

Phrase pattern conversion:
- `${var}` → `{c}` for `Character`, otherwise a named group `(?<var>.+?)`.
- Unhandled `{VAR}` tokens → named capture groups (`{LN}` → `\w+`; others → `.+?`).
- `{S}`, `{N}`, `{TS}` are left as **literals** in non-regex phrases (the EQLP runtime does not expand them there, which matches NAG's non-regex path — such phrases match literally upstream too).
- Regex phrases keep named groups; NAG group names map directly into EQLP's `matches` dictionary.

### 3.2 Folder Replication & Orphans

- The NAG `folders` array is replicated as folder nodes under the ingest root.
- Triggers whose `folderId` points to a folder that no longer exists in the DB ("dead" — **1,417 triggers** in the sample database) are placed in an **`Orphaned Triggers`** folder under the ingest root. This mirrors NAG's own startup behavior (`trigger-database.js` → `findOrphanedTriggers`), where orphans are auto-re-filed and the trigger stays live.
- Triggers with no `folderId` (the 3 predefined triggers, e.g. "Capture spell casting") are placed at the ingest root.

### 3.3 Deduplication Semantics

Within a single import, nodes are matched against the tree by **name + `OriginalId`** for trigger leaves (folder wrappers consolidate by name only). This prevents two distinct NAG triggers with the same display name from overwriting each other (verified: the sample DB has 0 duplicate `triggerId`s and 2,425 unique `(parent, name, triggerId)` identities → 0 triggers lost under this scheme; name-only dedup would have collapsed ~100).

`OriginalId` is persisted on nodes so a future in-place re-import could match nodes across imports. Today, each import still creates a fresh timestamped root, so re-imports duplicate the tree by design (user deletes what they don't need).

### 3.4 Action Type Mapping

| NAG type | Name | EQLP mapping |
|---|---|---|
| 0 | Text Overlay | `TextToDisplay` only. NAG's text display duration (seconds the text stays on screen before auto-hiding; default 6s in the public library) is deliberately **not** mapped to a timer — EQLP would render it as a visible countdown, not a text notification. Imported text persists until cleared by a clear action instead |
| 1 | Audio Playback | `SoundToPlay` (resolved via `files-database.json`) |
| 2 | Speech/TTS | `TextToSpeak` |
| 3 | Timer (fills up in NAG) | `EnableTimer`, `TimerType = 3` (EQLP **Progress** — fills; Countdown would drain) |
| 4 | Countdown (drains in NAG), optionally repeating | non-repeat → `TimerType = 1` (**Countdown**); `repeatTimer` → `TimerType = 4` (**Looping**) + `TimesToLoop = repeatCount` (unlimited → sentinel `999999`, reported as dropped "unlimited timer repeat (approximated)") |
| 5 | Set Variable | Per-phrase set-variable `VariableAction` (capture group converted to a named group, see §3.6) |
| 6 | Timer with Remain | Skipped feature — reported as "remain-after-ended timer" (EQLP removes timers on end) |
| 7 | Clear Variable | Global clears → `EndTimerClearVariables`; phrase-specific clears → per-node Clear `VariableAction` routed via the action's `phrases` array |
| 8 | Counter | **No visible timer** (NAG counters are invisible in-memory tallies). A counter-incrementing `VariableAction` is kept and the NAG duration becomes `RepeatedResetTime`. `resetCounterPhrases` produce an extra auto-generated "… (Counter Reset)" node that clears the variable |
| 9 | Clipboard/Chat Command | `TextToShare` / `TextToSendToChat` (`/command` prefix stripped; webhook-only in EQLP) |
| 10 | Buff Timer with Cast Time | Reported as dropped "cast time tracking" |
| 11 | Death Recap Display | No EQLP equivalent — reported as "death recap display" |
| 12 | Screen Glow | Reported as dropped "screen glow" (see §6) |
| 13 | Clear All | No EQLP equivalent — reported as "clear all" |
| 14 | Stopwatch | No EQLP equivalent — reported as "stopwatch timer" |
| 15 | List Widget | No EQLP equivalent — reported as "list widget" |

> Action-type names 11–15 verified against NAG v0.2.26 engine source (v0.2.16-era notes called 11 "hotkey" and 6 "Timer with Remain"); the sample DB is schema version 95, so current numbering governs it.

### 3.5 Timer Fields

- **`restartBehavior`** → `TriggerAgainOption`. NAG enum (`trigger.js`): `0=StartNewTimer, 1=RestartOnDuplicate, 2=RestartTimer, 3=DoNothing`. Mapping (verified against NAG runtime in `renderer.js`): `{0→0, 1→2, 2→1, 3→3}`; unknown values pass through.
- **`duration: null`** → NAG means "indefinite timer ended by end-early phrases". EQLP needs a fixed duration → defaulted to **60s**, reported as dropped "indefinite timer duration (defaulted to 60s)".
- **End-early phrases**: trigger-level + action-level `endEarlyPhrases` are merged into EQLP's **max 3 slots** (`EndEarlyPattern/2/3`, trigger-level first, action-level fills the rest, de-duplicated). Anything beyond 3 is dropped and reported as "extra end-early phrases dropped (max 3)".
- Warning/end sub-action text: `endingSoonText` → `WarningTextTo*`, `endedText` → `EndTextTo*`.

### 3.6 Variables & the "Capture spell casting" Fallback

Set-variable actions route to the phrases listed in the action's `phrases` array (or its single `phraseId`). Additionally, a **fallback** extends the last matched set-variable to any other regex capture phrase that has un-named capture groups and no `${var}` references — written for "Capture spell casting", where only phrase 0 ("You begin casting") lists an explicit set action but phrases 1–2 ("You activate …", "You begin singing …") should also store the spell name.

> **Known intentional deviation:** NAG v0.2.16 strictly scopes actions to their listed phrases (`log-watcher.js` — the old "no phrases = all phrases" behavior was explicitly removed), so in NAG only "begin casting" sets `SpellBeingCast`. The import deliberately extends this to the sibling capture phrases because it is a pure improvement for EQLP's variable-driven displays.

Clear-variable actions route per-phrase via the action's `phrases` array; display text routes the same way. A clear-variable action carrying an NAG alert `overlayId`/duration (e.g. the 15s "spell interrupted" alert) cannot be represented → reported as "clear variable action alert overlay".

### 3.7 Other Field Mappings

| NAG field | EQLP mapping |
|---|---|
| `score` (0–1) | `Priority` via `ConvertScore()` (0.9+→1, 0.7+→2, 0.5+→3, 0.3+→4, else→5). Note: the NAG engine does not use `score` at all — the mapping exists only to give useful EQLP priorities |
| `useCooldown` / `cooldownDuration` | `LockoutTime` (direct seconds) |
| `conditions` (operatorType: 0=IsNull→`!{v}`, 1=Equals→`{v} = "a" \|\| {v} = "b"`, 2=DoesNotEqual→negated equals / must-be-set, 16=Contains→`{v} contains "a"`) | `MatchVariableCondition` — verified against NAG v0.2.26 engine (`models/trigger.js` OperatorTypes + `log-watcher.js` checkCondition); NAG pipe-separates multiple values = OR |
| `comments` | `Trigger.Comments` (preserved verbatim; import notes appended below) |
| action `overlayId` | `SelectedOverlays` (NAG overlay IDs are EQLP node IDs after overlay import) |
| `onlyExecuteInDev: true` | Skipped ("Dev-only trigger") |
| `classLevels` | No equivalent — reported as "class level filtering" (70 triggers in the sample DB) |
| `captureMethod: "Sequential"` | **Skipped entirely** with reason "Sequential capture method (not supported)" (18 triggers). NAG sequential matching = phrases must match in order with capture-group carryover; EQLP has no equivalent |
| `predefined` | No special handling — imported like any other trigger (3 in the sample DB) |

### 3.8 Per-Phrase Action Scoping (Known Divergence)

NAG fires each action **only** on the phrases listed in that action's `phrases` array. The import builds one merged action set per trigger and clones it to every phrase node (with per-phrase overrides for display text, set/clear variables), so an action scoped to 1 of 3 phrases runs on all 3 phrase triggers in EQLP — extra timers/effects, not missing ones.

Affected: **~190 triggers** in the sample DB (any multi-phrase trigger where some action covers a strict subset of phrases). Each such trigger is flagged with the dropped-feature note **"per-phrase action scoping"** so the report stays honest. A full fix (build each phrase node from only its own actions) is deferred as a `ParseTrigger` restructure.

---

## 4. Overlay Import

- Read from `overlays-database.json`, imported **before** triggers.
- `overlayType`: `"Timer"` → timer overlay, `"Alert"` → text overlay, `"FCT"` → **skipped and counted** (no EQLP equivalent). The count is surfaced in the completion dialog ("N FCT skipped — unsupported") and the HTML report note. Sample DB: 3 of 16 overlays are FCT.
- NAG `overlayId` is used as the EQLP `TriggerNode.Id` (dedup key; enables trigger→overlay reference resolution and overlay dedup on re-import). `Overlay.Source = "nag:{overlayId}"`.
- Mapping highlights: font/size/weight/color, background color + transparency → ARGB, alignments, `textOverflow.whiteSpace=nowrap` → no-wrap, glow → drop shadow. Not mapped: line height, group headers, per-monitor bounds, borders, timer sort options.

---

## 5. Audio Files

- `files-database.json` maps NAG `audioFileId` → file name (e.g. `"rUXUVn5IdesjOGFv"` → `"thump2.mp3"`). Special case: `fileId "ShortWarningPing"` ("Speech ding") → `alert1.wav` (EQLP built-in).
- Audio files are **not** embedded in the NAG folder — users must copy `.wav`/`.mp3` files into EQLP's `data\sounds\`.
- Triggers whose audio file is missing from `data\sounds\` get status **Partial** and the file is listed per row ("Missing Audio") in the HTML report.

---

## 6. Dropped Features & How They Are Reported

Per-trigger `droppedFeatures` notes (exact strings, appended to Comments as `EQLP Import Notes: …` and shown in the report's Details column). Notes are split into **true gaps** (no EQLP equivalent — these make a trigger Partial) and **implemented approximations** (`NonStatusDroppedFeatures` — the behavior is imported as closely as EQLP allows; the note stays visible but does not downgrade status on its own):

| Note string | Meaning | Sample-DB impact |
|---|---|---|
| `class level filtering` | `classLevels` has no EQLP equivalent | 70 triggers |
| `speech interruption (approximated as priority 1)` | Action `interruptSpeech: true` (NAG preempts any currently-speaking text; `overlay.js:1273`). Approximation: the trigger is imported at **Priority 1** — the EQLP audio engine stops playing lower-priority audio and drops queued lower-priority events (`AudioManager.SpeakAsync`/playback loop). Same convention as GINA import (`GinaUtil`). Residual gaps (rare in practice): it won't preempt other priority-1 sources, and it preempts sound effects too, not just speech. **Does not affect status.** | 455 triggers (471 actions); ~395 of them would have been Partial for this note alone |
| `secondary phrases` | Action `secondaryPhrases` (extra phrase IDs the action also matches, `log-watcher.js:3046`); no equivalent | 18 triggers |
| `per-phrase action scoping` | Action set merged across phrase nodes; NAG scopes per phrase (§3.8) | ~190 triggers |
| `extra end-early phrases dropped (max 3)` | EQLP has 3 end-early slots; overflow dropped, first 3 kept in order | 165 triggers |
| `indefinite timer duration (defaulted to 60s)` | NAG `duration: null` → fixed 60s | (several) |
| `unlimited timer repeat (approximated)` | Looping timer with sentinel `TimesToLoop = 999999` | (several) |
| `remain-after-ended timer` | Action type 6 — EQLP removes timers at expiry | 15 triggers |
| `cast time tracking` | Action type 10 | 6 triggers |
| `clear variable action alert overlay` | Clear-variable action's NAG alert overlay + duration (e.g. 15s interrupt alert) | 1 trigger ("Capture spell casting") |
| `set variable (no name)` | Set-variable action without a variable name | rare |
| `screen glow` | Action type 12 (NAG v0.2.26 name; older notes said "screen flash") | 56 triggers |
| `phrase ${var} restriction (…)` | NAG treats `${var}` in a phrase as a match-time restriction (only matches stored values of that variable, never when empty); EQLP has no equivalent → the imported phrase matches any text | 1 trigger |
| `case-sensitive non-regex phrase(s) imported as case-insensitive` | Phrase `ignoreCase: false`; EQLP literal matching is always case-insensitive (regex phrases instead get a `(?-i)` prefix, preserving sensitivity) | 18 triggers |
| `counter condition` / `condition type N` | Condition types other than 1 (variable value) have no EQLP equivalent — condition dropped from that trigger's condition list | 0 in sample DB (all 576 are type 1) |
| `condition operator N on X` | Operator outside {0, 1, 2, 16} (e.g. 4/8 numeric comparisons, which NAG only uses for counter conditions) — condition dropped | 0 in sample DB |

Skipped-trigger reasons (visible in the report's Skipped rows): `Sequential capture method (not supported)` (18), `Dev-only trigger`, `No capture phrases`, unsupported/empty action sets.

Global surfaces:
- **Completion dialog**: totals, skipped-reason breakdown, FCT overlay skip count, duplicate-root warning when earlier imports exist.
- **HTML report** (`nag-import.html` in the EQLP log dir): summary stats, note that all imported triggers start disabled (+ FCT count), rows sorted Skipped → Partial → Success with status badge, folder path, action summary, details/dropped features, missing audio.

---

## 7. Character Enable State — Intentionally Not Imported

NAG's `characters-database.json` stores per-character enable/disable lists (`disabledTriggers`, directly or via `triggerProfile`). **The import does not read this file and writes no `TriggerState` rows.** All imported triggers start disabled for every EQLP character; the user enables what they want in the Triggers view.

This is a deliberate product decision, not a bug:
- EQLP keys per-character state by an opaque GUID assigned when a character is registered (or `"Default"` in no-character mode). NAG character **names** cannot be matched to those GUIDs — there is no join key between the two systems.
- An earlier implementation wrote `TriggerState` rows keyed by NAG character name, but the runtime (`GetEnabledTriggers(CurrentCharacterId)`) only reads rows keyed by that character's GUID/`"Default"` — the rows were never read. The feature was removed (including the supporting id-map plumbing in `TriggerStateDB.ImportTriggers`) rather than left as dead data.
- If per-character NAG state is ever wanted back, the smallest path is a name-fallback lookup at read time (imported state = default, manual GUID-keyed state wins).

---

## 8. Testing

- Test project: `EQLogParser.Wpf.Test` (`NagUtilTriggerImportTest.cs`) — trigger/overlay conversion, per-phrase routing, timer types, counters, restartBehavior mapping, dedup by OriginalId, orphan placement, all dropped-feature notes, FCT overlay counting, real-data fixtures (e.g. `capture-spell-casting.json`).
- Tests **cannot run on Linux** (`net8.0-windows10.0.17763.0`); Linux CI/dev builds use `-p:EnableWindowsTargeting=true` for compile-only verification. **Run the WPF test suite on Windows before committing.**

---

## 9. Key Design Decisions (from branch review)

1. **One node per phrase** (not alternation-joined) — avoids named-group collisions; multi-phrase triggers are `Name #i` siblings.
2. **Orphans re-filed, not skipped** — matches NAG's live behavior; dead-folder triggers stay usable.
3. **Counters import as invisible variables**, not visible countdowns — NAG counters have no timer component at runtime.
4. **Timer kinds mapped by fill direction** (NAG Timer fills → Progress; NAG Countdown drains → Countdown/Looping), verified against NAG v0.2.16 `renderer.js`.
5. **`restartBehavior` 1↔2 swap fixed** — original code mapped the two "restart" modes to each other's EQLP equivalents (761 actions affected).
6. **Dedup by name + OriginalId within an import** — prevents same-name sibling triggers from overwriting each other (was ~100 lost triggers in the sample DB).
7. **Character state not imported; everything starts off** (§7) — enablement/organization is the user's job.
8. **`interruptSpeech` triggers import at Priority 1 and count as fully Imported** — the priority bump is a real approximation (EQLP audio preempts lower-priority playback, matching GINA import's convention), and in actual EQ play two top-priority interrupt sources rarely overlap, so the note is informational rather than a gap. True gaps still mark triggers Partial; this one rule flips ~395 sample-DB triggers from Partial to Imported, keeping the report focused on real losses.
9. **Retracted earlier findings (for transparency):** the "`ConvertTemplates` corrupts `{C}`" concern was a false alarm (`GroupPattern.Replace` is a no-op there; runtime `PreProcessCodes()` handles native `{c}`), and "non-regex `{S}/{N}/{TS}` triggers broken by import" was retracted — those phrases are dead in NAG itself (non-regex path never expands them).
