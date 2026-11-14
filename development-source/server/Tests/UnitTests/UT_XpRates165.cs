using System.Reflection;
using DOL.GS;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Launcher Player XP rate and Gamebot XP rate options (owner 2026-11-10).</summary>
    [TestFixture, NonParallelizable]
    public sealed class UT_XpRates165
    {
        private double _playerRate;
        private double _botRate;
        private double _rvrRate;
        private bool _classic165;

        [SetUp]
        public void SaveRates()
        {
            _playerRate = Properties.XP_RATE;
            _botRate = Properties.BOT_XP_RATE;
            _rvrRate = Properties.RvR_XP_RATE;
            _classic165 = Properties.CLASSIC165_MODE;
        }

        [TearDown]
        public void RestoreRates()
        {
            Properties.XP_RATE = _playerRate;
            Properties.BOT_XP_RATE = _botRate;
            Properties.RvR_XP_RATE = _rvrRate;
            Properties.CLASSIC165_MODE = _classic165;
        }

        [Test]
        public void TrueModeNoLongerLocksRatesAtOne()
        {
            Properties.CLASSIC165_MODE = true;
            Properties.XP_RATE = 3;
            Properties.BOT_XP_RATE = 5;
            Assert.That(Classic165Mode.PlayerXpRate, Is.EqualTo(3));
            Assert.That(Classic165Mode.BotXpRate, Is.EqualTo(5));
        }

        [Test]
        public void DefaultsStayOriginalOneTimes()
        {
            Assert.That(XpRates165.BotMultiplier(false, false, 1, 1, 1), Is.EqualTo(1));
            Assert.That(XpRates165.BotMultiplier(true, false, 1, 1, 1), Is.EqualTo(1));
            Assert.That(XpRates165.PlayerMultiplier(false, 1, 1), Is.EqualTo(1));
        }

        [Test]
        public void AutonomousBotUsesGamebotRateOnly()
        {
            Assert.That(XpRates165.BotMultiplier(false, false, 10, 3, 2), Is.EqualTo(3));
            Assert.That(XpRates165.BotMultiplier(false, true, 10, 3, 2), Is.EqualTo(6));
        }

        [Test]
        public void HiredCompanionFollowsPlayerRate()
        {
            Assert.That(XpRates165.BotMultiplier(true, false, 5, 1, 2), Is.EqualTo(5));
            // In RvR zones (frontiers, battlegrounds, Darkness Falls) the player's rate still counts, times the RvR
            // rate, and so does the companion's (GitHub #51: it used to be the RvR rate alone).
            Assert.That(XpRates165.BotMultiplier(true, true, 5, 1, 2), Is.EqualTo(10));
            Assert.That(XpRates165.BotMultiplier(true, true, 5, 1, 1), Is.EqualTo(5));
            Assert.That(XpRates165.PlayerMultiplier(true, 5, 1), Is.EqualTo(5), "5x in Darkness Falls at the default RvR rate");
            Assert.That(XpRates165.PlayerMultiplier(true, 10, 1), Is.EqualTo(10));
            Assert.That(XpRates165.PlayerMultiplier(false, 5, 3), Is.EqualTo(5), "the RvR rate does nothing outside RvR zones");
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(-2.0)]
        [TestCase(0.0)]   // the launcher reads a stored 0 as 1x too (owner 2026-11-10)
        public void BadStoredRateCountsAsOne(double bad)
        {
            Assert.That(XpRates165.Sanitize(bad), Is.EqualTo(1));
        }

        [Test]
        public void HiredCompanionScalingReadsPlayerProperty()
        {
            Properties.XP_RATE = 4;
            Properties.BOT_XP_RATE = 2;
            Properties.RvR_XP_RATE = 1;
            MethodInfo scale = typeof(GameBot).GetMethod("ScaleBotExperience",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(scale.Invoke(null, new object[] { 100L, false, true, 0 }), Is.EqualTo(400L));
            Assert.That(scale.Invoke(null, new object[] { 100L, false, false, 0 }), Is.EqualTo(200L));
        }
    }
}
