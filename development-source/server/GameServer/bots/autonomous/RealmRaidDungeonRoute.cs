using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>One bounded route planner per expedition, not one full-world scan per member.</summary>
    public sealed class RealmRaidDungeonRoute
    {
        private readonly ushort _region;
        private readonly string[] _finalTypes;
        private readonly Vector3 _trigger;
        private readonly string[] _objectives;
        private readonly eRealm _realm;
        // Darkness Falls: certified waypoint chain from the realm's entrance to a named encounter.
        private Vector3[] _chain;
        private int _chainIndex;
        private GameNPC _chainTarget;
        private readonly HashSet<GameNPC> _cleared = new();
        // Named encounters defeated this expedition, by name: several scripted bosses come back as new objects (Lich Lord
        // Ilron, Lich Lord Sanguis, Spindler Broodmother were each fought twice in run 10).
        private readonly HashSet<string> _clearedNames = new(StringComparer.Ordinal);
        // Names of scripted bosses (GameEpicBoss) the raid killed. A boss's name is cleared only by a boss kill: Lich Lord
        // Ilron's images and High Lord Oro's clones carry their boss's name, and killing one of them first must not take the
        // boss off the list. Once the boss is dead, its look-alikes are cleared with it (Oro's clones come back after 30
        // minutes as new monsters and sorted before Legion).
        private readonly HashSet<string> _clearedBossNames = new(StringComparer.Ordinal);

        /// <summary>
        /// A monster whose name the raid has cleared is no route target: any monster after a kill of that name, a scripted
        /// boss only after a boss of that name died. Final encounters are never cleared by name.
        /// </summary>
        public static bool NameCleared(bool scriptedBoss, bool final, bool nameKilled, bool bossOfNameKilled) =>
            !final && (scriptedBoss ? bossOfNameKilled : nameKilled);

        /// <summary>
        /// Route targets in an epic dungeon (Caer Sidi, Tuscaran Glacier, Galladoria): for the first hour one monster of
        /// each name (the name rule above), then only the scripted bosses, the final encounters and the adds a living boss
        /// spawned. Almost every monster in these dungeons is epic-class, so "epic-class after the first hour" kept nearly
        /// all of them; the boss class says what was meant. Monsters on the way are still fought when they attack.
        /// </summary>
        public static bool IsEpicDungeonTarget(bool firstHour, bool scriptedBoss, bool final, bool encounterAdd) =>
            firstHour || scriptedBoss || final || encounterAdd;
        private long _routeStartTick;
        /// <summary>
        /// An epic dungeon raid clears one monster of each name for its first hour, then goes for its scripted bosses and
        /// the final one only (monsters on the way are fought when they attack; see <see cref="IsEpicDungeonTarget"/>). With trash coming back the list never
        /// emptied: Caer Sidi's raid killed Soul Reckoner, Host and Dorinakka and then cleared custodians and crypt
        /// knights for two hours, never reaching Apocalypse (run 10, 2026-10-15).
        /// </summary>
        public const long EpicTrashPhaseMilliseconds = 60 * 60_000;

        // Run 14 (2026-10-16): Galladoria's Corrupt Primal of Earth took no damage for five minutes from 30 raiders and was
        // skipped: its four Earthmenders (script-spawned, not named) healed it, and after the first hour the raid only
        // targets named encounters. Adds a script spawns beside the front are part of the fight; they come first.
        public const float EncounterAddRadius = 3_000f;
        // Only while a scripted boss (GameEpicBoss) is still alive nearby: any epic-class monster would do before, and in the
        // epic dungeons the trash is epic-class, so leftover adds stayed targets beside any trash. Run 17 (2026-10-17): Lich Lord Sanguis's blood
        // mages kept spawning after he died, the raid farmed them at the old front for 40 minutes and never went on to
        // Apocalypse.
        public static bool IsEncounterAdd(GameNPC npc, Vector3 front, IReadOnlyCollection<GameNPC> livingEncounters) =>
            npc.LoadedFromScript && npc is not IGameEpicNpc &&
            Vector3.DistanceSquared(front, new(npc.X, npc.Y, npc.Z)) <= EncounterAddRadius * EncounterAddRadius &&
            livingEncounters.Any(boss => boss.CurrentRegionID == npc.CurrentRegionID && boss.IsWithinRadius(npc, (int)EncounterAddRadius));
        private readonly Dictionary<GameNPC, long> _routeRetry = new();
        private readonly Dictionary<string, GameNPC> _finals = new();
        private GameNPC _target;
        private int _probeCursor;
        private static readonly Logging.Logger log = Logging.LoggerManager.Create(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private long _targetProgressTick, _targetStartTick, _holdSince, _nextHoldLog;
        // Lowest health points seen (not percent: a percent of Galladoria's 125,000-health Corrupt Primal of Fire is 1,250
        // points, and a few raiders wearing him down counted as no damage and he was skipped, run 23).
        private int _targetLowestHealth;
        // Encounter muster (see MusterForTarget).
        private GameNPC _musterTarget;
        private Vector3 _musterPoint;
        private long _musterSince, _nextMusterLog, _musterEngagedTick;
        private bool _musterEngaged;
        private float _musterKeepOut;
        // The muster target was seen in combat since the last (re-)muster, and since when it is out of combat again.
        private bool _musterSawCombat;
        private long _musterOutOfCombatSince;
        private int _musterWipes;
        /// <summary>Living Server (bot_living_leeroy, off by default): this raid rolled a Leeroy for its final encounter.</summary>
        public bool LeeroyRolled { get; set; }
        private bool _leeroyDone;
        /// <summary>The raider who charged, for the realm's word after the raid.</summary>
        public string LeeroyName { get; private set; }
        // Where the raid came from when the target was chosen: the last chain waypoint before the encounter, or the
        // position the target was proven from. The muster point lies on the way back toward it.
        private Vector3 _approachFrom;
        private bool _hasApproachFrom;
        // Before the first target: when 8 raiders first stood formed inside, and whether a target was ever chosen.
        private long _entryWaitSince;
        private bool _firstTargetChosen;

        /// <summary>
        /// While the raid musters for an encounter (the first time, or again after a wipe): the place to gather, on the
        /// way the raid came, outside the encounter's reach. Null otherwise. Late and released raiders regroup here.
        /// </summary>
        public Vector3? RegroupPoint { get; private set; }

        /// <summary>
        /// Every named encounter is pulled by the gathered raid, not by the first 8 to reach it: the final encounters,
        /// every scripted boss (GameEpicBoss) and, in a neutral dungeon, every objective (Darkness Falls' lords and
        /// princes, the three summoners were pulled piecemeal while the raid was strung out).
        /// </summary>
        public static bool NeedsMuster(bool final, bool scriptedBoss, bool objective) => final || scriptedBoss || objective;

        /// <summary>
        /// Raiders a lesser encounter waits for: a quarter of the roster, at least <see cref="MinimumRaidersAtTarget"/>
        /// and at most 60; an eighth after 5 minutes; after 15 minutes the muster gives up and those there go in.
        /// </summary>
        public static int EncounterMusterNeeded(int raiders, long musterMilliseconds) =>
            musterMilliseconds >= EncounterMusterGiveUpMilliseconds ? 0 :
            Math.Max(MinimumRaidersAtTarget, Math.Min(EncounterMusterMaximum,
                (int)(raiders * (musterMilliseconds >= EncounterMusterPatienceMilliseconds ? 0.125 : 0.25))));
        public const int EncounterMusterMaximum = 60;
        public const long EncounterMusterPatienceMilliseconds = 5 * 60_000;
        public const long EncounterMusterGiveUpMilliseconds = 15 * 60_000;

        /// <summary>
        /// A lesser encounter the raid has wiped on this many times is set aside like an undamaged one (the no-damage
        /// clock never runs out on a boss that is damaged on every attempt and resets after every wipe).
        /// </summary>
        public const int EncounterWipeLimit = 3;
        public static bool WipedOut(bool final, int wipes) => !final && wipes >= EncounterWipeLimit;

        /// <summary>
        /// Fights with a final encounter that ended with the boss back out of combat: wiped (or broken-off) attempts on the
        /// final. Read-only for the raid, which ends an event after repeated wipes.
        /// </summary>
        public int FinalWipes { get; private set; }

        /// <summary>A boss that was fought and has been out of combat for two pulses (10 s) is a wiped attempt.</summary>
        public const long WipeConfirmMilliseconds = 10_000;

        public static bool WipeConfirmed(bool sawCombat, long outOfCombatSince, long now) =>
            sawCombat && outOfCombatSince > 0 && now - outOfCombatSince >= WipeConfirmMilliseconds;

        public const long TargetNoProgressMilliseconds = 5 * 60_000;
        public const long BlockedTargetRetryMilliseconds = 15 * 60_000;
        public const long HoldReportMilliseconds = 10 * 60_000;
        public const long BlockedEndMilliseconds = 20 * 60_000;
        private long _blockedSince;
        // Set once every remaining target was on a retry timer and the raid went on to the final trigger: from then on
        // only the final encounters and monsters near the trigger (the horsemen, the primals and their adds) are targets.
        private bool _finalPhase;
        public const float FinalPhaseRadius = 4_000f;

        /// <summary>True when every remaining route candidate is on a retry timer (none is waiting to be probed).</summary>
        public static bool EverySetAside(IEnumerable<long> retryUntil, long now) => retryUntil.All(until => until > now);

        /// <summary>
        /// No live encounter reachable from the front or from any party for 20 minutes.
        /// Tuscaran (Oct 3-4) held 300 bots on "Checking blocked room approaches" for over
        /// six hours after the raid dropped to a lower level it could not climb back from.
        /// </summary>
        public bool Blocked { get; private set; }

        public static bool BlockedTooLong(long blockedSince, long now) =>
            blockedSince > 0 && now - blockedSince >= BlockedEndMilliseconds;

        /// <summary>
        /// A route target nobody has damaged for five minutes is set aside so the
        /// expedition moves on (Caer Sidi and Tuscaran held at their opening area
        /// for 3.5 hours on 2026-10-02). Final bosses are never set aside.
        /// </summary>
        public static bool ShouldSkipTarget(bool finalBoss, long sinceProgressMilliseconds) =>
            !finalBoss && sinceProgressMilliseconds >= TargetNoProgressMilliseconds;
        /// <summary>
        /// The front plus up to <paramref name="max"/> party positions, skipping any within
        /// 150 units across and 64 up/down of one already kept (a party standing 96 units
        /// above the front is a different floor and is kept).
        /// </summary>
        public static Vector3[] DistinctAnchors(Vector3 front, IEnumerable<Vector3> parties, int max)
        {
            var kept = new List<Vector3> { front };
            foreach (Vector3 p in parties)
            {
                if (kept.Count > max) break;
                if (kept.Any(k => Vector2.DistanceSquared(new(k.X, k.Y), new(p.X, p.Y)) <= 150 * 150 &&
                        MathF.Abs(k.Z - p.Z) <= 64))
                    continue;
                kept.Add(p);
            }
            return kept.ToArray();
        }
        public DbZonePoint Entrance { get; }
        public Vector3 Front { get; private set; }
        public Vector3 Destination { get; private set; }
        public string TargetName { get; private set; } = "Encounter staging";
        public int TargetLevel { get; private set; } = 50;
        public bool Hold { get; private set; } = true;
        public bool Complete { get; private set; }
        public string Status { get; private set; } = "Entering dungeon";
        public GameNPC[] FinalBosses => _finals.Values.ToArray();

        // Ordinary flying patrols need not occupy a melee-floor polygon.
        // Never defer a boss, a scripted subclass, or a grounded room guard.
        public const int RangedFlyerReach = 1200;
        public static bool IsOutOfRangedReach(GameNPC.eFlags flags, int monsterZ, int floorZ) =>
            AutonomousPveTargetPolicy.IsUnreachableFlyer(flags, monsterZ, floorZ) && Math.Abs(monsterZ - floorZ) > RangedFlyerReach;

        public static bool CanDeferAirbornePatrol(Type type, GameNPC.eFlags flags) =>
            type == typeof(GameEpicNPC) && (flags & GameNPC.eFlags.FLYING) != 0;

        private RealmRaidDungeonRoute(ushort region, DbZonePoint entrance, Vector3 entry, Vector3 trigger, string[] finals,
            string[] objectives = null, eRealm realm = eRealm.None)
        {
            _region = region; Entrance = entrance; Front = Destination = entry; _trigger = trigger; _finalTypes = finals;
            _objectives = objectives; _realm = realm;
        }

        /// <summary>
        /// The expedition's entrance and its first floor inside. Realm epic dungeons take any
        /// exterior entrance. A neutral expedition (Summoner's Hall, Darkness Falls) takes its own
        /// realm's entrance in its home region, directly or through one dungeon (Summoner's Hall
        /// is entered from Hall of the Corrupt, Dodens Gruva or Marfach Caverns), nearest to the
        /// muster hub first; <see cref="Entrance"/> is then the edge out of the home region, where
        /// the expedition stages.
        /// </summary>
        /// <summary>Why the last TryCreate found no entrance: each candidate and the check it failed (raid start log).</summary>
        public static string LastFailure { get; private set; } = "";

        public static bool TryCreate(ushort region, Vector3 trigger, string[] finals, out RealmRaidDungeonRoute route,
            eRealm realm = eRealm.None, ushort homeRegion = 0, string[] objectives = null, Vector3? hub = null)
        {
            route = null;
            var nav = PathfindingProvider.Instance;
            var why = new List<string>();
            LastFailure = "pathfinding unavailable";
            if (!nav.IsAvailable) return false;
            var crossings = AutonomousWorldBotController.RealmEventCrossings();
            IEnumerable<(DbZonePoint Outer, DbZonePoint Inner)> candidates = homeRegion == 0
                ? crossings.Where(e => e.TargetRegion == region).Select(e => (e, e))
                : RealmRaidNeutralEvents.Entrances(crossings, realm, homeRegion, region)
                    .OrderBy(pair => RealmRaidNeutralEvents.EntrancePreference(realm, pair.Outer.Id))
                    .ThenBy(pair => hub.HasValue ? Vector2.DistanceSquared(new(pair.Outer.SourceX, pair.Outer.SourceY), new(hub.Value.X, hub.Value.Y)) : 0);
            foreach (var (outer, inner) in candidates)
            {
                Zone outside = WorldMgr.GetRegion(outer.SourceRegion)?.GetZone(outer.SourceX, outer.SourceY);
                Zone inside = WorldMgr.GetRegion(region)?.GetZone(inner.TargetX, inner.TargetY);
                string id = $"zp{outer.Id}/{inner.Id}";
                if (outside == null || outside.IsDungeon || inside == null || !nav.HasNavmesh(inside))
                { why.Add($"{id}: {(outside == null ? "no outside zone" : outside.IsDungeon ? "outside is a dungeon" : inside == null ? "no inside zone" : "no inside navmesh")}"); continue; }
                if (homeRegion != 0 && !(region == AutonomousDarknessFallsPolicy.RegionId
                        ? AutonomousDarknessFallsNavigation.IsHomeEntrance(inner)
                        : AutonomousDungeonGoalCatalog.CanUseEntrance(inner, region, (int)trigger.X, (int)trigger.Y)))
                { why.Add($"{id}: not this realm's entrance"); continue; }
                if (homeRegion != 0 && !RealmRaidStaging.TryDungeonPost(nav, outside, new(outer.SourceX, outer.SourceY, outer.SourceZ), 0, [], out _))
                { why.Add($"{id}: no staging post outside"); continue; }
                Vector3 raw = new(inner.TargetX, inner.TargetY, inner.TargetZ);
                Vector3? floor = nav.GetClosestPoint(inside, raw, 48, 48, 64, nav.DefaultFilters);
                if (!floor.HasValue) { why.Add($"{id}: no navmesh floor at the inside arrival point"); continue; }
                if (!AutonomousRendezvousNavigation.HasLocalExit(nav, inside, floor.Value)) { why.Add($"{id}: inside arrival has no local exit"); continue; }
                // A neutral dungeon is too large for one entrance-to-final-boss proof (Darkness
                // Falls); the route proves each named encounter from the raid's positions instead.
                if (homeRegion == 0 && !AutonomousZonePointApproach.TryResolve(nav, inside, floor.Value, trigger, 220, out _))
                { why.Add($"{id}: no approach to the encounter"); continue; }
                route = new(region, outer, floor.Value, trigger, finals, objectives, realm);
                route.ObserveCompletion();
                LastFailure = "";
                return true;
            }
            LastFailure = why.Count == 0 ? "no entrance crossing into the region for this realm" : string.Join("; ", why);
            return false;
        }

        /// <summary>
        /// Moves the front along the chain: the next waypoint once formed parties stand within 300
        /// of the current one. At the end the encounter becomes the ordinary target. False when no
        /// chain is being walked.
        /// </summary>
        private bool AdvanceChain(GameLiving[] inside, long now)
        {
            if (_chain == null) return false;
            if (_chainTarget?.IsAlive != true || _chainTarget.ObjectState != GameObject.eObjectState.Active)
            { _chain = null; _chainTarget = null; return false; }
            while (_chainIndex < _chain.Length - 1 &&
                   inside.Count(p => p.IsWithinRadius(new Point3D((int)_chain[_chainIndex].X, (int)_chain[_chainIndex].Y, (int)_chain[_chainIndex].Z), 300)) >= 8)
                _chainIndex++;
            Front = Destination = _chain[_chainIndex];
            if (_chainIndex == _chain.Length - 1)
            {
                // The muster for the encounter gathers on the way back to the last waypoint before it.
                _approachFrom = _chain.Length > 1 ? _chain[^2] : _chain[0]; _hasApproachFrom = true;
                _target = _chainTarget; _chain = null; _chainTarget = null;
                // A fresh start: the chain's target kept the previous target's start, so Prince Ba'alorien
                // inherited Prince Abdin's absence allowance and was skipped at 100% with the raid arriving (2026-10-14).
                _targetProgressTick = _targetStartTick = now; _targetLowestHealth = _target.Health;
                _targetClosest = float.MaxValue; _targetApproachTick = now; _targetPresentTick = 0;
                Hold = false; Status = "Clearing " + _target.Name;
                return true;
            }
            Hold = false;
            Status = $"Advancing to {TargetName} (waypoint {_chainIndex + 1}/{_chain.Length})";
            return true;
        }

        /// <summary>
        /// A Darkness Falls encounter reached through a certified ordinary-spawn route: the proofs give,
        /// per realm, a chain of proven legs from the realm's entrance to a spawn's approach; the
        /// encounter must then be a short proven leg from that approach (nearest proofs first).
        /// </summary>
        private bool TryDarknessFallsChain(IPathfindingMgr nav, Zone zone, Vector3 encounter, out Vector3[] chain) =>
            TryDarknessFallsEncounterChain(nav, zone, _realm, encounter, out chain);

        /// <summary>
        /// Search order for certified approaches to a point: distance across plus four times the height difference. Darkness
        /// Falls is stacked; by straight 3D distance all eight approaches tried for five named encounters were on another
        /// floor, 23,000-55,000 units away on foot, while one 5,700-11,500 away on foot ranked 47th to 307th, so from an
        /// entrance only Prince Ba'alorien and High Lord Saeor had a chain (df-routes M1).
        /// </summary>
        public static float ApproachSearchKey(Vector3 approach, Vector3 encounter) =>
            Vector2.Distance(new(approach.X, approach.Y), new(encounter.X, encounter.Y)) + ApproachFloorWeight * MathF.Abs(approach.Z - encounter.Z);
        public const float ApproachFloorWeight = 4f;
        /// <summary>A named encounter's last leg may be as long as the longest certified leg (12,400 walking units).</summary>
        public const float DarknessFallsEncounterReach = 13_000f;
        public const int EncounterApproachProbes = 16;
        public const int EncounterApproachKeep = 3;
        public const long EncounterChainFailureMemoryMilliseconds = 10 * 60_000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(eRealm, ushort, int, int, int, long), (Vector3[] Chain, long Until)>
            EncounterChains = new();

        /// <summary>
        /// The certified chain to a named encounter, chosen once per realm, encounter and mesh revision: of the certified
        /// approach floors nearest in <see cref="ApproachSearchKey"/> order (16 at most), the first three with a proven last
        /// leg are measured on foot and the shortest leg wins. Kept for the life of the mesh; a failure is retried after
        /// 10 minutes. About 16 approach checks and 3 path queries once, nothing per pulse.
        /// </summary>
        public static bool TryDarknessFallsEncounterChain(IPathfindingMgr nav, Zone zone, eRealm realm, Vector3 encounter, out Vector3[] chain)
        {
            chain = null;
            if (realm == eRealm.None || zone == null) return false;
            long now = GameLoop.GameLoopTime;
            var key = (realm, zone.ID, (int)encounter.X >> 6, (int)encounter.Y >> 6, (int)encounter.Z >> 6, NavigationGeometryRevision.Read(zone));
            if (EncounterChains.TryGetValue(key, out var known) && (known.Chain != null || now < known.Until))
            {
                chain = known.Chain;
                return chain != null;
            }
            float bestLeg = float.MaxValue;
            int kept = 0;
            foreach (var item in AutonomousDarknessFallsNavigation.CertifiedApproaches(realm)
                         .Where(a => a.InWaypoints is { Length: >= 2 } &&
                                     Vector3.DistanceSquared(a.Approach, encounter) <= DarknessFallsEncounterReach * DarknessFallsEncounterReach)
                         .OrderBy(a => ApproachSearchKey(a.Approach, encounter))
                         .Take(EncounterApproachProbes))
            {
                if (!AutonomousZonePointApproach.TryResolve(nav, zone, item.Approach, encounter, 220, out Vector3 final, maxCandidates: 4))
                    continue;
                float leg = WalkingLength(nav, zone, item.Approach, final);
                if (leg < bestLeg)
                {
                    bestLeg = leg;
                    chain = item.InWaypoints.Append(final).ToArray();
                }
                if (++kept >= EncounterApproachKeep) break;
            }
            if (EncounterChains.Count > 1_024) EncounterChains.Clear();
            EncounterChains[key] = (chain, chain != null ? long.MaxValue : now + EncounterChainFailureMemoryMilliseconds);
            if (chain != null)
                log.Info($"REALM_EXPEDITION_CHAIN_CHOSEN realm={realm} at={(int)encounter.X},{(int)encounter.Y},{(int)encounter.Z} " +
                         $"waypoints={chain.Length} lastLeg={(int)bestLeg}");
            return chain != null;
        }

        /// <summary>Walking length of a straight path (float.MaxValue when there is none; a partial path adds the rest in a line).</summary>
        private static float WalkingLength(IPathfindingMgr nav, Zone zone, Vector3 from, Vector3 to)
        {
            Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
            PathfindingResult path = nav.GetPathStraight(zone, from, to, nav.DefaultFilters, nodes);
            if (path.Status != PathfindingStatus.PathFound && path.Status != PathfindingStatus.PartialPathFound)
                return float.MaxValue;
            float length = 0;
            Vector3 last = from;
            for (int i = 0; i < path.NodeCount; i++) { length += Vector3.Distance(last, nodes[i].Position); last = nodes[i].Position; }
            return length + Vector3.Distance(last, to);
        }

        /// <summary>The same certified chain for one realm's member (late raid members walk it to their post).</summary>
        public static bool TryDarknessFallsChain(IPathfindingMgr nav, Zone zone, eRealm realm, Vector3 encounter, out Vector3[] chain)
        {
            // The answer depends on the realm, the post and the mesh only, never on the asking bot. Every
            // raider whose post was not one leg away re-ran the full search (8 approaches x ~38 candidate
            // points, ~300 path queries, ~1 s) on every AI turn; the Prince Abdin raid on 2026-10-12 made
            // 577 such turns. Failures are remembered too: they repeat until the post moves.
            if (nav is not LocalPathfindingMgr || zone == null)
                return SearchDarknessFallsChain(nav, zone, realm, encounter, out chain);
            long now = GameLoop.GameLoopTime;
            var key = (realm, zone.ID, (int)encounter.X >> 6, (int)encounter.Y >> 6, (int)encounter.Z >> 6, NavigationGeometryRevision.Read(zone));
            if (DarknessFallsChains.TryGetValue(key, out var known) && now < known.Until)
            {
                chain = known.Chain;
                return chain != null;
            }
            // A failure holds for every post near it. The raid front moves a few dozen units at a time, so the exact
            // post rarely repeats: on 2026-10-17 every Prince Abdin raider re-ran the failing ~300-query search
            // (1-5 s) on each AI turn and the server lagged.
            var area = (realm, zone.ID, (int)encounter.X >> DarknessFallsChainFailureCellShift, (int)encounter.Y >> DarknessFallsChainFailureCellShift,
                (int)encounter.Z >> DarknessFallsChainFailureCellShift, key.Item6);
            if (DarknessFallsChainFailures.TryGetValue(area, out long failedUntil) && now < failedUntil)
            {
                chain = null;
                return false;
            }
            bool found = SearchDarknessFallsChain(nav, zone, realm, encounter, out chain);
            if (DarknessFallsChains.Count > 4_096) DarknessFallsChains.Clear();
            DarknessFallsChains[key] = (found ? chain : null, now + (found ? DarknessFallsChainMemoryMilliseconds : DarknessFallsChainFailureMemoryMilliseconds));
            if (!found)
            {
                if (DarknessFallsChainFailures.Count > 4_096) DarknessFallsChainFailures.Clear();
                DarknessFallsChainFailures[area] = now + DarknessFallsChainFailureMemoryMilliseconds;
            }
            return found;
        }

        public const long DarknessFallsChainMemoryMilliseconds = 120_000;
        public const long DarknessFallsChainFailureMemoryMilliseconds = 60_000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(eRealm, ushort, int, int, int, long), (Vector3[] Chain, long Until)>
            DarknessFallsChains = new();
        /// <summary>Failures are shared by posts in the same 512-unit cell.</summary>
        public const int DarknessFallsChainFailureCellShift = 9;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(eRealm, ushort, int, int, int, long), long>
            DarknessFallsChainFailures = new();

        private static bool SearchDarknessFallsChain(IPathfindingMgr nav, Zone zone, eRealm realm, Vector3 encounter, out Vector3[] chain)
        {
            chain = null;
            var _realm = realm;
            if (_realm == eRealm.None) return false;
            // Every certified approach floor, also those whose spawn is gone (their legs are still proven floor), in
            // floor-aware order instead of straight 3D distance (see ApproachSearchKey).
            var nearest = AutonomousDarknessFallsNavigation.CertifiedApproaches(_realm)
                .Where(item => item.InWaypoints is { Length: >= 2 } && Vector3.DistanceSquared(item.Approach, encounter) <=
                    DarknessFallsChainReach * DarknessFallsChainReach)
                .OrderBy(item => ApproachSearchKey(item.Approach, encounter))
                .Take(8);
            foreach (var item in nearest)
            {
                if (!AutonomousZonePointApproach.TryResolve(nav, zone, item.Approach, encounter, 220, out Vector3 final)) continue;
                chain = item.InWaypoints.Append(final).ToArray();
                return true;
            }
            return false;
        }

        /// <summary>
        /// The furthest chain waypoint (final approach first) that a party leader inside the dungeon has a
        /// complete corridor to; -1 when none of them can reach any waypoint.
        /// </summary>
        private int ReachableChainStart(IPathfindingMgr nav, Vector3[] chain, GameLiving[] inside)
        {
            // Where the parties stand, not the front: a stale front can sit on the unreachable waypoint itself.
            Vector3[] anchors = DistinctAnchors(Front, inside
                .Where(p => p.Group == null || p.Group.LivingLeader == p)
                .Select(p => new Vector3(p.X, p.Y, p.Z)), 3);
            Vector3[] leaders = anchors.Length > 1 ? anchors.Skip(1).ToArray() : anchors;
            for (int i = chain.Length - 1; i >= 0; i--)
            foreach (Vector3 from in leaders)
            {
                Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)from.X, (int)from.Y);
                if (zone != null && nav.HasNavmesh(zone) &&
                    AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, from, chain[i]))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Raiders a final encounter waits for before it is pulled: half of the raid's roster, at least
        /// <see cref="MinimumRaidersAtTarget"/> and at most 150; after 20 minutes of mustering a third will do. The roster,
        /// not the living: after a wipe with 30 survivors, half of the living let 15 go back in while the dead walked.
        /// </summary>
        public static int FinalMusterNeeded(int raiders, long musterMilliseconds) =>
            Math.Max(MinimumRaidersAtTarget, Math.Min(FinalMusterMaximum,
                (int)(raiders * (musterMilliseconds >= FinalMusterPatienceMilliseconds ? 0.33 : 0.5))));
        public const int FinalMusterMaximum = 150;
        public const long FinalMusterPatienceMilliseconds = 20 * 60_000;
        public const int FinalMusterRadius = 1_500;
        public const float FinalMusterStandOff = 1_400f;
        /// <summary>The muster point stands at least this far beyond the encounter's own aggro range.</summary>
        public const float MusterAggroClearance = 600f;
        /// <summary>No muster post, and no raider counted as mustered, within the aggro range plus this.</summary>
        public const float MusterPostAggroMargin = 300f;

        /// <summary>
        /// How far from the encounter the muster point stands: 1,400, or the encounter's aggro range plus 600 when that is
        /// more. The range is read from the monster (Olcasgean reaches 1,500 and Apocalypse aggroes at 1,300: a fixed
        /// 1,400 stood inside both).
        /// </summary>
        public static float MusterStandOff(int aggroRange) => Math.Max(FinalMusterStandOff, Math.Max(0, aggroRange) + MusterAggroClearance);

        /// <summary>The circle round the encounter that no muster post enters: its aggro range plus 300.</summary>
        public static float MusterKeepOutRadius(int aggroRange) => Math.Max(0, aggroRange) + MusterPostAggroMargin;

        private static int AggroRangeOf(GameNPC npc) => (npc?.Brain as StandardMobBrain)?.AggroRange ?? 0;
        public const long FinalEngageGraceMilliseconds = 2 * 60_000;

        /// <summary>
        /// Darkness Falls run 2026-10-13: the Albion raid reached Legion once eight raiders stood at the last
        /// waypoint and was then pulled in by twos and threes as the rest arrived; it wiped, released to Camelot
        /// and Legion reset to full health a minute after the last raider fell. A final encounter not yet in a
        /// fight now waits at a muster point on the walked approach, outside its aggro range, until enough of
        /// the raid stands there (<see cref="FinalMusterNeeded"/>); then the whole muster goes in together. After a
        /// wipe the boss leaves combat and the raid musters again at once: arrivals are held at the muster point instead
        /// of walking one by one into a boss at full health (the old two-minute grace held nobody). True while holding.
        /// </summary>
        private bool MusterForTarget(GameLiving[] participants, long now)
        {
            if (_musterTarget != _target)
            {
                _musterTarget = _target;
                _musterSince = now;
                _musterEngaged = false;
                _musterSawCombat = false;
                _musterOutOfCombatSince = 0;
                _musterWipes = 0;
                RealmRaidFormation.ClearKeepOut(_musterPoint);
                int aggro = AggroRangeOf(_target);
                _musterKeepOut = MusterKeepOutRadius(aggro);
                _musterPoint = MusterPoint(participants, MusterStandOff(aggro));
                RealmRaidFormation.SetKeepOut(_musterPoint, new(_target.X, _target.Y, _target.Z), _musterKeepOut);
            }
            if (_target.InCombat)
            {
                // A fight in progress is never interrupted, and the raid joins it (a pull by the first arrivals left the
                // rest standing at the muster point).
                _musterSawCombat = true;
                _musterOutOfCombatSince = 0;
                Destination = Front;
                return false;
            }
            if (_musterSawCombat)
            {
                // Fought and out of combat again: wiped or broken off, once confirmed over two pulses.
                if (_musterOutOfCombatSince == 0) _musterOutOfCombatSince = now;
                if (!WipeConfirmed(_musterSawCombat, _musterOutOfCombatSince, now))
                    return false;
                _musterWipes++;
                if (_finalTypes.Contains(_target.GetType().Name)) FinalWipes++;
                log.Warn($"REALM_EXPEDITION_ENGAGE_WIPED region={_region} target=\"{_target.Name}\" health={_target.HealthPercent} " +
                         $"wipes={_musterWipes} finalWipes={FinalWipes} alive={participants.Count(p => p.IsAlive)} roster={participants.Length}");
                _musterSawCombat = false;
                _musterOutOfCombatSince = 0;
                _musterEngaged = false;
                _musterSince = now;
                // A lesser encounter wiped on too often is not mustered for again: the caller sets it aside.
                if (WipedOut(_finalTypes.Contains(_target.GetType().Name), _musterWipes))
                    return false;
            }
            else if (_musterEngaged)
            {
                // The raid needs time to cross from the muster point to the boss (run 5: the engage repeated every ten
                // seconds while Govannon was still out of combat). A pull that never reached a fight musters again.
                if (now - _musterEngagedTick < FinalEngageGraceMilliseconds)
                    return false;
                _musterEngaged = false;
                _musterSince = now;
            }
            int living = participants.Count(p => p.IsAlive);
            bool final = _finalTypes.Contains(_target.GetType().Name);
            int needed = final ? FinalMusterNeeded(participants.Length, now - _musterSince)
                : EncounterMusterNeeded(participants.Length, now - _musterSince);
            var point = new Point3D((int)_musterPoint.X, (int)_musterPoint.Y, (int)_musterPoint.Z);
            // Only raiders at the muster point count, never those standing by the boss: anyone within 2,000 of it counted
            // before, so the first arrivals inside its reach filled the muster themselves.
            int mustered = participants.Count(p => p.IsAlive && p.CurrentRegionID == _region &&
                                                   p.IsWithinRadius(point, FinalMusterRadius) && !p.IsWithinRadius(_target, (int)_musterKeepOut));
            // Living Server's Leeroy (bot_living_leeroy, off by default): at the final encounter's first muster, once part
            // of the raid is there, one raider yells and charges in alone; the boss is in the fight, so the muster goes in
            // after him. A raid that arrives all at once gets him too: he jumps its pull.
            if (final && LeeroyRolled && !_leeroyDone && FinalWipes == 0 &&
                AutonomousRaidLeeroy.GoesNow(AutonomousRaidLeeroy.ShouldCharge(mustered, needed, now - _musterSince, _musterWipes, false) ||
                                             AutonomousRaidLeeroy.JumpsThePull(mustered, needed, _musterWipes, false), mustered))
            {
                _leeroyDone = true;
                GameLiving[] musteredRaiders = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region &&
                    p.IsWithinRadius(point, FinalMusterRadius) && !p.IsWithinRadius(_target, (int)_musterKeepOut)).ToArray();
                if (AutonomousRaidLeeroy.Pick(musteredRaiders) is GameBot leeroy && _target is GameNPC boss &&
                    AutonomousRaidLeeroy.Charge(leeroy, boss, musteredRaiders, _target.CurrentZone?.Description ?? _target.Name))
                {
                    LeeroyName = leeroy.Name;
                    _musterEngaged = true;
                    _musterEngagedTick = now;
                    Destination = Front;
                    log.Info($"REALM_EXPEDITION_LEEROY region={_region} target=\"{_target.Name}\" bot=\"{leeroy.Name}\" mustered={mustered} " +
                             $"needed={needed} living={living} minutes={(now - _musterSince) / 60_000.0:0.0}");
                    return false;
                }
            }
            if (mustered >= needed)
            {
                _musterEngaged = true;
                _musterEngagedTick = now;
                Destination = Front;
                log.Info($"REALM_EXPEDITION_{(final ? "FINAL_" : "")}ENGAGE region={_region} target=\"{_target.Name}\" mustered={mustered} needed={needed} " +
                         $"living={living} minutes={(now - _musterSince) / 60_000.0:0.0}");
                return false;
            }
            Destination = _musterPoint;
            RegroupPoint = _musterPoint;
            Hold = true;
            Status = $"Mustering for {_target.Name} ({mustered}/{needed})";
            // Waiting for the raid is not a stalled fight.
            _targetProgressTick = now;
            if (now >= _nextMusterLog)
            {
                _nextMusterLog = now + 2 * 60_000;
                log.Info($"REALM_EXPEDITION_{(final ? "FINAL_" : "")}MUSTER region={_region} target=\"{_target.Name}\" mustered={mustered} needed={needed} " +
                         $"living={living} at={(int)_musterPoint.X},{(int)_musterPoint.Y},{(int)_musterPoint.Z} minutes={(now - _musterSince) / 60_000.0:0.0}");
            }
            return true;
        }

        /// <summary>
        /// The point on the walked path from the encounter's approach toward the raid that lies <paramref name="standOff"/>
        /// from the encounter. A spot already between the stand-off and 200 beyond it is taken as it is. The main body used
        /// to be taken whenever it stood within 1,600 of the encounter, inside the reach of the bigger ones; a path that
        /// never leaves the stand-off circle now gives the node furthest from the encounter.
        /// </summary>
        private Vector3 MusterPoint(GameLiving[] participants, float standOff)
        {
            var nav = PathfindingProvider.Instance;
            Vector3 boss = new(_target.X, _target.Y, _target.Z);
            GameLiving[] inside = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region).ToArray();
            Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)Front.X, (int)Front.Y);
            Vector3 best = Front;
            float bestDistance = Vector3.Distance(Front, boss);
            if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
                return best;
            var toward = new List<Vector3>(2);
            // The way the raid came first (returning raiders come the same way), then the main body.
            if (_hasApproachFrom) toward.Add(_approachFrom);
            if (TryRegroupFront(inside, out Vector3 body)) toward.Add(body);
            Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
            foreach (Vector3 goal in toward)
            {
                float goalDistance = Vector3.Distance(goal, boss);
                if (goalDistance >= standOff && goalDistance <= standOff + 200)
                    return goal;
                // Searched from the encounter's approach toward the raid, so the path always starts at the encounter even
                // when it is too long to finish. From the raid's side a partial path ended wherever the search gave up:
                // Legion's muster in Darkness Falls was placed 19,000 units from him and nobody ever reached it (run 9).
                PathfindingResult path = nav.GetPathStraight(zone, Front, goal, nav.DefaultFilters, nodes);
                if (path.Status != PathfindingStatus.PathFound && path.Status != PathfindingStatus.PartialPathFound)
                    continue;
                for (int i = 1; i < path.NodeCount; i++)
                {
                    Vector3 a = nodes[i - 1].Position, b = nodes[i].Position;
                    float d = Vector3.Distance(b, boss);
                    if (d < standOff)
                    {
                        if (d > bestDistance) { best = b; bestDistance = d; }
                        continue;
                    }
                    // a inside the stand-off circle, b outside: the crossing point on a-b.
                    float lo = 0, hi = 1;
                    for (int step = 0; step < 12; step++)
                    {
                        float mid = (lo + hi) / 2;
                        if (Vector3.Distance(Vector3.Lerp(a, b, mid), boss) >= standOff) hi = mid; else lo = mid;
                    }
                    Vector3? floor = nav.GetClosestPoint(zone, Vector3.Lerp(a, b, hi), 64, 64, 128, nav.DefaultFilters);
                    return floor ?? b;
                }
            }
            return best;
        }

        public const long CallTargetAfterMilliseconds = 90_000;
        public const long FrontRegroupMilliseconds = 3 * 60_000;
        private long _frontAbsentSince;
        // The raiders' closest approach to an unreached front: while they keep closing in, the front is not given up.
        private float _frontClosest = float.MaxValue;
        private Vector3 _frontClosestFor;

        /// <summary>The living raider with the most living raiders within 1,000 units (the raid's main body).</summary>
        private static bool TryRegroupFront(GameLiving[] inside, out Vector3 front)
        {
            front = default;
            GameLiving best = null;
            int bestCount = 0;
            foreach (GameLiving candidate in inside.Where(p => p.Group == null || p.Group.LivingLeader == p))
            {
                int count = inside.Count(p => p.IsWithinRadius(candidate, 1000));
                if (count > bestCount) { best = candidate; bestCount = count; }
            }
            if (best == null || bestCount < MinimumRaidersAtTarget) return false;
            front = new(best.X, best.Y, best.Z);
            return true;
        }

        // The certified file holds ordinary spawns only; the nearest Albion approach is 6,100 units
        // from High Lord Saeor, 5,500 from Oro, 3,000 from Ba'alorien, and Nahemah's 199-unit one is a
        // floor above. With a 2,500 reach late raiders found no chain, failed their direct path and were
        // pocket-escaped out of Darkness Falls (run 12). The last leg is still a complete-corridor check.
        // (Nahemah's spawn moved off her unconnected dais onto the floor in front of it on 2026-10-13.)
        public const float DarknessFallsChainReach = 8_000f;
        public const int TargetPresenceRadius = 2_000;
        public const int MinimumRaidersAtTarget = 8;
        public const long TargetAbsencePauseLimitMilliseconds = 20 * 60_000;
        public const float TargetApproachStepUnits = 300f;
        // The last pulse at which 8 raiders stood by the target (0: never since it was chosen).
        private long _targetPresentTick;

        /// <summary>
        /// The health a target must fall below to count as damaged. A target out of combat above its old low has reset
        /// or healed: its current health becomes the mark, so the first blow of the next attempt is progress again.
        /// </summary>
        public static int ProgressMark(int lowest, int health, bool inCombat) =>
            !inCombat && health > lowest ? health : lowest;

        /// <summary>
        /// Fewer than 8 raiders by the target pauses the no-damage clock for up to 20 minutes counted from the last time
        /// 8 stood there (or from the target's start when they never did), and for as long as the raid is still closing
        /// in. Counted from the start only, a raid that wiped 21 minutes in had its boss set aside while it walked back.
        /// </summary>
        public static bool AbsencePausesClock(int near, long now, long targetStart, long lastPresent, bool approaching) =>
            near < MinimumRaidersAtTarget &&
            (now - Math.Max(targetStart, lastPresent) < TargetAbsencePauseLimitMilliseconds || approaching);
        private int _healthSample = -1;
        private GameLiving _healthSampleTarget;
        private long _nextHealLog;

        /// <summary>
        /// REALM_EXPEDITION_TARGET_HEALED: the raid's target got 5% or more of its health back since the last pulse, with
        /// what the fight looked like then. Hurionthex (Galladoria) went 76% -> 96% in half a minute with 270 raiders
        /// beside him, and none of the known resets explained it (2026-10-15).
        /// </summary>
        private void ReportHealthJump(GameLiving[] participants, long now)
        {
            int health = _target.HealthPercent;
            if (_healthSampleTarget != _target) { _healthSampleTarget = _target; _healthSample = health; return; }
            int before = _healthSample;
            _healthSample = health;
            if (health - before < 5 || now < _nextHealLog) return;
            _nextHealLog = now + 10_000;
            int reach = participants.Count(p => p.IsAlive && p.TargetObject == _target && p.IsWithinRadius(_target, 600));
            int targeting = participants.Count(p => p.IsAlive && p.TargetObject == _target);
            var npc = _target as GameNPC;
            string effects = string.Join(",", _target.effectListComponent.GetEffects().Select(e => e.SpellHandler?.Spell?.Name ?? e.EffectType.ToString()).Distinct().Take(8));
            log.Warn($"REALM_EXPEDITION_TARGET_HEALED region={_region} target=\"{_target.Name}\" from={before} to={health} inCombat={_target.InCombat} " +
                     $"lastHitAgoMs={(_target.LastAttackedByEnemyTick > 0 ? now - _target.LastAttackedByEnemyTick : -1)} " +
                     $"aggro={(npc?.Brain as DOL.AI.Brain.StandardMobBrain)?.AggroCount ?? -1} targeting={targeting} inReach={reach} " +
                     $"returning={npc?.IsReturningToSpawnPoint} fromSpawn={(npc == null ? -1 : (int)npc.GetDistanceTo(npc.SpawnPoint))} " +
                     $"model={npc?.Model} effects=\"{effects}\"");
        }
        public const long TargetApproachStallMilliseconds = 3 * 60_000;
        private float _targetClosest = float.MaxValue;
        private long _targetApproachTick;
        private long _nextTargetCall;
        public const long BusyCallAfterMilliseconds = 3 * 60_000;
        public const int ThinAttackTargeting = 8;

        /// <summary>
        /// An encounter's hazard, not a monster to clear: Galladoria's Watery Vortexes float over the water primal's pool,
        /// are out of reach, come back every six minutes and hit hard. Taken as the primal's adds, they were the raid's
        /// target over and over; three were skipped for no damage and the raid fell from 230 inside to 28 (run 22).
        /// </summary>
        /// <summary>
        /// Script adds that must die before their timer runs out, not monsters to clear: Grand Summoner Govannon's summoned
        /// sacrifice and summoned demon never attack, so no defence rule ever fired, they reached the centre and the Shade of
        /// Aelfgar spawned every time (sh-bosses L5). Lossren's Tortured Souls are not on the list: they return for ever.
        /// </summary>
        public static bool IsKillFirstBrain(string brainType) => brainType is "SummonedSacrificeBrain" or "SummonedDemonBrain";
        public const int KillFirstCallers = 16;
        public const int KillFirstReach = 2_500;

        /// <summary>Who is called onto a kill-first add first: idle ranged, busy ranged, idle melee, busy melee.</summary>
        public static int KillFirstRank(bool ranged, bool busy) => (ranged ? 0 : 2) + (busy ? 1 : 0);

        /// <summary>The event record keeps each scripted boss and final encounter that fell or was set aside (the travel
        /// lane's ledger); ordinary route targets are left out so the record stays short. One write per boss.</summary>
        private void RecordEncounter(string result)
        {
            if (_target is GameEpicBoss || _target != null && _finalTypes.Contains(_target.GetType().Name))
                RealmEventRecords.Encounter(_region, _realm, _target.Name, result);
        }

        /// <summary>
        /// Calls up to <see cref="KillFirstCallers"/> raiders within 2,500 onto each living kill-first add beside the target,
        /// nearest idle ranged raiders first; raiders already on it count. The boss stays the raid's target.
        /// </summary>
        private void CallKillFirst(GameNPC[] live, GameLiving[] participants)
        {
            foreach (GameNPC add in live)
            {
                if (add.Brain == null || !IsKillFirstBrain(add.Brain.GetType().Name) || !add.IsAlive ||
                    add.ObjectState != GameObject.eObjectState.Active || add.CurrentRegionID != _target.CurrentRegionID ||
                    !add.IsWithinRadius(_target, (int)EncounterAddRadius))
                    continue;
                int onIt = 0;
                var callable = new List<(GameBot Bot, DOL.AI.Brain.BotBrain Brain, int Rank, float Distance)>();
                foreach (GameLiving member in participants)
                {
                    if (member is not GameBot bot || !bot.IsAlive || bot.CurrentRegionID != add.CurrentRegionID) continue;
                    if (bot.TargetObject == add) { onIt++; continue; }
                    if (!bot.IsWithinRadius(add, KillFirstReach) || bot.Brain is not DOL.AI.Brain.BotBrain brain ||
                        !GameServer.ServerRules.IsAllowedToAttack(bot, add, true))
                        continue;
                    callable.Add((bot, brain, KillFirstRank(AutonomousRvrDefense.IsRangedDefender(bot), bot.InCombat || bot.IsAttacking),
                        (float)bot.GetDistanceTo(add)));
                }
                int called = 0;
                foreach (var (bot, brain, _, _) in callable.OrderBy(c => c.Rank).ThenBy(c => c.Distance).Take(Math.Max(0, KillFirstCallers - onIt)))
                {
                    bot.TargetObject = add;
                    brain.AddToAggroList(add, 10_000);
                    brain.FSM.SetCurrentState(DOL.GS.eFSMStateType.AGGRO);
                    brain.NextThinkTick = GameLoop.GameLoopTime;
                    called++;
                }
                if (called > 0)
                    log.Info($"REALM_EXPEDITION_KILL_FIRST region={_region} target=\"{_target.Name}\" add=\"{add.Name}\" health={add.HealthPercent} " +
                             $"onIt={onIt} called={called}");
            }
        }

        private static bool IsEncounterHazard(GameNPC npc) =>
            npc.Brain?.GetType().Name is "VortexBrain" or "WaterfallAntipassBrain";
        public const int ThinAttackNear = 16;

        /// <summary>
        /// The raid calls its target: when the encounter has taken no damage for 90 s although raid
        /// members stand near it (Summoner Lossren and Council Nokkvi skipped at 100%, 2026-10-06),
        /// idle living members within 1,500 units are ordered to attack it, at most every 30 s.
        /// Ordinary combat, healing and group defense take it from there.
        /// </summary>
        private void CallTarget(GameLiving[] participants, long now)
        {
            if (_target == null || now < _nextTargetCall) return;
            // A target losing health slowly to a handful while the raid stands round it is called too: Galladoria's
            // Guardian Earthmender (60,000 health, stationary) went from 100% to 73% in 11 minutes with 20-190 raiders
            // within 1,000 and 1-7 of them on it, and the no-damage call never came (run 22). Idle raiders only.
            bool thin = false;
            if (now - _targetProgressTick < CallTargetAfterMilliseconds)
            {
                int onIt = 0, around = 0;
                foreach (GameLiving p in participants)
                {
                    if (!p.IsAlive || p.CurrentRegionID != _target.CurrentRegionID) continue;
                    if (p.TargetObject == _target) onIt++;
                    if (p.IsWithinRadius(_target, 1500)) around++;
                }
                thin = onIt < ThinAttackTargeting && around >= ThinAttackNear;
                if (!thin) return;
            }
            _nextTargetCall = now + 30_000;
            int called = 0;
            // After three idle minutes the call also takes raiders already fighting the encounter's trash: in
            // Darkness Falls the succubi and ambassadors around Princess Nahemah kept every one of 65 raiders
            // busy for five minutes and she was skipped at 100% (run 17). Some of the fighters switch; the
            // rest keep the adds off them.
            // A thin attack takes busy fighters too: the target keeps losing a little health, so the idle clock never runs out
            // (run 27: 207 raiders by the Magical Earthmender, 100 on the Corrupt Primal of Earth it heals, 2 on it).
            bool takeBusy = thin || now - _targetProgressTick >= BusyCallAfterMilliseconds;
            int busyLeft = takeBusy ? Math.Max(MinimumRaidersAtTarget, participants.Count(p => p.IsAlive && p.IsWithinRadius(_target, 1500)) / 3) : 0;
            foreach (GameLiving member in participants)
            {
                if (member is not GameBot bot || !bot.IsAlive || bot.CurrentRegionID != _target.CurrentRegionID ||
                    !bot.IsWithinRadius(_target, 1500) || bot.TargetObject == _target ||
                    bot.Brain is not DOL.AI.Brain.BotBrain brain || !GameServer.ServerRules.IsAllowedToAttack(bot, _target, true))
                    continue;
                if (bot.InCombat || bot.IsAttacking)
                {
                    if (busyLeft <= 0) continue;
                    busyLeft--;
                }
                // The bot holds the call itself (sh-bosses L5): without that its party's kill target took it straight
                // back on its next think. A bot that does not take the call (a tank holding its own target) is
                // ordered the old way.
                if (!brain.TakeRaidCall(_target))
                {
                    bot.TargetObject = _target;
                    brain.AddToAggroList(_target, bot.InCombat || bot.IsAttacking ? 10_000 : 1);
                    brain.FSM.SetCurrentState(DOL.GS.eFSMStateType.AGGRO);
                    brain.NextThinkTick = GameLoop.GameLoopTime; // act on the call now, not at the next idle think
                }
                called++;
            }
            if (called > 0)
                log.Info($"REALM_EXPEDITION_TARGET_CALLED region={_region} target=\"{_target.Name}\" health={_target.HealthPercent} called={called} " +
                         $"idleSeconds={(now - _targetProgressTick) / 1000}");
        }

        public void ObserveCompletion()
        {
            foreach (var npc in WorldMgr.GetRegion(_region)?.Objects.OfType<GameNPC>() ?? [])
                if (_finalTypes.Contains(npc.GetType().Name) && (!_finals.TryGetValue(npc.GetType().Name, out var old) || old.IsAlive))
                    _finals[npc.GetType().Name] = npc;
            if (_finalTypes.All(type => _finals.TryGetValue(type, out var boss) && !boss.IsAlive))
            { Complete = true; Status = "Final encounter defeated"; }
        }

        // Run 21 (2026-10-17): 70+ raiders fought Spindler Broodmother for over 30 minutes and nothing showed how she
        // fared. Once a minute: the current target's health, whether it is in combat, and the raiders around it.
        private long _nextTargetStatusLog;

        public void Advance(GameLiving[] participants, long now)
        {
            AdvanceCore(participants, now);
            GameNPC target = _target;
            if (target != null && target.IsAlive && now >= _nextTargetStatusLog)
            {
                _nextTargetStatusLog = now + 60_000;
                int near = participants.Count(p => p.IsAlive && p.CurrentRegionID == target.CurrentRegionID && p.IsWithinRadius(target, 1_000));
                int onIt = participants.Count(p => p.IsAlive && p.TargetObject == target);
                // What the raiders by the target fight instead (run 26: 205 raiders within 1,000 of a Magical Earthmender, 3 on it).
                string busyWith = string.Join(",", participants
                    .Where(p => p.IsAlive && p.TargetObject is GameLiving other && other != target && p.IsWithinRadius(target, 1_000))
                    .GroupBy(p => p.TargetObject.Name).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key}:{g.Count()}"));
                log.Info($"REALM_EXPEDITION_TARGET_STATUS region={_region} target=\"{target.Name}\" level={target.Level} " +
                         $"health={target.HealthPercent}% inCombat={target.InCombat} raidersWithin1000={near} targeting={onIt} " +
                         $"at={target.X},{target.Y},{target.Z} spawnDistance={(int)target.GetDistanceTo(target.SpawnPoint)} busyWith=\"{busyWith}\"");
            }
            if (!Hold || Complete) { _holdSince = 0; return; }
            if (_holdSince == 0) { _holdSince = now; return; }
            if (now - _holdSince < HoldReportMilliseconds || now < _nextHoldLog) return;
            _nextHoldLog = now + HoldReportMilliseconds;
            int inside = participants.Count(p => p.IsAlive && p.CurrentRegionID == _region);
            log.Warn($"REALM_EXPEDITION_HOLD region={_region} minutes={(now - _holdSince) / 60_000} status=\"{Status}\" " +
                     $"front={(int)Front.X},{(int)Front.Y},{(int)Front.Z} target=\"{TargetName}\" inside={inside}" +
                     (_region == 60 ? $" apoc={ApocInitializator.DescribeState()}" : "") +
                     (_region == 191 ? $" galladoria={DescribeLiveEncounters()}" : ""));
        }

        // Run 28 (2026-10-20): Galladoria's raid waited 30 minutes at the Olcasgean staging with three primals dead and the
        // Water primal never a target. Every living encounter NPC of the region, and why the raid passes it by.
        private string DescribeLiveEncounters()
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (GameNPC npc in WorldMgr.GetRegion(_region)?.Objects.OfType<GameNPC>() ?? [])
            {
                if (npc is not IGameEpicNpc || !npc.IsAlive) continue;
                string why = npc.ObjectState != GameObject.eObjectState.Active ? "inactive" :
                    npc.Realm != eRealm.None ? $"realm{(int)npc.Realm}" :
                    (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) != 0 ? $"flags{(int)npc.Flags}" :
                    _cleared.Contains(npc) ? "cleared" :
                    NameCleared(npc is GameEpicBoss, _finalTypes.Contains(npc.GetType().Name), _clearedNames.Contains(npc.Name), _clearedBossNames.Contains(npc.Name)) ? "clearedName" :
                    IsEncounterHazard(npc) ? "hazard" :
                    _routeRetry.TryGetValue(npc, out long retry) ? $"retry{(retry - GameLoop.GameLoopTime) / 1000}s" : "candidate";
                parts.Add($"{npc.Name}@{npc.X},{npc.Y},{npc.Z}:{npc.HealthPercent}%:{why}");
            }
            return $"deadPrimals={DOL.AI.Brain.OIBrain.DeadPrimalsCount} [{string.Join("; ", parts)}]";
        }

        private void AdvanceCore(GameLiving[] participants, long now)
        {
            if (Complete) return;
            if (_routeStartTick == 0) _routeStartTick = now;
            RegroupPoint = null;
            GameNPC[] live = WorldMgr.GetRegion(_region)?.Objects.OfType<GameNPC>().ToArray() ?? [];
            foreach (GameNPC stale in _routeRetry.Keys.Where(n => !n.IsAlive || n.ObjectState != GameObject.eObjectState.Active).ToArray())
                _routeRetry.Remove(stale);
            foreach (GameNPC npc in live.Where(n => _finalTypes.Contains(n.GetType().Name)))
                if (!_finals.TryGetValue(npc.GetType().Name, out var old) || old.IsAlive) _finals[npc.GetType().Name] = npc;
            if (_target != null && !_target.IsAlive)
            {
                log.Info($"REALM_EXPEDITION_TARGET_DEFEATED region={_region} target=\"{_target.Name}\" level={_target.Level} " +
                         $"minutes={(now - _targetStartTick) / 60_000.0:0.0}");
                RecordEncounter("defeated");
                _cleared.Add(_target);
                _clearedNames.Add(_target.Name);
                if (_target is GameEpicBoss) _clearedBossNames.Add(_target.Name);
                _target = null;
            }
            bool finalDeaths = _finalTypes.All(type => _finals.TryGetValue(type, out var boss) && !boss.IsAlive);
            if (finalDeaths) { Complete = true; Status = "Final encounter defeated"; return; }
            if (_target?.IsAlive == true && _target.ObjectState == GameObject.eObjectState.Active)
            {
                // A target that reset (a wipe, a leash home, a heal on a kill) is measured from its new health: against the
                // old low the returning raid showed no progress until it was below it again, and the boss was set aside
                // while the raid was winning (Summoner Roesia skipped at 28%, 2026-10-07).
                _targetLowestHealth = ProgressMark(_targetLowestHealth, _target.Health, _target.InCombat);
                if (_target.Health < _targetLowestHealth) { _targetLowestHealth = _target.Health; _targetProgressTick = now; }
                ReportHealthJump(participants, now);
                // The no-damage clock runs only while the raid is actually at its target. A raid
                // wiped or regrouping elsewhere skipped Prince Ba'alorien and Princess Nahemah at
                // 100% (run 11). Paused at most 20 minutes per absence, so an unreachable one still goes.
                int near = participants.Count(p => p.IsAlive && p.CurrentRegionID == _region &&
                                                   p.IsWithinRadius(_target, TargetPresenceRadius));
                if (near >= MinimumRaidersAtTarget) _targetPresentTick = now;
                // Still closing in also pauses it: a raid walking a long way to its target is not stalled.
                float closest = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region)
                    .Select(p => (float)p.GetDistanceTo(_target)).DefaultIfEmpty(float.MaxValue).Min();
                if (closest < _targetClosest - TargetApproachStepUnits) { _targetClosest = closest; _targetApproachTick = now; }
                bool approaching = now - _targetApproachTick < TargetApproachStallMilliseconds;
                if (AbsencePausesClock(near, now, _targetStartTick, _targetPresentTick, approaching))
                    _targetProgressTick = Math.Max(_targetProgressTick, now - 1_000);
                // A tethered boss outside its tether is immune while the raid leads it home: not a stall.
                if (_target is GameNPC tethered && AutonomousTetherReset.IsOutOfTether(tethered))
                {
                    _targetProgressTick = Math.Max(_targetProgressTick, now - 1_000);
                    Hold = false; Status = $"Drawing {_target.Name} back to its lair ({_target.HealthPercent}%)"; return;
                }
                // Never switch a live grind objective because another party has not
                // arrived, but never hold the whole expedition on a target nobody damages.
                bool final = _finalTypes.Contains(_target.GetType().Name);
                if (NeedsMuster(final, _target is GameEpicBoss, RealmRaidNeutralEvents.IsObjective(_objectives, _target.Name)) &&
                    MusterForTarget(participants, now))
                    return;
                bool wipedOut = _musterTarget == _target && WipedOut(final, _musterWipes);
                if (!wipedOut && !ShouldSkipTarget(final, now - _targetProgressTick))
                {
                    CallTarget(participants, now);
                    if (_target is GameEpicBoss) CallKillFirst(live, participants);
                    Hold = false; Status = $"Clearing {_target.Name} ({_target.HealthPercent}%)"; return;
                }
                log.Warn($"REALM_EXPEDITION_TARGET_SKIPPED region={_region} target=\"{_target.Name}\" at={_target.X},{_target.Y},{_target.Z} " +
                         $"health={_target.HealthPercent} front={(int)Front.X},{(int)Front.Y},{(int)Front.Z} near={near} " +
                         $"reason=\"{(wipedOut ? $"wiped {_musterWipes} times" : "no damage for five minutes")}\"");
                RecordEncounter("skipped");
                _routeRetry[_target] = now + BlockedTargetRetryMilliseconds;
                _musterTarget = null; // a fresh muster (and wipe count) when it is tried again
            }
            _target = null;
            GameLiving[] inside = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region).ToArray();
            if (AdvanceChain(inside, now)) return;
            bool atFront = inside.Any(p => p.IsWithinRadius(new Point3D((int)Front.X, (int)Front.Y, (int)Front.Z), 1000));
            if (inside.Length >= 8 && !atFront)
            {
                // The front can be left on a spot nobody reaches: Tuscaran Glacier's griffon icewing
                // was skipped and its approach stayed the front, so 268 raiders inside "waited for
                // formed parties" for eight hours (2026-10-12). After a few minutes the front moves to
                // where most raiders stand, and the next encounter is searched from there.
                if (_frontAbsentSince == 0) _frontAbsentSince = now;
                // A long climb is not a stall: Caer Sidi's final staging is three proven path legs up from the raid, and
                // the front was moved back down after three minutes with the raid still on its way (run 22).
                if (_frontClosestFor != Front) { _frontClosestFor = Front; _frontClosest = float.MaxValue; }
                float closestToFront = float.MaxValue;
                foreach (GameLiving p in inside)
                    closestToFront = Math.Min(closestToFront, Vector3.Distance(new(p.X, p.Y, p.Z), Front));
                if (closestToFront < _frontClosest - TargetApproachStepUnits)
                {
                    _frontClosest = closestToFront;
                    _frontAbsentSince = now;
                }
                if (now - _frontAbsentSince >= FrontRegroupMilliseconds && TryRegroupFront(inside, out Vector3 regrouped))
                {
                    log.Warn($"REALM_EXPEDITION_FRONT_REGROUP region={_region} from={(int)Front.X},{(int)Front.Y},{(int)Front.Z} " +
                             $"to={(int)regrouped.X},{(int)regrouped.Y},{(int)regrouped.Z} inside={inside.Length} " +
                             $"reason=\"nobody reached the front for {(now - _frontAbsentSince) / 60_000} minutes\"");
                    Front = Destination = regrouped;
                    _frontAbsentSince = 0;
                    atFront = true;
                }
            }
            else _frontAbsentSince = 0;
            if (inside.Length < 8 || !atFront)
            { Hold = true; Destination = Front; Status = "Waiting for formed parties inside"; return; }
            // Form up inside the door before the first target: the first 8 through it set the whole raid walking into the
            // first summoner (or Caer Sidi's first rooms) while most of it was still in the approach dungeon (sh-routes 1).
            if (!_firstTargetChosen)
            {
                if (_entryWaitSince == 0) _entryWaitSince = now;
                var door = new Point3D((int)Front.X, (int)Front.Y, (int)Front.Z);
                int formed = inside.Count(p => p.IsWithinRadius(door, 1000));
                int formNeeded = EncounterMusterNeeded(participants.Length, now - _entryWaitSince);
                if (formed < formNeeded)
                {
                    Hold = true; Destination = Front; RegroupPoint = Front;
                    Status = $"Forming up inside ({formed}/{formNeeded})";
                    if (now >= _nextMusterLog)
                    {
                        _nextMusterLog = now + 2 * 60_000;
                        log.Info($"REALM_EXPEDITION_ENTRY_MUSTER region={_region} formed={formed} needed={formNeeded} inside={inside.Length} " +
                                 $"roster={participants.Length} minutes={(now - _entryWaitSince) / 60_000.0:0.0}");
                    }
                    return;
                }
            }
            var nav = PathfindingProvider.Instance;
            Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)Front.X, (int)Front.Y);
            if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            { Hold = true; Status = "Blocked: dungeon navigation unavailable"; return; }
            GameNPC[] encounters = live.Where(n => n is GameEpicBoss && n.IsAlive && n.ObjectState == GameObject.eObjectState.Active).ToArray();
            GameNPC[] candidates = live.Where(n => n.IsAlive && n.ObjectState == GameObject.eObjectState.Active && n.Realm == eRealm.None &&
                n.Level > 0 && n is not GameBot && n is not GameSummonedPet && n.Brain is not IControlledBrain &&
                n is not GameMerchant && n is not GameTrainer && n is not GameTeleporter && n is not GameTaxi &&
                (n.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 && !_cleared.Contains(n) &&
                // A pet class's charm body (a copy of an overland monster it is charming) is no dungeon monster: a
                // Hibernian raid stood four minutes by a "grogan" in Galladoria that none of it could attack (run 22).
                n.TempProperties.GetProperty<bool>(AutonomousPetSupport.SyntheticCharmPetProperty) != true &&
                !IsEncounterHazard(n) &&
                (!_finalPhase || _finalTypes.Contains(n.GetType().Name) ||
                    Vector3.DistanceSquared(_trigger, new(n.X, n.Y, n.Z)) <= FinalPhaseRadius * FinalPhaseRadius) &&
                !NameCleared(n is GameEpicBoss, _finalTypes.Contains(n.GetType().Name), _clearedNames.Contains(n.Name), _clearedBossNames.Contains(n.Name)) &&
                // A neutral expedition goes for its named encounters only; monsters on the way are
                // fought when they attack (Darkness Falls holds 2,465 spawns).
                (_objectives == null ? IsEpicDungeonTarget(now - _routeStartTick < EpicTrashPhaseMilliseconds, n is GameEpicBoss,
                        _finalTypes.Contains(n.GetType().Name), IsEncounterAdd(n, Front, encounters))
                    : RealmRaidNeutralEvents.IsObjective(_objectives, n.Name)))
                .OrderBy(n => _finalTypes.Contains(n.GetType().Name) ? 1 : 0)
                .ThenBy(n => IsEncounterAdd(n, Front, encounters) ? 0 : 1)
                .ThenBy(n => Vector3.DistanceSquared(Front, new(n.X, n.Y, n.Z))).ToArray();
            // Probe from the front and from where the parties actually stand, near or far:
            // Tuscaran's front once sat on a pad under the floor its parties stood on.
            Vector3[] anchors = DistinctAnchors(Front, inside
                    .Where(p => p.Group == null || p.Group.LivingLeader == p)
                    .Select(p => new Vector3(p.X, p.Y, p.Z)), 6);
            int probes = 0;
            // Resume after the last examined candidate. With more blocked
            // spawns than the per-minute probe budget, always starting at zero
            // would retry the same expired failures and starve later rooms.
            for (int examined = 0; examined < candidates.Length && probes < 3; examined++)
            {
                GameNPC npc = candidates[_probeCursor % candidates.Length];
                _probeCursor = (_probeCursor + 1) % candidates.Length;
                if (_routeRetry.GetValueOrDefault(npc) > now) continue;
                probes++;
                Vector3 raw = AutonomousDungeonGoalCatalog.TryGet(npc, out var point) ? point.Position : new(npc.X, npc.Y, npc.Z);
                // A flyer is approached on the floor under it (the approach search only looks 256 up or down).
                if ((npc.Flags & GameNPC.eFlags.FLYING) != 0 &&
                    nav.GetClosestPoint(zone, raw, 128, 128, RangedFlyerReach, nav.DefaultFilters) is Vector3 below && raw.Z - below.Z > 256)
                    raw = below;
                Vector3 approach = default, provenFrom = Front;
                bool reachable = false;
                foreach (Vector3 anchor in anchors)
                {
                    Zone anchorZone = WorldMgr.GetRegion(_region)?.GetZone((int)anchor.X, (int)anchor.Y) ?? zone;
                    if (nav.HasNavmesh(anchorZone) && AutonomousZonePointApproach.TryResolve(nav, anchorZone, anchor, raw, 220, out approach))
                    { reachable = true; provenFrom = anchor; break; }
                }
                if (!reachable && _region == AutonomousDarknessFallsPolicy.RegionId && _objectives != null &&
                    TryDarknessFallsChain(nav, zone, raw, out Vector3[] chain))
                {
                    // Too far for one proven path: walk the certified waypoint chain to it. Run 18: the chain
                    // always began at its first waypoint by the realm entrance; a raid already down on Oro's floor
                    // (one-way drops) could not path back up, and 300 raiders looped on NoPath for half an hour.
                    // Start at the furthest waypoint the parties can reach; with none, set the target aside.
                    int start = ReachableChainStart(nav, chain, inside);
                    if (start < 0)
                    { _routeRetry[npc] = now + 60_000; continue; }
                    _chain = chain; _chainIndex = start; _chainTarget = npc;
                    TargetName = npc.Name; TargetLevel = npc.Level; _probeCursor = 0; _blockedSince = 0; _firstTargetChosen = true;
                    AdvanceChain(inside, now);
                    return;
                }
                if (!reachable)
                { _routeRetry[npc] = now + 60_000; continue; }
                // A flyer hovering far above its floor approach cannot be meleed, but one within spell and bow reach is
                // shot down from below (2026-10-16: Galladoria's Corrupt Primal of Air circles 690 above the floor and never
                // fights; skipped, it never died, and Olcasgean wakes only when all four primals are dead).
                if (IsOutOfRangedReach(npc.Flags, npc.Z, (int)approach.Z))
                { _routeRetry[npc] = now + BlockedTargetRetryMilliseconds; continue; }
                _approachFrom = provenFrom; _hasApproachFrom = true;
                _target = npc; Front = Destination = approach; TargetName = npc.Name; TargetLevel = npc.Level;
                _targetProgressTick = _targetStartTick = now; _targetLowestHealth = npc.Health;
                _targetClosest = float.MaxValue; _targetApproachTick = now; _targetPresentTick = 0;
                _probeCursor = 0; _blockedSince = 0; _firstTargetChosen = true;
                Hold = false; Status = "Clearing " + npc.Name; return;
            }
            Hold = true;
            // Candidates not yet probed this round are still being checked. When every remaining one is on a retry timer
            // (unreachable for now, set aside for no damage, a flyer out of reach) the raid goes on to the final trigger:
            // in Caer Sidi and Galladoria the final does not exist before the raid stands there, so one monster it could
            // not kill or reach kept it away for good (epic-bosses B1). Tuscaran already does this through its sort order.
            if (!EverySetAside(candidates.Select(n => _routeRetry.GetValueOrDefault(n)), now))
            {
                Status = "Checking blocked room approaches; no false completion";
                CountBlocked(candidates, now);
                return;
            }
            // The encounter controller must see a real party at its native trigger.
            // Waiting through dialogue/phase timers is not an empty-camp failure.
            Vector3 trigger = default;
            bool triggerReachable = false;
            foreach (Vector3 anchor in anchors)
            {
                Zone anchorZone = WorldMgr.GetRegion(_region)?.GetZone((int)anchor.X, (int)anchor.Y) ?? zone;
                if (nav.HasNavmesh(anchorZone) && AutonomousZonePointApproach.TryResolve(nav, anchorZone, anchor, _trigger, 220, out trigger))
                { triggerReachable = true; break; }
            }
            if (!triggerReachable)
            {
                // "Route blocked" now means the final trigger itself cannot be reached.
                Status = "Checking blocked room approaches; no false completion";
                CountBlocked(candidates, now);
                return;
            }
            _blockedSince = 0;
            Front = Destination = trigger;
            if (candidates.Length > 0 && !_finalPhase)
            {
                _finalPhase = true;
                log.Warn($"REALM_EXPEDITION_FINAL_PHASE region={_region} setAside={candidates.Length} " +
                         $"names=\"{string.Join(",", candidates.Select(n => n.Name).Distinct().Take(8))}\" reason=\"every remaining target is on a retry timer\"");
            }
            TargetName = "Final encounter staging";
            _firstTargetChosen = true;
            Status = candidates.Length == 0 ? "Waiting for the next scripted encounter phase" :
                $"Waiting for the next scripted encounter phase; {candidates.Length} set aside";
        }

        private void CountBlocked(GameNPC[] candidates, long now)
        {
            if (_blockedSince == 0) _blockedSince = now;
            if (BlockedTooLong(_blockedSince, now) && !Blocked)
            {
                Blocked = true;
                log.Warn($"REALM_EXPEDITION_ROUTE_BLOCKED region={_region} front={(int)Front.X},{(int)Front.Y},{(int)Front.Z} " +
                         $"remaining={candidates.Length} nearest=\"{candidates.FirstOrDefault()?.Name}\" at={candidates.FirstOrDefault()?.X},{candidates.FirstOrDefault()?.Y},{candidates.FirstOrDefault()?.Z} " +
                         $"reason=\"no remaining encounter and not the final trigger reachable from the front or any party for 20 minutes\"");
            }
        }
    }
}
