# Living Server 1.1.4 port: the rest of the server, commit by commit

The talk layer came across first ([PORTED-FILES.md](PORTED-FILES.md)). This log covers the rest of Living Server
`v1.1.4-beta`, ported one Living Server commit at a time onto this branch: each file is merged three ways (Living
Server's parent commit as the base, Living Server's commit as theirs, this branch as ours), conflicts are resolved by
hand, and the result builds and passes its own tests before it is committed.

The rule for this edition (owner, 2026-11-14): a Living Server feature that changes how True 1.65 plays comes across
**switched off**, so the game follows the 1.65 rules out of the box. Its switch is listed here and in the launcher.

| Living Server commit | What | Here | Switch (default) |
|---|---|---|---|
| `e9cacc9d` | Bots react to attacks like players (stefanrows' incoming fire) | Ported. `BotBrain.cs` merged by hand: True 1.65's Nearsight cure kept, the group poison cure after it. `IsAutonomousWorldBot` → `IsWorldGamebot`. | `bot_incoming_fire_reaction` (off) |
| `15e4f378` | Quiet twisting: a bot's song restarts play no music | **Not ported:** True 1.65 has its own (`GameBot.SongSoundQuietUntil`). | — |
| `77e23c90` | Gamebots stop following a player who leaves or disbands (issue #33) | Ported beside True 1.65's own fix (which disbands a group with no player left): with other players still in the group, the leaver's gamebots follow one of them or leave. Hired companions are never released (`IsWorldGamebot`). | — (a fix) |
| `31a46a00` | A mezzed enemy is finished once no other enemy is free nearby | Ported unchanged. | — (a fix) |
| `324aef8f` | The effect leak; siege weapons read their owner once (rambodolphin13) | **Already in True 1.65.** | — |
| `4bd2343f`, `064a3870` | Merchant spot search in its zone, within 2,000 units (issue #25) | **Already in True 1.65** (`SpotSearchRange`). | — |
| `61843b43` | A bot with a full bag stops holding at the medallion merchant (rambodolphin13) | Ported. | — (a fix) |
| `eec4b744` | Water breathing gets its ECS effect: Nereid potions (Rmarcoux86) | Ported unchanged. | — (a fix) |
| `5ec1f6f4` | Merchant windows lay out clashing rows instead of logging errors | Ported unchanged. | — (a fix) |
