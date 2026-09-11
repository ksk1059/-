using System;
using System.Collections.Generic;
using System.IO;
using Unity.InferenceEngine;
using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Core;
using WheelLeg.Robot;

namespace WheelLeg.Control
{
    /// <summary>
    /// Runs a trained policy from a serialised model file, as an <see cref="IController"/> like
    /// the two baselines.
    ///
    /// Evaluation deliberately does not go through ML-Agents' own inference path.
    /// BehaviorParameters.Model needs a Unity.InferenceEngine.ModelAsset, which the editor's
    /// ONNX importer produces and which cannot be built from a loose file at runtime, so that
    /// path would force one player build per trained policy. Loading the model here instead
    /// keeps SERVER_OPS_1.md's one-build rule: the policy is a path on the command line exactly
    /// like every other run parameter, and a five-seed sweep is five runs of one binary.
    ///
    /// It also puts the policy behind the same interface as the baselines, which is what SPEC
    /// section 8 asks for: all three see the identical 60-value observation and write the
    /// identical 16-value action, so a comparison cannot be confounded by the plumbing.
    ///
    /// run.model must be a .sentis file. Convert a trainer's .onnx once with
    /// WheelLeg/Convert Model To Sentis; the ONNX parser is editor-only.
    /// </summary>
    public sealed class PolicyController : IController, IDisposable
    {
        // ML-Agents names its exported tensors. The deterministic output is the distribution
        // mean rather than a sample from it, which is what an evaluation run wants: sampling
        // would make two runs of the same seed disagree and defeat the whole comparison.
        static readonly string[] PreferredOutputs =
        {
            "deterministic_continuous_actions",
            "continuous_actions"
        };
        static readonly string[] PreferredInputs = { "obs_0" };

        Model _model;
        Worker _worker;
        Tensor<float> _input;
        string _inputName;
        string _outputName;

        public string Name { get { return "policy"; } }

        /// <param name="rng">
        /// Unused. The action is the model's deterministic output, so there is nothing to draw.
        /// </param>
        public void Initialize(SimConfig config, DeterministicRandom rng)
        {
            string path = config.Run.ModelPath;
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("controller_type is 'policy' but run.model is empty");
            if (!File.Exists(path))
                throw new InvalidOperationException("run.model does not exist: " + path);

            _model = ModelLoader.Load(path);
            if (_model == null)
                throw new InvalidOperationException("could not load a model from " + path
                    + "; it must be a .sentis file, not a .onnx");

            _inputName = Resolve(InputNames(_model), PreferredInputs, "input", path);
            _outputName = Resolve(OutputNames(_model), PreferredOutputs, "output", path);

            // CPU on purpose, not a config value. The server runs -nographics where no compute
            // device is available, and a GPU backend does not reproduce bit for bit between
            // machines, which an evaluation comparison depends on.
            _worker = new Worker(_model, BackendType.CPU);
            _input = new Tensor<float>(new TensorShape(1, RobotLayout.ObservationSize));

            Debug.Log("[WheelLeg] policy loaded from " + path
                + " (input '" + _inputName + "', output '" + _outputName + "')");
        }

        /// <summary>Nothing to reset: the policy is a pure function of the observation.</summary>
        public void OnEpisodeBegin()
        {
        }

        public void ComputeAction(float[] observation, float dt, float[] actionOut)
        {
            _input.Upload(observation);
            _worker.SetInput(_inputName, _input);
            _worker.Schedule();

            Tensor<float> output = _worker.PeekOutput(_outputName) as Tensor<float>;
            if (output == null)
            {
                // Leaving the previous action in place would look like a policy that has
                // stopped moving rather than one that could not be read.
                throw new InvalidOperationException(
                    "policy output '" + _outputName + "' is not a float tensor");
            }

            float[] values = output.DownloadToArray();
            int count = Mathf.Min(actionOut.Length, values.Length);
            for (int i = 0; i < count; i++) actionOut[i] = Mathf.Clamp(values[i], -1f, 1f);
            for (int i = count; i < actionOut.Length; i++) actionOut[i] = 0f;

            if (values.Length < actionOut.Length)
            {
                throw new InvalidOperationException("policy produced " + values.Length
                    + " values but the action is " + actionOut.Length
                    + "; the model was trained against a different action space");
            }
        }

        public void Dispose()
        {
            if (_input != null) { _input.Dispose(); _input = null; }
            if (_worker != null) { _worker.Dispose(); _worker = null; }
            _model = null;
        }

        static List<string> InputNames(Model model)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < model.inputs.Count; i++) names.Add(model.inputs[i].name);
            return names;
        }

        static List<string> OutputNames(Model model)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < model.outputs.Count; i++) names.Add(model.outputs[i].name);
            return names;
        }

        /// <summary>
        /// Picks the first preferred tensor name the model actually has. Falls back to the only
        /// candidate when there is exactly one, and otherwise fails naming what the model does
        /// offer, because silently binding the wrong tensor produces a run that looks finished.
        /// </summary>
        static string Resolve(List<string> available, string[] preferred, string kind, string path)
        {
            for (int i = 0; i < preferred.Length; i++)
            {
                if (available.Contains(preferred[i])) return preferred[i];
            }
            if (available.Count == 1) return available[0];

            throw new InvalidOperationException(path + ": none of the expected " + kind
                + " tensors (" + string.Join(", ", preferred) + ") exist in the model. It has: "
                + (available.Count == 0 ? "<none>" : string.Join(", ", available.ToArray()))
                + ". Add the correct name to PolicyController.");
        }
    }
}
