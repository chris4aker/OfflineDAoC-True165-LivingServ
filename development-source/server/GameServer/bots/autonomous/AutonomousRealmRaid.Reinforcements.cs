using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DOL.GS;

/// <summary>
/// Raiders who come back after the raid went in: every member that died and released, and every late member.
/// Audits 2026-11-11 (dragon-routes L3, df-routes T2, df-bosses L7, sh-routes 2, epic-routes 3, epic-bosses L3): they
/// walked back alone, never rode, and arrived one by one into a boss at full health or through respawned trash. Now,
/// as veteran players do after a wipe, they gather outside first (at the hub they stood up at, or the raid's staging
/// posts, whichever is nearer) and go back in together as a wave of eight, or after a short wait. The gather point is
/// decided once per release (per death count), never per tick.
/// </summary>
public static partial class AutonomousRealmRaid
{
    public const int ReinforcementWaveSize = 8;
    /// <summary>Everyone expected has gathered: still wait this long for corpses that are about to release.</summary>
    public const long ReinforcementShortWaitMilliseconds = 60_000;
    /// <summary>Never hold a gathered reinforcement longer than this.</summary>
    public const long ReinforcementMaxWaitMilliseconds = 180_000;
    public const float ReinforcementGatherRadius = 600;
    /// <summary>A dragon raider farther than this from the lair (beyond the staging ring) is not with the raid.</summary>
    public const float DragonReinforcementDistance = 6_700;
    private const int HubGatherKey = -2, DungeonStagingGatherKey = -1;

    public static bool ReinforcementMustGather(bool started, bool catchingUp, bool isDungeon, bool insideEncounterRegion,
        bool inGatherRegion, float distanceToLair) =>
        started && catchingUp && inGatherRegion && (isDungeon ? !insideEncounterRegion : distanceToLair > DragonReinforcementDistance);

    public static bool ReinforcementWaveGoes(int arrived, int expected, long firstArrival, long now) =>
        arrived > 0 && (arrived >= ReinforcementWaveSize || now - firstArrival >= ReinforcementMaxWaitMilliseconds ||
                        arrived >= expected && now - firstArrival >= ReinforcementShortWaitMilliseconds);

    public readonly record struct ReinforcementOrder(Vector3 Point, ushort Region, bool Arrived, int Gathered, int Expected);

    private sealed class Reinforcement
    {
        public GameBot Bot;
        public int DeathCount;
        public bool Gather;
        public int Key;
        public Vector3 Point;
        public ushort Region;
        public long ArrivedTick;
        public bool Released;
    }

    private sealed class ReinforcementBook
    {
        public readonly Dictionary<long, Reinforcement> Bots = new();
        public long StatsUntil;
        public readonly Dictionary<int, (int Arrived, int Expected, long First)> Stats = new();
    }

    // Dies with its raid: nothing to clean up when a raid ends.
    private static readonly ConditionalWeakTable<Raid, ReinforcementBook> ReinforcementBooks = new();

    /// <summary>
    /// True while this raider must travel to its gather point or hold there; false when it goes on as before (not a
    /// reinforcement, already with the raid, or its wave has gone). One lookup per AI turn, like GetTravelView.
    /// </summary>
    public static bool TryReinforcementHold(GameBot bot, out ReinforcementOrder order)
    {
        order = default;
        Group group = bot?.Group;
        if (group == null || !bot.IsAlive) return false;
        lock (Sync)
        {
            if (!Membership.TryGetValue(group, out Raid raid) || !raid.Started || !raid.Parties.TryGetValue(group, out Party party) ||
                !party.CatchingUp.Contains(bot.DatabaseID)) return false;
            ReinforcementBook book = ReinforcementBooks.GetValue(raid, _ => new ReinforcementBook());
            int deaths = bot.PersistentRecord?.DeathCount ?? 0;
            long now = GameLoop.GameLoopTime;
            if (!book.Bots.TryGetValue(bot.DatabaseID, out Reinforcement entry) || entry.Bot != bot || entry.DeathCount != deaths)
                book.Bots[bot.DatabaseID] = entry = DecideReinforcement(raid, bot, deaths);
            if (!entry.Gather || entry.Released || bot.CurrentRegionID != entry.Region) return false;
            if (entry.ArrivedTick == 0 && AtGatherPoint(raid, entry))
            {
                entry.ArrivedTick = now;
                book.StatsUntil = 0;
            }
            var (arrived, expected, first) = WaveStats(raid, book, entry.Key, now);
            if (arrived > 0 && ReinforcementWaveGoes(arrived, expected, first, now))
            {
                foreach (Reinforcement member in book.Bots.Values.Where(r => r.Key == entry.Key && r.Gather && !r.Released && r.ArrivedTick > 0))
                    member.Released = true;
                book.StatsUntil = 0;
                Log.Info($"REALM_RAID_REINFORCEMENT_WAVE event={raid.Definition.Id} gather={GatherName(entry.Key)} size={arrived} " +
                         $"expected={expected} waited={(now - first) / 1000}s");
                if (entry.Released) return false;
            }
            order = new(entry.Point, entry.Region, entry.ArrivedTick > 0, arrived, Math.Max(expected, arrived));
            return true;
        }
    }

