using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace DOL.GS;

/// <summary>
/// Living Server's Epic Special Forces, ported to True 1.65 and OFF by default (server setting bot_epic_special_forces,
/// the launcher's Living Server tab). Each realm's elite guild of level-50 gamebots in endgame gear, who answer when a Horn
/// raid is in trouble: after the raid's first wipe at its final encounter (True 1.65's three-wipes-and-out rule is
/// unchanged), up to <see cref="MaximumMembers"/> of them arrive at the raid's muster hub and walk in like late raiders, the
/// way True 1.65's reinforcements do (AutonomousRealmRaid.Reinforcements: they gather and go in as a wave of eight). An
/// encounter beaten here the last two times brings them from the start.
///
/// How many come (Living Server's owner, 2026-11-04): 24 to start; a win they took part in sends one party (8) fewer next
/// time, down to 8, and they always answer a wipe; a win without them changes nothing; a defeat sends one party more, up
/// to 24 (<see cref="CallUpParties"/>), from each encounter's record (RealmRaidRecord, read from the realm event ledger).
///
/// They are the Living Server owner's one exception to gamebots earning their armor ("they are a special exception"),
/// and so an exception to True 1.65's AGENTS rule 4 on gear only: they fight, die, release and rest by the player rules.
/// Each member is made for one raid and lives in memory only: never saved, never in the roster or the population count,
/// no loot or coin from the raid (that stays with the realm's own raiders and the horn sounder), and removed when the
/// raid ends. A crash or restart leaves nothing behind. No difficulty or combat number changes.
///
/// From chris4aker's Living Server (OfflineDAoC-LivingServer, 1.1.4).
/// </summary>
public static class AutonomousEpicSpecialForces
{
    public const string GuildName = "Epic Special Forces";
    /// <summary>The switch (server setting bot_epic_special_forces, off by default): off, no call is made and none arrives.</summary>
    public static bool Enabled => ServerProperties.Properties.BOT_EPIC_SPECIAL_FORCES;
    public const int PartySize = 8;
    public const int MaximumMembers = 24;
    public const int MaximumParties = MaximumMembers / PartySize;
    /// <summary>They always answer a wipe with at least one party.</summary>
    public const int MinimumParties = 1;
    /// <summary>Far above any roster id (offline_world_bots counts up from 1), so the two never meet.</summary>
    public const long FirstBotId = 1_900_000_000;
    private static long _lastBotId = FirstBotId;

    private static readonly Logging.Logger Log = Logging.LoggerManager.Create(typeof(AutonomousEpicSpecialForces));
    private static readonly object Sync = new();
    private sealed record Call(string RaidId, string RaidName, eRealm Realm, int Members, string Reason);
    private static readonly Queue<Call> Calls = new();
    private static readonly Queue<string> Dismissals = new();
    // Raids that have had their call this run (one call per raid).
    private static readonly HashSet<string> Answered = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<GameBot>> Members = new(StringComparer.Ordinal);
    private static readonly HashSet<GameBot> SeenOutOfWorld = new();
    // How many joined each running raid (kept when members leave), for its ledger row and its record.
    private static readonly Dictionary<string, int> JoinedCount = new(StringComparer.Ordinal);
    private static long _nextCleanup;

    /// <summary>One party per realm, in the shape every expedition party takes: tank, healers, support, damage.</summary>
    public static readonly IReadOnlyDictionary<eRealm, eCharacterClass[]> PartyClasses = new Dictionary<eRealm, eCharacterClass[]>
    {
        [eRealm.Albion] = [eCharacterClass.Armsman, eCharacterClass.Paladin, eCharacterClass.Cleric, eCharacterClass.Cleric,
            eCharacterClass.Friar, eCharacterClass.Minstrel, eCharacterClass.Wizard, eCharacterClass.Mercenary],
        [eRealm.Midgard] = [eCharacterClass.Warrior, eCharacterClass.Thane, eCharacterClass.Healer, eCharacterClass.Healer,
            eCharacterClass.Shaman, eCharacterClass.Skald, eCharacterClass.Runemaster, eCharacterClass.Berserker],
        [eRealm.Hibernia] = [eCharacterClass.Hero, eCharacterClass.Champion, eCharacterClass.Druid, eCharacterClass.Druid,
            eCharacterClass.Bard, eCharacterClass.Warden, eCharacterClass.Eldritch, eCharacterClass.Blademaster],
    };

    public static bool IsWin(string outcome) => outcome == "Boss defeated";
    /// <summary>Beaten by the fight: beaten back from the final encounter three times ("Failed"), or a time-out after a
    /// wipe (a time-out without one is <see cref="TimedOutWithoutWipe"/>, no result).</summary>
    public static bool IsLoss(string outcome) => outcome is "Failed" or "Defeated" or "Timed out";

