using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OfflineDaoc.Shared;

namespace DOL.GS.Tests
{
    /// <summary>2003-style bot names (owner 2026-10-29: 75% in the 2003 style, the rest lore names, after research).</summary>
    [TestFixture]
    public class UT_EraBotNames
    {
        private static Func<string, bool> Rejected()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "GameServer", "config", "invalidnames.txt");
                if (File.Exists(path))
                    return EraBotNames.LoadRejected(path);
                path = Path.Combine(dir.FullName, "source", "server", "GameServer", "config", "invalidnames.txt");
                if (File.Exists(path))
                    return EraBotNames.LoadRejected(path);
            }
            Assert.Fail("config/invalidnames.txt not found");
            return null;
        }

        [Test]
        public void ThreeInFourNamesAreIn2003Style()
        {
            Assert.That(EraBotNames.Mix.Sum(m => m.Percent), Is.EqualTo(100));
            Assert.That(EraBotNames.Mix.Where(m => m.Style != EraBotNames.Style.Lore).Sum(m => m.Percent), Is.EqualTo(75));
            var random = new Random(7);
            var counts = Enumerable.Range(0, 20_000).Select(_ => EraBotNames.PickStyle(random)).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
            foreach ((EraBotNames.Style style, int percent) in EraBotNames.Mix)
                Assert.That(counts.GetValueOrDefault(style) / 200.0, Is.EqualTo(percent).Within(1.5), style.ToString());
        }

        [Test]
        public void EveryStyleMakesNamesAPlayerCouldHaveAndNoChatWord()
        {
            Func<string, bool> rejected = Rejected();
            var random = new Random(11);
            foreach ((EraBotNames.Style style, _) in EraBotNames.Mix.Where(m => m.Style != EraBotNames.Style.Lore))
            foreach (int realm in new[] { 1, 2, 3 })
            foreach (bool female in new[] { false, true })
            {
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < 300; i++)
                {
                    string name = EraBotNames.Generate(style, realm, female, "Thane", random, taken.Contains, rejected, () => "Gisenalora");
                    if (name == null)
                        continue;
                    Assert.That(EraBotNames.Allowed(name, style), Is.True, $"{style}: {name}");
                    Assert.That(rejected(name), Is.False, $"{style}: {name} is on the invalid-names list");
                    Assert.That(name.ToLowerInvariant(), Does.Not.EndWith("lol"));
                    taken.Add(name);
                }
                Assert.That(taken.Count, Is.GreaterThan(style == EraBotNames.Style.FirstName ? 25 : 150), $"{style} realm {realm} female {female}");
            }
        }

        [Test]
        public void ChatWordsAreNeverNames()
        {
            foreach (string word in new[] { "Axe", "First", "Miracle", "Cave", "Blood", "Healer", "Tank", "Anyone", "Hellfire", "Max" })
                Assert.That(EraBotNames.Allowed(word), Is.False, word);
            Assert.That(EraBotNames.Allowed("Grizlas"), Is.True);
            Assert.That(EraBotNames.Allowed("Souleater"), Is.True);
            Assert.That(EraBotNames.Allowed("MrCoffee"), Is.False, "a capital inside a first name");
            Assert.That(EraBotNames.Allowed("Bonklol"), Is.False, "a post-2003 joke");
            Assert.That(EraBotNames.Allowed("Nate", EraBotNames.Style.FirstName), Is.True);
            Assert.That(EraBotNames.Allowed("Will", EraBotNames.Style.FirstName), Is.False);
            Assert.That(EraBotNames.Allowed("XxShadowxX", EraBotNames.Style.Decorated), Is.True);
            Assert.That(EraBotNames.Allowed("XxShadowxX"), Is.False, "mixed case only for xX names");
            Assert.That(EraBotNames.Allowed("Gankr420", EraBotNames.Style.Numbered), Is.True);
            Assert.That(EraBotNames.Allowed("D4rkst0rm", EraBotNames.Style.Numbered), Is.True);
            Assert.That(EraBotNames.Allowed("Darkblade88", EraBotNames.Style.Numbered), Is.False, "a hate-symbol number");
            Assert.That(EraBotNames.Allowed("Gank420"), Is.False, "digits only for number names");
        }

    }
}
