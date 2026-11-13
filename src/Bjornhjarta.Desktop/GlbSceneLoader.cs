using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Bjornhjarta.Desktop;

/// <summary>
/// Small, deliberately scoped glTF 2.0 binary importer for the first WPF 3D preview.
/// Supports triangle meshes, interleaved bufferViews, indexed/nonindexed geometry,
/// UV0, embedded/base64/local albedo images, and standard node TRS/matrix transforms.
/// Normal/ORM/emissive maps, skins, morphs, compression extensions and full PBR are
/// not rendered by WPF's built-in Viewport3D and must not be advertised as supported.
/// User-selected local models only; no game archive extraction or redistribution.
/// </summary>
internal static class GlbSceneLoader
{
    private const int MaxFileBytes = 256 * 1024 * 1024;
    private const int MaxVerticesPerPrimitive = 1_000_000;
    private const int MaxNodeDepth = 40;
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    internal sealed record LoadedScene(Model3DGroup Model, int TriangleCount, int TexturedPrimitives);

    public static LoadedScene Load(string filePath)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists || file.Length < 20 || file.Length > MaxFileBytes)
            throw new InvalidDataException("Model is missing or exceeds the 256 MB preview limit.");

        var bytes = File.ReadAllBytes(filePath);
        var span = bytes.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[..4]) != GlbMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4)) != (uint)bytes.Length)
            throw new InvalidDataException("Invalid glTF 2.0 GLB header.");

        byte[]? jsonBytes = null;
        byte[]? binaryBytes = null;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var len = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
            offset += 8;
            if (len > bytes.Length - offset || len > int.MaxValue)
                throw new InvalidDataException("GLB chunk length is out of bounds.");
            if (type == JsonChunk && jsonBytes is null)
                jsonBytes = bytes.AsSpan(offset, (int)len).ToArray();
            if (type == BinChunk && binaryBytes is null)
                binaryBytes = bytes.AsSpan(offset, (int)len).ToArray();
            offset += (int)len;
        }
        if (offset != bytes.Length || jsonBytes is null || binaryBytes is null)
            throw new InvalidDataException("GLB requires a complete JSON and binary chunk.");

        using var doc = JsonDocument.Parse(jsonBytes);
        return new Reader(doc.RootElement, binaryBytes, Path.GetDirectoryName(Path.GetFullPath(filePath))!).Read();
    }

    private sealed class Reader(JsonElement root, byte[] binary, string folder)
    {
        private readonly JsonElement _root = root;
        private readonly byte[] _bin = binary;
        private readonly string _folder = folder;
        private readonly Dictionary<int, Material> _materials = [];
        private readonly Dictionary<int, BitmapSource?> _images = [];
        private int _triangles;
        private int _textured;

        public LoadedScene Read()
        {
            var required = GetArray(_root, "extensionsRequired");
            if (required.GetArrayLength() > 0)
                throw new InvalidDataException("GLB uses required glTF extensions that the lightweight WPF preview cannot decode: " +
                    string.Join(", ", required.EnumerateArray().Select(x => x.GetString())));
            var result = new Model3DGroup();
            var nodes = GetArray(_root, "nodes");
            var meshes = GetArray(_root, "meshes");
            var sceneIndices = new List<int>();
            var scenes = GetArray(_root, "scenes");
            if (scenes.GetArrayLength() > 0)
            {
                var sceneIndex = ReadInt(_root, "scene", 0);
                if (sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength())
                    throw new InvalidDataException("Scene index out of bounds.");
                sceneIndices.AddRange(ReadIntArray(scenes[sceneIndex], "nodes"));
            }
            else if (nodes.GetArrayLength() > 0)
            {
                var childNodes = nodes.EnumerateArray()
                    .SelectMany(node => ReadIntArray(node, "children")).ToHashSet();
                sceneIndices.AddRange(Enumerable.Range(0, nodes.GetArrayLength()).Where(i => !childNodes.Contains(i)));
            }
            else
            {
                for (var i = 0; i < meshes.GetArrayLength(); i++)
                    result.Children.Add(ReadMesh(i));
            }

            foreach (var index in sceneIndices)
                result.Children.Add(ReadNode(index, [], 0));
            if (_triangles == 0)
                throw new InvalidDataException("The GLB contains no supported triangle mesh primitives.");
            return new LoadedScene(result, _triangles, _textured);
        }

        private Model3D ReadNode(int index, HashSet<int> ancestors, int depth)
        {
            var nodes = GetArray(_root, "nodes");
            if (depth > MaxNodeDepth || index < 0 || index >= nodes.GetArrayLength() || !ancestors.Add(index))
                throw new InvalidDataException("GLB node tree is invalid, recursive, or too deep.");
            var node = nodes[index];
            var group = new Model3DGroup();
            if (node.TryGetProperty("mesh", out var meshIndex))
                group.Children.Add(ReadMesh(meshIndex.GetInt32()));
            foreach (var childIndex in ReadIntArray(node, "children"))
                group.Children.Add(ReadNode(childIndex, ancestors, depth + 1));
            group.Transform = ReadNodeTransform(node);
            ancestors.Remove(index);
            return group;
        }

        private Model3DGroup ReadMesh(int meshIndex)
        {
            var meshes = GetArray(_root, "meshes");
            if (meshIndex < 0 || meshIndex >= meshes.GetArrayLength())
                throw new InvalidDataException("GLB mesh index out of range.");
            var result = new Model3DGroup();
            foreach (var primitive in GetArray(meshes[meshIndex], "primitives").EnumerateArray())
            {
                if (ReadInt(primitive, "mode", 4) != 4) // TRIANGLES only
                    continue;
                var attributes = primitive.GetProperty("attributes");
                if (!attributes.TryGetProperty("POSITION", out var positionIndex))
                    continue;
                var positions = ReadAttribute(positionIndex.GetInt32(), "VEC3", 5126);
                if (positions.Length == 0 || positions.Length > MaxVerticesPerPrimitive * 3)
                    throw new InvalidDataException("Primitive has too many vertices or no positions.");
                var count = positions.Length / 3;
                var mesh = new MeshGeometry3D();
                for (var i = 0; i < count; i++)
                {
                    var x = positions[i * 3]; var y = positions[i * 3 + 1]; var z = positions[i * 3 + 2];
                    if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                        throw new InvalidDataException("Mesh has nonfinite vertex coordinates.");
                    mesh.Positions.Add(new Point3D(x, y, z));
                }

                if (attributes.TryGetProperty("NORMAL", out var normalsIndex))
                {
                    var normals = ReadAttribute(normalsIndex.GetInt32(), "VEC3", 5126);
                    if (normals.Length == positions.Length)
                        for (var i = 0; i < count; i++)
                            mesh.Normals.Add(new Vector3D(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]));
                }

                if (attributes.TryGetProperty("TEXCOORD_0", out var texIndex))
                {
                    var uvs = ReadAttribute(texIndex.GetInt32(), "VEC2", 5126);
                    if (uvs.Length == count * 2)
                        for (var i = 0; i < count; i++)
                            mesh.TextureCoordinates.Add(new Point(uvs[i * 2], uvs[i * 2 + 1]));
                }

                int[] indices;
                if (primitive.TryGetProperty("indices", out var indicesElement))
                    indices = ReadIndices(indicesElement.GetInt32());
                else
                    indices = Enumerable.Range(0, count).ToArray();
                if (indices.Length % 3 != 0 || indices.Length > MaxVerticesPerPrimitive * 6)
                    throw new InvalidDataException("Unsupported or overlarge primitive index buffer.");
                foreach (var vertex in indices)
                {
                    if (vertex < 0 || vertex >= count)
                        throw new InvalidDataException("Primitive triangle index exceeds vertex count.");
                    mesh.TriangleIndices.Add(vertex);
                }

                var materialIndex = ReadInt(primitive, "material", -1);
                var material = GetMaterial(materialIndex, out var doubleSided);
                var model = new GeometryModel3D(mesh, material);
                if (doubleSided)
                    model.BackMaterial = material;
                result.Children.Add(model);
                _triangles += indices.Length / 3;
            }
            return result;
        }

        private Material GetMaterial(int index, out bool doubleSided)
        {
            doubleSided = false;
            var materials = GetArray(_root, "materials");
            if (index < 0 || index >= materials.GetArrayLength())
                return new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(176, 184, 170)));
            var materialJson = materials[index];
            doubleSided = materialJson.TryGetProperty("doubleSided", out var side) && side.ValueKind == JsonValueKind.True;
            if (_materials.TryGetValue(index, out var cached)) return cached;
            Brush brush = new SolidColorBrush(Color.FromRgb(184, 190, 174));
            if (materialJson.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out var factor) && factor.ValueKind == JsonValueKind.Array && factor.GetArrayLength() == 4)
                {
                    var comps = factor.EnumerateArray().Select(x => Math.Clamp(x.GetDouble(), 0, 1)).ToArray();
                    brush = new SolidColorBrush(Color.FromArgb((byte)(comps[3] * 255), (byte)(comps[0] * 255), (byte)(comps[1] * 255), (byte)(comps[2] * 255)));
                }
                if (pbr.TryGetProperty("baseColorTexture", out var tex))
                {
                    var textureIndex = ReadInt(tex, "index", -1);
                    var textures = GetArray(_root, "textures");
                    if (textureIndex >= 0 && textureIndex < textures.GetArrayLength())
                    {
                        var sourceIndex = ReadInt(textures[textureIndex], "source", -1);
                        var source = ReadImage(sourceIndex);
                        if (source is not null)
                        {
                            brush = new ImageBrush(source) { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.RelativeToBoundingBox };
                            _textured++;
                        }
                    }
                }
            }
            var result = new DiffuseMaterial(brush);
            _materials[index] = result;
            return result;
        }

        private BitmapSource? ReadImage(int index)
        {
            var images = GetArray(_root, "images");
            if (index < 0 || index >= images.GetArrayLength()) return null;
            if (_images.TryGetValue(index, out var cached)) return cached;
            var image = images[index];
            byte[]? data = null;
            if (image.TryGetProperty("bufferView", out var viewIndex))
            {
                var (start, length, _) = ReadBufferView(viewIndex.GetInt32());
                if (length > 32 * 1024 * 1024)
                    throw new InvalidDataException("Texture exceeds the 32 MB preview limit.");
                data = _bin.AsSpan(start, length).ToArray();
            }
            else if (image.TryGetProperty("uri", out var uriValue))
            {
                var uri = uriValue.GetString() ?? "";
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = uri.IndexOf(',');
                    if (comma > 0 && uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    {
                        if (uri.Length - comma > 45 * 1024 * 1024)
                            throw new InvalidDataException("Data URI texture exceeds the preview limit.");
                        data = Convert.FromBase64String(uri[(comma + 1)..]);
                    }
                }
                else
                {
                    var decoded = Uri.UnescapeDataString(uri);
                    if (Path.IsPathRooted(decoded) || Uri.TryCreate(decoded, UriKind.Absolute, out _))
                        throw new InvalidDataException("External GLB image path is not local to the model folder.");
                    var candidate = Path.GetFullPath(Path.Combine(_folder, decoded.Replace('/', Path.DirectorySeparatorChar)));
                    var prefix = _folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("External GLB image path escapes the model folder.");
                    if (File.Exists(candidate) && new FileInfo(candidate).Length <= 32 * 1024 * 1024)
                        data = File.ReadAllBytes(candidate);
                }
            }
            if (data is null || data.Length > 32 * 1024 * 1024)
            {
                _images[index] = null;
                return null;
            }
            try
            {
                using var stream = new MemoryStream(data);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                _images[index] = bitmap;
                return bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
            {
                _images[index] = null;
                return null;
            }
        }

        private float[] ReadAttribute(int accessorIndex, string type, int componentType)
        {
            var (accessor, start, stride, count, components, componentBytes) = Accessor(accessorIndex);
            if (ReadInt(accessor, "componentType", -1) != componentType ||
                !string.Equals(accessor.GetProperty("type").GetString(), type, StringComparison.Ordinal))
                return [];
            var output = new float[count * components];
            for (var i = 0; i < count; i++)
                for (var c = 0; c < components; c++)
                    output[i * components + c] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_bin.AsSpan(start + i * stride + c * componentBytes, 4)));
            return output;
        }

        private int[] ReadIndices(int accessorIndex)
        {
            var (accessor, start, stride, count, components, _) = Accessor(accessorIndex);
            if (components != 1 || accessor.GetProperty("type").GetString() != "SCALAR")
                throw new InvalidDataException("Indices must have SCALAR type.");
            var componentType = ReadInt(accessor, "componentType", -1);
            var result = new int[count];
            for (var i = 0; i < count; i++)
            {
                var offset = start + i * stride;
                result[i] = componentType switch
                {
                    5121 => _bin[offset],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(_bin.AsSpan(offset, 2)),
                    5125 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_bin.AsSpan(offset, 4))),
                    _ => throw new InvalidDataException("Unsupported index componentType.")
                };
            }
            return result;
        }

        private (JsonElement Accessor, int Start, int Stride, int Count, int Components, int ComponentBytes) Accessor(int index)
        {
            var accessors = GetArray(_root, "accessors");
            if (index < 0 || index >= accessors.GetArrayLength())
                throw new InvalidDataException("Accessor index out of bounds.");
            var accessor = accessors[index];
            if (accessor.TryGetProperty("sparse", out _))
                throw new InvalidDataException("Sparse accessors are not yet supported by the 3D preview.");
            if (!accessor.TryGetProperty("bufferView", out var viewIndex))
                throw new InvalidDataException("Accessor without bufferView is unsupported.");
            var (start, length, declaredStride) = ReadBufferView(viewIndex.GetInt32());
            var type = accessor.GetProperty("type").GetString();
            var comps = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new InvalidDataException("Unsupported accessor element type.") };
            var compType = ReadInt(accessor, "componentType", -1);
            var compBytes = compType switch { 5121 => 1, 5123 => 2, 5125 => 4, 5126 => 4, _ => throw new InvalidDataException("Unsupported accessor component type.") };
            var elementSize = checked(comps * compBytes);
            var stride = declaredStride == 0 ? elementSize : declaredStride;
            var offset = ReadInt(accessor, "byteOffset", 0);
            var count = ReadInt(accessor, "count", 0);
            if (count < 0 || count > MaxVerticesPerPrimitive * 6 || offset < 0 || stride < elementSize ||
                (count > 0 && (long)offset + (long)(count - 1) * stride + elementSize > length))
                throw new InvalidDataException("Accessor data is outside the bufferView bounds.");
            return (accessor, checked(start + offset), stride, count, comps, compBytes);
        }

        private (int Start, int Length, int Stride) ReadBufferView(int index)
        {
            var views = GetArray(_root, "bufferViews");
            if (index < 0 || index >= views.GetArrayLength())
                throw new InvalidDataException("BufferView index out of bounds.");
            var view = views[index];
            if (ReadInt(view, "buffer", -1) != 0)
                throw new InvalidDataException("Only the primary GLB binary buffer is supported.");
            var start = ReadInt(view, "byteOffset", 0);
            var length = ReadInt(view, "byteLength", -1);
            var stride = ReadInt(view, "byteStride", 0);
            if (start < 0 || length < 0 || (long)start + length > _bin.Length)
                throw new InvalidDataException("BufferView is outside GLB binary chunk.");
            return (start, length, stride);
        }

        private static Transform3D ReadNodeTransform(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var matrix) && matrix.ValueKind == JsonValueKind.Array && matrix.GetArrayLength() == 16)
            {
                var v = matrix.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                return new MatrixTransform3D(new Matrix3D(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
                    v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]));
            }
            var group = new Transform3DGroup();
            if (node.TryGetProperty("scale", out var scale) && scale.GetArrayLength() == 3)
            {
                var s = scale.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                group.Children.Add(new ScaleTransform3D(s[0], s[1], s[2]));
            }
            if (node.TryGetProperty("rotation", out var rotation) && rotation.GetArrayLength() == 4)
            {
                var q = rotation.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                group.Children.Add(new RotateTransform3D(new QuaternionRotation3D(new Quaternion(q[0], q[1], q[2], q[3]))));
            }
            if (node.TryGetProperty("translation", out var translation) && translation.GetArrayLength() == 3)
            {
                var t = translation.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                group.Children.Add(new TranslateTransform3D(t[0], t[1], t[2]));
            }
            return group;
        }

        private static JsonElement GetArray(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
                ? value : EmptyArray;
        private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();
        private static int ReadInt(JsonElement element, string propertyName, int fallback) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var i) ? i : fallback;
        private static IEnumerable<int> ReadIntArray(JsonElement element, string propertyName) =>
            GetArray(element, propertyName).EnumerateArray().Select(x => x.GetInt32());
    }
}
