using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using WheelLeg.Config;

namespace WheelLeg.Logging
{
    /// <summary>
    /// One area's frame log: buffering, flushing, file rotation and the disk cap.
    /// One instance per training area, never shared, so 32 areas never touch one file
    /// handle. The batch is allocated once and refilled, so a twelve-hour run has the same
    /// memory footprint as a five-minute one (SERVER_OPS_1.md 2-6).
    /// </summary>
    public sealed class FrameLogger : IDisposable
    {
        readonly SimConfig _config;
        readonly string _directory;
        readonly int _areaId;
        readonly FrameBatch _batch;

        IFrameSink _sink;
        int _partIndex;
        long _closedPartBytes;
        /// <summary>Bytes the previous flush cost, used to predict whether the next one fits.</summary>
        long _lastFlushBytes;
        bool _stopped;
        bool _disposed;

        public FrameLogger(SimConfig config, string outputDirectory, int areaId)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (outputDirectory == null) throw new ArgumentNullException("outputDirectory");

            _config = config;
            _areaId = areaId;
            _directory = Path.Combine(outputDirectory, config.Logging.SubDir);
            // A disabled logger must not reserve the buffer: at 32 areas it is the single
            // largest allocation in the process.
            _batch = config.Logging.Enabled ? new FrameBatch(config.Logging.MaxBufferedFrames) : null;
        }

        /// <summary>False once logging is off, the area hit its byte cap, or the logger was
        /// disposed; skip gathering then.</summary>
        public bool IsWriting { get { return _batch != null && !_stopped && !_disposed; } }

        /// <summary>Bytes this area has committed across every part it has written.</summary>
        public long TotalBytes
        {
            get { return _closedPartBytes + (_sink == null ? 0L : _sink.BytesWritten); }
        }

        public void SetDouble(int column, double value)
        {
            if (_batch != null) _batch.SetDouble(column, value);
        }

        public void SetLong(int column, long value)
        {
            if (_batch != null) _batch.SetLong(column, value);
        }

        public void SetString(int column, string value)
        {
            if (_batch != null) _batch.SetString(column, value);
        }

        public void SetDoubles(int firstColumn, float[] values, int count)
        {
            if (_batch != null) _batch.SetDoubles(firstColumn, values, count);
        }

        public void CommitRow()
        {
            if (_batch == null || _stopped || _disposed) return;
            _batch.CommitRow();
            if (_batch.Count >= _config.Logging.FlushIntervalFrames || _batch.IsFull) Flush();
        }

        public void Flush()
        {
            if (_batch == null || _batch.Count == 0) return;
            if (_stopped)
            {
                _batch.Clear();
                return;
            }

            if (TotalBytes + _lastFlushBytes > _config.Logging.MaxTotalBytes)
            {
                Stop();
                _batch.Clear();
                return;
            }

            if (_sink == null) OpenNextPart();

            long before = _sink.BytesWritten;
            _sink.Write(_batch);
            _sink.Flush();
            _lastFlushBytes = _sink.BytesWritten - before;
            _batch.Clear();

            if (_sink.BytesWritten >= _config.Logging.MaxFileBytes) CloseCurrentPart();
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Flush();
                CloseCurrentPart();
            }
            finally
            {
                // Set last, but always: the flag is what stops a later CommitRow from
                // reopening a part that nothing will ever close a footer on.
                _disposed = true;
            }
        }

        void OpenNextPart()
        {
            IFrameSink sink = _config.Logging.Format == LogFormat.Csv
                ? (IFrameSink)new CsvSink()
                : new ParquetSink();

            Directory.CreateDirectory(_directory);
            string name = string.Format(CultureInfo.InvariantCulture, "area{0:D3}_part{1:D5}.{2}",
                _areaId, _partIndex, sink.Extension);
            sink.Open(Path.Combine(_directory, name));
            _sink = sink;
        }

        void CloseCurrentPart()
        {
            if (_sink == null) return;
            _sink.Dispose();
            _closedPartBytes += _sink.BytesWritten;
            _sink = null;
            _partIndex++;
        }

        /// <summary>
        /// The disk is shared with two other users, so overrunning it blocks them rather
        /// than only this run. Stopping is therefore the correct failure mode, not throwing.
        /// </summary>
        void Stop()
        {
            _stopped = true;
            CloseCurrentPart();
            Debug.LogWarning(string.Format(CultureInfo.InvariantCulture,
                "FrameLogger area {0}: log cap reached ({1} bytes on disk, max_total_bytes {2}). "
                + "Frame logging is off for this area for the rest of the run; simulation continues.",
                _areaId, TotalBytes, _config.Logging.MaxTotalBytes));
        }
    }
}
