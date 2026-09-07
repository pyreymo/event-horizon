# Native draw admission contract

Verified 2026-09-08 against the local CN executable; no live-game validation.
EXE SHA-256: `e06704e3fa9c3bd43a0c8d238945163d7be14c5700cc4d350618b8ec1dd4e1a6`.
FFXIVClientStructsCN reference: `e8e850e0914acb3450f436467a09181be0fd2b1f`.

## Binary evidence

| Operation | Supplied I64 address | Disk EXE address |
| --- | --- | --- |
| GameObjectManager.Update | `140AF8C50` | `140AF8D70` |
| Candidate sort call | `140AF8F02` | `140AF9022` |
| Recursive candidate sort | `140AFA1E0` | `140AFA300` |
| GetDrawLimit | `140AF8580` | `140AF86A0` |
| Candidate priority admission check | `140AF8F58` | `140AF9078` |
| DisableDraw branch | `140AF8FB7` | `140AF90D7` |

The I64's imported names/address map is unreliable: its symbol named
`GameObjectManager.Update` points elsewhere. Its stored input hash matches the EXE,
but the bytes/addresses differ. Direct comparison of the 0x220-byte sort and
0x444-byte Update bodies found differences only in external call/global-reference
operands; the candidate layout, control flow, and draw dispatch match. The plugin
resolves a unique callsite signature, never one of these absolute addresses.

Update collects character candidates anew from manager slots 0–199 and 489–818
(skipping 200–488). A candidate is `{ GameObject* object; int priority; uint padding; }`,
16 bytes, with priority at +8 copied from `GameObject+0x93`. Native sort compares
priority first and `GameObject+0x94` (current distance) second. Its Win64 arguments
are `(first, lastExclusive, int64 ideal, byte comparer)`; Update supplies count as
ideal and zero as comparer. Only Update and two recursive calls reference this sort
in the examined I64. Its return register is unused by Update.

After sorting, Update adjusts its adaptive draw limit, calls GetDrawLimit, and
walks **every** record. Admission requires native budget remaining, native render
flags passing `0x10000102` (with a special native exception), and signed
`candidate.priority <= 15`. It calls virtual slot 12 (+0x60, EnableDraw) on admission
and slot 13 (+0x68, DisableDraw) otherwise. Some character categories do not consume
the native counter; this logic stays entirely in the game. GetDrawLimit respects
manager +0xC override, settings/context and the adaptive limit at +0x4CF0.

DisableDraw does not remove objects from the manager or candidate collection. Each
following pass replaces the stack records, so eligibility can recover without a
plugin restore operation, including after disabling/unloading the plugin.

## Integration invariants

- One sort-entry hook, with a thread-local recursion guard. Original sort completes
  before policy runs, once on the outer range, including ranges of 0/1/32/33+ records.
- Policy edits a temporary copy and commits only on success. A managed exception
  preserves that pass's vanilla records and disables policy until reload.
- Only remote player records are reordered, within existing remote-player slots.
  Local-player, NPC, enemy and dependent-object ordering remains native. Eligible
  preview/exempt players come first, then counted players by configured rule rank,
  viewport/distance and native-order ties. Native priority >15 is never lowered.
- Policy rejection sets **record** priority to at least 16. It does not write
  GameObject priority/RenderFlags, remove candidates, change GetDrawLimit, call
  EnableDraw/DisableDraw, or manage model transitions. Non-player hide rules use the
  same record rejection and only apply to characters collected by this native pass.
- Player budget caps counted candidates with native priority <=15; exemption is
  only from that budget, never the game's total limit. Out-of-range candidates do
  not consume it. Native flags can further reduce actual draws. The plugin does not
  duplicate the game's flag checks or their special exception.
- Recent target/chat timers are rule state; nearby selection is a distance predicate. Prediction,
  retention scoring, show throttling, admission holds, topology tracking, hidden-flag
  ownership and restore/swap queues are removed. Preview/ground markers consume a
  per-pass decision snapshot, validate object identity and never restore objects.
  Preview reports rule admission, not proof that a model was drawn.

## Offline verification

```powershell
pwsh -NoProfile -File scripts/Verify-NativeAdmission.ps1 -Executable '<game>/ffxiv_dx11.exe'
dotnet build EventHorizon.sln -c Release --no-restore
dotnet build EventHorizon.sln -c Debug --no-restore
```

The verifier reads PE .text, requires unique signatures, resolves the sort's
GetDrawLimit continuation and verifies that priority >15 branches to DisableDraw.
It does not launch/attach to the game or modify the EXE/I64. Recheck signatures,
layout, callers and ABI after client updates; signature matching/build success
alone does not establish live-game behavior or cross-plugin compatibility.
