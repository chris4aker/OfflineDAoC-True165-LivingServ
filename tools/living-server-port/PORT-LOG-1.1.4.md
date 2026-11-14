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
| `4c1a9de9`, `f312163d`, `28e07ff5`, `e1ed0c97`, `c98a47de` | Rez fixes: rezzers clear the area or back off to a clear spot, one question, one raise at a time | **Not ported:** True 1.65 rewrote its resurrection support (power rests, raid guests); `ResurrectSpellHandler`'s own fixes are already in it. The clear-spot step would need writing again on True 1.65's code. | — |
| `4cdf5169` | /invite by name says "busy" before "too far away" | **Not needed:** True 1.65 lets you /invite a gamebot by name wherever it is, and its invite already says when it is busy. | — |
| `4f87a372` | A gamebot in the player's group goes through the portal with the player | **Already in True 1.65** (owner 2026-11-03: invited gamebots go along at zone points). | — |
| `116ecbae` | No silent 1.5 s pause for whispers to an NPC, so two quick quest-keyword clicks both arrive | Ported: True 1.65 had `WhisperThrottle` but didn't call it. | — (a fix) |
| `955a658b` | Low-power Animists keep one damage shroom | **Not ported:** True 1.65 replaced that planning with the period turret cap (owner 2026-11-05, GitHub #16). | — |
| `03652578`, `b0749ea9`, `378bda3f`, `270379ff`, `8205ae3f`, `ffeea6f2` | /allrelease, and released gamebots' way back to a player's group (portal keeps in battlegrounds, planned routes, Cancain's route first) | **/allrelease ported** (`/grouprelease` too), for world gamebots only: each runs the release its death timer would. **The way back is not:** True 1.65 has its own rules for invited gamebots' deaths and battleground releases (`InvitedGamebotKeepsWaiting165`, battlegrounds fix G2), and Living Server's join-the-leader step moves a bot across regions, a shortcut players don't have. | — (a command) |
| `c9ed6179`, `7071f265`, `4ba84b65` | Friends list: add gamebots with /friend, their login notices, the whole online list | Ported. `AutonomousBotRegistry` keeps True 1.65's Realm Exchange relisting; Living Server's guild hooks stay out (bot guilds are off here). | — (a feature, no switch) |
| `c388ad98` | Levelling bots bend their route around far-above monsters and remembered death spots (stefanrows) | **Not ported:** True 1.65 has its own answer (owner 2026-11-10 P2: a solo bot whose path passes a death hotspot picks another camp), and the bend adds an area scan to every route check, which True 1.65's scale rules forbid. | — |
| `333529b6` | Route give-ups at the goal: goal snap, keep gates, give-up memory (stefanrows) | **Not ported:** it rewrites route failure handling in `AutonomousWorldBotController`, which True 1.65 has reworked since (zone seams, reversals, boarding quarantine). The project's navigation rule asks for evidence of the same failure here first. | — |
| `f3e0233c`, `290a5e9e`, `db57318e` and the other October gamebot AI commits (camps, matchmaking, dungeons, raids, sieges) | Living Server's fixes to Offline DAoC 0.37's gamebot AI | **Not ported, for now:** True 1.65's gamebot AI has moved on from 0.37 with its own fixes for the same areas. Each one waits for a play test that shows its problem on True 1.65. | — |
