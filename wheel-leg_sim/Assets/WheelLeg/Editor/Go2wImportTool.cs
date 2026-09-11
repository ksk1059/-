using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Robotics.UrdfImporter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WheelLeg.Config;

namespace WheelLeg.EditorTools
{
    /// <summary>
    /// Result of one import check. Kept as three plain lists so the menu item decides how to
    /// surface each line; nothing here logs by itself, which lets tests call Validate directly.
    /// </summary>
    public class ValidationReport
    {
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Info = new List<string>();

        /// <summary>First object an error was attributed to, for Selection on report.</summary>
        public GameObject FirstProblem;

        /// <summary>Set when at least one wheel is not free spinning, which "Fix Wheel Joints" repairs.</summary>
        public bool WheelTwistLocked;

        public bool Ok { get { return Errors.Count == 0; } }
    }

    public static class Go2wImportTool
    {
        // Asset location of the downloaded URDF, relative to Assets/. Not a tuning value:
        // the importer only ever runs in the editor, never in the server build.
        const string RobotFolder = "Robots";
        const string RobotSubFolder = "go2w";
        const string UrdfFileName = "go2w_description.urdf";

        [MenuItem("WheelLeg/Import Go2-W URDF")]
        public static void ImportMenu()
        {
            try
            {
                string urdfPath = UrdfAbsolutePath();
                if (!File.Exists(urdfPath))
                {
                    Debug.LogError("Go2-W URDF not found at " + urdfPath);
                    return;
                }

                ImportSettings settings = new ImportSettings();
                settings.chosenAxis = ImportSettings.axisType.yAxis;
                // Every collision in this URDF is already a primitive, so there is nothing for a
                // convex decomposer to decompose. vHACD would still run over the visual meshes and
                // take minutes per link.
                settings.convexMethod = ImportSettings.convexDecomposer.unity;

                // The importer ends with GameObjectUtility.SetParentAndAlign(robot, Selection.activeObject),
                // so whatever happens to be selected becomes the robot's parent and its transform then
                // offsets every spawn and reset. The importer reselects the robot itself afterwards.
                Selection.activeObject = null;

                GameObject imported = null;
                using (IEnumerator<GameObject> import = UrdfRobotExtensions.Create(urdfPath, settings))
                {
                    while (import.MoveNext())
                    {
                        if (import.Current != null) imported = import.Current;
                    }
                }

                if (imported == null)
                {
                    Debug.LogError("Go2-W import produced no GameObject; see the errors logged above.");
                    return;
                }

                Selection.activeGameObject = imported;
                EditorSceneManager.MarkSceneDirty(imported.scene);
                Debug.Log("Imported '" + imported.name + "' from " + urdfPath);

                // The importer drops the robot into whatever scene is open. Saving it into the
                // simulation scene leaves an unconfigured robot collapsed at the world origin,
                // which is where area 0 spawns: the real robot trips over it, and because it is
                // a separate articulation every contact with it reads as ground contact and
                // ends the episode on base_contact. SimBootstrap refuses to start in that
                // state, but saying so here is what stops it happening.
                if (imported.scene.path == SceneBuilder.ScenePath)
                {
                    Debug.LogWarning("[WheelLeg] the robot was imported into the simulation scene "
                        + SceneBuilder.ScenePath + ". Run WheelLeg/Build Robot Prefab, then delete "
                        + "'" + imported.name + "' from this scene before saving it. Leaving it "
                        + "here puts a second, unconfigured robot at the world origin and "
                        + "SimBootstrap will refuse to start.");
                }

                LogReport(Validate(imported), imported);
            }
            catch (Exception e)
            {
                Debug.LogError("Go2-W import failed: " + e);
            }
        }

        [MenuItem("WheelLeg/Validate Imported Go2-W")]
        public static void ValidateMenu()
        {
            try
            {
                GameObject root = ResolveRobotRoot();
                if (root == null)
                {
                    Debug.LogError(NoRootMessage());
                    return;
                }

                ValidationReport report = Validate(root);
                LogReport(report, root);

                if (report.WheelTwistLocked && EditorUtility.DisplayDialog(
                        "Go2-W wheel joints are limited",
                        "One or more wheels imported with a limited twist axis and will stop turning "
                        + "after a fraction of a revolution. Set them to free motion now?",
                        "Fix Wheel Joints", "Leave As Is"))
                {
                    FixWheelJointsMenu();
                }
            }
            catch (Exception e)
            {
                Debug.LogError("Go2-W validation failed: " + e);
            }
        }