    /// <summary>
    /// The record's name for a time-out with no wipe (owner, 2026-11-05): neither a win nor a loss, for the raid's size and
    /// the Special Forces alike. Marfach Caverns timed out after 91 pulls on an encounter it never reached: a routing fault,
    /// not a lost fight. The ledger and the Herald still say "Timed out".
    /// </summary>
    public const string TimedOutWithoutWipe = RealmEventRecordStore.TimedOutWithoutWipe;

    /// <summary>The outcome a raid's record keeps: a time-out after one or more wipes is a loss; without one it is no result.</summary>
    public static string RecordOutcome(string outcome, int wipes) => RealmEventRecordStore.RecordOutcome(outcome, wipes);

    /// <summary>
    /// Parties the realm sends after a wipe (1-3), over the encounter's whole record (oldest first): three to start; a win
    /// the Special Forces took part in, one fewer, never under one; a win without them, no change; a defeat or time-out,
    /// one more, never over three. Uncounted results (interrupted, failed rallies...) change nothing.
    /// </summary>
    public static int CallUpParties(IEnumerable<RaidResult> resultsOldestFirst)
    {
        int parties = MaximumParties;
        foreach (RaidResult result in resultsOldestFirst ?? [])
        {
            if (result.AssistedWin) parties = Math.Max(MinimumParties, parties - 1);
            else if (IsLoss(result.Outcome)) parties = Math.Min(MaximumParties, parties + 1);
        }
        return parties;
    }

    public static int CallUpSize(IEnumerable<RaidResult> resultsOldestFirst) => CallUpParties(resultsOldestFirst) * PartySize;

    /// <summary>A Horn raid's wipe at its final encounter calls them (once per raid; Request keeps the one-call rule)
    /// while the raid still has a retry: True 1.65 calls the battle off at its third wipe.</summary>
    // A launcher-forced raid gets them after a wipe too (the owner's test raids); only the call at the start of a
    // battle leaves forced raids out, because their record isn't kept. `forced` is kept for the callers' clarity.
    public static bool SpecialForcesAnswer(int finalWipes, bool forced) =>
        Enabled && finalWipes >= 1 && !RealmRaidRecruitmentPolicy.HornBattleFailed(finalWipes);

    /// <summary>The last two counted results (wins and losses) at this dungeon were both losses.</summary>
    public static bool ComesFromStart(IEnumerable<string> outcomesOldestFirst)
    {
        string[] last = (outcomesOldestFirst ?? []).Where(o => IsWin(o) || IsLoss(o)).TakeLast(2).ToArray();
        return last.Length == 2 && last.All(IsLoss);
    }

    public static bool ComesFromStart(string raidId) => Enabled && ComesFromStart(RealmRaidRecord.Get(raidId));

    private static List<RaidResult> History(string raidId) => RealmRaidRecord.GetResults(raidId);

    /// <summary>Asks for the raid's call-up, sized by its encounter's record (<see cref="CallUpParties"/>); the members
    /// arrive on the next coordinator tick. Safe inside the raid lock.</summary>
    public static void Request(string raidId, string raidName, eRealm realm, string reason)
    {
        if (!Enabled) return;
        RealmRaidRecord.EnsureLoading();
        List<RaidResult> history = History(raidId);
        int members = CallUpSize(history);
        lock (Sync)
        {
            if (!Answered.Add(raidId)) return;
            if (members == 0)
            {
                if (Log.IsInfoEnabled)
                    Log.Info($"REALM_RAID_SPECIAL_FORCES_NOT_NEEDED event={raidId} reason=\"{reason}\" history=\"{string.Join(",", history)}\"");
                return;
            }
            Calls.Enqueue(new(raidId, raidName, realm, members, reason));
        }
    }

    /// <summary>The raid ended: its Special Forces go home, and a Horn raid's result joins the encounter's record (a raid
    /// started from the launcher is not part of it), with whether they took part in it.</summary>
    public static void RaidEnded(string raidId, string outcome, bool forced = false)
    {
        bool joined;
        lock (Sync)
        {
            Answered.Remove(raidId);
            Dismissals.Enqueue(raidId);
            joined = JoinedCount.Remove(raidId, out int count) && count > 0;
        }
        RealmRaidRecord.Add(raidId, outcome, forced, joined);
    }

    /// <summary>How many Special Forces joined this raid so far (including any who have since left it).</summary>
    public static int Joined(string raidId)
    {
        lock (Sync) return JoinedCount.GetValueOrDefault(raidId);
    }

