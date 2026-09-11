using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WheelLeg.Logging
{
    /// <summary>
    /// Apache Parquet writer with no third-party dependency. Parquet.Net targets net8.0 and
    /// drags native IronCompress binaries along; neither survives a headless IL2CPP Linux
    /// player, and a logger whose first real run is on the server is a logger untested.
    ///
    /// Only the subset the frame schema needs is implemented: PLAIN encoding, UNCOMPRESSED,
    /// no dictionary, no statistics, one row group per <see cref="Write"/>. Every column is
    /// REQUIRED, so a page carries neither definition nor repetition levels and a null is not
    /// representable at all. Rows stream straight to disk; nothing but the (small) row group
    /// index is held in memory, which is what keeps a twelve-hour run bounded.
    /// </summary>
    public sealed class ParquetSink : IFrameSink
    {
        // Parquet Type.
        const int TypeInt64 = 2;
        const int TypeDouble = 5;
        const int TypeByteArray = 6;

        // Parquet FieldRepetitionType.
        const int RepetitionRequired = 0;

        // Parquet ConvertedType.
        const int ConvertedUtf8 = 0;

        // Parquet Encoding.
        const int EncodingPlain = 0;
        const int EncodingRle = 3;

        // Parquet CompressionCodec.
        const int CodecUncompressed = 0;

        // Parquet PageType.
        const int PageTypeDataPage = 0;

        /// <summary>Format version of the metadata, not of this writer. 1 == data page v1.</summary>
        const int FormatVersion = 1;

        const int WriteBufferBytes = 1 << 16;
        const int InitialStringBufferBytes = 256;

        static readonly Encoding Utf8 = new UTF8Encoding(false);
        static readonly byte[] Magic = { (byte)'P', (byte)'A', (byte)'R', (byte)'1' };

        /// <summary>Where one column of one row group landed, needed to build the footer.</summary>
        struct ChunkIndex
        {
            public long Offset;
            public long ByteSize;
            public long NumValues;
        }

        readonly List<ChunkIndex[]> _rowGroups = new List<ChunkIndex[]>();
        readonly List<long> _rowGroupRows = new List<long>();
        readonly byte[][] _columnNames;
        readonly byte[] _rootName;
        readonly byte[] _createdBy;

        byte[] _stringBuffer = new byte[InitialStringBufferBytes];
        Stream _stream;
        ByteSink _out;
        ThriftCompactWriter _thrift;
        long _numRows;
        bool _closed;

        public ParquetSink()
        {
            _columnNames = new byte[FrameSchema.Count][];
            for (int c = 0; c < _columnNames.Length; c++)
                _columnNames[c] = Utf8.GetBytes(FrameSchema.Names[c]);
            _rootName = Utf8.GetBytes("wheelleg_frame");
            _createdBy = Utf8.GetBytes("WheelLeg.Logging.ParquetSink");
        }

        public string Extension { get { return "parquet"; } }

        public long BytesWritten { get { return _out == null ? 0L : _out.Position; } }

        public void Open(string path)
        {
            if (_stream != null)
                throw new InvalidOperationException("ParquetSink is already open");
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _out = new ByteSink(_stream, WriteBufferBytes);
            _thrift = new ThriftCompactWriter(_out);
            _out.Write(Magic, 0, Magic.Length);
        }

        public void Write(FrameBatch batch)
        {
            if (_out == null) throw new InvalidOperationException("ParquetSink.Open was not called");
            if (_closed) throw new InvalidOperationException("ParquetSink is closed");
            if (batch.Count == 0) return;

            int columns = FrameSchema.Count;
            ChunkIndex[] chunks = new ChunkIndex[columns];
            for (int c = 0; c < columns; c++)
            {
                long start = _out.Position;
                WriteColumnData(batch, c);
                chunks[c].Offset = start;
                chunks[c].ByteSize = _out.Position - start;
                chunks[c].NumValues = batch.Count;
            }

            _rowGroups.Add(chunks);
            _rowGroupRows.Add(batch.Count);
            _numRows += batch.Count;
            _out.Flush();
        }

        public void Flush()
        {
            if (_out != null && !_closed) _out.Flush();
        }

        public void Dispose()
        {
            if (_stream == null || _closed) return;
            WriteFooter();
            _closed = true;
            _out.Flush();
            _stream.Dispose();
            _stream = null;
        }

        void WriteColumnData(FrameBatch batch, int column)
        {
            int rows = batch.Count;
            ColumnType type = FrameSchema.Types[column];

            int pageBytes;
            if (type == ColumnType.String)
            {
                string[] values = batch.StringColumn(column);
                pageBytes = 0;
                for (int r = 0; r < rows; r++)
                    pageBytes += 4 + Utf8.GetByteCount(values[r] ?? string.Empty);
            }
            else
            {
                pageBytes = rows * 8;
            }

            WriteDataPageHeader(rows, pageBytes);

            switch (type)
            {
                case ColumnType.Double:
                {
                    double[] values = batch.DoubleColumn(column);
                    for (int r = 0; r < rows; r++) _out.WriteDoubleLe(values[r]);
                    break;
                }
                case ColumnType.Int64:
                {
                    long[] values = batch.LongColumn(column);
                    for (int r = 0; r < rows; r++) _out.WriteInt64Le(values[r]);
                    break;
                }
                default:
                {
                    string[] values = batch.StringColumn(column);
                    for (int r = 0; r < rows; r++) WritePlainByteArray(values[r]);
                    break;
                }
            }
        }

        void WritePlainByteArray(string value)
        {
            string s = value ?? string.Empty;
            int count = Utf8.GetByteCount(s);
            if (_stringBuffer.Length < count) _stringBuffer = new byte[count];
            Utf8.GetBytes(s, 0, s.Length, _stringBuffer, 0);
            _out.WriteInt32Le(count);
            _out.Write(_stringBuffer, 0, count);
        }

        void WriteDataPageHeader(int numValues, int pageBytes)
        {
            _thrift.StructBegin();
            _thrift.I32(1, PageTypeDataPage);
            _thrift.I32(2, pageBytes);          // uncompressed_page_size
            _thrift.I32(3, pageBytes);          // compressed_page_size
            _thrift.StructField(5);             // data_page_header
            _thrift.I32(1, numValues);
            _thrift.I32(2, EncodingPlain);
            // Required even though max definition and repetition level are both 0 and no
            // level bytes follow; RLE is what every other writer puts here.
            _thrift.I32(3, EncodingRle);
            _thrift.I32(4, EncodingRle);
            _thrift.StructEnd();
            _thrift.StructEnd();
        }

        void WriteFooter()
        {
            long start = _out.Position;

            _thrift.StructBegin();
            _thrift.I32(1, FormatVersion);

            _thrift.ListField(2, ThriftCompactWriter.TypeStruct, FrameSchema.Count + 1);
            WriteRootSchemaElement();
            for (int c = 0; c < FrameSchema.Count; c++) WriteLeafSchemaElement(c);

            _thrift.I64(3, _numRows);

            _thrift.ListField(4, ThriftCompactWriter.TypeStruct, _rowGroups.Count);
            for (int g = 0; g < _rowGroups.Count; g++) WriteRowGroup(g);

            _thrift.Binary(6, _createdBy);
            _thrift.StructEnd();

            _out.WriteInt32Le((int)(_out.Position - start));
            _out.Write(Magic, 0, Magic.Length);
        }

        void WriteRootSchemaElement()
        {
            _thrift.StructBegin();
            _thrift.Binary(4, _rootName);
            _thrift.I32(5, FrameSchema.Count);
            _thrift.StructEnd();
        }

        void WriteLeafSchemaElement(int column)
        {
            ColumnType type = FrameSchema.Types[column];

            _thrift.StructBegin();
            _thrift.I32(1, ParquetTypeOf(type));
            _thrift.I32(3, RepetitionRequired);
            _thrift.Binary(4, _columnNames[column]);
            if (type == ColumnType.String)
            {
                _thrift.I32(6, ConvertedUtf8);
                _thrift.StructField(10);        // logicalType
                _thrift.StructField(1);         // union member STRING
                _thrift.StructEnd();            // StringType {}
                _thrift.StructEnd();            // LogicalType
            }
            _thrift.StructEnd();
        }

        void WriteRowGroup(int index)
        {
            ChunkIndex[] chunks = _rowGroups[index];
            long totalBytes = 0;
            for (int c = 0; c < chunks.Length; c++) totalBytes += chunks[c].ByteSize;

            _thrift.StructBegin();
            _thrift.ListField(1, ThriftCompactWriter.TypeStruct, chunks.Length);
            for (int c = 0; c < chunks.Length; c++) WriteColumnChunk(c, chunks[c]);
            _thrift.I64(2, totalBytes);
            _thrift.I64(3, _rowGroupRows[index]);
            _thrift.StructEnd();
        }

        void WriteColumnChunk(int column, ChunkIndex chunk)
        {
            _thrift.StructBegin();
            _thrift.I64(2, chunk.Offset);       // file_offset
            _thrift.StructField(3);             // meta_data

            _thrift.I32(1, ParquetTypeOf(FrameSchema.Types[column]));
            _thrift.ListField(2, ThriftCompactWriter.TypeI32, 1);
            _thrift.I32Value(EncodingPlain);
            _thrift.ListField(3, ThriftCompactWriter.TypeBinary, 1);
            _thrift.BinaryValue(_columnNames[column]);
            _thrift.I32(4, CodecUncompressed);
            _thrift.I64(5, chunk.NumValues);
            _thrift.I64(6, chunk.ByteSize);     // total_uncompressed_size
            _thrift.I64(7, chunk.ByteSize);     // total_compressed_size
            _thrift.I64(9, chunk.Offset);       // data_page_offset

            _thrift.StructEnd();                // ColumnMetaData
            _thrift.StructEnd();                // ColumnChunk
        }

        static int ParquetTypeOf(ColumnType type)
        {
            switch (type)
            {
                case ColumnType.Double: return TypeDouble;
                case ColumnType.Int64: return TypeInt64;
                default: return TypeByteArray;
            }
        }

        /// <summary>
        /// Buffered write-through sink that knows its own byte offset. The offset is the
        /// only way to fill in the page offsets the footer needs, and Stream.Position is
        /// not reliable once a stream is wrapped.
        /// </summary>
        sealed class ByteSink
        {
            readonly Stream _stream;
            readonly byte[] _buffer;
            int _fill;
            long _position;

            public ByteSink(Stream stream, int bufferBytes)
            {
                _stream = stream;
                _buffer = new byte[bufferBytes];
            }

            public long Position { get { return _position; } }

            public void WriteByte(byte value)
            {
                if (_fill == _buffer.Length) Drain();
                _buffer[_fill++] = value;
                _position++;
            }

            public void Write(byte[] source, int offset, int count)
            {
                if (count >= _buffer.Length)
                {
                    Drain();
                    _stream.Write(source, offset, count);
                }
                else
                {
                    if (_fill + count > _buffer.Length) Drain();
                    Buffer.BlockCopy(source, offset, _buffer, _fill, count);
                    _fill += count;
                }
                _position += count;
            }

            public void WriteInt32Le(int value)
            {
                if (_fill + 4 > _buffer.Length) Drain();
                _buffer[_fill++] = (byte)value;
                _buffer[_fill++] = (byte)(value >> 8);
                _buffer[_fill++] = (byte)(value >> 16);
                _buffer[_fill++] = (byte)(value >> 24);
                _position += 4;
            }

            public void WriteInt64Le(long value)
            {
                if (_fill + 8 > _buffer.Length) Drain();
                _buffer[_fill++] = (byte)value;
                _buffer[_fill++] = (byte)(value >> 8);
                _buffer[_fill++] = (byte)(value >> 16);
                _buffer[_fill++] = (byte)(value >> 24);
                _buffer[_fill++] = (byte)(value >> 32);
                _buffer[_fill++] = (byte)(value >> 40);
                _buffer[_fill++] = (byte)(value >> 48);
                _buffer[_fill++] = (byte)(value >> 56);
                _position += 8;
            }

            public void WriteDoubleLe(double value)
            {
                WriteInt64Le(BitConverter.DoubleToInt64Bits(value));
            }

            public void Flush()
            {
                Drain();
                _stream.Flush();
            }

            void Drain()
            {
                if (_fill == 0) return;
                _stream.Write(_buffer, 0, _fill);
                _fill = 0;
            }
        }

        /// <summary>
        /// The slice of the Thrift compact protocol that Parquet metadata uses. Field ids are
        /// always emitted in ascending order, which is what makes the one-byte delta form of
        /// the field header valid.
        /// </summary>
        sealed class ThriftCompactWriter
        {
            public const byte TypeI32 = 5;
            public const byte TypeI64 = 6;
            public const byte TypeBinary = 8;
            public const byte TypeList = 9;
            public const byte TypeStruct = 12;

            const int MaxDepth = 8;

            readonly ByteSink _out;
            readonly int[] _fieldIdStack = new int[MaxDepth];
            int _depth;
            int _lastFieldId;

            public ThriftCompactWriter(ByteSink output)
            {
                _out = output;
            }

            public void StructBegin()
            {
                _fieldIdStack[_depth++] = _lastFieldId;
                _lastFieldId = 0;
            }

            public void StructEnd()
            {
                _out.WriteByte(0);
                _lastFieldId = _fieldIdStack[--_depth];
            }

            public void I32(int id, int value)
            {
                FieldHeader(id, TypeI32);
                ZigZag32(value);
            }

            public void I64(int id, long value)
            {
                FieldHeader(id, TypeI64);
                ZigZag64(value);
            }

            public void Binary(int id, byte[] value)
            {
                FieldHeader(id, TypeBinary);
                BinaryValue(value);
            }

            public void StructField(int id)
            {
                FieldHeader(id, TypeStruct);
                StructBegin();
            }

            public void ListField(int id, byte elementType, int count)
            {
                FieldHeader(id, TypeList);
                if (count <= 14)
                {
                    _out.WriteByte((byte)((count << 4) | elementType));
                }
                else
                {
                    _out.WriteByte((byte)(0xF0 | elementType));
                    Varint((uint)count);
                }
            }

            public void I32Value(int value)
            {
                ZigZag32(value);
            }

            public void BinaryValue(byte[] value)
            {
                Varint((uint)value.Length);
                _out.Write(value, 0, value.Length);
            }

            void FieldHeader(int id, byte type)
            {
                int delta = id - _lastFieldId;
                if (delta > 0 && delta <= 15)
                {
                    _out.WriteByte((byte)((delta << 4) | type));
                }
                else
                {
                    _out.WriteByte(type);
                    ZigZag32(id);
                }
                _lastFieldId = id;
            }

            void ZigZag32(int value)
            {
                Varint((uint)((value << 1) ^ (value >> 31)));
            }

            void ZigZag64(long value)
            {
                Varint64((ulong)((value << 1) ^ (value >> 63)));
            }

            void Varint(uint value)
            {
                while ((value & ~0x7Fu) != 0)
                {
                    _out.WriteByte((byte)((value & 0x7Fu) | 0x80u));
                    value >>= 7;
                }
                _out.WriteByte((byte)value);
            }

            void Varint64(ulong value)
            {
                while ((value & ~0x7FUL) != 0)
                {
                    _out.WriteByte((byte)((value & 0x7FUL) | 0x80UL));
                    value >>= 7;
                }
                _out.WriteByte((byte)value);
            }
        }
    }
}
