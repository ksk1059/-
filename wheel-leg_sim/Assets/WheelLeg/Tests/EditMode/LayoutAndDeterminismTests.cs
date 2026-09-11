using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Control;
using WheelLeg.Core;
using WheelLeg.Env;
using WheelLeg.Logging;
using WheelLeg.Robot;

namespace WheelLeg.Tests
{
    public class LayoutTests
    {
        [Test]
        public void ObservationSizeIsSixtyAndMatchesTheNamedLayout()
        {
            // BehaviorParameters Space Size is set from RobotLayout.ObservationSize, and a
            // mismatch between the two stops training from starting at all (SPEC 10-A item 3).
            Assert.AreEqual(60, RobotLayout.ObservationSize);
            Assert.AreEqual(RobotLayout.ObservationSize, ObservationLayout.End);
            Assert.AreEqual(ObservationLayout.End,
                ObservationLayout.PreviousAction + RobotLayout.ActionSize);
        }

        [Test]
        public void ActionSizeIsSixteenSplitTwelveLegsFourWheels()
        {
            Assert.AreEqual(16, RobotLayout.ActionSize);
            Assert.AreEqual(12, RobotLayout.WheelActionOffset);
            Assert.AreEqual(RobotLayout.ActionSize,
                RobotLayout.WheelActionOffset + RobotLayout.WheelCount);
        }

        [Test]
        public void LegJointIndexingIsHipThighCalfPerLeg()
        {
            Assert.AreEqual(0, RobotLayout.LegJoint(0, RobotLayout.HipOffset));
            Assert.AreEqual(5, RobotLayout.LegJoint(1, RobotLayout.CalfOffset));
            Assert.AreEqual(11, RobotLayout.LegJoint(3, RobotLayout.CalfOffset));
        }
    }

    public class FrameSchemaTests
    {
        [Test]
        public void ColumnNamesAreUniqueAndTypedConsistently()
        {
            Assert.AreEqual(FrameSchema.Names.Length, FrameSchema.Types.Length);
            HashSet<string> seen = new HashSet<string>();
            foreach (string name in FrameSchema.Names)
                Assert.IsTrue(seen.Add(name), "duplicate column name: " + name);
        }

        [Test]
        public void MandatoryProvenanceColumnsExist()
        {
            // Without these three a finished run cannot be attributed to a condition.
            CollectionAssert.Contains(FrameSchema.Names, "terrain_seed");
            CollectionAssert.Contains(FrameSchema.Names, "controller_type");
            CollectionAssert.Contains(FrameSchema.Names, "seed_band");
        }

        [Test]
        public void ObservationAndActionBlocksAreFullyPresent()
        {
            for (int i = 0; i < RobotLayout.ObservationSize; i++)
                Assert.AreEqual("obs_" + i, FrameSchema.Names[FrameSchema.Obs + i]);
            for (int i = 0; i < RobotLayout.ActionSize; i++)
                Assert.AreEqual("action_" + i, FrameSchema.Names[FrameSchema.Action + i]);
        }

        [Test]
        public void BatchRoundTripsValuesByColumn()
        {
            FrameBatch batch = new FrameBatch(4);
            batch.SetDouble(FrameSchema.Roll, 0.25);
            batch.SetLong(FrameSchema.Step, 7);
            batch.SetString(FrameSchema.FailReason, "tilted");
            batch.CommitRow();

            Assert.AreEqual(1, batch.Count);
            Assert.AreEqual(0.25, batch.DoubleColumn(FrameSchema.Roll)[0], 1e-12);
            Assert.AreEqual(7, batch.LongColumn(FrameSchema.Step)[0]);
            Assert.AreEqual("tilted", batch.StringColumn(FrameSchema.FailReason)[0]);
        }
    }

    public class DeterministicRandomTests
    {
        [Test]
        public void SameSeedProducesSameSequence()
        {
            DeterministicRandom a = new DeterministicRandom(1234);
            DeterministicRandom b = new DeterministicRandom(1234);
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(a.NextFloat(), b.NextFloat());
                Assert.AreEqual(a.NextGaussian(), b.NextGaussian());
            }
        }

        [Test]
        public void DerivedSeedsAreStableAndDistinctPerArea()
        {
            int first = DeterministicRandom.DeriveSeed(1000, "area", 0);
            Assert.AreEqual(first, DeterministicRandom.DeriveSeed(1000, "area", 0));
            Assert.AreNotEqual(first, DeterministicRandom.DeriveSeed(1000, "area", 1));
            Assert.AreNotEqual(first, DeterministicRandom.DeriveSeed(1001, "area", 0));
            Assert.AreNotEqual(first, DeterministicRandom.DeriveSeed(1000, "terrain-seed", 0));
        }

        [Test]
        public void DerivedSeedsAreNonNegativeAcrossManyAreas()
        {
            for (int i = 0; i < 256; i++)
                Assert.GreaterOrEqual(DeterministicRandom.DeriveSeed(-5, "area", i), 0);
        }
    }

    public class TerrainDeterminismTests
    {
        static TerrainConfig MakeConfig()
        {
            const string text =
                "size: [16.0, 16.0]\n" +
                "resolution: 32\n" +
                "difficulty: [0.0, 1.0]\n" +
                "slope_deg: [0.0, 12.0]\n" +
                "roughness_amplitude: [0.0, 0.06]\n" +
                "roughness_wavelength: [0.6, 3.0]\n" +
                "step_height: [0.0, 0.05]\n" +
                "step_spacing: 2.0\n" +
                "friction: [0.6, 1.0]\n";
            return TerrainConfig.Read(ConfigNode.Root(YamlParser.Parse(text, "terrain-test")));
        }

        [Test]
        public void SameSeedGeneratesIdenticalVertices()
        {
            // SPEC section 6: identical seed must reproduce identical geometry, otherwise no
            // two runs are comparable.
            TerrainConfig config = MakeConfig();
            Vector3[] a = TerrainGenerator.GenerateVertices(config, 0.7f, 4242);
            Vector3[] b = TerrainGenerator.GenerateVertices(config, 0.7f, 4242);

            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].x, b[i].x, 0f, "vertex " + i + " x");
                Assert.AreEqual(a[i].y, b[i].y, 0f, "vertex " + i + " y");
                Assert.AreEqual(a[i].z, b[i].z, 0f, "vertex " + i + " z");
            }
        }

        [Test]
        public void DifferentSeedGeneratesDifferentTerrain()
        {
            TerrainConfig config = MakeConfig();
            Vector3[] a = TerrainGenerator.GenerateVertices(config, 1.0f, 1);
            Vector3[] b = TerrainGenerator.GenerateVertices(config, 1.0f, 2);

            bool anyDifference = false;
            for (int i = 0; i < a.Length && !anyDifference; i++)
                anyDifference = !Mathf.Approximately(a[i].y, b[i].y);
            Assert.IsTrue(anyDifference, "two different seeds produced the same height field");
        }

        [Test]
        public void ZeroDifficultyIsExactlyFlatWithNoSeparateCodePath()
        {
            TerrainConfig config = MakeConfig();
            Vector3[] flat = TerrainGenerator.GenerateVertices(config, 0f, 99);
            for (int i = 0; i < flat.Length; i++)
                Assert.AreEqual(0f, flat[i].y, 1e-6f, "vertex " + i + " is not flat at difficulty 0");
        }
    }
}
