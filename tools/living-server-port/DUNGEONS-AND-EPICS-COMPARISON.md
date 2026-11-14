# Dungeons and epic encounters: True 1.65 and Living Server compared

Written 2026-11-14 for the port of Living Server 1.1.4 onto True 1.65. It sets what shadowofze changed in True 1.65 (to
v0.7, `f68cc75`) beside what Living Server changed (to `v1.1.4-beta`), so each port decision can be checked.

**How it was measured.** Both projects grew from Offline DAoC. Each side's files were compared with the 0.37 release
(`ab4df02`), the latest common release, line by line. The two did not share code after it: in the three biggest raid
files the two sides added 1,544 lines between them and only 32 are the same. True 1.65's public history starts at
v0.1 (`b097caf`, 2026-11-01); work before it shows as one import, so for those changes the date and reason are not
known. A few identical blocks on both sides (the horsemen's state log) show that both carry some shadowofze code from
after 0.37; those are counted as neither side's work.

## The big difference: who starts a raid

| | True 1.65 | Living Server |
|---|---|---|
| What starts a PvE raid | Only a level-50 player, with the **Horn of Rallying** at the inn (a dragon, an epic dungeon, Summoner's Hall or Darkness Falls). "PvE events never start by themselves." | The realm's raids run **on their own** on a schedule (a random roll that sets aside the last raid won), plus raids forced from the launcher. |
| Raid size | Set by how many answer the horn: off at 300 answers, or 3 minutes after 200; saved level 50s who are offline answer too. Rally clock 160/200/300 s by size. | **Sized from the period** (RAID-SIZES.md: the comfortable 2002-2004 raid per encounter, from Freddyshouse threads) and then **by its own record**: start at 125%, a win trims 10 points, a loss adds 20.3, floor 70%, ceiling 200%, settling near two wins in three. |
| When a raid ends | Three wipes at the final encounter or an hour without progress; the sounder can call it off (`/horn calloff`). | Four hours. A time-out counts as a loss only after a wipe. |
| After a wipe | Released raiders gather outside and come back in as a wave of eight (`AutonomousRealmRaid.Reinforcements`). | The raid regroups and the **Epic Special Forces** (24 level-50 bots in handed-out epic gear, fewer after wins they helped) join the retry. Dragon raids get the same rules (`RealmRaidDragonFight`). |
| Darkness Falls | Opens to the realm holding the most keeps, recounted on each capture; raid ends when the realm loses access. | Raided like the other dungeons; no raid AI for its bosses. |

So the two answered "how do bots beat an epic" in opposite ways: True 1.65 makes the player call the raid and makes
the bots fight each boss correctly; Living Server runs raids itself, sizes them until they win about two in three,
and sends a special force when they lose.

## Bosses, one by one

"Script" means the boss's own file under `scripts/namedmobs`. A dash means that side didn't touch it.

| Boss or place | True 1.65 | Living Server | Clash? |
|---|---|---|---|
| **Legion** (Darkness Falls) | Ranged bots stand within 1,400, where he takes damage (df-bosses L2). No bladeturn on raiders while he is the boss, so his blast isn't fed (L3). His blast lock-out can't stick for ever (M3). | Listed as "no raid AI". | No |
| **Darkness Falls princes** | Devour pets only, never gamebots (`IsDevourablePet`, df-bosses L1). | The same fix, its own way (`NearestDevourablePet`). | Same fix twice; keep True 1.65's |
| **High Lord Baln, Moran the Mighty** | Call only their own kind for help, not the raid's bots and pets (df-bosses M1). | — | No |
| **Dragons** (Golestandt, Gjalpinulva, Cuuldurach) | Bots stop hitting a dragon outside its 2,000 lair circle and lead it home (dragon-fight L1). It lands with an empty aggro list, resets after 8 s out of combat, Cuuldurach's take-off has the route guard, battle posts within 1,000 of the lair, only a flying dragon holds the parties, healers purge the long stun, sister parties open together, kill news counts the bots. | A dragon outside its lair counts as out of tether (`DragonLairPlacement.IsOutOfLair`, 2026-10-20). Dragon raids regroup after a wipe and call the Special Forces (`RealmRaidDragonFight`). Leeroy can jump a landed dragon. | The lair rule: both, keep True 1.65's. The regroup: different (wave of eight vs regroup and Special Forces) |
| **Grand Summoner Govannon** (Summoner's Hall) | Raiders step out of his area spells while cast; his sacrifice and demon are killed first (raid calls hold a bot on its add); a Shade of Aelfgar each time his adds reach the centre; his reset restores strength, size and model; the database Shade beside him no longer spawns. | His original 300,000 health is back behind a switch (`AllowGovannonFullHealth`, off: 200,000). | No (the switch is off by default) |
| **Summoner Lossren, Cunovinda** | Pets leave Lossren for his souls (he takes 5 damage from pets); his infected ghoul returns after a reset; Cunovinda and the Shade keep the offline teleport rule. | — | No |
| **Apocalypse and the Four Horsemen** (Caer Sidi) | Caer Sidi no longer locks after a failed horseman fight; the Spectral Provisioner isn't made for a player during a bot raid; area spells avoided (Rain of Fire). | Originals behind switches: a horseman out of its tether heals to full (`AllowHorsemenTetherHeal`); only certain classes and bows damage Funus (`AllowFunusWeaponPuzzle`). | No (switches off) |
| **Skeletal Sacristan, Soul Reckoner** (Caer Sidi) | — | Originals behind switches: the Sacristan's patrol and resists; no damage to the Reckoner while a soul lives, and his 900 room. | No |
| **Galladoria** (Olcasgean, primals, Aroon, Xaga, Beatha, Tine) | Olcasgean and the Giant Sporite Cluster pay out once, from one body; the primals behave as scripted. | Originals behind switches: Aroon's damage order through the nine Scaiths, the bridge spot within 350, the fire primal's full patches, the Earthmenders' class puzzle, passivity, quick reset and mutual heals, Beatha heals Xaga, the fairies fly. | Both edited Olcasgean's file (different lines) |
| **Tuscaran Glacier** (Kvasir, King Tuscar and Queen Kula, Torst, Suttung, Vifil) | The raid needs King and Queen both up; Suttung's berserker stance can't be lost for good; Vifil's tunnel guardians come on every pull after a kill. | Originals behind switches: cold heals Kvasir (`AllowKvasirColdHeal`); damage to one twin heals the other (`AllowTuscarTwinHeal`). | Both edited the twins' file (different lines) |
| **Xanxicar** (Crystal Cave) | Two lines in v0.1. | Tactic: at 80/60/40/20% everyone but his party pulls back out of the stomp. | No |
| **Scurceol Hyrde** (Krondon) | — | Tactic: each party shoots its own Lyft Miht orb from a spot with a clear line; nobody hits him until the last orb dies; fought at home. Seen working once on a real install (RC3). | No |
| **Orshom Brong and Orylle** (Krondon) | — | Tactic: casters with a Cold spell shoot Orshom's Fire from outside its 300; everyone else stays out of it. | No |
| **Aidon the Archwizard** (Hall of the Corrupt) | — | Tactic: kill three of his four illusions and leave the last; fight him at home. | No |
| **Black Lady and the Ancient Black Oak** (Marfach Caverns) | — | Tactic: fight her at home (1,100 inside her 1,500 tether); the Oak dies with her. Seen working on a real install. | No |
| **Nosdoden** (Trollheim) | Two lines in v0.1. | Tactic: stop and clear each wave of essences, then go back to him. | No |
| **Prima encounters** (outdoor: Cuinn, Talvor, Brungar, Zargin, the Unseelie Envoy, Thergus, Gormror, Hamorik...) | 15+ scripted encounters from the Prima guides, new in 0.4. | — | No |

**Every scripted boss, for both:** True 1.65 gave raid bosses a way to tell bots their rules (`RaidBossTraits`:
damage range, lair circle, bladeturn), and bots now step out of any boss's area spell while it is cast, keep fighting a
boss that turns untargetable mid-fight, and send pets at the adds of a boss pets can't hurt. Living Server read all
208 boss scripts into a catalog (`docs/encounters/EPIC-ENCOUNTERS.md`: 244 encounters, 850 rules, each checked
against its script by a person) that names what each boss does and what a raid would need to do.

## The raid route (how a raid walks a dungeon)

Both rewrote `RealmRaidDungeonRoute` and `AutonomousRealmRaid` heavily, independently.

| | True 1.65 | Living Server |
|---|---|---|
| Musters | Every named encounter gets a muster and is pulled by the gathered raid; the muster point and posts stay outside the boss's aggro range; re-muster on the roster; returning raiders join the muster inside. | Raids muster where the boss lives and walk to where it actually stands; early pulls are answered by the whole raid at once, or it pulls back; a muster that stops gaining raiders for 20 minutes is set aside. |
| Stuck encounters | Three wipes on a lesser encounter set it aside; the no-damage clock forgets a reset boss's old low; go on to the final trigger when the rest are set aside. | An encounter not reached after three pulls is set aside for 15 minutes; a lost skirmish doesn't use up an attempt. |
| Travel | Raiders aim at their own spot at a zone crossing; formation slots skip unplaceable posts; Darkness Falls chains use the shortest proven last leg per encounter. | Trollheim staging posts, Lord Gildas's step, hub departs with half the raid after 30 minutes. |
| Records | An event ledger of battle start, encounter results and deaths; each boss that fell or was set aside. | Each encounter's record of wins and losses drives the raid's size and the Special Forces (`RealmRaidRecord`). |
| Loot | A quarter of scripted encounters' loot to the players (not epic trash); raid parties are one side for claims. | Raid loot logged; dues from raid coin when bot guilds are on. |

## Dungeons outside raids

| | True 1.65 | Living Server |
|---|---|---|
| Hunting | A large camp rework: bots spread over camps instead of piling in (`AutonomousCampCompetition`), duplicate camps merged, spots that keep coming up empty learned and dropped, crowded-camp widening, what a 2003 player knew about a spot (pack factor, casters, favourite prey; `AutonomousCampKnowledge`), party pullers that work, solo melee skipping flyers. | Parties grinding in a dungeon also go for its named boss once per task, when the boss is a real quest kill target at their level or has its own drops (`AutonomousDungeonBossGoals`); route proofs per camp search capped to stop server stalls (`AutonomousCampTargetProof`). |
| Dungeon travel | — | `AutonomousDungeonTraversal`, `AutonomousDungeonPolicy`: Old Frontiers dungeons only as a destination, not a road; a bot saved in Darkness Falls returns at its realm's entrance. |
| Content | Darkness Falls: 245 over-dense spawns thinned, entry certificate judged per spawn; dungeon floors and doors fixed in the client; 978 Prima spawns restored. | — |

## What this means for the port

- **Living Server's restored boss rules** (22 rules, 17 switches in `EpicEncounterMechanics`, off) touch only files True
  1.65 left alone or touched on other lines. They port with every switch off, so each boss plays as True 1.65 has it.
- **Living Server's raid tactics** (8 encounters) are for bosses True 1.65 has not worked on (Krondon, Hall of the
  Corrupt, Marfach Caverns, Trollheim, Crystal Cave), so they don't fight True 1.65's own rules. They need hooks in True
  1.65's raid route, which is rewritten; behind a switch, off.
- **The Epic Special Forces** answer an automatic raid's wipe in Living Server; here they would answer a Horn raid's
  wipe, behind a switch, off. Their handed-out gear is the exception to "bots earn what they wear" that the owner made
  for Living Server.
- **Raid sizing by record** has nothing to size here: a Horn raid's size is who answers. It is not ported; the record
  (`RealmRaidRecord`) comes only as far as the Special Forces need it.
- **Dragon regroups** (`RealmRaidDragonFight`) overlap True 1.65's own wipe handling (waves, three wipes and out); not
  ported, True 1.65's wins.
- **Dungeon boss goals** for parties change where gamebots hunt; behind a switch, off.