        [MenuItem("WheelLeg/Fix Wheel Joints")]
        public static void FixWheelJointsMenu()
        {
            try
            {
                GameObject root = ResolveRobotRoot();
                if (root == null)
                {
                    Debug.LogError(NoRootMessage());
                    return;
                }

                string error;
                SimConfig config = LoadConfig(out error);
                if (config == null)
                {
                    Debug.LogError("Cannot resolve wheel link names: " + error);
                    return;
                }

                string[] wheelNames = config.Robot.WheelLinkNames();
                int repaired = 0;
                for (int i = 0; i < wheelNames.Length; i++)
                {
                    ArticulationBody body = FindBody(root.transform, wheelNames[i]);
                    if (body == null)
                    {
                        Debug.LogError("Wheel link '" + wheelNames[i] + "' has no ArticulationBody under "
                            + root.name);
                        continue;
                    }
                    if (body.twistLock == ArticulationDofLock.FreeMotion) continue;

                    Undo.RecordObject(body, "Fix Wheel Joints");
                    body.twistLock = ArticulationDofLock.FreeMotion;
                    EditorUtility.SetDirty(body);
                    repaired++;
                }

                if (repaired > 0) EditorSceneManager.MarkSceneDirty(root.scene);
                Debug.Log("Fix Wheel Joints: set free motion on " + repaired + " of "
                    + wheelNames.Length + " wheels.");
            }
            catch (Exception e)
            {
                Debug.LogError("Fix Wheel Joints failed: " + e);
            }
        }

        public static ValidationReport Validate(GameObject robotRoot)
        {
            ValidationReport report = new ValidationReport();
            if (robotRoot == null)
            {
                report.Errors.Add("No robot root to validate.");
                return report;
            }

            // Link names come from the same config the runtime reads, so a rename in env.yaml
            // can never leave the editor check agreeing with a runtime that no longer matches.
            string error;
            SimConfig config = LoadConfig(out error);
            if (config == null)
            {
                report.Errors.Add("Config load failed, cannot resolve link names: " + error);
                return report;
            }

            RobotConfig robot = config.Robot;
            Transform root = robotRoot.transform;

            Transform baseLink = FindDescendant(root, robot.BaseLinkName);
            if (baseLink == null)
                report.Errors.Add("Base link '" + robot.BaseLinkName + "' not found under " + robotRoot.name);
            else
                report.Info.Add("Base link '" + robot.BaseLinkName + "' found.");

            ValidateLegJoints(root, robot, report);
            ValidateWheels(root, robot, report);
            ReportMassAndColliders(robotRoot, report);
            return report;
        }

        static void ValidateLegJoints(Transform root, RobotConfig robot, ValidationReport report)
        {
            string[] names = robot.LegJointLinkNames();
            for (int i = 0; i < names.Length; i++)
            {
                ArticulationBody body = FindBody(root, names[i]);
                if (body == null)
                {
                    report.Errors.Add("Leg joint " + i + " link '" + names[i] + "' has no ArticulationBody.");
                    continue;
                }

                ArticulationDrive drive = body.xDrive;
                if (body.twistLock != ArticulationDofLock.LimitedMotion)
                {
                    report.Errors.Add("Leg joint " + i + " '" + names[i] + "' twistLock is "
                        + body.twistLock + ", expected LimitedMotion; an unlimited leg joint cannot be "
                        + "normalised into the [-1, 1] action range.");
                    NoteOffender(report, body.gameObject);
                }
                else if (drive.upperLimit <= drive.lowerLimit)
                {
                    report.Errors.Add(string.Format(CultureInfo.InvariantCulture,
                        "Leg joint {0} '{1}' has an empty xDrive range: {2:F3}..{3:F3} deg",
                        i, names[i], drive.lowerLimit, drive.upperLimit));
                    NoteOffender(report, body.gameObject);
                }
                else
                {
                    report.Info.Add(string.Format(CultureInfo.InvariantCulture,
                        "Leg joint {0} '{1}': {2:F2}..{3:F2} deg", i, names[i], drive.lowerLimit, drive.upperLimit));
                }
            }
        }