    /// <summary>Game loop, coordinator phase, outside the raid and coordinator locks: arrivals and departures.</summary>
    public static void Pulse(long now)
    {
        if (Enabled) RealmRaidRecord.EnsureLoading();
        while (true)
        {
            string dismissed;
            lock (Sync) { if (!Dismissals.TryDequeue(out dismissed)) break; }
            Dismiss(dismissed, "the raid ended");
        }
        while (true)
        {
            Call call;
            lock (Sync) { if (!Calls.TryDequeue(out call)) break; }
            Arrive(call);
        }
        if (now < _nextCleanup) return;
        _nextCleanup = now + 10_000;
        // Anyone no longer part of a running raid leaves (a broken-up party, a member taken out of the world).
        // switched off mid-raid: the ones already here stay with their raid until it ends (nothing new arrives)
        foreach (var (raidId, members) in Snapshot())
        {
            if (!AutonomousRealmRaid.IsRunning(raidId)) { Dismiss(raidId, "the raid is no longer running"); continue; }
            foreach (GameBot bot in members)
            {
                bool outOfWorld = bot.ObjectState != GameObject.eObjectState.Active;
                bool gone = bot.ObjectState == GameObject.eObjectState.Deleted || bot.Group == null ||
                            AutonomousRealmRaid.GetView(bot.Group) == null;
                lock (Sync)
                {
                    if (outOfWorld && !SeenOutOfWorld.Add(bot)) gone = true; // out of the world on two checks running
                    else if (!outOfWorld) SeenOutOfWorld.Remove(bot);
                }
                if (gone) Remove(raidId, bot);
            }
        }
    }

    private static (string RaidId, GameBot[] Members)[] Snapshot()
    {
        lock (Sync) return Members.Select(p => (p.Key, p.Value.ToArray())).ToArray();
    }

    public static int Count(string raidId)
    {
        lock (Sync) return Members.GetValueOrDefault(raidId)?.Count ?? 0;
    }

