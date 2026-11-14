using System;
using System.Linq;
using DOL.GS;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// True 1.65 edition: Living Server's Epic Special Forces (from chris4aker's Living Server 1.1.4), off by default. With
    /// the switch on, a Horn raid's first wipe at its final encounter calls up to 24 of them, fewer after wins they helped.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class UT_EpicSpecialForces165
    {
        private bool _was;

        [SetUp]
        public void Reset()
        {
            _was = Properties.BOT_EPIC_SPECIAL_FORCES;
            AutonomousEpicSpecialForces.ResetForTests();
        }

        [TearDown]
        public void Restore()
        {
            Properties.BOT_EPIC_SPECIAL_FORCES = _was;
            AutonomousEpicSpecialForces.ResetForTests();
        }

        [Test]
        public void OffByDefaultNoCallIsMade()
        {
            Properties.BOT_EPIC_SPECIAL_FORCES = false;
            Assert.That(AutonomousEpicSpecialForces.Enabled, Is.False);
            AutonomousEpicSpecialForces.SetHistoryForTests("epic-hibernia", "Failed", "Failed");
            Assert.That(AutonomousEpicSpecialForces.ComesFromStart("epic-hibernia"), Is.False, "off: never from the start");
            Assert.That(AutonomousEpicSpecialForces.SpecialForcesAnswer(1, false), Is.False, "off: a wipe calls nobody");
            AutonomousEpicSpecialForces.Request("epic-hibernia", "Galladoria", eRealm.Hibernia, "a wipe");
            Assert.That(AutonomousEpicSpecialForces.PendingCalls, Is.Zero);
        }

        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        [TestCase(3, false)]   // True 1.65 calls the battle off at its third wipe: no retry left to join
        public void AWipeWithARetryLeftCallsThem(int finalWipes, bool answer)
        {
            Properties.BOT_EPIC_SPECIAL_FORCES = true;
            Assert.That(AutonomousEpicSpecialForces.SpecialForcesAnswer(finalWipes, false), Is.EqualTo(answer));
        }

        [Test]
        public void TheCallUpShrinksWithEveryWinTheyHelpedAndGrowsWithEveryLoss()
        {
            RaidResult helped = new("Boss defeated", WithSpecialForces: true);
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousEpicSpecialForces.CallUpSize([]), Is.EqualTo(24), "no record: the full guild");
                Assert.That(AutonomousEpicSpecialForces.CallUpSize([helped]), Is.EqualTo(16));
                Assert.That(AutonomousEpicSpecialForces.CallUpSize([helped, helped, helped, helped]), Is.EqualTo(8), "they always answer a wipe");
                Assert.That(AutonomousEpicSpecialForces.CallUpSize(["Boss defeated", "Boss defeated"]), Is.EqualTo(24), "wins without them change nothing");
                Assert.That(AutonomousEpicSpecialForces.CallUpSize([helped, helped, "Failed"]), Is.EqualTo(16), "True 1.65's Failed is a defeat");
                Assert.That(AutonomousEpicSpecialForces.CallUpSize([helped, helped, "Timed out"]), Is.EqualTo(16), "a time-out after a wipe too");
                Assert.That(AutonomousEpicSpecialForces.CallUpSize([helped, AutonomousEpicSpecialForces.TimedOutWithoutWipe]), Is.EqualTo(16),
                    "a time-out without a wipe is no result");
                Assert.That(AutonomousEpicSpecialForces.CallUpSize([helped, "Failed rally", "Route blocked", "Stopped by player"]), Is.EqualTo(16));
            });
        }

        [Test]
        public void TheyComeFromTheStartOnlyAfterTwoDefeatsInARow()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousEpicSpecialForces.ComesFromStart(new[] { "Failed", "Timed out" }), Is.True);
                Assert.That(AutonomousEpicSpecialForces.ComesFromStart(new[] { "Failed", "Failed rally", "Failed" }), Is.True);
                Assert.That(AutonomousEpicSpecialForces.ComesFromStart(new[] { "Failed", "Boss defeated" }), Is.False);
                Assert.That(AutonomousEpicSpecialForces.ComesFromStart(Array.Empty<string>()), Is.False);
            });
        }

        [Test]
        public void ARaidIsAnsweredOnceAndAlwaysByAtLeastOneParty()
        {
            Properties.BOT_EPIC_SPECIAL_FORCES = true;
            AutonomousEpicSpecialForces.SetHistoryForTests("epic-hibernia");
            AutonomousEpicSpecialForces.Request("epic-hibernia", "Galladoria", eRealm.Hibernia, "a wipe");
            AutonomousEpicSpecialForces.Request("epic-hibernia", "Galladoria", eRealm.Hibernia, "a wipe");
            Assert.That(AutonomousEpicSpecialForces.PendingCalls, Is.EqualTo(1));
            Assert.That(AutonomousEpicSpecialForces.NextCallMembersForTests, Is.EqualTo(24));

            RaidResult helped = new("Boss defeated", WithSpecialForces: true);
            RealmRaidRecord.SetResultsForTests("epic-albion", helped, helped, helped);
            AutonomousEpicSpecialForces.Request("epic-albion", "Caer Sidi", eRealm.Albion, "a wipe");
            Assert.That(AutonomousEpicSpecialForces.PendingCalls, Is.EqualTo(2), "three wins they helped: one party still comes");
        }

        [Test]
        public void AFinishedRaidJoinsTheRecordButALauncherRaidDoesNot()
        {
            Properties.BOT_EPIC_SPECIAL_FORCES = true;
            AutonomousEpicSpecialForces.SetHistoryForTests("epic-midgard", "Failed");
            AutonomousEpicSpecialForces.RaidEnded("epic-midgard", "Boss defeated", forced: true);
            Assert.That(RealmRaidRecord.Get("epic-midgard"), Is.EqualTo(new[] { "Failed" }), "a raid started from the launcher is left out");
            AutonomousEpicSpecialForces.RaidEnded("epic-midgard", "Failed");
            Assert.That(AutonomousEpicSpecialForces.ComesFromStart("epic-midgard"), Is.True, "two defeats in a row");
        }

        [Test]
        public void TheyComeOnTopOfTheRealmsPartiesAndSlots()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RealmRaidRecruitmentPolicy.FormationSlots,
                    Is.EqualTo(RealmRaidRecruitmentPolicy.MaximumParties + AutonomousEpicSpecialForces.MaximumParties));
                Assert.That(RealmRaidRecruitmentPolicy.PartyCapRefusal(true, 38, 300, 0, 8), Is.Null, "a full raid still takes them");
                Assert.That(RealmRaidRecruitmentPolicy.PartyCapRefusal(true, 38, 300, 3, 8), Is.Not.Null, "three parties of their own at most");
                Assert.That(RealmRaidRecruitmentPolicy.PartyCapRefusal(false, 38, 296, 3, 4), Is.Not.Null, "the realm's own limit stands");
                Assert.That(RealmRaidRecruitmentPolicy.PartyCapRefusal(false, 37, 296, 3, 4), Is.Null);
                Assert.That(RealmRaidRecruitmentPolicy.PartyCapRefusal(false, 37, 297, 0, 4), Is.Not.Null, "300 bots");
            });
        }

        [Test]
        public void EachRealmsGuildPartyIsAFullExpeditionPartyOfThePeriodsClasses()
        {
            Assert.Multiple(() =>
            {
                foreach (var (realm, classes) in AutonomousEpicSpecialForces.PartyClasses)
                {
                    Assert.That(classes, Has.Length.EqualTo(AutonomousEpicSpecialForces.PartySize), realm.ToString());
                    Assert.That(AutonomousBotGroupCoordinator.PveFormationSize(classes[0], classes.Skip(1).ToArray()), Is.EqualTo(8), realm.ToString());
                    foreach (eCharacterClass characterClass in classes)
                        Assert.That(AutonomousBotIdentityGenerator.GetEraClasses(realm), Does.Contain(characterClass), $"{realm} {characterClass}");
                }
            });
        }

        [Test]
        public void TheirIdsNeverMeetARosterId() =>
            Assert.That(AutonomousEpicSpecialForces.FirstBotId, Is.GreaterThan(1_000_000_000L).And.LessThan(int.MaxValue));

        [Test]
        public void TheLedgerRowSaysWhoJoinedAndHowManyWipesItTook()
        {
            string details = RealmEventRecordStore.WithSpecialForces(RealmEventRecordStore.WithWipes("Timed out: no encounter.", 2), 16);
            Assert.Multiple(() =>
            {
                Assert.That(RealmEventRecordStore.SpecialForcesIn(details + " | Deaths: 40"), Is.EqualTo(16));
                Assert.That(RealmEventRecordStore.WipesIn(details), Is.EqualTo(2));
                Assert.That(RealmEventRecordStore.SpecialForcesIn("Boss defeated."), Is.Zero, "an old row: they did not join");
                Assert.That(RealmEventRecordStore.RecordOutcome("Timed out", RealmEventRecordStore.WithWipes("x", 0)),
                    Is.EqualTo(RealmEventRecordStore.TimedOutWithoutWipe));
                Assert.That(RealmEventRecordStore.RecordOutcome("Timed out", "an old row"), Is.EqualTo("Timed out"));
            });
        }

        [Test]
        public void TheLedgerGivesAnEncountersFinishedResultsOldestFirst()
        {
            string path = TemporarySqlite.NewDatabasePath("ledger");
            try
            {
                Assert.That(RealmEventRecordStore.Results(path, "epic-hibernia", 6), Is.Empty, "no ledger yet");
                RealmEventRecordStore.Save(path, new[]
                {
                    new RealmEventRecord("a", "epic-hibernia", "Galladoria", "Raid", "Hibernia", "2026-10-18T10:00:00Z", "2026-10-18T12:00:00Z", "Ended", "Failed", "Failed: beaten back. Wipes: 3.", 60, 50),
                    new RealmEventRecord("b", "epic-hibernia", "Galladoria", "Raid", "Hibernia", "2026-10-19T10:00:00Z", "2026-10-19T12:00:00Z", "Ended", "Boss defeated",
                        RealmEventRecordStore.WithSpecialForces("Won. Wipes: 1.", 24), 60, 50),
                    new RealmEventRecord("c", "epic-hibernia", "Galladoria", "Raid", "Hibernia", "2026-10-19T15:00:00Z", "", "Battle", "In progress", "", 60, 50),
                    new RealmEventRecord("d", "epic-hibernia", "Galladoria", "Raid", "Hibernia", "2026-10-20T10:00:00Z", "2026-10-20T12:00:00Z", "Ended", "Boss defeated",
                        RealmEventRecordStore.ForcedRaidPrefix + "Won.", 60, 50),
                });
                Assert.That(RealmEventRecordStore.Results(path, "epic-hibernia", 6, automaticOnly: true),
                    Is.EqualTo(new[] { ("Failed", 0), ("Boss defeated", 24) }));
                Assert.That(RealmRaidRecord.FromLedger(RealmEventRecordStore.Results(path, "epic-hibernia", 6, automaticOnly: true))
                    .Count(r => r.AssistedWin), Is.EqualTo(1));
            }
            finally
            {
                TemporarySqlite.DeleteDatabase(path);
            }
        }
    }
}
