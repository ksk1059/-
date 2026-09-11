using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WheelLeg.Logging
{
    /// <summary>
    /// Plain CSV fallback for the same schema, selected by logging.format. Values use the
    /// invariant culture and round-trip ("R") formatting so a run recorded on a machine with
    /// a comma decimal separator parses identically everywhere, and the line ending is
    /// forced to "\n" so a Windows-recorded file and a Linux-recorded one compare byte for
    /// byte.
    /// </summary>
    public sealed class CsvSink : IFrameSink
    {
        const int WriteBufferBytes = 1 << 16;

        static readonly Encoding Utf8 = new UTF8Encoding(false);

        readonly StringBuilder _quoted = new StringBuilder(64);

        FileStream _stream;
        StreamWriter _writer;
        long _bytesWritten;

        public string Extension { get { return "csv"; } }

        public long BytesWritten { get { return _bytesWritten; } }

        public void Open(string path)
        {
            if (_stream != null)
                throw new InvalidOperationException("CsvSink is already open");
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, Utf8, WriteBufferBytes);
            _writer.NewLine = "\n";

            for (int c = 0; c < FrameSchema.Count; c++)
            {
                if (c > 0) _writer.Write(',');
                _writer.Write(FrameSchema.Names[c]);
            }
            _writer.Write('\n');
            Flush();
        }

        public void Write(FrameBatch batch)
        {
            if (_writer == null) throw new InvalidOperationException("CsvSink.Open was not called");

            int columns = FrameSchema.Count;
            for (int r = 0; r < batch.Count; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    if (c > 0) _writer.Write(',');
                    switch (FrameSchema.Types[c])
                    {
                        case ColumnType.Double:
                            _writer.Write(batch.DoubleColumn(c)[r].ToString("R", CultureInfo.InvariantCulture));
                            break;
                        case ColumnType.Int64:
                            _writer.Write(batch.LongColumn(c)[r].ToString(CultureInfo.InvariantCulture));
                            break;
                        default:
                            WriteQuoted(batch.StringColumn(c)[r]);
                            break;
                    }
                }
                _writer.Write('\n');
            }
            Flush();
        }

        public void Flush()
        {
            if (_writer == null) return;
            _writer.Flush();
            _bytesWritten = _stream.Position;
        }

        public void Dispose()
        {
            if (_writer == null) return;
            Flush();
            _writer.Dispose();
            _writer = null;
            _stream = null;
        }

        void WriteQuoted(string value)
        {
            string s = value ?? string.Empty;
            _quoted.Length = 0;
            _quoted.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '"') _quoted.Append('"');
                _quoted.Append(ch);
            }
            _quoted.Append('"');
            _writer.Write(_quoted.ToString());
        }
    }
}
