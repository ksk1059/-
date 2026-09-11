using UnityEngine;
using UnityEngine.Rendering;
using WheelLeg.Config;
using WheelLeg.Core;

namespace WheelLeg.Env
{
    /// <summary>
    /// One training area's ground. The mesh is built in this GameObject's local space and
    /// centred on its transform, so replicating an area at another world offset needs no
    /// code change.
    ///
    /// The Mesh, MeshCollider and PhysicsMaterial are created once and refilled on every
    /// rebuild rather than reallocated: terrain is rebuilt every episode and a 12 hour run
    /// must not grow (SERVER_OPS_1.md section 2-6).
    /// </summary>
    public sealed class TerrainSurface : MonoBehaviour
    {
        MeshCollider _collider;
        Mesh _mesh;
        PhysicsMaterial _physicsMaterial;
        TerrainField _field;
        Vector3[] _vertices;
        int[] _triangles;
        int _resolution;

        /// <summary>
        /// The friction coefficient this episode's ground was built with.
        ///
        /// The robot has to be given the same number. PhysX combines the two materials in a
        /// contact, and with both sides set to this value and the default Average rule the
        /// contact gets exactly it. Left alone, the robot's colliders carry no material at all
        /// and PhysX substitutes its 0.6 default, so a configured 1.0 would reach the contact
        /// as (1.0 + 0.6) / 2 = 0.8 and terrain.friction would not mean what it says.
        /// </summary>
        public float Friction { get; private set; }

        /// <summary>
        /// Regenerates the ground from the seed. The friction draw shares the seed's
        /// stream with the height field, so the whole surface reproduces from the seed.
        /// </summary>
        public void Build(TerrainConfig config, float difficulty, int seed)
        {
            EnsureComponents();

            DeterministicRandom rng = new DeterministicRandom(seed);
            if (_field == null) _field = new TerrainField();
            _field.Configure(config, difficulty, rng);

            Friction = rng.Range(config.Friction.Min, config.Friction.Max);
            _physicsMaterial.dynamicFriction = Friction;
            _physicsMaterial.staticFriction = Friction;

            int vertexCount = TerrainGenerator.VertexCount(config);
            if (_vertices == null || _vertices.Length != vertexCount)
                _vertices = new Vector3[vertexCount];
            TerrainGenerator.FillVertices(config, _field, _vertices);

            // Topology only depends on the resolution, so it survives a rebuild untouched.
            if (_resolution != config.Resolution)
            {
                _triangles = new int[TerrainGenerator.TriangleIndexCount(config)];
                TerrainGenerator.FillTriangles(config, _triangles);
                _resolution = config.Resolution;
            }

            _mesh.Clear();
            _mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.vertices = _vertices;
            _mesh.triangles = _triangles;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            // PhysX rebakes the collision mesh only when sharedMesh is assigned. Without
            // the null round trip the collider keeps the previous episode's terrain while
            // the renderer shows the new one.
            _collider.sharedMesh = null;
            _collider.sharedMesh = _mesh;
        }

        /// <summary>
        /// World-space height of the ground under <paramref name="worldPoint"/>, for
        /// placing the robot at spawn. Read from the analytic height field rather than a
        /// raycast, so it is valid immediately after <see cref="Build"/> without waiting
        /// for a physics step. Before the first Build this is the transform's own plane.
        /// </summary>
        public float SurfaceHeightAt(Vector3 worldPoint)
        {
            if (_field == null) return transform.position.y;
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            local.y = _field.Height(local.x, local.z);
            return transform.TransformPoint(local).y;
        }

        void EnsureComponents()
        {
            if (_mesh != null) return;

            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
            _collider = GetComponent<MeshCollider>();
            if (_collider == null) _collider = gameObject.AddComponent<MeshCollider>();

            _mesh = new Mesh();
            _mesh.name = "TerrainSurface";
            // Rewritten every episode, so tell the driver not to treat it as static data.
            _mesh.MarkDynamic();
            meshFilter.sharedMesh = _mesh;

            _physicsMaterial = new PhysicsMaterial("TerrainSurface");
            _collider.sharedMaterial = _physicsMaterial;

            meshRenderer.sharedMaterial = ResolveMaterial();
        }

        /// <summary>
        /// Built-in and URP name their lit shaders differently, and a dedicated-server
        /// build ships neither. A missing shader is not an error: a headless run has
        /// nothing to render, so the renderer is simply left without a material.
        /// </summary>
        static Material ResolveMaterial()
        {
            string[] shaderNames = { "Universal Render Pipeline/Lit", "Standard", "Legacy Shaders/Diffuse" };
            for (int i = 0; i < shaderNames.Length; i++)
            {
                Shader shader = Shader.Find(shaderNames[i]);
                if (shader != null) return new Material(shader);
            }
            return null;
        }
    }
}
