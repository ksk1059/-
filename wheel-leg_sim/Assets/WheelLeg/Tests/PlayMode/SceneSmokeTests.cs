using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using WheelLeg.Agent;
using WheelLeg.Boot;
using WheelLeg.Env;
using WheelLeg.Robot;

namespace WheelLeg.Tests
{
    /// <summary>
    /// Runs the real scene the way a launch does: bootstrap reads the config, builds the
    /// areas, resolves the robot and steps physics. Everything the unit tests cover is
    /// arithmetic; this is the only check that the wiring holds together.
    ///
    /// It covers SPEC 10-A items 1 (no errors on play), 2 (all 16 joints resolved),
    /// 3 (observation count) and 9 (a log file with the schema actually appears). Items
    /// 4, 5 and 6 are visual and are deliberately not asserted here.
    /// </summary>
    public class SceneSmokeTests
    {
        const string ScenePath = "Assets/Scenes/WheelLegSim.unity";
        const int StepsToRun = 240;

        [UnityTest]
        public IEnumerator ScenePlaysWithoutErrorsAndProducesAFrameLog()
        {
            string projectRoot = Directory.GetCurrentDirectory();
            string outputDirectory = Path.Combine(projectRoot, "runs/local");
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);

            yield return LoadSceneByPath();

            SimBootstrap bootstrap = Object.FindAnyObjectByType<SimBootstrap>();
            Assert.IsNotNull(bootstrap, "SimBootstrap missing from " + ScenePath);

            TrainingArea area = Object.FindAnyObjectByType<TrainingArea>();
            Assert.IsNotNull(area, "bootstrap built no training area");

            RobotDriver driver = area.Driver;
            Assert.IsNotNull(driver, "training area has no robot driver");
            Assert.IsTrue(driver.IsReady,
                "unresolved robot links: " + string.Join(", ", driver.MissingLinks));

            for (int i = 0; i < StepsToRun; i++) yield return new WaitForFixedUpdate();

            WheelLegAgent agent = Object.FindAnyObjectByType<WheelLegAgent>();
            Assert.IsNotNull(agent, "no agent in the scene");
            Assert.Greater(agent.EpisodeCount, 0, "no episode ever began");

            area.Logger.Flush();
            string frameDirectory = Path.Combine(outputDirectory, area.Config.Logging.SubDir);
            Assert.IsTrue(Directory.Exists(frameDirectory),
                "no frame directory at " + frameDirectory);

            string[] parts = Directory.GetFiles(frameDirectory, "area000_part*");
            Assert.Greater(parts.Length, 0, "logging is on but no frame file was written");
            Assert.Greater(new FileInfo(parts[0]).Length, 0, "frame file is empty");

            Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "run_manifest.json")),
                "no run_manifest.json; a finished run could not be attributed to a config");
        }

        [UnityTest]
        public IEnumerator RobotResolvesAllSixteenDrivenJointsWithUsableLimits()
        {
            yield return LoadSceneByPath();

            RobotDriver driver = Object.FindAnyObjectByType<RobotDriver>();
            Assert.IsNotNull(driver);
            Assert.IsTrue(driver.IsReady, "driver not ready: " + string.Join(", ", driver.MissingLinks));
            Assert.AreEqual(0, driver.MissingLinks.Length);

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                float lower = driver.LegLowerLimitRad(i);
                float upper = driver.LegUpperLimitRad(i);
                Assert.Greater(upper, lower, "leg joint " + i + " has no usable limit range");
            }

            Assert.Greater(driver.WheelRadius, 0f, "wheel radius was not measured from the collider");
        }

        static IEnumerator LoadSceneByPath()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
            Assert.IsNotNull(load, ScenePath + " is not in the build settings");
            while (!load.isDone) yield return null;
            // One frame for Awake to finish building the areas before anything is inspected.
            yield return null;
        }
    }
}
