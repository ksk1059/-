using System.IO;
using UnityEditor;
using UnityEngine;
using WheelLeg.Logging;

namespace WheelLeg.EditorTools
{
    /// <summary>
    /// Emits a sample frame log into the project so it can be opened with an external
    /// reader. The Parquet container is written by hand, so "it compiled and the magic bytes
    /// are there" is not evidence that pyarrow or pandas can read it; only opening the file
    /// with a real reader is.
    /// </summary>
    public static class ParquetSelfCheck
    {
        const string OutputDirectory = "Builds/selfcheck";

        [MenuItem("WheelLeg/Emit Sample Frame Log", false, 60)]
        public static void Emit()
        {
            string directory = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
            Directory.CreateDirectory(directory);

            WriteWith(new ParquetSink(), Path.Combine(directory, "sample.parquet"));
            WriteWith(new CsvSink(), Path.Combine(directory, "sample.csv"));
            Debug.Log("[WheelLeg] sample frame logs written to " + directory);
        }

        static void WriteWith(IFrameSink sink, string path)
        {
            using (sink)
            {
                sink.Open(path);
                sink.Write(MakeBatch(0, 16));
                sink.Flush();
                // A second row group: the file layout differs from the single-group case and
                // that difference is exactly where a hand-written footer tends to break.
                sink.Write(MakeBatch(16, 16));
                sink.Flush();
            }
            Debug.Log("[WheelLeg] wrote " + path + " (" + new FileInfo(path).Length + " bytes)");
        }

        static FrameBatch MakeBatch(int firstRow, int rows)
        {
            FrameBatch batch = new FrameBatch(rows);
            for (int r = 0; r < rows; r++)
            {
                int row = firstRow + r;
                for (int c = 0; c < FrameSchema.Count; c++)
                {
                    switch (FrameSchema.Types[c])
                    {
                        case ColumnType.Double:
                            batch.SetDouble(c, row + c / 1000.0);
                            break;
                        case ColumnType.Int64:
                            batch.SetLong(c, row * 1000L + c);
                            break;
                        default:
                            batch.SetString(c, "r" + row + "_c" + c);
                            break;
                    }
                }
                batch.CommitRow();
            }
            return batch;
        }
    }
}
