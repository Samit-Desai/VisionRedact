namespace VisionRedact.IntegrationTests.AI;

// just enough of an ONNX TensorProto reader for the zoo's test_data_set_N/*.pb files: dims + floats.
// saves a protobuf dependency for three fixtures. tag = (field << 3) | wire type
// fields used: 1 = dims, 4 = float_data, 9 = raw_data
internal static class TensorProtoReader
{
    public static (long[] Dims, float[] Data) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var dims = new List<long>();
        float[]? data = null;
        var floats = new List<float>();
        var pos = 0;

        while (pos < bytes.Length)
        {
            var tag = ReadVarint(bytes, ref pos);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: // varint
                    var value = ReadVarint(bytes, ref pos);
                    if (field == 1)
                    {
                        dims.Add((long)value);
                    }

                    break;
                case 2: // length-delimited
                    var length = (int)ReadVarint(bytes, ref pos);
                    var span = bytes.AsSpan(pos, length);
                    if (field == 1)
                    {
                        var p = 0;
                        var packed = span.ToArray();
                        while (p < packed.Length)
                        {
                            dims.Add((long)ReadVarint(packed, ref p));
                        }
                    }
                    else if (field == 9)
                    {
                        data = new float[length / 4];
                        Buffer.BlockCopy(bytes, pos, data, 0, length);
                    }
                    else if (field == 4)
                    {
                        for (var i = 0; i + 4 <= length; i += 4)
                        {
                            floats.Add(BitConverter.ToSingle(bytes, pos + i));
                        }
                    }

                    pos += length;
                    break;
                case 5: // fixed32
                    if (field == 4)
                    {
                        floats.Add(BitConverter.ToSingle(bytes, pos));
                    }

                    pos += 4;
                    break;
                case 1: // fixed64
                    pos += 8;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wire} in {path}.");
            }
        }

        return (dims.ToArray(), data ?? floats.ToArray());
    }

    private static ulong ReadVarint(byte[] buffer, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = buffer[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}
