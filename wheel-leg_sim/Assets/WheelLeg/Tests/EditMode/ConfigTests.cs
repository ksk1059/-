using NUnit.Framework;
using WheelLeg.Config;

namespace WheelLeg.Tests
{
    public class YamlParserTests
    {
        [Test]
        public void ParsesNestedMapsSequencesAndComments()
        {
            const string text =
                "# leading comment\n" +
                "run:\n" +
                "  mode: train      # trailing comment\n" +
                "  areas: 32\n" +
                "terrain:\n" +
                "  size: [16.0, 8.5]\n" +
                "  seeds:\n" +
                "    - 1\n" +
                "    - 2\n" +
                "name: \"quoted: value\"\n";

            YamlNode root = YamlParser.Parse(text, "test");
            ConfigNode node = ConfigNode.Root(root);

            Assert.AreEqual("train", node.Child("run").Str("mode"));
            Assert.AreEqual(32, node.Child("run").Int("areas"));
            Assert.AreEqual(new[] { 16.0f, 8.5f }, node.Child("terrain").Floats("size", 2));
            Assert.AreEqual(new[] { 1, 2 }, node.Child("terrain").IntArray("seeds"));
            Assert.AreEqual("quoted: value", node.Str("name"));
        }

        [Test]
        public void RejectsTabIndentation()
        {
            Assert.Throws<YamlException>(() => YamlParser.Parse("run:\n\tmode: train\n", "test"));
        }

        [Test]
        public void RejectsDuplicateKeys()
        {
            Assert.Throws<YamlException>(() => YamlParser.Parse("a: 1\na: 2\n", "test"));
        }

        [Test]
        public void MergeOverridesLeavesUntouchedKeysAlone()
        {
            YamlNode under = YamlParser.Parse("run:\n  areas: 1\n  seed: 5\n", "base");
            YamlNode over = YamlParser.Parse("run:\n  areas: 32\n", "profile");
            ConfigNode merged = ConfigNode.Root(YamlNode.Merge(under, over));

            Assert.AreEqual(32, merged.Child("run").Int("areas"));
            Assert.AreEqual(5, merged.Child("run").Int("seed"));
        }
    }

    public class ConfigNodeTests
    {
        [Test]
        public void UnreadKeyIsReportedAsUnknown()
        {
            ConfigNode node = ConfigNode.Root(YamlParser.Parse("a: 1\nb: 2\n", "test"));
            Assert.AreEqual(1, node.Int("a"));
            ConfigException e = Assert.Throws<ConfigException>(node.AssertFullyConsumed);
            StringAssert.Contains("b", e.Message);
        }

        [Test]
        public void WrongLengthRangeIsRejected()
        {
            ConfigNode node = ConfigNode.Root(YamlParser.Parse("r: [1.0, 2.0, 3.0]\n", "test"));
            Assert.Throws<ConfigException>(() => node.Floats("r", 2));
        }

        [Test]
        public void NonNumericValueIsRejected()
        {
            ConfigNode node = ConfigNode.Root(YamlParser.Parse("n: banana\n", "test"));
            Assert.Throws<ConfigException>(() => node.Float("n"));
        }
    }

    public class SeedBandTests
    {
        static ConfigNode Seeds(string trainBand, string evalBand)
        {
            string text =
                "train_band: " + trainBand + "\n" +
                "eval_band: " + evalBand + "\n" +
                "eval_list: [0]\n" +
                "eval_episodes_per_seed: 1\n";
            return ConfigNode.Root(YamlParser.Parse(text, "test"));
        }

        [Test]
        public void OverlappingBandsAreRejected()
        {
            // Evaluation terrain seen during training invalidates every later comparison,
            // so this has to fail at load rather than at analysis time.
            Assert.Throws<ConfigException>(() => SeedConfig.Read(Seeds("[0, 500]", "[0, 299]")));
        }

        [Test]
        public void DisjointBandsAreAccepted()
        {
            SeedConfig config = SeedConfig.Read(Seeds("[1000, 9999]", "[0, 299]"));
            Assert.AreEqual("train", config.BandName(1234));
            Assert.AreEqual("eval", config.BandName(7));
            Assert.AreEqual("unbanded", config.BandName(500));
        }
    }
}
