# Living Server for True 1.65: the talk layer

**Status: in development. It builds and its automated tests pass. Nobody has played it yet.** See
[What has been checked](#what-has-been-checked) before relying on anything here.

Living Server is [chris4aker's add-on](https://github.com/chris4aker/OfflineDAoC-LivingServer) for the original
Offline DAoC (0.37): gamebots with personalities and voices that talk, argue, remember you and hold grudges. This
branch carries it onto Offline DAoC True 1.65: first the **talk layer**, then the rest of Living Server 1.1.4, each
part that changes how True 1.65 plays switched off ([the port log](../tools/living-server-port/PORT-LOG-1.1.4.md)
says what came across, what didn't and why).

- Carried from: Living Server Beta 1.1.4 (`v1.1.4-beta`). Its server code is 1.1.3's (`dd2e856`) but for the version line.
- Carried onto: Offline DAoC True 1.65 main at `f68cc75` (v0.7, the work for 0.8; the project is now
  `zomboden/OfflineDAoC-True165`). First carried onto `bfdb8d8` on 2026-11-11, moved here on 2026-11-14.
- This is the Living Server edition of True 1.65: the owner's own edition (owner, 2026-11-14). The aim is everything
  in Living Server 1.1.4, then 1.2 when it ships. Where True 1.65's rules and a Living Server feature differ, the
  feature comes across switched off, so the game follows the 1.65 rules out of the box.
- True 1.65 itself took Living Server's lines into its banter library in v0.7 (rewritten, about 3,800 lines), so the
  same material can now be heard twice: as banter lines and as the voices here. That overlap is not sorted out yet.

## What the talk layer adds

| Part | What you see |
|---|---|
| Bot voices | 34,532 written lines in 2003 chat style, chosen by a gamebot's realm, class, level, personality and what it is doing. |
| Personalities | Each gamebot is a Leveler, Socialite, Daredevil, Explorer, Crafter or No-lifer. This sets how it writes and how much it talks. |
| Scenes and debates | 393 realm chat scenes with a cast of bots: smite against rejuv, buffbots, zergs, relic panic. |
| Storylines | Pairs of bots live out a drama over days: a kill steal, a guild split, a ninja loot. |
| Answers | Bots answer what you say in /say, /group, /b, /trade and by /send, and follow the thread of the talk. |
| Memory of you | Bots remember grouping with you, your rezzes and your insults. They greet you, send a tell now and then, and gossip. |
| Your deeds | Bots that see you solo a named monster or go on a kill streak tell the realm. |
| Server life | Grats on a level. Off until switched on: server firsts, a dead bot asking you for a rez, frontier alarms, a bot's nemesis. |
| Rez from a stranger | With `bot_living_death` on, a gamebot that can resurrect raises a dead player of its realm nearby. You still get the accept box. |
| Rumours | True ones: a rare named monster that has just come back. |
| In your group | A gamebot in your group talks in /group and calls the fight: pull, adds, out of power, wipe. |
| Characters | A few beggars on capital corners, a gold spammer that gets banned, a few roleplayers. |
| Holidays | Around the holidays on your PC's calendar the chat changes. |
| Friends | Gamebots remember the bots they grouped with and who stood them up. |

Commands: `/personality` (a gamebot's type), `/lswhy` (why a bot said or didn't say something), and for GMs
`/lstest` (fires a Living Server moment on demand, so each can be checked in game).

## How it joins True 1.65

- **The banter library and the voices share the chat.** For an unprompted /say or realm line a coin
  (`AutonomousBotChatCoordinator.BanterShare`, 0.5) picks which is asked first; either stands in when the other has
  nothing that fits. Replies to a banter line come from the banter library. When you talk to a bot, the voices answer
  first and the banter library answers where they have nothing, before the old formal lines. Realm Exchange adverts
  are answered from either. `BotBanter165`'s own events (horn rallies, realm notices, guild chat) are untouched.
- **Hired companions are never part of it.** In True 1.65 a hired companion is a persistent bot too, so the ported
  code speaks only for `GameBot.IsWorldGamebot` and reads `AutonomousBotRegistry.WorldSnapshot()`. A companion keeps
  `CompanionChat165`, its own return lines and its own /say.
- **Gamebots leave only through the population system.** A bot that rage quits after losing a debate asks
  `AutonomousBotLifecycle.RequestLogout`; it is never deleted directly, and that system decides when it plays again.
- **No realm-wide /lfg.** True 1.65 has no /lfg, so gamebots don't post there. Their LFG lines go to /broadcast in
  town and to the realm's talk.
- **/faction stays GM-only for players**, as True 1.65 has it. You read the realm's talk; you answer in /say,
  /group, /gu, /b, /trade or by /send. Joining a debate or taking a side in a storyline needs /faction, so a normal
  account can watch those but not take part.
- **Seven lines were taken out of the corpus:** six about Cathal Valley (a battleground from after patch 1.65) and
  one that mentions /lfg. The rest passed a check against the banter validator's out-of-period list
  (`validate_banter165.py`): no Trials of Atlantis or later terms, classes or zones.
- **Bots follow the player rules.** Beggars receive real coin from players and bots and nothing is created. A bot on
  a siege, a raid, a relic run or a horse route turns an invitation down and says why.

## Settings

Server properties (the `ServerProperty` table). The defaults are Living Server's own, with one exception:
personalities are on, because the voices are chosen by them. As in Living Server, the chat parts are on and the
parts that change what a bot does (firsts, death, alarms, nemesis) are off until you switch them on.

| Setting | Default | What it does |
|---|---|---|
| `bot_voices_enabled` | on | The written lines. Off: True 1.65's chat as it was (banter library first). |
| `bot_voices_debates` | on | Realm chat scenes and debates. |
| `bot_voices_tells` | on | A bot that remembers you sends a tell now and then. |
| `bot_voices_chat_rate` | 2 | How much bots talk on their own, 0 (only answer) to 3. |
| `bot_personalities_enabled` | on | Personality types. Here they set voice, chattiness and town breaks only. |
| `bot_living_memory` | on | Bots remember you. |
| `bot_living_grats` | on | Grats on a level. |
| `bot_living_firsts` | off | Server firsts are announced. |
| `bot_living_death` | off | Rez requests and thanks; gamebots raise dead players nearby. |
| `bot_living_alarms` | off | Frontier bots call out enemies they see. |
| `bot_living_nemesis` | off | A bot remembers who killed it. |
| `bot_living_deeds` | on | Bots tell the realm what they saw you do. |
| `bot_living_storylines` | on | Dramas over days. |
| `bot_living_characters` | on | The gold spammer and the roleplayers. |
| `bot_living_rumours` | on | True rumours. |
| `bot_living_holidays` | on | Holiday chatter. |
| `bot_living_beggars` | on | Beggars in the capitals. |
| `bot_group_bonds` | on | Bots remember who they grouped with. It does not change who groups with whom yet. |
| `bot_schedule_mode` | off | Leave off. True 1.65's population system decides who is online. |
| `bot_living_breaks` | off | Leave off for the same reason. |
| `bot_living_body_language` | off | Wired in towns only; not ready. |
| `bot_living_leeroy` | off | One end-game raid in twenty has a raider who charges the final boss early. |
| `bot_incoming_fire_reaction` | off | Gamebots hit by a player take a moment to react, as a person does. |
| `bot_era_names` | off | New gamebots get names in the style of 2003's servers. |
| `xp_rate_by_level`, `bot_xp_rate_by_level` | empty | XP rates by level range; empty keeps True 1.65's rates. |
| `quest_xp_by_level` | off | Quest XP follows `xp_rate_by_level`. |
| `bot_guilds_enabled` and the other `bot_guild_*` | off | Gamebots found and run their own realm guilds (below). |
| `bot_epic_encounter_tactics` | off | Bot raids play Living Server's tactics for eight epic encounters, on top of True 1.65's boss rules. |
| `epic_original_mechanics` | empty | Original OpenDAoC boss rules to turn back on, by name or `all` (17 switches; each makes its boss harder). |
| `bot_chat_period_channels` | on | True 1.65's own switch: bot talk on the period channels (below). Off: everything on `<Faction>`. |

The launcher's **Living Server** tab sets all of these (with the server stopped); its suggested settings turn on the
chat parts and leave everything that changes how the game plays off.

**Bot guilds** (`bot_guilds_enabled`, off): gamebots form realm guilds and carry the guild name and emblem, group
with guildmates first, talk in /gu, recruit you, and with their parts on claim keeps and defend them first, pay dues
and earn merit, and form realm alliances. Gamebots you /ginvite into your own guild work as True 1.65 has them,
switch or no switch: they pay its dues and never earn it merit. A bot guild's member you /ginvite leaves its bot
guild for yours (its leader and officers turn you down).

**The channels.** Every voice line used to go out as `<Faction>`. Now a gamebot's line goes on True 1.65's period
channel, chosen as True 1.65 chooses it for its own banter: RvR talk on the realm's chat group, trade and general talk
on /broadcast to the players of the realm in the speaker's region, questions on /advice. A thread, debate or
storyline stays on the channel and in the region its first line went to, so a /broadcast scene is heard where it
started and nowhere else. Bots answering a GM's /faction line answer on /faction.

## Not carried over

**Left behind on purpose** (they break True 1.65's rules, or True 1.65 has its own):
- Epic Special Forces (gamebots handed epic gear) and faster power regeneration (the tab keeps True 1.65's rate).
- The Atlas daily quests and the Hearth quest chains, Living Server's quest markers and journal: True 1.65 has its own.
- Bot logins and logouts by schedule, "brb" breaks and linkdeads: True 1.65's population system does this.
- Flanking, and Living Server's fixes to 0.37's gamebot AI where True 1.65 has its own (the port log lists each).
- Everything for the original client.

**Here but switched off** (they need their own pass):
- Body language.
- Personality tastes in camp, dungeon and goal choice, and friends looking for each other when groups form. These
  would change how True 1.65's gamebots hunt, so they wait for a play test.

**Later parts of Living Server:**
- The Epic Special Forces and Living Server's quest journal, epic chains and quest fixes (in progress).
- From Living Server 1.2, once it is released: bot-led groups, RvR perception, the lore, raid skill.
- The route fixes and the diagnostics.

## What has been checked

On the current base (`f68cc75`), 2026-11-14:

- **Build:** the whole solution builds with 0 errors (.NET 10).
- **Automated tests:** all 6,740 pass on the latest run. True 1.65's own
  `UT_BotNameService.Claims_FiftyThousandPerRealmUniqueAndNeverThrow` fails on some runs: it fails the same way on
  unmodified True 1.65 v0.7 (4 of 5 runs on 2026-11-14, a name claim waiting over 250 ms for the registry's lock).
- **The launcher's tests** need Windows; they build here and run in the Windows CI workflow
  (`.github/workflows/living-server.yml`).
- **Moving to the new base:** 236 of the 241 files applied unchanged. Five needed a hand merge: `GameBot.cs`,
  `AutonomousChatKnowledge.cs`, `AutonomousRealmRaid.cs`, `emotes.cs` (both sides kept) and
  `AutonomousBotChatCoordinator.cs` (Living Server's version, with True 1.65's newer /lfg line, zone counts and
  reply sample added in a marked region at the end).

**Not checked:**
- The server has not been started on the new base. (On the old base it started on the clean world, loaded the
  lines and logged in gamebots.)
- Nothing has been seen in game on any base.
- The voices on the period channels have not been heard in game. True 1.65's newer /advice answer and reply sampling
  are in the coordinator, but Living Server's own reply paths don't use them.
- Bot guilds have not been switched on in game on True 1.65.
- How the banter library and the voices sound together, and server load at 1,000 gamebots a realm.

## Working on it

- [tools/living-server-port/PORTED-FILES.md](../tools/living-server-port/PORTED-FILES.md) lists every file: copied
  unchanged from Living Server, copied with the one mechanical rename, edited for True 1.65, and the True 1.65 files
  that were changed.
- The mechanical rename in copied files: `IsAutonomousWorldBot` became `IsWorldGamebot`, and
  `AutonomousBotRegistry.Snapshot()` became `AutonomousBotRegistry.WorldSnapshot()`.
- Voice lines: edit the `.lines` files in `development-source/server/GameServer/bots/autonomous/data/chat`, then
  run `python tools/bot-voices/build_corpus.py` to rebuild `corpus.json`. The format is in
  [bot-voices/DESIGN.md](bot-voices/DESIGN.md); the other documents there were written for Offline DAoC 0.37.
- Living Server tests that test parts not carried over were left out or trimmed; the list is in PORTED-FILES.md.
