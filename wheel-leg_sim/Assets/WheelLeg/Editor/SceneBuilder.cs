using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using WheelLeg.Agent;
using WheelLeg.Boot;
using WheelLeg.Robot;

namespace WheelLeg.EditorTools
{
    /// <summary>
    /// Builds the robot prefab and the single simulation scene.
    ///
    /// There is exactly one scene. Training areas are created at runtime from
    /// run.areas, so scaling from one area to thirty-two is a config change and never a
    /// scene edit; and an episode reset never reloads a scene (SERVER_OPS_1.md section 4-1).
    /// </summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/WheelLegSim.unity";
        public const string PrefabPath = "Assets/Resources/Go2W.prefab";
        public const string RobotRootName = "go2w_description";

        [MenuItem("WheelLeg/Build Robot Prefab", false, 20)]
        public static void BuildRobotPrefabMenu()
        {
            GameObject robot = FindRobotRoot();
            if (robot == null)
            {
                Debug.LogError("[WheelLeg] no imported robot in the scene. "
                    + "Run WheelLeg/Import Go2-W URDF first, or select the robot root.");
                return;
            }

            GameObject prefab = BuildRobotPrefab(robot);
            if (prefab != null)
            {
                Selection.activeObject = prefab;
                Debug.Log("[WheelLeg] robot prefab written to " + PrefabPath);
            }
        }