    private static void Arrive(Call call)
    {
        if (!AutonomousRealmRaid.TryGetSpecialForcesPost(call.RaidId, out ushort region, out Vector3 post) ||
            !PartyClasses.TryGetValue(call.Realm, out eCharacterClass[] classes))
        {
            Log.Warn($"REALM_RAID_SPECIAL_FORCES_FAILED event={call.RaidId} reason=\"the raid has no muster hub\"");
            return;
        }
        var names = new HashSet<string>(AutonomousBotRegistry.Snapshot().Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        int joined = 0;
        for (int party = 0; party < call.Members / PartySize; party++)
        {
            GameBot[] members = classes.Select((c, slot) => Create(call.Realm, c, region, post, party, slot, names)).ToArray();
            if (members.Any(b => b == null))
            {
                foreach (GameBot made in members.Where(b => b != null)) made.Delete();
                Log.Warn($"REALM_RAID_SPECIAL_FORCES_FAILED event={call.RaidId} party={party + 1} reason=\"a member could not enter the world\"");
                continue;
            }
            foreach (GameBot bot in members) AutonomousRealmRaid.ReserveSpecialForce(bot, call.RaidId);
            if (!AutonomousBotGroupCoordinator.FormForcedRaidParty(call.RaidId, members, out string refusal))
            {
                foreach (GameBot bot in members) bot.Delete();
                Log.Warn(FailedLine(call.RaidId, party + 1, refusal));
                continue;
            }
            lock (Sync)
            {
                if (!Members.TryGetValue(call.RaidId, out var list)) Members[call.RaidId] = list = new();
                list.AddRange(members);
                JoinedCount[call.RaidId] = JoinedCount.GetValueOrDefault(call.RaidId) + members.Length;
            }
            joined += members.Length;
        }
        Log.Info(ArrivedLine(call.RaidId, call.Realm, joined, call.Members, call.Reason, History(call.RaidId).Select(r => r.ToString())));
        if (joined == 0) return;
        GameWideAnnouncements.Queue(AnnouncementKind.PveRealmEvent,
            $"The {GuildName} answer {GlobalConstants.RealmToName(call.Realm)}'s call at {call.RaidName}!");
        RealmEventNotices.Queue(call.RaidId, call.Realm,
            $"{joined} of the {GuildName} have arrived at the hub and are marching to join the {call.RaidName} expedition.");
    }

    /// <summary>The log line for a party the raid refused, naming the refusal (in-game check 2026-11-04 logged only
    /// "the raid did not accept the party", three times, and the cause had to be read out of the code).</summary>
    public static string FailedLine(string raidId, int party, string refusal) =>
        $"REALM_RAID_SPECIAL_FORCES_FAILED event={raidId} party={party} reason=\"the raid did not accept the party\" " +
        $"refusal=\"{(string.IsNullOrWhiteSpace(refusal) ? "unknown" : refusal)}\"";

    /// <summary>The realm by its name: eRealm.Albion shares its value with _FirstPlayerRealm, so ToString() can print that.</summary>
    public static string ArrivedLine(string raidId, eRealm realm, int joined, int asked, string reason, IEnumerable<string> history) =>
        $"REALM_RAID_SPECIAL_FORCES_ARRIVED event={raidId} realm={GlobalConstants.RealmToName(realm)} members={joined} asked={asked} " +
        $"reason=\"{reason}\" history=\"{string.Join(",", history ?? [])}\"";

    private static GameBot Create(eRealm realm, eCharacterClass characterClass, ushort region, Vector3 post, int party, int slot, ISet<string> names)
    {
        try
        {
            eGender gender = Random.Shared.Next(2) == 0 ? eGender.Male : eGender.Female;
            var identity = AutonomousBotIdentityGenerator.GenerateForClass(realm, gender, characterClass, names);
            names.Add(identity.Name);
            // A small circle around the hub, one party to a ring.
            double angle = (party * PartySize + slot) * Math.PI * 2 / MaximumMembers;
            int x = (int)(post.X + Math.Cos(angle) * (80 + 50 * party)), y = (int)(post.Y + Math.Sin(angle) * (80 + 50 * party)), z = (int)post.Z;
            string now = DateTime.UtcNow.ToString("O");
            var record = new OfflineWorldBotRecord
            {
                BotId = Interlocked.Increment(ref _lastBotId), Name = identity.Name, Realm = (int)realm,
                ClassId = (int)characterClass, ClassName = BotManager.GetClassNameById((byte)characterClass), RaceId = (int)identity.Race, RaceName = identity.Race.ToString(),
                Gender = (int)gender, Level = 50, Experience = 169_999_999_950L, IsOnline = true, IsAlive = true,
                Activity = GuildName, CurrentGoal = "Reinforcing the realm's expedition", LastUpdateUtc = now,
                RegionId = region, X = x, Y = y, Z = z, BindRegionId = region, BindX = x, BindY = y, BindZ = z,
            };
            GameBot bot = GameBot.CreateEpicSpecialForce(record);
            bot.SynchronizePositionForLoginValidation();
            AutonomousStuckWatchdog.RepairInvalidLoginPosition(bot); // off the mesh beside the hub: onto it
            if (!bot.AddToWorld()) { bot.Delete(); return null; }
            return bot;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not create an {GuildName} {characterClass} for {GlobalConstants.RealmToName(realm)}", exception);
            return null;
        }
    }

    private static void Dismiss(string raidId, string why)
    {
        GameBot[] members;
        lock (Sync)
        {
            if (!Members.Remove(raidId, out var list)) return;
            members = list.ToArray();
            foreach (GameBot bot in members) SeenOutOfWorld.Remove(bot);
        }
        foreach (GameBot bot in members) bot.Delete();
        if (Log.IsInfoEnabled)
            Log.Info($"REALM_RAID_SPECIAL_FORCES_LEFT event={raidId} members={members.Length} reason=\"{why}\"");
    }

    private static void Remove(string raidId, GameBot bot)
    {
        lock (Sync)
        {
            if (Members.TryGetValue(raidId, out var list)) list.Remove(bot);
            SeenOutOfWorld.Remove(bot);
        }
        bot.Delete();
    }

    /// <summary>Tests only.</summary>
    public static void ResetForTests()
    {
        lock (Sync)
        {
            Calls.Clear(); Dismissals.Clear(); Answered.Clear(); Members.Clear(); SeenOutOfWorld.Clear(); JoinedCount.Clear();
        }
        RealmRaidRecord.ResetForTests();
    }

    /// <summary>Tests only: the dungeon's record, oldest first.</summary>
    public static void SetHistoryForTests(string raidId, params string[] outcomes) => RealmRaidRecord.SetForTests(raidId, outcomes);

    /// <summary>Tests only: Special Forces that joined a raid (as Arrive counts them).</summary>
    public static void JoinedForTests(string raidId, int members)
    {
        lock (Sync) JoinedCount[raidId] = JoinedCount.GetValueOrDefault(raidId) + members;
    }

    /// <summary>Tests only: the size of the next call waiting to arrive (0 when none).</summary>
    public static int NextCallMembersForTests
    {
        get { lock (Sync) return Calls.TryPeek(out var call) ? call.Members : 0; }
    }

    /// <summary>Tests only: calls waiting to arrive.</summary>
    public static int PendingCalls
    {
        get { lock (Sync) return Calls.Count; }
    }
}
