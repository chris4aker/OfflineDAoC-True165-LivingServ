using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS
{
    /// <summary>
    /// Living Server's raid tactics in True 1.65's raid route (EpicEncounterTactics, bot_epic_encounter_tactics, off by
    /// default). Carried from Living Server 1.1.4's RealmRaidDungeonRoute: the required-add stands (Scurceol Hyrde's orbs
    /// shot from the floor), the switch to a tactic's target, and setting a tactic aside. True 1.65's route calls
    /// <see cref="ApplyTactics"/> once per turn; with the switch off it returns at once and the route is True 1.65's.
    /// </summary>
    public sealed partial class RealmRaidDungeonRoute
    {
        /// <summary>How far from a required add its ranged raiders stand at most: inside the usual 1,500 spell range, with room to spare.</summary>
        public const int RequiredAddRangedReach = 1_400;
        /// <summary>A ranged stand is never closer than this to the boss (Scurceol's melee reach is 350)...</summary>
        public const int RequiredAddStandMinimumBossDistance = 400;
        /// <summary>...and is looked for first beyond this (his aggro range is 600), so the casters don't stand in his fight.</summary>
        public const int RequiredAddStandBossClearance = 700;
        /// <summary>A stand is on the raid's floor around the boss: within this of his floor's height. On the real Krondon mesh the
        /// platform is 17,050-17,250 (his floor 17,178) and the orbs' perch slopes start at 17,307: those slopes see their orb
        /// but nobody can walk onto them, and every walk tried on one costs a corridor search.</summary>
        public const int RequiredAddStandFloorBand = 112;
        /// <summary>The sight line runs from a raider's eyes, this far over the stand, to this far over the add's own spot.</summary>
        public const int SightEyeHeight = 60, SightAimHeight = 20;
        /// <summary>Walkable mesh within this band over the sight line means solid ground there (a ledge, a perch, a rock top).</summary>
        public const int SightSolidBand = 400;
        /// <summary>The mesh's own height error: a surface less than this over the line does not block it.</summary>
        public const int SightTolerance = 8;
        /// <summary>The sight line is sampled this often (world units across).</summary>
        public const int SightSampleStep = 32;
        /// <summary>Where the floor ray toward the add ends, the floor beyond must fall away by more than this (a drop, not a wall).</summary>
        public const int SightDropStep = 64;
        /// <summary>How far below a drop's edge the ground under it is looked for.</summary>
        public const int SightDropDepth = 3_000;
        /// <summary>At most this many stands with sight are tried against the raid's walk (each try is a corridor search).</summary>
        public const int RequiredAddStandReachTries = 8;

        /// <summary>
        /// Krondon, 2026-11-06 (01:49 and 02:16): Scurceol Hyrde's tactic sent the raid to his four Lyft Miht orbs, which float
        /// 230-580 above him. The ground-route gate failed for every orb, and the raid walked off to Fuladl. 1.1.2 fought them from a
        /// floor spot instead, but tested sight with the mesh's floor ray to the floor under the orb. The real mesh (probe
        /// UT_ScurceolOrbStandProbe, 2026-11-08) shows why that never worked: each orb hangs 48-55 over the top of its own rock
        /// perch, 400-530 across a pit (floor 900 lower) from the platform the raid stands on. A floor ray can't cross a pit, so
        /// 0 of the 277-321 reachable floor spots within 1,400 of each orb passed (PC soak: "no reachable floor spot within 1400
        /// has line of sight to it", three times; one orb of four died). The probe's 3-D line from the platform found 53, 254,
        /// 1-35 and 154 clear spots for the four orbs.
        /// Now: floor spots on rings around the add (every 50 out to 1,400, every 10 degrees) and the old four, within reach
        /// of it, on the boss's floor (<see cref="RequiredAddStandFloorBand"/>) and not on the boss, are tried nearest the add first (those beyond <see cref="RequiredAddStandBossClearance"/>
        /// of the boss before the rest); a spot needs <see cref="HasSightToAirborne"/>, then a walk from the raid.
        /// False, with why, when none works.
        /// </summary>
        /// <param name="reach">The approach the raid can walk to for a floor point (only approaches the filter accepts), or null
        /// when none is proven.</param>
        public static bool TryRangedStand(IPathfindingMgr nav, Zone zone, Vector3 add, Vector3 boss,
            Func<Vector3, Func<Vector3, bool>, Vector3?> reach, out Vector3 stand, out string why)
        {
            stand = default;
            if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            { why = "no navigation mesh here"; return false; }
            var floors = new List<Vector3>();
            void Add(Vector3? p)
            {
                if (p is Vector3 v && !floors.Any(f => Vector3.DistanceSquared(f, v) < 48 * 48)) floors.Add(v);
            }
            Add(nav.GetClosestPoint(zone, add, 128, 128, RangedFlyerReach, nav.DefaultFilters));
            Vector3? bossFloor = nav.GetClosestPoint(zone, boss, 64, 64, 256, nav.DefaultFilters);
            Add(bossFloor);
            var level = new Vector3(add.X, add.Y, boss.Z);
            foreach (float t in new[] { 0.67f, 0.33f })
                Add(nav.GetClosestPoint(zone, Vector3.Lerp(boss, level, t), 96, 96, 256, nav.DefaultFilters));
            // The raid's floor around the add: the boss's own height, where the raid fights.
            float floorZ = bossFloor?.Z ?? boss.Z;
            // Every 50 out and every 10 degrees: on the real mesh orb 3's clear spots are a few patches of the platform's far edge,
            // and a sparser ring (every 100, every 15 degrees) found one.
            for (int radius = 200; radius <= RequiredAddRangedReach; radius += 50)
            for (int degrees = 0; degrees < 360; degrees += 10)
            {
                float a = degrees * MathF.PI / 180f;
                Add(nav.GetClosestPoint(zone, new Vector3(add.X + MathF.Cos(a) * radius, add.Y + MathF.Sin(a) * radius, floorZ),
                    48, 48, 320, nav.DefaultFilters));
            }
            if (floors.Count == 0)
            { why = "no floor under it or near its boss"; return false; }
            bool Allowed(Vector3 p) => Vector3.Distance(p, add) <= RequiredAddRangedReach &&
                                       Vector3.Distance(p, boss) >= RequiredAddStandMinimumBossDistance &&
                                       MathF.Abs(p.Z - floorZ) <= RequiredAddStandFloorBand;
            Vector3[] ordered = floors.Where(Allowed)
                .OrderBy(p => Vector3.Distance(p, boss) < RequiredAddStandBossClearance ? 1 : 0)
                .ThenBy(p => Vector3.DistanceSquared(p, add)).ToArray();
            if (ordered.Length == 0)
            { why = $"none of {floors.Count} floor spots is within {RequiredAddRangedReach} of it, off its boss and on his floor"; return false; }
            bool Accept(Vector3 p) => Allowed(p) && HasSightToAirborne(nav, zone, p, add);
            int sighted = 0, tried = 0;
            foreach (Vector3 floor in ordered)
            {
                if (!HasSightToAirborne(nav, zone, floor, add)) continue;
                sighted++;
                if (reach(floor, Accept) is Vector3 approach)
                {
                    stand = approach;
                    why = $"ranged from the floor {(int)Vector3.Distance(approach, add)} away, {(int)Vector3.Distance(approach, boss)} from its boss";
                    return true;
                }
                if (++tried >= RequiredAddStandReachTries) break;
            }
            why = sighted == 0 ? $"none of {ordered.Length} floor spots within {RequiredAddRangedReach} has a clear line to it"
                : $"none of the {tried} nearest floor spots with a clear line to it is reachable from the raid";
            return false;
        }

        /// <summary>
        /// Whether a raider on <paramref name="floor"/> sees <paramref name="add"/> in the air, judged from the mesh (the server has
        /// no other geometry). The mesh holds only walkable surfaces, so two checks:
        /// 1. The 3-D line from the eyes to the add passes under no walkable surface (within <see cref="SightSolidBand"/> over it):
        ///    the top of a ledge, a wall or the add's own perch in the way.
        /// 2. A wall up to the ceiling has no top: the floor ray toward the spot under the add either gets there, or the floor ends
        ///    at a drop, where the ground beyond lies more than <see cref="SightDropStep"/> lower (the pit, not a wall).
        /// </summary>
        public static bool HasSightToAirborne(IPathfindingMgr nav, Zone zone, Vector3 floor, Vector3 add)
        {
            Vector3 eye = floor + new Vector3(0, 0, SightEyeHeight), aim = add + new Vector3(0, 0, SightAimHeight);
            float across = Vector2.Distance(new(eye.X, eye.Y), new(aim.X, aim.Y));
            int samples = Math.Max(1, (int)(across / SightSampleStep));
            // From the add's end first: its perch blocks most lines, so a blocked line is found in a few queries.
            for (int k = samples - 1; k >= 1; k--)
            {
                Vector3 at = Vector3.Lerp(eye, aim, k / (float)samples);
                if (nav.GetClosestPointInBounds(zone, at, new Vector3(-4, -4, SightTolerance), new Vector3(4, 4, SightSolidBand),
                        nav.DefaultFilters) != null)
                    return false;
            }
            var under = new Vector3(add.X, add.Y, floor.Z);
            if (across < 1 || nav.HasLineOfSight(zone, floor, under, nav.DefaultFilters)) return true;
            // Where the floor ray stops (to a 64th of the way, 5-20 units)...
            float reached = 0, stopped = 1;
            for (int i = 0; i < 6; i++)
            {
                float mid = (reached + stopped) / 2;
                if (nav.HasLineOfSight(zone, floor, Vector3.Lerp(floor, under, mid), nav.DefaultFilters)) reached = mid; else stopped = mid;
            }
            // ...and just past it, the highest ground from the floor's height down.
            Vector3 dir = Vector3.Normalize(under - floor);
            Vector3 past = Vector3.Lerp(floor, under, stopped) + dir * 32;
            Vector3? beyond = nav.GetClosestPointInBounds(zone, past, new Vector3(-8, -8, -SightDropDepth), new Vector3(8, 8, SightDropStep),
                nav.DefaultFilters);
            return beyond is Vector3 ground && ground.Z < floor.Z - SightDropStep;
        }

        private bool TryEncounterApproach(GameNPC npc, GameLiving[] participants, out Vector3 approach) =>
            TryPointApproach(new(npc.X, npc.Y, npc.Z), participants, out approach);

        private bool TryPointApproach(Vector3 raw, GameLiving[] participants, out Vector3 approach)
        {
            approach = raw;
            var nav = PathfindingProvider.Instance;
            if (!nav.IsAvailable) return false;
            Vector3[] anchors = DistinctAnchors(Front, participants
                .Where(p => p.IsAlive && p.CurrentRegionID == _region && (p.Group == null || p.Group.LivingLeader == p))
                .Select(p => new Vector3(p.X, p.Y, p.Z)), 6);
            foreach (Vector3 anchor in anchors)
            {
                Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)anchor.X, (int)anchor.Y);
                if (zone != null && nav.HasNavmesh(zone) && AutonomousZonePointApproach.TryResolve(nav, zone, anchor, raw, 220, out Vector3 found))
                { approach = found; return true; }
            }
            return false;
        }

        /// <summary>The region's NPCs as of this turn (AdvanceCore), for the tactic checks.</summary>
        private IReadOnlyList<GameNPC> _live = [];

        /// <summary>
        /// One turn of the tactics, from AdvanceCore once the current target is known to be alive: hazards are noted, the
        /// target's tactic may send the raid to another encounter (a required add, from its floor stand), a blasting boss gets
        /// its withdraw spots, and a boss fought at home that left home pauses the no-damage clock like one out of its
        /// tether. True: the turn is used (the raid is leading the boss home).
        /// </summary>
        private bool ApplyTactics(GameNPC[] live, GameLiving[] participants, long now)
        {
            _live = live;
            if (!EpicEncounterTactics.Enabled) return false;
            EpicEncounterTactics.ObserveHazards(live);
            if (_target?.IsAlive != true || _target.ObjectState != GameObject.eObjectState.Active) return false;
            if (EpicEncounterTactics.RouteTarget(_target, live, Front, out string tacticReason) is { } tacticTarget && tacticTarget != _target)
            {
                // A required add is fought from a floor spot its ranged raiders can reach and see it from, never walked to.
                if (EpicEncounterTactics.RequiredAddOwner(tacticTarget, live) is { } addOwner)
                {
                    if (TryRequiredAddStand(addOwner, tacticTarget, participants, now, out GameNPC add, out Vector3 stand, out string why))
                        SwitchByTactic(add, $"{tacticReason}; {why}", participants, now, stand);
                    else SetTacticAside(addOwner, why, now);
                }
                else SwitchByTactic(tacticTarget, tacticReason, participants, now);
            }
            if (_target?.IsAlive != true || _target.ObjectState != GameObject.eObjectState.Active) return false;
            // A boss that blasts the room (Xanxicar): its withdraw spots on the floor towards the way in.
            EpicEncounterTactics.EnsureBlastPath(_target,
                Entrance is { } way && way.TargetRegion == _region ? new Vector3(way.TargetX, way.TargetY, way.TargetZ) : null);
            if (_target is GameNPC home && EpicEncounterTactics.OutsideHome(home))
            {
                _targetProgressTick = Math.Max(_targetProgressTick, now - 1_000);
                Hold = false; Status = $"Drawing {_target.Name} back to its lair ({_target.HealthPercent}%)";
                return true;
            }
            return false;
        }

        /// <summary>The current target's tactic sends the raid to another encounter (EpicEncounterTactics): a required add
        /// of the boss, or the boss an encounter dies with. Logged as REALM_EXPEDITION_TACTIC for the run reports.</summary>
        private void SwitchByTactic(GameNPC to, string reason, GameLiving[] participants, long now, Vector3? stand = null)
        {
            Vector3 approach;
            bool resolved;
            if (stand is Vector3 ranged) { approach = ranged; resolved = true; }
            else
            {
                resolved = TryEncounterApproach(to, participants, out approach);
                // A flying add is fought from the floor under it (the approach search only looks 256 up or down).
                if (!resolved && (to.Flags & GameNPC.eFlags.FLYING) != 0)
                {
                    var nav = PathfindingProvider.Instance;
                    Zone zone = WorldMgr.GetRegion(_region)?.GetZone(to.X, to.Y);
                    if (zone != null && nav.IsAvailable && nav.HasNavmesh(zone) &&
                        nav.GetClosestPoint(zone, new Vector3(to.X, to.Y, to.Z), 128, 128, RangedFlyerReach, nav.DefaultFilters) is Vector3 below)
                    { approach = below; resolved = true; }
                }
            }
            GameNPC owner = EpicEncounterTactics.RequiredAddOwner(to, _live ?? []) ?? _target;
            log.Info($"REALM_EXPEDITION_TACTIC region={_region} from=\"{_target?.Name}\" to=\"{to.Name}\" health={to.HealthPercent} " +
                     $"front={(int)approach.X},{(int)approach.Y},{(int)approach.Z} approach={(stand != null ? "ranged-stand" : resolved ? "resolved" : "raw")} " +
                     $"reason=\"{reason}\" tactic={EpicEncounterTactics.For(owner)?.Encounter}");
            _target = to; TargetName = to.Name; TargetLevel = to.Level;
            Front = Destination = approach;
            _targetProgressTick = _targetStartTick = now; _targetLowestHealth = to.Health;
            _targetClosest = float.MaxValue; _targetApproachTick = now;
            _targetPresentTick = 0;
            Hold = false; Status = "Clearing " + to.Name;
        }

        /// <summary>
        /// The required add of <paramref name="boss"/> the raid fights next, and the floor spot it fights it from
        /// (<see cref="TryRangedStand"/>): <paramref name="preferred"/> first, then the others nearest the front. Adds with
        /// no stand are set aside a minute each. False, with every add's reason, when none has one.
        /// </summary>
        private bool TryRequiredAddStand(GameNPC boss, GameNPC preferred, GameLiving[] participants, long now,
            out GameNPC add, out Vector3 stand, out string why)
        {
            add = null; stand = default;
            static Vector3 At(GameObject o) => new(o.X, o.Y, o.Z);
            GameNPC[] adds = EpicEncounterTactics.ShieldingAdds(boss, _live ?? [])
                .Where(a => _routeRetry.GetValueOrDefault(a) <= now)
                .OrderBy(a => a == preferred ? 0 : 1).ThenBy(a => Vector3.DistanceSquared(Front, At(a))).ToArray();
            if (adds.Length == 0) { why = "every required add was set aside a moment ago"; return false; }
            var nav = PathfindingProvider.Instance;
            Vector3[] anchors = DistinctAnchors(Front, participants
                .Where(p => p.IsAlive && p.CurrentRegionID == _region && (p.Group == null || p.Group.LivingLeader == p))
                .Select(At), 6);
            Vector3? Reach(Vector3 floor, Func<Vector3, bool> accept)
            {
                foreach (Vector3 anchor in anchors)
                {
                    Zone anchorZone = WorldMgr.GetRegion(_region)?.GetZone((int)anchor.X, (int)anchor.Y);
                    if (anchorZone != null && nav.HasNavmesh(anchorZone) &&
                        AutonomousZonePointApproach.TryResolve(nav, anchorZone, anchor, floor, 220, out Vector3 approach, accept))
                        return approach;
                }
                return null;
            }
            // Every add gets its own stand: its party's shooters fight it from there (EpicEncounterTactics.Decide). In 1.1.2 only the
            // route's add had one, and on the 2026-11-08 container run the shooters given the other three orbs walked under them.
            var reasons = new List<string>();
            foreach (GameNPC a in adds)
            {
                Zone zone = WorldMgr.GetRegion(_region)?.GetZone(a.X, a.Y);
                if (TryRangedStand(nav, zone, At(a), At(boss), Reach, out Vector3 own, out string found))
                {
                    EpicEncounterTactics.SetStand(a, own);
                    if (add == null) { add = a; stand = own; reasons.Insert(0, found); }
                    continue;
                }
                reasons.Add($"{a.Name}@{a.X},{a.Y},{a.Z}: {found}");
                _routeRetry[a] = now + 60_000;
            }
            why = string.Join("; ", reasons);
            if (add == null) return false;
            EpicEncounterTactics.SetStand(boss, stand);
            return true;
        }

        /// <summary>No required add of <paramref name="boss"/> can be fought (no ranged raider sees one from the floor):
        /// the boss's tactic is set aside like an unreachable target, and tried again later.</summary>
        private void SetTacticAside(GameNPC boss, string why, long now)
        {
            log.Warn($"REALM_EXPEDITION_TACTIC_ASIDE region={_region} encounter=\"{boss.Name}\" at={boss.X},{boss.Y},{boss.Z} " +
                     $"tactic={EpicEncounterTactics.For(boss)?.Encounter} minutes={BlockedTargetRetryMilliseconds / 60_000} reason=\"{why}\"");
            _routeRetry[boss] = now + BlockedTargetRetryMilliseconds;
            if (_musterTarget == boss) _musterTarget = null;
            if (_target == boss || EpicEncounterTactics.RequiredAddOwner(_target, _live ?? []) == boss) _target = null;
        }
    }
}