    /// <summary>
    /// A returning or late raider inside the raid's dungeon while the route musters for its current target: go to the
    /// muster (RealmRaidDungeonRoute.RegroupPoint, outside the target's aggro range on the way back toward the last
    /// waypoint), not to the party's old post beside the boss. False when no muster is running.
    /// </summary>
    public static bool TryReinforcementRegroup(GameBot bot, out Vector3 point)
    {
        point = default;
        Group group = bot?.Group;
        if (group == null || !bot.IsAlive) return false;
        lock (Sync)
        {
            if (!Membership.TryGetValue(group, out Raid raid) || !raid.Started || raid.DungeonRoute?.RegroupPoint is not Vector3 regroup ||
                bot.CurrentRegionID != raid.Definition.Region || !raid.Parties.TryGetValue(group, out Party party) ||
                !party.CatchingUp.Contains(bot.DatabaseID)) return false;
            point = regroup;
            return true;
        }
    }

    private static string GatherName(int key) => key == HubGatherKey ? "hub" : key == DungeonStagingGatherKey ? "staging" : $"staging-{key}";

    private static Reinforcement DecideReinforcement(Raid raid, GameBot bot, int deaths)
    {
        var entry = new Reinforcement { Bot = bot, DeathCount = deaths };
        Vector3 here = new(bot.X, bot.Y, bot.Z);
        ushort stagingRegion = raid.Definition.IsDungeon ? raid.DungeonRoute.Entrance.SourceRegion : raid.Definition.Region;
        float lairDistance = 0;
        if (!raid.Definition.IsDungeon)
        {
            Point3D home = DragonLairPlacement.Home(raid.Definition.Realm);
            lairDistance = Vector2.Distance(new(here.X, here.Y), new(home.X, home.Y));
        }
        // Candidates in the bot's own region (it walks there without crossing regions): a dungeon raid's hub, where
        // its dead stand up, and the staging posts outside the entrance; a dragon raid's staging posts.
        var candidates = new List<(int Key, Vector3 Point)>();
        if (raid.Definition.IsDungeon && raid.Hub != null && raid.Hub.Region == bot.CurrentRegionID)
            candidates.Add((HubGatherKey, raid.Hub.Center));
        if (stagingRegion == bot.CurrentRegionID)
            foreach (var post in raid.StagingPosts)
                candidates.Add((raid.Definition.IsDungeon ? DungeonStagingGatherKey : post.Key, post.Value));
        entry.Gather = ReinforcementMustGather(raid.Started, true, raid.Definition.IsDungeon,
            bot.CurrentRegionID == raid.Definition.Region, candidates.Count > 0, lairDistance);
        if (!entry.Gather) return entry;
        var nearest = candidates.MinBy(c => Vector3.DistanceSquared(c.Point, here));
        entry.Key = nearest.Key;
        entry.Point = nearest.Point;
        entry.Region = bot.CurrentRegionID;
        return entry;
    }

    private static bool AtGatherPoint(Raid raid, Reinforcement entry)
    {
        Vector3 here = new(entry.Bot.X, entry.Bot.Y, entry.Bot.Z);
        if (entry.Key == DungeonStagingGatherKey)
            return raid.StagingPosts.Values.Any(p => Vector3.DistanceSquared(p, here) <= ReinforcementGatherRadius * ReinforcementGatherRadius);
        return Vector3.DistanceSquared(entry.Point, here) <= ReinforcementGatherRadius * ReinforcementGatherRadius;
    }

