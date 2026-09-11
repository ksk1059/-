using System.Threading;
using WheelLeg.Config;
using WheelLeg.Core;

namespace WheelLeg.Env
{
    /// <summary>
    /// Supplies the terrain seed for each episode. Training draws from the training band
    /// forever; evaluation walks a fixed list and then reports exhaustion so the process can
    /// exit on its own instead of waiting for a human (SERVER_OPS_1.md section 2-2).
    /// </summary>
    public interface ITerrainSeedSource
    {
        /// <summary>False once the run has produced every episode it was asked for.</summary>
        bool TryNext(int areaId, out int seed);

        /// <summary>Episodes finished so far across all areas.</summary>
        int Completed { get; }

        /// <summary>Episodes the run will produce in total, or -1 if unbounded.</summary>
        int Total { get; }
    }

    /// <summary>Unbounded draw from the training seed band.</summary>
    public sealed class TrainingSeedSource : ITerrainSeedSource
    {
        readonly SeedConfig _config;
        readonly DeterministicRandom[] _perArea;
        int _completed;

        public TrainingSeedSource(SeedConfig config, int topSeed, int areaCount)
        {
            _config = config;
            _perArea = new DeterministicRandom[areaCount];
            for (int i = 0; i < areaCount; i++)
                _perArea[i] = new DeterministicRandom(DeterministicRandom.DeriveSeed(topSeed, "terrain-seed", i));
        }

        public bool TryNext(int areaId, out int seed)
        {
            seed = _perArea[areaId].RangeInt(_config.TrainBandLow, _config.TrainBandHigh + 1);
            Interlocked.Increment(ref _completed);
            return true;
        }

        public int Completed { get { return _completed; } }
        public int Total { get { return -1; } }
    }

    /// <summary>
    /// Walks the evaluation seed list, repeating each seed the configured number of times.
    /// One process sweeps the whole list; restarting the process per seed would pay the
    /// startup cost thousands of times over (SERVER_OPS_1.md section 1).
    /// </summary>
    public sealed class EvaluationSeedSource : ITerrainSeedSource
    {
        readonly int[] _seeds;
        readonly int _repeats;
        int _issued;
        int _completed;

        public EvaluationSeedSource(SeedConfig config)
        {
            _seeds = config.EvalList;
            _repeats = config.EvalEpisodesPerSeed;
        }

        public bool TryNext(int areaId, out int seed)
        {
            int index = Interlocked.Increment(ref _issued) - 1;
            if (index >= Total)
            {
                seed = 0;
                return false;
            }
            seed = _seeds[index / _repeats];
            Interlocked.Increment(ref _completed);
            return true;
        }

        public int Completed { get { return _completed; } }
        public int Total { get { return _seeds.Length * _repeats; } }
    }
}
