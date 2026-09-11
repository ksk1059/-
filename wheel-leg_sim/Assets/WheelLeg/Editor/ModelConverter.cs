using System.IO;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

namespace WheelLeg.EditorTools
{
    /// <summary>
    /// Converts a trainer's .onnx into the .sentis file the player loads at runtime.
    ///
    /// The ONNX parser is editor-only, so a player cannot read an .onnx from disk. Rather than
    /// build one player per trained policy, this runs once per policy in the editor and the
    /// resulting file is passed to any build with --model. That is what keeps SERVER_OPS_1.md's
    /// one-build rule intact for evaluation.
    ///
    /// Drop the trainer's .onnx anywhere under Assets/ so Unity imports it as a ModelAsset,
    /// select it, then run this. The .sentis lands next to the .onnx.
    /// </summary>
    public static class ModelConverter
    {
        const string MenuPath = "WheelLeg/Convert Model To Sentis";
        const string FolderMenuPath = "WheelLeg/Convert All Models In Assets-Models";

        /// <summary>
        /// Where the folder-based conversion looks. A fixed location makes the conversion
        /// scriptable: the selection-based menu item above needs a human to click an asset,
        /// which a batch or an automated check cannot do.
        /// </summary>
        public const string ModelFolder = "Assets/Models";

        [MenuItem(FolderMenuPath, false, 41)]
        public static void ConvertFolderMenu()
        {
            if (!AssetDatabase.IsValidFolder(ModelFolder))
            {
                Debug.LogError("[WheelLeg] " + ModelFolder + " does not exist. Copy the trainer's "
                    + "results/<run-id>/*.onnx into it, then run " + FolderMenuPath + ".");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:" + typeof(ModelAsset).Name,
                new[] { ModelFolder });
            if (guids.Length == 0)
            {
                Debug.LogError("[WheelLeg] no imported model assets under " + ModelFolder);
                return;
            }

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                ModelAsset asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(path);
                if (asset != null) Convert(asset);
            }
        }

        [MenuItem(MenuPath, false, 40)]
        public static void ConvertSelectedMenu()
        {
            Object[] selected = Selection.GetFiltered(typeof(ModelAsset), SelectionMode.Assets);
            if (selected.Length == 0)
            {
                Debug.LogError("[WheelLeg] select one or more imported model assets (.onnx) first. "
                    + "Copy the trainer's results/<run-id>/*.onnx under Assets/ so Unity imports "
                    + "it, select it in the Project window, then run " + MenuPath + ".");
                return;
            }

            for (int i = 0; i < selected.Length; i++) Convert((ModelAsset)selected[i]);
        }

        [MenuItem(MenuPath, true)]
        static bool ConvertSelectedValidate()
        {
            return Selection.GetFiltered(typeof(ModelAsset), SelectionMode.Assets).Length > 0;
        }

        static void Convert(ModelAsset asset)
        {
            string sourcePath = AssetDatabase.GetAssetPath(asset);
            string outputPath = Path.ChangeExtension(sourcePath, ".sentis");

            Model model = ModelLoader.Load(asset);
            if (model == null)
            {
                Debug.LogError("[WheelLeg] could not load " + sourcePath);
                return;
            }

            ModelWriter.Save(outputPath, model);

            // The tensor names are what PolicyController binds to at runtime, and a mismatch
            // there is only discoverable from the model itself, so they are reported here where
            // the conversion happens rather than left to a failed run to reveal.
            string inputs = Describe(model, true);
            string outputs = Describe(model, false);
            Debug.Log("[WheelLeg] wrote " + outputPath
                + "\n  inputs:  " + inputs
                + "\n  outputs: " + outputs
                + "\n  pass this file to a run with --model " + outputPath);

            AssetDatabase.Refresh();
        }

        static string Describe(Model model, bool wantInputs)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            int count = wantInputs ? model.inputs.Count : model.outputs.Count;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(wantInputs ? model.inputs[i].name : model.outputs[i].name);
            }
            return count == 0 ? "<none>" : text.ToString();
        }
    }
}
