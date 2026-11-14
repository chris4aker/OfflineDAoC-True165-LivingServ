# The epic encounter catalog (from Living Server)

`EPIC-ENCOUNTERS.md` and `epic-encounters.json` are Living Server 1.1.4's catalog of every scripted boss: 208 scripts,
244 encounters, 850 rules, each checked by a person against its script. `hand-notes.json` holds those checks and the raid
tactics (`aiTactics`) that `EpicEncounterTactics.Catalog.cs` is written from.

They came across unchanged on 2026-11-14 and were generated on Living Server's code: the file and line references point
at Living Server's scripts, and the "fork today" column describes Offline DAoC 0.37 as Living Server had it, not True 1.65.
True 1.65 changed several of these bosses since (see
[tools/living-server-port/DUNGEONS-AND-EPICS-COMPARISON.md](../../tools/living-server-port/DUNGEONS-AND-EPICS-COMPARISON.md)).

The generator is `tools/living-server/epic_encounter_catalog.py` (its paths point at this repository). It has not been
run here yet: a fresh run would re-read True 1.65's scripts, and the hand notes whose script changed would need checking
again before the catalog is regenerated.

In this edition the tactics are behind `bot_epic_encounter_tactics` and the restored original mechanics behind
`epic_original_mechanics`, both off by default.