        public static GameObject BuildRobotPrefab(GameObject robot)
        {
            StripImporterControlComponents(robot);
            FillEmptyMaterialSlots(robot);
            // Before saving, not after: an empty mesh reference baked into the prefab is what
            // made all four calves invisible.
            RelinkMissingMeshes(robot);

            AddComponentIfMissing<RobotDriver>(robot);
            AddComponentIfMissing<WheelLegAgent>(robot);
            AddComponentIfMissing<DecisionRequester>(robot);

            BehaviorParameters behavior = AddComponentIfMissing<BehaviorParameters>(robot);
            behavior.BehaviorName = "WheelLegGo2W";
            behavior.BrainParameters.VectorObservationSize = RobotLayout.ObservationSize;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(RobotLayout.ActionSize);

            // The bootstrap loads this by name when no prefab reference is wired, which is
            // what a headless player does.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(
                Directory.GetCurrentDirectory(), PrefabPath)));
            AssetDatabase.Refresh();

            return PrefabUtility.SaveAsPrefabAsset(robot, PrefabPath);
        }

        [MenuItem("WheelLeg/Build Simulation Scene", false, 21)]
        public static void BuildSceneMenu()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[WheelLeg] " + PrefabPath + " not found. "
                    + "Run WheelLeg/Import Go2-W URDF then WheelLeg/Build Robot Prefab.");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject bootstrapObject = new GameObject("SimBootstrap");
            SimBootstrap bootstrap = bootstrapObject.AddComponent<SimBootstrap>();
            SerializedObject serialized = new SerializedObject(bootstrap);
            serialized.FindProperty("_robotPrefab").objectReferenceValue = prefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Editor-only conveniences. A -nographics server build never instantiates either,
            // but leaving them in the scene costs nothing and makes the local check viewable.
            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.AddComponent<Camera>();
            cameraObject.transform.position = new Vector3(0f, 3f, -6f);
            cameraObject.transform.rotation = Quaternion.Euler(20f, 0f, 0f);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(
                Directory.GetCurrentDirectory(), ScenePath)));
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            AddSceneToBuildSettings(ScenePath);
            Debug.Log("[WheelLeg] scene written to " + ScenePath + " and set as the only build scene");
        }

        static void AddSceneToBuildSettings(string path)
        {
            // Exactly one scene ships. Anything else invites "which scene did the server run".
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
        }

        internal static GameObject FindRobotRoot()
        {
            if (Selection.activeGameObject != null) return Selection.activeGameObject.transform.root.gameObject;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == RobotRootName) return root;
            }
            return null;
        }

        /// <summary>
        /// Removes the URDF Importer's manual keyboard controller and the JointControl
        /// components it manages. Controller.UpdateControlType writes its own inspector
        /// stiffness and damping into every joint's xDrive on each Update, which silently
        /// overwrote the configured gains with zero and left the robot a ragdoll for the whole
        /// run. It is a demo controller; an RL environment must be the only thing driving the
        /// joints.
        /// </summary>
        public static int StripImporterControlComponents(GameObject robot)
        {
            int removed = 0;
            MonoBehaviour[] behaviours = robot.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null) continue;
                string name = behaviour.GetType().FullName;
                if (name == null || !name.StartsWith("Unity.Robotics.UrdfImporter.Control.")) continue;
                Object.DestroyImmediate(behaviour, true);
                removed++;
            }
            if (removed > 0)
                Debug.Log("[WheelLeg] removed " + removed + " URDF Importer control component(s) "
                    + "that would overwrite the configured drive gains at runtime");
            return removed;
        }

        /// <summary>
        /// Recalculates the bounding volume of any mesh the prefab draws that has vertices but
        /// an empty bounds.
        ///
        /// The URDF Importer builds meshes for STL visuals by filling in vertices and triangles
        /// without ever calling Mesh.RecalculateBounds, so the mesh keeps the default zero
        /// bounds. Renderer culling is a frustum test against those bounds, so the mesh is
        /// discarded before it is ever drawn: on Go2-W all four calves vanish and the robot
        /// looks like it is standing on its thighs. The collider is a separate URDF primitive
        /// and is unaffected, so the physics was always correct and only the picture was wrong.
        /// The .dae visuals go through Unity's model importer and do not have this problem.
        /// </summary>
        [MenuItem("WheelLeg/Repair Mesh Bounds", false, 24)]
        public static void RepairMeshBoundsMenu()
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (contents == null)
            {
                Debug.LogError("[WheelLeg] no prefab at " + PrefabPath);
                return;
            }

            try
            {
                int relinked = RelinkMissingMeshes(contents);
                if (relinked > 0) PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Reconnects every MeshFilter whose mesh reference is empty to the mesh asset of the
        /// same name, and returns how many were reconnected.
        ///
        /// The URDF Importer writes an STL visual out as one or more Mesh assets and then builds
        /// the hierarchy that points at them. On Go2-W the calf visuals come from calf.stl and
        /// calf_mirror.stl and are large enough to be split at the 65535 vertex limit, into
        /// calf_0/1/2 and calf_mirror_0/1. If Build Robot Prefab runs before those assets have
        /// finished importing, the MeshFilters are still empty and the prefab is saved with the
        /// gap baked in. The assets themselves are fine and the on-disk references inside
        /// calf.prefab are correct; only the saved robot prefab lost them.
        ///
        /// The effect is that all four calves draw nothing, so the robot looks like it is
        /// standing on its thighs. The calf collider is a separate URDF cylinder primitive and
        /// is unaffected, so this never touched the physics: only the picture was wrong.
        /// </summary>
        static int RelinkMissingMeshes(GameObject root)
        {
            System.Text.StringBuilder report = new System.Text.StringBuilder();
            int relinked = 0;
            int missing = 0;

            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null) continue;
                missing++;

                string meshName = filter.gameObject.name;
                Mesh asset = FindMeshAsset(meshName);
                if (asset == null)
                {
                    report.Append("\n  ").Append(meshName.PadRight(20))
                        .Append("NO mesh asset of this name exists; the visual cannot be restored");
                    continue;
                }
                filter.sharedMesh = asset;
                relinked++;
                report.Append("\n  ").Append(meshName.PadRight(20))
                    .Append("relinked, ").Append(asset.vertexCount).Append(" vertices, bounds ")
                    .Append(asset.bounds.size.ToString("F3"));
            }

            if (missing == 0)
            {
                Debug.Log("[WheelLeg] mesh references: all MeshFilters already have a mesh");
                return 0;
            }
            Debug.Log("[WheelLeg] mesh references: " + relinked + " of " + missing
                + " empty MeshFilter(s) reconnected by name" + report);
            return relinked;
        }

        /// <summary>The Mesh asset whose file name is exactly <paramref name="meshName"/>.</summary>
        static Mesh FindMeshAsset(string meshName)
        {
            string[] guids = AssetDatabase.FindAssets(meshName + " t:Mesh");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Path.GetFileNameWithoutExtension(path) != meshName) continue;
                Mesh loaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (loaded != null) return loaded;
            }
            return null;
        }

        /// <summary>
        /// Reports, per URDF link, what visual meshes and colliders the built prefab actually
        /// carries and how big they are. A link that renders nothing, or renders something the
        /// wrong size, is invisible in the log and in the physics: only looking at the numbers
        /// catches it.
        /// </summary>
        [MenuItem("WheelLeg/Audit Robot Prefab Geometry", false, 23)]
        public static void AuditPrefabGeometryMenu()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[WheelLeg] no prefab at " + PrefabPath);
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                System.Text.StringBuilder report = new System.Text.StringBuilder();
                ArticulationBody[] bodies = instance.GetComponentsInChildren<ArticulationBody>(true);
                int rendererless = 0;
                foreach (ArticulationBody body in bodies)
                {
                    // Only this link's own geometry: a child link's meshes belong to that link.
                    int meshes = 0;
                    Bounds visual = new Bounds();
                    foreach (Renderer renderer in body.GetComponentsInChildren<Renderer>(true))
                    {
                        if (OwningBody(renderer.transform, instance.transform) != body) continue;
                        if (meshes == 0) visual = renderer.bounds; else visual.Encapsulate(renderer.bounds);
                        meshes++;
                    }

                    int colliders = 0;
                    Bounds solid = new Bounds();
                    foreach (Collider collider in body.GetComponentsInChildren<Collider>(true))
                    {
                        if (OwningBody(collider.transform, instance.transform) != body) continue;
                        if (colliders == 0) solid = collider.bounds; else solid.Encapsulate(collider.bounds);
                        colliders++;
                    }

                    if (meshes == 0) rendererless++;
                    report.Append("\n  ").Append(body.name.PadRight(18))
                        .Append("renderers=").Append(meshes)
                        .Append(" visualSize=").Append(meshes > 0 ? visual.size.ToString("F3") : "-")
                        .Append("  colliders=").Append(colliders)
                        .Append(" colliderSize=").Append(colliders > 0 ? solid.size.ToString("F3") : "-")
                        .Append(meshes == 0 ? "   <-- DRAWS NOTHING" : "");
                }
                Debug.Log("[WheelLeg] prefab geometry audit: " + bodies.Length + " links, "
                    + rendererless + " with no renderer of their own" + report);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// The ArticulationBody a collider or renderer physically belongs to: the nearest one at
        /// or above it, which is how PhysX and Unity's collision messages attribute it too.
        /// </summary>
        static ArticulationBody OwningBody(Transform node, Transform stopAt)
        {
            for (Transform t = node; t != null; t = t.parent)
            {
                ArticulationBody body = t.GetComponent<ArticulationBody>();
                if (body != null) return body;
                if (t == stopAt) break;
            }
            return null;
        }

        /// <summary>
        /// Repairs the magenta submeshes on the prefab that is already built, so fixing them
        /// does not require re-importing the URDF and rebuilding the prefab from scratch.
        /// </summary>
        [MenuItem("WheelLeg/Fix Prefab Materials", false, 22)]
        public static void FixPrefabMaterialsMenu()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[WheelLeg] no prefab at " + PrefabPath
                    + "; run WheelLeg/Build Robot Prefab first");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                FillEmptyMaterialSlots(instance);
                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                Debug.Log("[WheelLeg] prefab materials repaired at " + PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Replaces every renderer material this project cannot render, which Unity draws as
        /// solid magenta, with the active pipeline's default material.
        ///
        /// Go2-W's Collada meshes carry their materials embedded in the model asset
        /// (materialLocation 1), and the importer gives them a built-in render pipeline shader.
        /// This project is URP, which cannot compile those shaders, so the affected submeshes
        /// draw magenta - most visibly the four thighs, whose thigh.dae declares two materials.
        ///
        /// Cosmetic only: a material has no effect on physics, so this is not what makes the
        /// robot jump. It is worth fixing anyway because a magenta robot hides real visual
        /// problems, which are the only way SPEC 10-A items 4, 5 and 6 can be judged.
        ///
        /// Every renderer is logged either way, so a run of this menu item is also the
        /// measurement of which materials were actually broken.
        /// </summary>
        static void FillEmptyMaterialSlots(GameObject robot)
        {
            // The active render pipeline's own default, not the builtin Default-Material: the
            // builtin material uses the Standard shader, which is exactly what URP cannot
            // render, so it would draw the same magenta this is meant to remove.
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Material fallback = pipeline != null
                ? pipeline.defaultMaterial
                : AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            if (fallback == null)
            {
                Debug.LogError("[WheelLeg] no default material available from the active render "
                    + "pipeline; cannot repair materials");
                return;
            }

            int replaced = 0;
            int slots = 0;
            System.Text.StringBuilder report = new System.Text.StringBuilder();
            Renderer[] renderers = robot.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    slots++;
                    Material material = materials[i];
                    string shader = material == null
                        ? "<null material>"
                        : (material.shader == null ? "<null shader>" : material.shader.name);
                    bool broken = IsUnrenderable(material);
                    report.Append("\n  ").Append(renderer.gameObject.name).Append('[').Append(i)
                        .Append("] material=")
                        .Append(material == null ? "<null>" : material.name)
                        .Append(" shader=").Append(shader)
                        .Append(broken ? "   REPLACED" : "");
                    if (!broken) continue;
                    materials[i] = fallback;
                    changed = true;
                    replaced++;
                }
                if (changed) renderer.sharedMaterials = materials;
            }

            Debug.Log("[WheelLeg] material audit: " + replaced + " of " + slots
                + " renderer slots replaced with " + fallback.name + report);
        }

        /// <summary>
        /// True for a material this project's render pipeline will draw as magenta: no material
        /// at all, a shader that failed to compile, or a built-in pipeline shader under URP.
        /// </summary>
        static bool IsUnrenderable(Material material)
        {
            if (material == null || material.shader == null) return true;
            if (GraphicsSettings.currentRenderPipeline == null) return false;

            string name = material.shader.name;
            return name == "Hidden/InternalErrorShader"
                || name == "Standard"
                || name == "Standard (Specular setup)"
                || name == "Autodesk Interactive"
                || name.StartsWith("Legacy Shaders/");
        }

        static T AddComponentIfMissing<T>(GameObject target) where T : Component
        {
            T existing = target.GetComponent<T>();
            return existing != null ? existing : target.AddComponent<T>();
        }
    }
}
