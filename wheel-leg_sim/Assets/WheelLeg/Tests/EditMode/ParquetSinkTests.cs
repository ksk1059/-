using System.IO;
using NUnit.Framework;
using WheelLeg.Logging;

namespace WheelLeg.Tests
{
    /// <summary>
    /// The Parquet container is written by hand (no third-party dependency survives a
    /// headless IL2CPP Linux player), so these tests exist to catch a malformed file before
    /// a twelve-hour server run produces gigabytes of it.
    ///
    /// C# side checks structure only. The authoritative check is opening the emitted file
    /// with pyarrow; the file this writes is left on disk for that purpose and its path is
    /// printed.
    /// </summary>
    public class ParquetSinkTests
    {
        static string OutputPath(string name)
        {
            string directory = Path.Combine(Path.GetTempPath(), "wheelleg_parquet_tests");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name);
        }

        static FrameBatch MakeBatch(int rows)
        {
            FrameBatch batch = new FrameBatch(rows);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < FrameSchema.Count; c++)
                {
                    switch (FrameSchema.Types[c])
                    {
                        case ColumnType.Double: batch.SetDouble(c, r * 1000.0 + c * 0.5); break;
                        case ColumnType.Int64: batch.SetLong(c, r * 100L + c); break;
                        default: batch.SetString(c, "r" + r + "c" + c); break;
                    }
                }
                batch.CommitRow();
            }
            return batch;
        }

        [Test]
        public void WritesAFileFramedByTheParquetMagic()
        {
            string path = OutputPath("schema_check.parquet");
            using (ParquetSink sink = new ParquetSink())
            {
                sink.Open(path);
                sink.Write(MakeBatch(8));
                sink.Flush();
            }

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Greater(bytes.Length, 12, "file is too short to be a parquet file");

            Assert.AreEqual((byte)'P', bytes[0]);
            Assert.AreEqual((byte)'A', bytes[1]);
            Assert.AreEqual((byte)'R', bytes[2]);
            Assert.AreEqual((byte)'1', bytes[3]);

            int last = bytes.Length;
            Assert.AreEqual((byte)'P', bytes[last - 4]);
            Assert.AreEqual((byte)'A', bytes[last - 3]);
            Assert.AreEqual((byte)'R', bytes[last - 2]);
            Assert.AreEqual((byte)'1', bytes[last - 1]);

            int footerLength =
                bytes[last - 8] | (bytes[last - 7] << 8) | (bytes[last - 6] << 16) | (bytes[last - 5] << 24);
            Assert.Greater(footerLength, 0, "footer length is not positive");
            Assert.Less(footerLength, bytes.Length - 8, "footer length exceeds the file");

            TestContext.Out.WriteLine("parquet written for external validation: " + path);
        }

        [Test]
        public void MultipleWritesAppendRowGroupsAndGrowTheFile()
        {
            string path = OutputPath("rowgroups_check.parquet");
            long afterFirst;
            using (ParquetSink sink = new ParquetSink())
            {
                sink.Open(path);
                sink.Write(MakeBatch(4));
                sink.Flush();
                afterFirst = sink.BytesWritten;
                sink.Write(MakeBatch(4));
                sink.Flush();
                Assert.Greater(sink.BytesWritten, afterFirst,
                    "a second row group did not increase the byte count");
            }

            Assert.IsTrue(File.Exists(path));
            TestContext.Out.WriteLine("parquet written for external validation: " + path);
        }

        [Test]
        public void CsvSinkWritesHeaderAndOneRowPerFrame()
        {
            string path = OutputPath("csv_check.csv");
            using (CsvSink sink = new CsvSink())
            {
                sink.Open(path);
                sink.Write(MakeBatch(3));
                sink.Flush();
            }

            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(4, lines.Length, "expected a header plus three rows");
            StringAssert.StartsWith("t,", lines[0]);
            StringAssert.Contains("terrain_seed", lines[0]);
            Assert.AreEqual(FrameSchema.Count, lines[0].Split(',').Length);
        }
    }
}