    // Recounted at most once a second per raid (and at once after an arrival or a wave), not on every waiting bot's turn.
    private static (int Arrived, int Expected, long First) WaveStats(Raid raid, ReinforcementBook book, int key, long now)
    {
        if (now >= book.StatsUntil)
        {
            book.Stats.Clear();
            foreach (long id in book.Bots.Where(p => !StillReinforcing(raid, p.Value)).Select(p => p.Key).ToArray())
                book.Bots.Remove(id);
            foreach (Reinforcement r in book.Bots.Values)
            {
                if (!r.Gather || r.Released || !r.Bot.IsAlive) continue;
                var (arrived, expected, first) = book.Stats.GetValueOrDefault(r.Key, (0, 0, long.MaxValue));
                if (r.ArrivedTick > 0) { arrived++; first = Math.Min(first, r.ArrivedTick); }
                book.Stats[r.Key] = (arrived, expected + 1, first);
            }
            book.StatsUntil = now + 1_000;
        }
        return book.Stats.GetValueOrDefault(key, (0, 0, long.MaxValue));
    }

    private static bool StillReinforcing(Raid raid, Reinforcement r) =>
        r.Bot.Group != null && Membership.GetValueOrDefault(r.Bot.Group) == raid &&
        raid.Parties.TryGetValue(r.Bot.Group, out Party party) && party.CatchingUp.Contains(r.Bot.DatabaseID) &&
        AutonomousBotRegistry.Contains(r.Bot.DatabaseID);

    // ---- Formation slots -------------------------------------------------------------------------------------------

    /// <summary>A slot whose hub or staging post could not be placed is not searched again for this long.</summary>
    public const long FailedSlotRetryMilliseconds = 60_000;

    /// <summary>
    /// The lowest free formation slot that can be placed, skipping a slot that just failed (epic-routes M7: the lowest
    /// free slot was always taken, so one slot with no hub or staging post blocked every later party and capped the
    /// raid below 200). <paramref name="place"/> is tried at most once per slot.
    /// </summary>
    public static int NextPlaceableSlot(int maximum, Func<int, bool> used, Func<int, bool> recentlyFailed, Func<int, bool> place,
        Action<int> failed)
    {
        for (int slot = 0; slot < maximum; slot++)
        {
            if (used(slot) || recentlyFailed(slot)) continue;
            if (place(slot)) return slot;
            failed(slot);
        }
        return -1;
    }

    private static readonly ConditionalWeakTable<Raid, Dictionary<int, long>> FailedSlots = new();

    /// <summary>For TryJoin, TryGetForcedStaging and CommitForcedParty (caller holds the raid lock).</summary>
    private static bool TryPlaceableSlot(Raid raid, out int slot, out Vector3 staging, out Vector3 hubPost, bool specialForces = false)
    {
        long now = GameLoop.GameLoopTime;
        Dictionary<int, long> failedUntil = FailedSlots.GetValue(raid, _ => new Dictionary<int, long>());
        Vector3 foundStaging = default, foundHub = default;
        // the Epic Special Forces (Living Server, off by default) take the three slots past the realm's 38 first, so they
        // come on top of the realm's parties, and a realm slot only when none of those three can be placed
        Func<int, bool> used = s => raid.Parties.Values.Any(p => p.FormationSlot == s);
        Func<int, bool> recentlyFailed = s => failedUntil.GetValueOrDefault(s) > now;
        Func<int, bool> place = s => TryStaging(raid, s, out foundStaging) && TryHubPost(raid, s, out foundHub);
        Action<int> failed = s => failedUntil[s] = now + FailedSlotRetryMilliseconds;
        slot = specialForces
            ? NextPlaceableSlot(RealmRaidRecruitmentPolicy.FormationSlots,
                s => s < RealmRaidRecruitmentPolicy.MaximumParties || used(s), recentlyFailed, place, failed)
            : -1;
        if (slot < 0)
            slot = NextPlaceableSlot(specialForces ? RealmRaidRecruitmentPolicy.FormationSlots : RealmRaidRecruitmentPolicy.MaximumParties,
                used, recentlyFailed, place, failed);
        staging = foundStaging;
        hubPost = foundHub;
        return slot >= 0;
    }
}
