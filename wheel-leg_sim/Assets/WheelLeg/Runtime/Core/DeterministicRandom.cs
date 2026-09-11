using System;

namespace WheelLeg.Core
{
    /// <summary>
    /// Per-instance random source. UnityEngine.Random is global state shared by every
    /// training area in the process, so it can neither be seeded reproducibly nor kept
    /// from one area consuming another's stream. This type is the only source of
    /// randomness in the simulation.
    /// </summary>
    public sealed class DeterministicRandom
    {
        readonly Random _rng;
        double _spareGaussian;
        bool _hasSpare;

        public readonly int Seed;

        public DeterministicRandom(int seed)
        {
            Seed = seed;
            _rng = new Random(seed);
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float NextFloat()
        {
            return (float)_rng.NextDouble();
        }

        /// <summary>Uniform in [min, max).</summary>
        public float Range(float min, float max)
        {
            return min >= max ? min : min + (float)_rng.NextDouble() * (max - min);
        }

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        public int RangeInt(int minInclusive, int maxExclusive)
        {
            return _rng.Next(minInclusive, maxExclusive);
        }

        public bool Chance(float probability)
        {
            return _rng.NextDouble() < probability;
        }

        /// <summary>Normal(0, 1), Box-Muller with the second sample cached.</summary>
        public float NextGaussian()
        {
            if (_hasSpare)
            {
                _hasSpare = false;
                return (float)_spareGaussian;
            }

            double u1, u2;
            do
            {
                u1 = _rng.NextDouble();
            } while (u1 <= double.Epsilon);
            u2 = _rng.NextDouble();

            double magnitude = Math.Sqrt(-2.0 * Math.Log(u1));
            _spareGaussian = magnitude * Math.Sin(2.0 * Math.PI * u2);
            _hasSpare = true;
            return (float)(magnitude * Math.Cos(2.0 * Math.PI * u2));
        }

        public float NextGaussian(float standardDeviation)
        {
            return standardDeviation <= 0f ? 0f : NextGaussian() * standardDeviation;
        }

        /// <summary>
        /// Derives a child seed from a parent seed and a stream label. Areas get their
        /// seeds this way so the whole process reproduces from the one top-level seed
        /// instead of each area drawing an arbitrary one.
        /// </summary>
        public static int DeriveSeed(int parentSeed, string stream, int index)
        {
            unchecked
            {
                // SplitMix64 finalizer over a FNV-1a hash of the label.
                ulong h = 1469598103934665603UL;
                for (int i = 0; i < stream.Length; i++)
                {
                    h ^= stream[i];
                    h *= 1099511628211UL;
                }
                ulong x = h ^ ((ulong)(uint)parentSeed << 32) ^ (uint)index;
                x += 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                x ^= x >> 31;
                return (int)(x & 0x7FFFFFFF);
            }
        }
    }
}
