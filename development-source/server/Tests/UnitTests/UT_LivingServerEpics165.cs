using System.Linq;
using DOL.GS;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// True 1.65 edition: Living Server's epic encounter work is off by default. The restored original mechanics follow the
    /// epic_original_mechanics setting, and with bot_epic_encounter_tactics off no boss has a tactic.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class UT_LivingServerEpics165
    {
        private bool _tacticsWere;

        [SetUp]
        public void Remember() => _tacticsWere = Properties.BOT_EPIC_ENCOUNTER_TACTICS;

        [TearDown]
        public void Restore()
        {
            Properties.BOT_EPIC_ENCOUNTER_TACTICS = _tacticsWere;
            EpicEncounterMechanics.ApplySetting(string.Empty);
        }

        [Test]
        public void EveryOriginalMechanicIsOffByDefault()
        {
            Assert.That(EpicEncounterMechanics.ApplySetting(string.Empty), Is.Empty);
            Assert.That(EpicEncounterMechanics.ApplySetting(null), Is.Empty);
            foreach (string name in EpicEncounterMechanics.RestoreSwitches)
                Assert.That(typeof(EpicEncounterMechanics).GetProperty(name)!.GetValue(null), Is.EqualTo(false), name);
        }

        [Test]
        public void TheSettingTurnsOnTheNamedSwitchesOnly()
        {
            string[] on = EpicEncounterMechanics.ApplySetting("allowgovannonfullhealth, AllowTuscarTwinHeal;NotASwitch");
            Assert.That(on, Is.EquivalentTo(new[] { "AllowGovannonFullHealth", "AllowTuscarTwinHeal" }));
            Assert.That((EpicEncounterMechanics.AllowGovannonFullHealth, EpicEncounterMechanics.AllowTuscarTwinHeal,
                EpicEncounterMechanics.AllowKvasirColdHeal), Is.EqualTo((true, true, false)));
            Assert.That(EpicEncounterMechanics.ApplySetting("all"), Has.Length.EqualTo(EpicEncounterMechanics.RestoreSwitches.Length));
            Assert.That(EpicEncounterMechanics.ApplySetting(""), Is.Empty, "a later empty setting turns them all off again");
            Assert.That(EpicEncounterMechanics.AllowGovannonFullHealth, Is.False);
        }

        [Test]
        public void WithTheTacticsOffNoBossHasATacticOrAHazard()
        {
            Properties.BOT_EPIC_ENCOUNTER_TACTICS = false;
            Assert.That(EpicEncounterTactics.Enabled, Is.False);
            Assert.That(EpicEncounterTactics.Catalog, Is.Not.Empty, "the catalog is carried");
            Properties.BOT_EPIC_ENCOUNTER_TACTICS = true;
            Assert.That(EpicEncounterTactics.All.Select(t => t.Boss), Does.Contain("ScurceolHyrde"));
        }

        [Test]
        public void FunusAndTheEarthmendersAcceptTheOriginalClasses()
        {
            Assert.That(EpicEncounterRestores.FunusAccepts((int)eCharacterClass.Cleric, eObjectType.Blunt), Is.True);
            Assert.That(EpicEncounterRestores.FunusAccepts((int)eCharacterClass.Scout, eObjectType.Longbow), Is.True);
            Assert.That(EpicEncounterRestores.FunusAccepts((int)eCharacterClass.Scout, eObjectType.SlashingWeapon), Is.False);
            Assert.That(EpicEncounterRestores.EarthmenderAccepts(EpicEncounterRestores.Earthmender.Natural, (int)eCharacterClass.Druid), Is.True);
            Assert.That(EpicEncounterRestores.EarthmenderAccepts(EpicEncounterRestores.Earthmender.Natural, (int)eCharacterClass.Hero), Is.False);
            Assert.That(EpicEncounterRestores.AroonAccepts(eDamageType.Thrust, [true, false]), Is.False, "thrust opens after the second Scaith");
            Assert.That(EpicEncounterRestores.AroonAccepts(eDamageType.Slash, [true, false]), Is.True);
        }
    }
}
