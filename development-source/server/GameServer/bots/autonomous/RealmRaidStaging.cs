using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;

namespace DOL.GS;

public static class RealmRaidStaging
{
    public static bool TryDungeonPost(IPathfindingMgr nav, Zone exterior, Vector3 portal, int slot,
        IReadOnlyCollection<Vector3> occupied, out Vector3 staging)
    {
        staging = default;
        if (exterior == null || nav == null || !nav.IsAvailable || !nav.HasNavmesh(exterior)) return false;
        PostHazard[] hazards = Hazards(exterior.ZoneRegion, portal, 1800 + HazardSearchMargin);
        for (int attempt = 0; attempt < 36; attempt++)
        {
            double angle = (attempt * 30 + slot * 137.5) * Math.PI / 180;
            float radius = 500 + slot % 5 * 200 + (slot >= 30 ? attempt / 12 * 250 : 0);
            Vector3 raw = portal + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
            Vector3? floor = nav.GetClosestPoint(exterior, raw, 64, 64, 512, nav.DefaultFilters);
            if (!floor.HasValue || !IsClear(floor.Value, hazards) || !AutonomousRendezvousNavigation.HasLocalExit(nav, exterior, floor.Value) ||
                occupied.Any(p => Vector3.DistanceSquared(p, floor.Value) < 200 * 200) ||
                (occupied.Count > 0 && !AutonomousZoneItinerary.HasCompleteCorridor(nav,exterior,occupied.First(),floor.Value)) ||
                !AutonomousZonePointApproach.TryResolve(nav, exterior, floor.Value, portal, 220, out _)) continue;
            staging = floor.Value;
            return true;
        }
        return false;
    }
    public static bool TryDragonPost(IPathfindingMgr nav, Zone zone, Vector3 home, int slot, IReadOnlyCollection<Vector3> occupied, out Vector3 staging)
    {
        staging = default;
        if (slot < 0 || slot >= RealmRaidRecruitmentPolicy.FormationSlots || nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        // Posts on the ground the route planner places the lair on (its zone road areas). Hibernia's post 6 lay on
        // ground the planner took for another area, so the party's route led out through Shannon Estuary and no zone
        // step reached the post (route audit 2026-10-18).
        int? lairArea = AutonomousZoneRoads.AreaAt(zone, home);
        PostHazard[] hazards = Hazards(zone.ZoneRegion, home, 6100 + HazardSearchMargin);
        for (int attempt = 0; attempt < 36; attempt++)
        {
            double angle = (attempt * 47.5 + slot * 137.5) * Math.PI / 180;
            float radius = 3600 + attempt % 6 * 500;
            var raw = home + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
            var floor = nav.GetClosestPoint(zone, raw, 96, 96, 4096, nav.DefaultFilters);
            if (!floor.HasValue || Math.Abs(floor.Value.Z - home.Z) > 768 || !IsClear(floor.Value, hazards) ||
                lairArea.HasValue && AutonomousZoneRoads.AreaAt(zone, floor.Value) is int area && area != lairArea.Value ||
                occupied.Any(p => Vector3.DistanceSquared(p, floor.Value) < 600 * 600) ||
                !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value) ||
                !AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, home, 300, out _) ||
                !HasOpenPartySpace(nav, zone, floor.Value)) continue;
            staging = floor.Value;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The hub posts' safety rule, for staging posts too (dragon and dungeon audits 2026-11-11: 7 of Midgard's 38
    /// dragon posts stood in the aggro reach of level 54-63 drakulv and flies, 3 in Hibernia, 2 in Albion). No post
    /// within reach of an aggressive neutral monster's spawn (its aggro range plus a party's spread, at least the hub's
    /// 750), or within 100 of any other NPC. A monster that cons grey to a level 50 never attacks one (StandardMobBrain
    /// CanAggroTarget), so it does not count.
    /// </summary>
    public readonly record struct PostHazard(Vector3 Position, float Clearance);
    public const float MonsterClearance = 750, NpcClearance = 100, PartySpread = 300;
    private const int RaiderLevel = 50;
    private const float HazardSearchMargin = 2000;
    private const long HazardCacheMilliseconds = 120_000;

    public static float HazardClearance(bool aggressiveNeutral, int level, int aggroRange) =>
        !aggressiveNeutral || (ConColor)ConLevels.GetConLevel(RaiderLevel, level) <= ConColor.GREY
            ? NpcClearance : Math.Max(MonsterClearance, aggroRange + PartySpread);

    public static bool IsClear(Vector3 post, IReadOnlyList<PostHazard> hazards)
    {
        foreach (PostHazard hazard in hazards)
            if (Vector3.DistanceSquared(hazard.Position, post) < hazard.Clearance * hazard.Clearance) return false;
        return true;
    }

    private static readonly object HazardSync = new();
    private static readonly Dictionary<(ushort Region, int X, int Y), (long Until, PostHazard[] Hazards)> HazardCache = new();

    // One region scan per raid start (the posts of all 38 slots are searched within two minutes), never per AI turn.
    private static PostHazard[] Hazards(Region region, Vector3 center, float radius)
    {
        if (region == null) return [];
        long now = GameLoop.GameLoopTime;
        var key = (region.ID, (int)center.X, (int)center.Y);
        lock (HazardSync)
        {
            if (HazardCache.TryGetValue(key, out var cached) && now < cached.Until) return cached.Hazards;
            foreach (var expired in HazardCache.Where(p => now >= p.Value.Until).Select(p => p.Key).ToArray())
                HazardCache.Remove(expired);
            var hazards = new List<PostHazard>();
            foreach (GameNPC npc in region.Objects.OfType<GameNPC>())
            {
                if (npc is GameBot or GameSummonedPet || !npc.IsAlive) continue;
                bool aggressive = npc.Realm == eRealm.None && npc.Brain is StandardMobBrain { AggroLevel: > 0 };
                // An aggressive monster is judged where it spawns (it returns there); anything else where it stands.
                Point3D spawn = aggressive ? npc.SpawnPoint : null;
                Vector3 position = spawn != null && (spawn.X != 0 || spawn.Y != 0) ? new(spawn.X, spawn.Y, spawn.Z) : new(npc.X, npc.Y, npc.Z);
                if (Vector3.DistanceSquared(position, center) > radius * radius) continue;
                hazards.Add(new(position, HazardClearance(aggressive, npc.Level,
                    aggressive ? ((StandardMobBrain)npc.Brain).AggroRange : 0)));
            }
            PostHazard[] result = hazards.ToArray();
            HazardCache[key] = (now + HazardCacheMilliseconds, result);
            return result;
        }
    }

    // Prove a party can spread around the post, not merely that one nav polygon exists.
    public static bool HasOpenPartySpace(IPathfindingMgr nav, Zone zone, Vector3 center)
    {
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        foreach (Vector2 offset in new[] { new Vector2(260, 0), new(-260, 0), new(0, 260), new(0, -260) })
        {
            var raw = center + new Vector3(offset, 0);
            var floor = nav.GetClosestPoint(zone, raw, 32, 32, 128, nav.DefaultFilters);
            if (!floor.HasValue || Math.Abs(floor.Value.Z - center.Z) > 128 ||
                !nav.HasLineOfSight(zone, center, floor.Value, nav.DefaultFilters) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, center, floor.Value) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, center)) return false;
        }
        return true;
    }

    /// <summary>Grounded and either at the lair or fighting wherever it was dragged.</summary>
    public static bool DragonCountsAsLanded(bool flying, bool nearLair, bool inCombat) =>
        !flying && (nearLair || inCombat);
}
