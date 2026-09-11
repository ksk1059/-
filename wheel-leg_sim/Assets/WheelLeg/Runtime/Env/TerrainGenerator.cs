using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Core;

namespace WheelLeg.Env
{
    /// <summary>
    /// The analytic height field one terrain is drawn from: a tilted plane, a sum of
    /// sinusoids and a staircase, all with randomised orientation and phase. Keeping the
    /// field analytic rather than baking a height array means spawn placement can be
    /// queried without raycasting into a collider that may not have rebaked yet.
    ///
    /// There is no flat-ground special case (SPEC.md section 6): at difficulty 0 the
    /// slope, roughness and step amplitudes all interpolate to their range minimum, and
    /// a range of [0, 0] therefore multiplies every term to exactly zero.
    /// </summary>
    public sealed class TerrainField
    {
        const float Tau = 2f * Mathf.PI;

        /// <summary>
        /// How many sinusoids are summed. This is structural, not a scale value: it is
        /// the same locally and on the server, and difficulty drives the amplitudes.
        /// </summary>
        public const int WaveCount = 4;

        readonly float[] _waveAmplitude = new float[WaveCount];
        readonly float[] _waveFrequency = new float[WaveCount];
        readonly float[] _waveDirX = new float[WaveCount];
        readonly float[] _waveDirZ = new float[WaveCount];
        readonly float[] _wavePhase = new float[WaveCount];

        float _slopeGradientX;
        float _slopeGradientZ;
        float _stepHeight;
        float _stepSpacing;
        float _stepAxisX;
        float _stepAxisZ;
        float _stepPhase;
        float _originHeight;

        /// <summary>
        /// Draws every field parameter from <paramref name="rng"/> in a fixed order and
        /// a fixed count, so a caller that keeps drawing from the same stream afterwards
        /// (terrain friction) also reproduces from the seed.
        /// </summary>
        public void Configure(TerrainConfig config, float difficulty, DeterministicRandom rng)
        {
            float slopeGradient = Mathf.Tan(config.SlopeDeg.Lerp(difficulty) * Mathf.Deg2Rad);
            float slopeAzimuth = rng.Range(0f, Tau);
            _slopeGradientX = slopeGradient * Mathf.Cos(slopeAzimuth);
            _slopeGradientZ = slopeGradient * Mathf.Sin(slopeAzimuth);

            // Two mesh cells is the shortest wave the vertex grid can carry. Anything
            // shorter aliases into spikes a wheel catches on instead of the roughness the
            // configured wavelength describes, so the grid pitch bounds the draw.
            float minWavelength = 2f * Mathf.Max(config.SizeX, config.SizeZ) / (config.Resolution - 1);
            // Equal shares, so the summed sinusoids peak at exactly the configured amplitude.
            float waveAmplitude = config.RoughnessAmplitude.Lerp(difficulty) / WaveCount;

            for (int i = 0; i < WaveCount; i++)
            {
                float wavelength = Mathf.Max(minWavelength,
                    rng.Range(config.RoughnessWavelength.Min, config.RoughnessWavelength.Max));
                float heading = rng.Range(0f, Tau);
                _waveAmplitude[i] = waveAmplitude;
                _waveFrequency[i] = Tau / wavelength;
                _waveDirX[i] = Mathf.Cos(heading);
                _waveDirZ[i] = Mathf.Sin(heading);
                _wavePhase[i] = rng.Range(0f, Tau);
            }

            float stepHeading = rng.Range(0f, Tau);
            _stepAxisX = Mathf.Cos(stepHeading);
            _stepAxisZ = Mathf.Sin(stepHeading);
            _stepHeight = config.StepHeight.Lerp(difficulty);
            _stepSpacing = config.StepSpacing;
            _stepPhase = rng.NextFloat();

            // Referencing the field to its own centre keeps the mesh straddling the area
            // transform, so an area replicated at any world offset still spawns its robot
            // at the same local height.
            _originHeight = 0f;
            _originHeight = Height(0f, 0f);
        }

        /// <summary>
        /// Height in area-local metres at local (x, z), relative to the area transform.
        /// Defined everywhere, including outside the meshed extent.
        /// </summary>
        public float Height(float x, float z)
        {
            float h = _slopeGradientX * x + _slopeGradientZ * z;
            for (int i = 0; i < WaveCount; i++)
            {
                float along = _waveDirX[i] * x + _waveDirZ[i] * z;
                h += _waveAmplitude[i] * Mathf.Sin(_waveFrequency[i] * along + _wavePhase[i]);
            }
            float stepAlong = _stepAxisX * x + _stepAxisZ * z;
            h += _stepHeight * Mathf.Floor(stepAlong / _stepSpacing + _stepPhase);
            return h - _originHeight;
        }
    }

    /// <summary>
    /// Builds the terrain vertex grid. Every entry point is a pure function of its
    /// arguments: no Time, no frame count, no UnityEngine.Random, no state outside the
    /// call. Identical (config, difficulty, seed) therefore yields bit-identical
    /// vertices, which is what SPEC.md section 6 requires and what the determinism test
    /// asserts.
    /// </summary>
    public static class TerrainGenerator
    {
        public static int VertexCount(TerrainConfig config)
        {
            return config.Resolution * config.Resolution;
        }

        public static int TriangleIndexCount(TerrainConfig config)
        {
            int cells = config.Resolution - 1;
            return cells * cells * 6;
        }

        public static Vector3[] GenerateVertices(TerrainConfig config, float difficulty, int seed)
        {
            TerrainField field = new TerrainField();
            field.Configure(config, difficulty, new DeterministicRandom(seed));
            Vector3[] vertices = new Vector3[VertexCount(config)];
            FillVertices(config, field, vertices);
            return vertices;
        }

        /// <summary>
        /// Row-major grid, index = iz * resolution + ix, centred on local origin so the
        /// area transform alone decides where the ground sits in the world.
        /// </summary>
        public static void FillVertices(TerrainConfig config, TerrainField field, Vector3[] vertices)
        {
            int resolution = config.Resolution;
            float pitchX = config.SizeX / (resolution - 1);
            float pitchZ = config.SizeZ / (resolution - 1);
            float originX = -0.5f * config.SizeX;
            float originZ = -0.5f * config.SizeZ;

            for (int iz = 0; iz < resolution; iz++)
            {
                float z = originZ + iz * pitchZ;
                int row = iz * resolution;
                for (int ix = 0; ix < resolution; ix++)
                {
                    float x = originX + ix * pitchX;
                    vertices[row + ix] = new Vector3(x, field.Height(x, z), z);
                }
            }
        }

        /// <summary>
        /// Winding is chosen so RecalculateNormals faces the surface along +Y; a flipped
        /// grid is invisible from above and shades the robot black.
        /// </summary>
        public static void FillTriangles(TerrainConfig config, int[] triangles)
        {
            int resolution = config.Resolution;
            int t = 0;
            for (int iz = 0; iz < resolution - 1; iz++)
            {
                for (int ix = 0; ix < resolution - 1; ix++)
                {
                    int a = iz * resolution + ix;
                    int b = a + 1;
                    int c = a + resolution;
                    int d = c + 1;
                    triangles[t++] = a;
                    triangles[t++] = c;
                    triangles[t++] = b;
                    triangles[t++] = b;
                    triangles[t++] = c;
                    triangles[t++] = d;
                }
            }
        }
    }
}
