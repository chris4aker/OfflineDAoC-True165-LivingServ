using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// The forced Albion Krondon raid of the owner's PC test, 2026-11-09 (RC3, build b72716e): a hub that waited 38 minutes
    /// for its last member, a member held in Avalon City for 30 minutes, slow walks between packs and to the musters, 100
    /// raiders burned in Orshom's Fire, and melee walking under Scurceol Hyrde's floating orbs. Unit tests of the rules only:
    /// no world tick, combat or pathing.
    /// </summary>
    [TestFixture, NonParallelizable]
    public sealed class UT_ForcedKrondonRaidFixes
    {
        private const long Minute = 60_000L;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        // ---- 1. the forced raid's hub and staging -------------------------------------------------------------------

        // ---- 2. the hold before a dungeon threat ---------------------------------------------------------------------

        // ---- 3. the raid's next pack ---------------------------------------------------------------------------------

        // ---- 4 and 5. Orshom's Fire and the floating orbs -------------------------------------------------------------

        public sealed class FixTwin : GameEpicBoss
        {
            public bool Fighting;
            public override bool IsAlive => true;
            public override bool InCombat => Fighting;
        }
        public sealed class FixFire : GameEpicNPC
        {
            public bool Fighting;
            public override bool IsAlive => true;
            public override bool InCombat => Fighting;
        }
        public sealed class FixShielded : GameEpicBoss
        {
            public override bool IsAlive => true;
            public override bool InCombat => true;
        }
        public sealed class FixOrb : GameEpicNPC
        {
            public bool Fighting;
            public override bool IsAlive => true;
            public override bool InCombat => Fighting;
        }
        public sealed class FixRaider : GameNPC
        {
            public override bool IsAlive => true;
        }

        private Region _region;
        private ushort _nextId = 300;
        private EpicTestServerScope _server;

        private bool _tacticsWere;

        [SetUp]
        public void SetUp()
        {
            _tacticsWere = DOL.GS.ServerProperties.Properties.BOT_EPIC_ENCOUNTER_TACTICS;
            DOL.GS.ServerProperties.Properties.BOT_EPIC_ENCOUNTER_TACTICS = true;   // True 1.65 edition: the tactics are behind bot_epic_encounter_tactics, off by default
            _server = new EpicTestServerScope();
            _region = new Region(new RegionData { Id = 61, Name = "Krondon fix test", Description = "Krondon fix test", Mobs = [] });
            EpicEncounterTactics.Register(new("FixTwin", "test/twin", [], 0, null, Hazards: ["FixFire"], HazardRadius: 300, HazardDamageType: "Cold"));
            EpicEncounterTactics.Register(new("FixShielded", "test/shielded", ["FixOrb"], 0, null));
            EpicEncounterTactics.ResetBlastForTests();
        }

        [TearDown]
        public void TearDown()
        {
            DOL.GS.ServerProperties.Properties.BOT_EPIC_ENCOUNTER_TACTICS = _tacticsWere;
            EpicEncounterTactics.Unregister("FixTwin");
            EpicEncounterTactics.Unregister("FixShielded");
            EpicEncounterTactics.ResetBlastForTests();
            _server.Dispose();
        }

        private T Npc<T>(int x, int y, int z, bool flying = false) where T : GameNPC
        {
            T npc = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            typeof(GameObject).GetField("_objectInRadiusCachesLock", Hidden)?.SetValue(npc, Activator.CreateInstance(
                typeof(GameObject).GetField("_objectInRadiusCachesLock", Hidden).FieldType));
            if (flying) typeof(GameNPC).GetField("m_flags", Hidden).SetValue(npc, GameNPC.eFlags.FLYING);
            // No brain (the route asks for one: a brain type marks some hazards).
            typeof(GameNPC).GetField("m_brains", Hidden)?.SetValue(npc, new System.Collections.ArrayList());
            npc.CurrentRegion = _region;
            npc.movementComponent = new NpcMovementComponent(npc);
            ((GameLiving)npc).movementComponent = npc.movementComponent;
            npc.X = x; npc.Y = y; npc.Z = z;
            npc.movementComponent.ForceUpdatePosition();
            npc.SpawnPoint = new Point3D(x, y, z);
            npc.ObjectID = _nextId++;
            npc.ObjectState = GameObject.eObjectState.Active;
            return npc;
        }

        private static List<Spell> Spells(eDamageType type) =>
            [new Spell(new DbSpell { Name = "test bolt", Damage = 200, DamageType = (int)type, Type = eSpellType.DirectDamage.ToString(),
                Target = eSpellTarget.ENEMY.ToString(), Range = 1500 }, 50)];

        [Test]
        public void TheCatalogMarksOrshomsFireAsAColdOnlyHazardOfTheTwins()
        {
            var fire = (OrshomFire)RuntimeHelpers.GetUninitializedObject(typeof(OrshomFire));
            EpicEncounterTactics.Hazard rule = EpicEncounterTactics.HazardOf(fire);
            Assert.That(rule, Is.Not.Null, "Orshom's Fire is a ground hazard");
            Assert.That(rule.Radius, Is.EqualTo(300), "its Fire Burn radius (OrylleAndOrshom.cs)");
            Assert.That(rule.VulnerableTo, Is.EqualTo(eDamageType.Cold));
            Assert.That(rule.Owners, Is.EquivalentTo(new[] { "Orshom", "Orylle" }));
            Assert.That(EpicEncounterTactics.IsHazard((Orshom)RuntimeHelpers.GetUninitializedObject(typeof(Orshom))), Is.False);
            Assembly gs = typeof(GameNPC).Assembly;
            foreach (var t in EpicEncounterTactics.Catalog.Where(t => t.HasHazards))
                foreach (string type in t.Hazards)
                    Assert.That(gs.GetTypes().Any(x => x.Name == type && typeof(GameNPC).IsAssignableFrom(x)), Is.True, type);
        }

        [Test]
        public void OnlyColdCastersShootTheFireAndOnlyFromOutsideIt()
        {
            // The real spots: the fire at 31406,69599, Orshom Brong's spawn about 900 away.
            var twin = Npc<FixTwin>(31697, 70458, 15610);
            var fire = Npc<FixFire>(31406, 69599, 15605);
            twin.Fighting = fire.Fighting = true;
            var live = new List<GameNPC> { twin, fire };
            var meleeInIt = Npc<FixRaider>(31477, 69574, 15611);  // where Ferken burned: 75 from it
            var meleeOutside = Npc<FixRaider>(31406, 70300, 15605);
            var wizardInIt = Npc<FixRaider>(31500, 69650, 15605);
            var wizardOutside = Npc<FixRaider>(31406, 70500, 15605);
            wizardInIt.HarmfulSpells = Spells(eDamageType.Cold);
            wizardOutside.HarmfulSpells = Spells(eDamageType.Cold);
            var fireMage = Npc<FixRaider>(31406, 70500, 15605);
            fireMage.HarmfulSpells = Spells(eDamageType.Heat);

            var a = EpicEncounterTactics.Decide(meleeInIt, fire, live, 0, false);
            Assert.That(a.Action, Is.EqualTo(EpicEncounterTactics.BotAction.StandOff));
            Assert.That(a.StandAt, Is.Not.Null);
            Assert.That(Vector2.Distance(new(a.StandAt.Value.X, a.StandAt.Value.Y), new(fire.X, fire.Y)),
                Is.GreaterThanOrEqualTo(300 + EpicEncounterTactics.HazardMargin), "out of its radius with room");
            var b = EpicEncounterTactics.Decide(meleeOutside, fire, live, 0, false);
            Assert.That((b.Action, b.Target), Is.EqualTo((EpicEncounterTactics.BotAction.Retarget, (GameNPC)twin)), "fight the twin, not the fire");
            Assert.That(EpicEncounterTactics.Decide(fireMage, fire, live, 0, true).Target, Is.SameAs(twin), "heat does nothing to it");
            Assert.That(EpicEncounterTactics.Decide(wizardInIt, fire, live, 0, true).Action, Is.EqualTo(EpicEncounterTactics.BotAction.StandOff));
            Assert.That(EpicEncounterTactics.Decide(wizardOutside, fire, live, 0, true).Action, Is.EqualTo(EpicEncounterTactics.BotAction.None),
                "a cold caster outside it shoots it");
            // With its twins dead it is simply left.
            twin.Fighting = false;
            var alone = EpicEncounterTactics.DecideHazard(meleeOutside, fire, [fire], false);
            Assert.That((alone.Action, alone.StandAt), Is.EqualTo((EpicEncounterTactics.BotAction.StandOff, (Vector3?)null)));
        }

        [Test]
        public void ARaiderStandingInTheFireStepsOutAndNoPostGoesInIt()
        {
            var fire = Npc<FixFire>(31406, 69599, 15605);
            EpicEncounterTactics.ObserveHazards([fire]);
            Assert.That(EpicEncounterTactics.HazardAt(61, new Vector3(31477, 69574, 15611)), Is.SameAs(fire));
            Assert.That(EpicEncounterTactics.HazardAt(61, new Vector3(31406, 69599 + 449, 15605)), Is.SameAs(fire), "the margin counts");
            Assert.That(EpicEncounterTactics.HazardAt(61, new Vector3(31406, 69599 + 451, 15605)), Is.Null);
            Assert.That(EpicEncounterTactics.HazardAt(61, new Vector3(31406, 69599, 15605 + 1500)), Is.Null, "another floor");
            Assert.That(EpicEncounterTactics.HazardAt(62, new Vector3(31406, 69599, 15605)), Is.Null, "another region");
            Vector3 exit = EpicEncounterTactics.HazardExit(new(31406, 69599, 15605), 300, new(31477, 69574, 15611));
            Assert.That(Vector2.Distance(new(exit.X, exit.Y), new(31406, 69599)), Is.EqualTo(550).Within(0.5));
            Assert.That(exit.X, Is.GreaterThan(31406), "away from it, on the raider's side");
        }

        [Test]
        public void MeleeOnAFlyingRequiredAddHoldsAtTheStandInsteadOfWalkingUnderIt()
        {
            var boss = Npc<FixShielded>(51941, 20068, 17174);
            var orb = Npc<FixOrb>(52713, 20841, 17511, flying: true);
            var live = new List<GameNPC> { boss, orb };
            var melee = Npc<FixRaider>(52656, 21354, 16990); // under the orb, on the floor below its perch
            var stand = new Vector3(50984, 21741, 17169); // the PC's 03:16:53 ranged stand
            EpicEncounterTactics.SetStand(boss, stand);
            var advice = EpicEncounterTactics.Decide(melee, orb, live, 0, canShootFlyers: false);
            Assert.That((advice.Action, advice.StandAt), Is.EqualTo((EpicEncounterTactics.BotAction.StandOff, (Vector3?)stand)));
            Assert.That(EpicEncounterTactics.Decide(melee, orb, live, 0, canShootFlyers: true).Action, Is.EqualTo(EpicEncounterTactics.BotAction.None),
                "a shooter fights it");
            Assert.That(EpicEncounterTactics.IsRequiredAddClass(orb), Is.True);
            Assert.That(EpicEncounterTactics.IsRequiredAddClass(boss), Is.False);
        }

    }
}
