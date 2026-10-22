using System.Text;

namespace FuzzyFindAnything.Core;

public sealed record OnnxTensor(string Name, int ElementType, long[] Dimensions);
public sealed record OnnxExternalData(string Location, long Offset, long? Length);
public sealed record OnnxMetadata(OnnxTensor[] Inputs, OnnxTensor[] Outputs, OnnxExternalData[] ExternalData)
{
    public static OnnxMetadata Read(string path)
    {
        using var stream = File.OpenRead(path);
        return new Reader(stream).Read();
    }

    private sealed class Reader(Stream stream)
    {
        private sealed record Field(int Number, int Wire, long Start, long End, ulong Integer);
        private readonly List<OnnxTensor> inputs = [], outputs = [];
        private readonly List<OnnxExternalData> external = [];
        private int fieldsRead;

        public OnnxMetadata Read()
        {
            bool graph = false;
            foreach (var field in Fields(stream.Length))
                if (field.Number == 7 && field.Wire == 2)
                {
                    if (graph) throw new InvalidDataException("The ONNX model has duplicate graphs.");
                    Graph(field.End, 0, true); graph = true;
                }
            if (!graph || inputs.Count == 0 || outputs.Count == 0)
                throw new InvalidDataException("The ONNX model has no usable input/output graph.");
            return new(inputs.ToArray(), outputs.ToArray(), external.Distinct().ToArray());
        }

        private ulong Varint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                int next = stream.ReadByte();
                if (next < 0 || shift == 63 && next > 1) throw new InvalidDataException("Malformed ONNX integer.");
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new InvalidDataException("Malformed ONNX integer.");
        }
        private IEnumerable<Field> Fields(long end)
        {
            while (stream.Position < end)
            {
                if (++fieldsRead > 5_000_000) throw new InvalidDataException("The ONNX metadata exceeds the inspection bound.");
                ulong tag = Varint();
                int number = checked((int)(tag >> 3)), wire = (int)(tag & 7);
                if (number == 0) throw new InvalidDataException("Invalid ONNX field.");
                ulong integer = 0;
                long length;
                switch (wire)
                {
                    case 0: integer = Varint(); length = 0; break;
                    case 1: length = 8; break;
                    case 2: length = checked((long)Varint()); break;
                    case 5: length = 4; break;
                    default: throw new InvalidDataException("Unsupported ONNX wire type.");
                }
                long start = stream.Position, next = checked(start + length);
                if (next > end) throw new InvalidDataException("An ONNX field exceeds its containing message.");
                yield return new(number, wire, start, next, integer);
                stream.Position = next;
            }
            if (stream.Position != end) throw new InvalidDataException("Invalid ONNX message boundary.");
        }
        private string Text(Field field)
        {
            if (field.Wire != 2 || field.End - field.Start > 1_048_576)
                throw new InvalidDataException("Unsupported ONNX metadata string.");
            byte[] value = new byte[checked((int)(field.End - field.Start))];
            stream.ReadExactly(value);
            return new UTF8Encoding(false, true).GetString(value);
        }
        private void Graph(long end, int depth, bool root)
        {
            if (depth > 32) throw new InvalidDataException("The ONNX graph nesting exceeds the inspection bound.");
            foreach (var field in Fields(end))
            {
                if (field.Wire != 2) continue;
                switch (field.Number)
                {
                    case 1: Node(field.End, depth); break;
                    case 5: Tensor(field.End); break;
                    case 11 when root: inputs.Add(ValueInfo(field.End)); break;
                    case 12 when root: outputs.Add(ValueInfo(field.End)); break;
                    case 15: Sparse(field.End); break;
                }
            }
        }
        private void Node(long end, int depth)
        {
            foreach (var field in Fields(end))
                if (field.Number == 5 && field.Wire == 2)
                    foreach (var attribute in Fields(field.End))
                    {
                        if (attribute.Wire != 2) continue;
                        if (attribute.Number is 5 or 10) Tensor(attribute.End);
                        else if (attribute.Number is 6 or 11) Graph(attribute.End, depth + 1, false);
                        else if (attribute.Number is 22 or 23) Sparse(attribute.End);
                    }
        }
        private void Sparse(long end)
        {
            foreach (var field in Fields(end))
                if (field.Number is 1 or 2 && field.Wire == 2) Tensor(field.End);
        }
        private void Tensor(long end)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            bool isExternal = false;
            foreach (var field in Fields(end))
            {
                if (field.Number == 14 && field.Wire == 0) isExternal = field.Integer == 1;
                if (field.Number != 13 || field.Wire != 2) continue;
                string? key = null, value = null;
                foreach (var entry in Fields(field.End))
                {
                    if (entry.Number == 1) key = Text(entry);
                    else if (entry.Number == 2) value = Text(entry);
                }
                if (key is null || value is null || !metadata.TryAdd(key, value))
                    throw new InvalidDataException("Invalid ONNX external-data entry.");
            }
            if (!isExternal && metadata.Count == 0) return;
            if (!metadata.TryGetValue("location", out string? location))
                throw new InvalidDataException("ONNX external weights have no location.");
            _ = CachedModel.LocalName(location);
            long Number(string name) => metadata.TryGetValue(name, out string? text) &&
                long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long result)
                    ? result : throw new InvalidDataException("Invalid ONNX external-data " + name);
            external.Add(new(location, metadata.ContainsKey("offset") ? Number("offset") : 0,
                metadata.ContainsKey("length") ? Number("length") : null));
        }
        private OnnxTensor ValueInfo(long end)
        {
            string? name = null;
            int elementType = 0;
            var dimensions = new List<long>();
            foreach (var field in Fields(end))
            {
                if (field.Number == 1) name = Text(field);
                if (field.Number != 2 || field.Wire != 2) continue;
                foreach (var type in Fields(field.End))
                    if (type.Number == 1 && type.Wire == 2)
                        foreach (var tensor in Fields(type.End))
                        {
                            if (tensor.Number == 1 && tensor.Wire == 0) elementType = checked((int)tensor.Integer);
                            if (tensor.Number != 2 || tensor.Wire != 2) continue;
                            foreach (var shape in Fields(tensor.End))
                            {
                                if (shape.Number != 1 || shape.Wire != 2) continue;
                                long dimension = -1;
                                foreach (var item in Fields(shape.End))
                                    if (item.Number == 1 && item.Wire == 0) dimension = checked((long)item.Integer);
                                dimensions.Add(dimension);
                                if (dimensions.Count > 16) throw new InvalidDataException("Unsupported ONNX tensor rank.");
                            }
                        }
            }
            if (string.IsNullOrEmpty(name) || elementType == 0)
                throw new InvalidDataException("Invalid ONNX input/output tensor description.");
            return new(name, elementType, dimensions.ToArray());
        }
    }
}
