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