        static void ValidateWheels(Transform root, RobotConfig robot, ValidationReport report)
        {
            string[] names = robot.WheelLinkNames();
            for (int i = 0; i < names.Length; i++)
            {
                ArticulationBody body = FindBody(root, names[i]);
                if (body == null)
                {
                    report.Errors.Add("Wheel " + i + " link '" + names[i] + "' has no ArticulationBody.");
                    continue;
                }

                if (body.twistLock != ArticulationDofLock.FreeMotion)
                {
                    report.Errors.Add("Wheel " + i + " '" + names[i] + "' twistLock is " + body.twistLock
                        + ", expected FreeMotion; the URDF joint is continuous and a limited wheel stops "
                        + "turning after a fraction of a revolution.");
                    report.WheelTwistLocked = true;
                    NoteOffender(report, body.gameObject);
                }
                else
                {
                    report.Info.Add("Wheel " + i + " '" + names[i] + "': free spinning.");
                }

                // Counting MeshColliders here would always fire: Unity has no cylinder collider, so
                // UrdfGeometryCollision imports a URDF <cylinder> as a convex MeshCollider as well.
                // UrdfCollision.geometryType is what records which geometry the URDF actually asked
                // for, and Mesh is the one that would roll on facets.
                UrdfCollision[] collisions = body.GetComponentsInChildren<UrdfCollision>(true);
                for (int c = 0; c < collisions.Length; c++)
                {
                    if (collisions[c].geometryType != GeometryTypes.Mesh) continue;
                    report.Errors.Add("Wheel " + i + " '" + names[i] + "' has a mesh collision; the wheel "
                        + "collision must be the cylinder primitive or the wheel rolls on facets.");
                    NoteOffender(report, collisions[c].gameObject);
                    break;
                }
            }
        }

        static void ReportMassAndColliders(GameObject robotRoot, ValidationReport report)
        {
            ArticulationBody[] bodies = robotRoot.GetComponentsInChildren<ArticulationBody>(true);
            float totalMass = 0f;
            for (int i = 0; i < bodies.Length; i++) totalMass += bodies[i].mass;

            Collider[] colliders = robotRoot.GetComponentsInChildren<Collider>(true);
            report.Info.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} ArticulationBodies, total mass {1:F3} kg, {2} colliders.",
                bodies.Length, totalMass, colliders.Length));
        }

        static void NoteOffender(ValidationReport report, GameObject offender)
        {
            if (report.FirstProblem == null) report.FirstProblem = offender;
        }

        static void LogReport(ValidationReport report, GameObject robotRoot)
        {
            for (int i = 0; i < report.Info.Count; i++) Debug.Log(report.Info[i], robotRoot);
            for (int i = 0; i < report.Warnings.Count; i++) Debug.LogWarning(report.Warnings[i], robotRoot);
            for (int i = 0; i < report.Errors.Count; i++) Debug.LogError(report.Errors[i], robotRoot);

            if (report.Ok)
            {
                Debug.Log("Go2-W validation passed (" + report.Warnings.Count + " warning(s)).", robotRoot);
                return;
            }

            Debug.LogError("Go2-W validation FAILED with " + report.Errors.Count + " error(s).", robotRoot);
            if (report.FirstProblem != null) Selection.activeGameObject = report.FirstProblem;
        }

        static SimConfig LoadConfig(out string error)
        {
            try
            {
                error = null;
                return ConfigLoader.Load(new CommandLine(new string[0]));
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        static string UrdfAbsolutePath()
        {
            return Path.Combine(Path.Combine(Path.Combine(Application.dataPath, RobotFolder), RobotSubFolder),
                UrdfFileName);
        }

        /// <summary>Root GameObject name the importer gives the robot: the URDF's robot name.</summary>
        static string RobotRootName()
        {
            return Path.GetFileNameWithoutExtension(UrdfFileName);
        }

        /// <summary>
        /// Selection first, otherwise the scene root named after the URDF robot.
        /// This is not the runtime GameObject.Find that SERVER_OPS_1 section 2-1 bans: that ban
        /// exists because 32 training areas share one process and a global lookup would return a
        /// neighbour area's object. An editor menu item runs before any area exists, against the
        /// one scene the user has open, and never ships in the player build.
        /// </summary>
        static GameObject ResolveRobotRoot()
        {
            string rootName = RobotRootName();

            GameObject selected = Selection.activeGameObject;
            if (selected != null)
            {
                // Selecting a link deep in the hierarchy should still validate the whole robot.
                Transform t = selected.transform;
                while (t != null && t.name != rootName) t = t.parent;
                return t != null ? t.gameObject : selected;
            }

            Scene scene = SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == rootName) return roots[i];
            }
            return null;
        }

        static string NoRootMessage()
        {
            return "No robot selected and no root GameObject named '" + RobotRootName()
                + "' in the active scene. Run WheelLeg/Import Go2-W URDF first, or select the robot.";
        }

        static ArticulationBody FindBody(Transform root, string linkName)
        {
            Transform link = FindDescendant(root, linkName);
            return link == null ? null : link.GetComponent<ArticulationBody>();
        }

        static Transform FindDescendant(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
