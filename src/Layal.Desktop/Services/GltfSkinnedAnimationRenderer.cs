using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Layal.Services;

/// <summary>
/// Small, self-contained GLB 2.0 skinning renderer for Layal's WPF mascot preview.
/// The reader intentionally supports a validated core subset (one skinned triangle primitive,
/// float TRS animation channels, JOINTS_0/WEIGHTS_0, and embedded GLB buffers) and fails closed
/// for features it cannot render instead of silently displaying a broken mesh.
/// </summary>
public sealed class GltfSkinnedAnimationRenderer : IDisposable
{
    private const string EmbeddedModelName = "Layal.Assets.3D.RiggedFigure.glb";
    private readonly Viewport3D _viewport;
    private readonly GltfAsset _asset;
    private readonly MeshGeometry3D _mesh;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private double _elapsedBeforeClock;
    private bool _disposed;
    private int _renderedFrames;

    public GltfAssetInspection Diagnostics { get; }
    public bool IsPlaying => _timer.IsEnabled;
    public int RenderedFrames => _renderedFrames;
    public string AnimationName => _asset.AnimationName;
    public double AnimationDurationSeconds => _asset.AnimationDurationSeconds;

    private GltfSkinnedAnimationRenderer(Viewport3D viewport, GltfAsset asset, bool autoPlay)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _asset = asset ?? throw new ArgumentNullException(nameof(asset));
        Diagnostics = asset.GetInspection();

        _mesh = new MeshGeometry3D
        {
            TriangleIndices = new Int32Collection(asset.Indices)
        };
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(asset.BaseColor)));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(220, 230, 255)), 24));
        var visual = new ModelVisual3D
        {
            Content = new GeometryModel3D(_mesh, material)
            {
                BackMaterial = material
            }
        };

        _viewport.Children.Clear();
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(118, 126, 146)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(255, 252, 255), new Vector3D(-0.35, -0.55, -1)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(120, 205, 245), new Vector3D(0.65, 0.12, -0.55)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(180, 158, 255), new Vector3D(-0.7, 0.1, 0.35)));
        _viewport.Children.Add(new ModelVisual3D { Content = lights });
        _viewport.Children.Add(visual);

        _timer = new DispatcherTimer(DispatcherPriority.Render, _viewport.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += AnimationTimer_Tick;
        RenderAt(0);
        if (autoPlay)
            Start();
    }

    public static GltfSkinnedAnimationRenderer LoadBundled(Viewport3D viewport, bool autoPlay = true)
    {
        using var stream = OpenBundledModel();
        return new GltfSkinnedAnimationRenderer(viewport, GltfAsset.Load(stream), autoPlay);
    }

    public static Stream OpenBundledModel()
    {
        var stream = typeof(GltfSkinnedAnimationRenderer).Assembly.GetManifestResourceStream(EmbeddedModelName);
        return stream ?? throw new FileNotFoundException(
            $"The bundled GLB asset '{EmbeddedModelName}' is missing from the application assembly.");
    }

    /// <summary>Parses and evaluates the bundled skinned GLB without creating a visual or starting a timer.</summary>
    public static GltfAssetInspection InspectBundledAsset()
    {
        using var stream = OpenBundledModel();
        return GltfAsset.Load(stream).GetInspection();
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_timer.IsEnabled)
            return;
        _clock.Restart();
        _timer.Start();
    }

    public void Stop()
    {
        if (_disposed || !_timer.IsEnabled)
            return;
        _timer.Stop();
        _elapsedBeforeClock += _clock.Elapsed.TotalSeconds;
        _clock.Reset();
    }

    /// <summary>Render an explicit animation timestamp. Exposed for deterministic UI/integration tests.</summary>
    public void RenderAt(double elapsedSeconds)
    {
        ThrowIfDisposed();
        _viewport.Dispatcher.VerifyAccess();

        var vertices = _asset.SkinVertices(elapsedSeconds);
        var positions = new Point3DCollection(vertices.Length);
        var normals = new Vector3DCollection(vertices.Length);
        var skinnedNormals = _asset.SkinNormals(elapsedSeconds);

        for (var index = 0; index < vertices.Length; index++)
        {
            positions.Add(new Point3D(vertices[index].X, vertices[index].Y, vertices[index].Z));
            var normal = skinnedNormals[index];
            normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
        }

        _mesh.Positions = positions;
        _mesh.Normals = normals;
        _renderedFrames++;
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (!_disposed)
            RenderAt(_elapsedBeforeClock + _clock.Elapsed.TotalSeconds);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GltfSkinnedAnimationRenderer));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _timer.Tick -= AnimationTimer_Tick;
        _viewport.Dispatcher.VerifyAccess();
        _viewport.Children.Clear();
        _disposed = true;
    }

    public sealed record GltfAssetInspection(
        int NodeCount,
        int VertexCount,
        int TriangleCount,
        int JointCount,
        int AnimationCount,
        int AnimationChannelCount,
        double MaximumAnimatedVertexDisplacement);

    private sealed class GltfAsset
    {
        private readonly byte[] _binary;
        private readonly BufferView[] _views;
        private readonly Accessor[] _accessors;
        private readonly Node[] _nodes;
        private readonly int _meshNodeIndex;
        private readonly int[] _jointNodes;
        private readonly Matrix4x4[] _inverseBindMatrices;
        private readonly Vector3[] _sourcePositions;
        private readonly Vector3[] _sourceNormals;
        private readonly int[] _jointIndices;
        private readonly float[] _jointWeights;
        private readonly Animation[] _animations;
        private readonly Matrix4x4 _fitTransform;
        private readonly Matrix4x4 _axisRotation;

        public int[] Indices { get; }
        public Color BaseColor { get; }
        public string AnimationName => _animations.Length == 0 ? "Static pose" : _animations[0].Name;
        public double AnimationDurationSeconds => _animations.Length == 0 ? 0 : _animations[0].Duration;

        private GltfAsset(
            byte[] binary,
            BufferView[] views,
            Accessor[] accessors,
            Node[] nodes,
            int meshNodeIndex,
            int[] jointNodes,
            Matrix4x4[] inverseBindMatrices,
            Vector3[] sourcePositions,
            Vector3[] sourceNormals,
            int[] jointIndices,
            float[] jointWeights,
            int[] indices,
            Animation[] animations,
            Color baseColor)
        {
            _binary = binary;
            _views = views;
            _accessors = accessors;
            _nodes = nodes;
            _meshNodeIndex = meshNodeIndex;
            _jointNodes = jointNodes;
            _inverseBindMatrices = inverseBindMatrices;
            _sourcePositions = sourcePositions;
            _sourceNormals = sourceNormals;
            _jointIndices = jointIndices;
            _jointWeights = jointWeights;
            Indices = indices;
            _animations = animations;
            BaseColor = baseColor;
            (_axisRotation, _fitTransform) = CreateFitTransform(sourcePositions);
        }

        public static GltfAsset Load(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var file = memory.ToArray();
            if (file.Length < 20)
                throw new InvalidDataException("The GLB file is too short to contain a valid header.");
            if (ReadUInt32(file, 0) != 0x46546C67)
                throw new InvalidDataException("The asset is not a binary glTF (GLB) file.");
            if (ReadUInt32(file, 4) != 2)
                throw new NotSupportedException("Only GLB 2.0 is supported.");
            if (ReadUInt32(file, 8) != file.Length)
                throw new InvalidDataException("The GLB header length does not match the file size.");

            var jsonLength = checked((int)ReadUInt32(file, 12));
            if (ReadUInt32(file, 16) != 0x4E4F534A || jsonLength < 2 || 20 + jsonLength > file.Length)
                throw new InvalidDataException("The GLB JSON chunk is missing or malformed.");

            using var document = JsonDocument.Parse(file.AsMemory(20, jsonLength));
            var root = document.RootElement;
            var binaryOffset = 20 + jsonLength;
            byte[] binary = Array.Empty<byte>();
            if (binaryOffset + 8 <= file.Length)
            {
                var binaryLength = checked((int)ReadUInt32(file, binaryOffset));
                var binaryType = ReadUInt32(file, binaryOffset + 4);
                if (binaryType != 0x004E4942 || binaryLength < 0 || binaryOffset + 8L + binaryLength > file.Length)
                    throw new InvalidDataException("The GLB binary chunk is malformed.");
                binary = file.AsSpan(binaryOffset + 8, binaryLength).ToArray();
            }
            if (binary.Length == 0)
                throw new NotSupportedException("This renderer requires a GLB with its geometry buffers embedded.");

            if (TryGetArray(root, "buffers", out var buffers))
            {
                foreach (var buffer in buffers.EnumerateArray())
                {
                    if (TryGetString(buffer, "uri", out _))
                        throw new NotSupportedException("External .gltf buffer URIs are not supported; use a self-contained GLB.");
                }
            }

            var views = ReadViews(root);
            var accessors = ReadAccessors(root);
            var nodes = ReadNodes(root);
            BuildParents(nodes);

            var meshNodeIndex = -1;
            var meshIndex = -1;
            var skinIndex = -1;
            for (var i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].MeshIndex >= 0 && nodes[i].SkinIndex >= 0)
                {
                    meshNodeIndex = i;
                    meshIndex = nodes[i].MeshIndex;
                    skinIndex = nodes[i].SkinIndex;
                    break;
                }
            }
            if (meshNodeIndex < 0)
                throw new NotSupportedException("The GLB has no node that references both a mesh and a skin.");

            var meshes = GetRequiredArray(root, "meshes");
            if (meshIndex >= meshes.GetArrayLength())
                throw new InvalidDataException("The skinned node references a missing mesh.");
            var primitives = GetRequiredArray(meshes[meshIndex], "primitives");
            if (primitives.GetArrayLength() != 1)
                throw new NotSupportedException("This preview renderer currently requires one skinned triangle primitive.");

            var primitive = primitives[0];
            var mode = GetOptionalInt(primitive, "mode", 4);
            if (mode != 4)
                throw new NotSupportedException("Only triangle-list glTF primitives are supported.");

            var attributes = GetRequiredElement(primitive, "attributes");
            var positions = ReadVector3(ReadFloatAccessor(root, views, accessors, GetRequiredInt(attributes, "POSITION"), binary, 3));
            var normals = ReadVector3(ReadFloatAccessor(root, views, accessors, GetRequiredInt(attributes, "NORMAL"), binary, 3));
            var joints = ReadUnsignedAccessor(root, views, accessors, GetRequiredInt(attributes, "JOINTS_0"), binary, 4);
            var weights = ReadFloatAccessor(root, views, accessors, GetRequiredInt(attributes, "WEIGHTS_0"), binary, 4);
            var indexAccessor = GetOptionalInt(primitive, "indices", -1);
            if (indexAccessor < 0)
                throw new NotSupportedException("The skinned primitive must provide an index accessor.");
            var rawIndices = ReadUnsignedAccessor(root, views, accessors, indexAccessor, binary, 1);
            var indices = new int[rawIndices.Length];
            for (var i = 0; i < rawIndices.Length; i++)
            {
                if (rawIndices[i] >= positions.Length)
                    throw new InvalidDataException("The mesh contains an index outside the vertex array.");
                indices[i] = checked((int)rawIndices[i]);
            }

            if (normals.Length != positions.Length || joints.Length != positions.Length * 4 || weights.Length != positions.Length * 4)
                throw new InvalidDataException("The skinned primitive's POSITION/NORMAL/JOINTS_0/WEIGHTS_0 attributes have inconsistent vertex counts.");

            var skins = GetRequiredArray(root, "skins");
            if (skinIndex >= skins.GetArrayLength())
                throw new InvalidDataException("The skinned node references a missing skin.");
            var skin = skins[skinIndex];
            var jointNodes = ReadIntArray(GetRequiredElement(skin, "joints"));
            if (jointNodes.Length == 0)
                throw new InvalidDataException("The skin contains no joints.");

            var inverseBind = new Matrix4x4[jointNodes.Length];
            var inverseAccessor = GetOptionalInt(skin, "inverseBindMatrices", -1);
            if (inverseAccessor >= 0)
            {
                var values = ReadFloatAccessor(root, views, accessors, inverseAccessor, binary, 16);
                if (values.Length != jointNodes.Length * 16)
                    throw new InvalidDataException("The inverse-bind matrix count does not match the skin's joint count.");
                for (var i = 0; i < inverseBind.Length; i++)
                    inverseBind[i] = ReadGltfMatrix(values, i * 16);
            }
            else
            {
                Array.Fill(inverseBind, Matrix4x4.Identity);
            }

            foreach (var joint in jointNodes)
            {
                if ((uint)joint >= (uint)nodes.Length)
                    throw new InvalidDataException("The skin references a joint that does not exist.");
            }
            foreach (var joint in joints)
            {
                if (joint >= jointNodes.Length)
                    throw new InvalidDataException("A vertex references a joint outside the skin.");
            }

            var animations = ReadAnimations(root, views, accessors, binary);
            var baseColor = ReadBaseColor(root, primitive);
            return new GltfAsset(
                binary, views, accessors, nodes, meshNodeIndex, jointNodes, inverseBind,
                positions, normals, ConvertToInt(joints), weights, indices, animations, baseColor);
        }

        public GltfAssetInspection GetInspection()
        {
            var maximumDisplacement = 0d;
            if (_animations.Length > 0 && _animations[0].Duration > 0)
            {
                var baseFrame = SkinVertices(0);
                var movedFrame = SkinVertices(_animations[0].Duration * 0.41);
                for (var i = 0; i < baseFrame.Length; i++)
                    maximumDisplacement = Math.Max(maximumDisplacement, Vector3.Distance(baseFrame[i], movedFrame[i]));
            }

            var channels = 0;
            foreach (var animation in _animations)
                channels += animation.Channels.Length;

            return new GltfAssetInspection(
                _nodes.Length,
                _sourcePositions.Length,
                Indices.Length / 3,
                _jointNodes.Length,
                _animations.Length,
                channels,
                maximumDisplacement);
        }

        public Vector3[] SkinVertices(double elapsedSeconds)
        {
            var skinMatrices = GetSkinMatrices(elapsedSeconds);
            var result = new Vector3[_sourcePositions.Length];
            for (var vertex = 0; vertex < result.Length; vertex++)
            {
                var position = Vector3.Zero;
                var totalWeight = 0f;
                for (var influence = 0; influence < 4; influence++)
                {
                    var slot = vertex * 4 + influence;
                    var weight = _jointWeights[slot];
                    if (weight <= 0)
                        continue;
                    var joint = _jointIndices[slot];
                    position += Vector3.Transform(_sourcePositions[vertex], skinMatrices[joint]) * weight;
                    totalWeight += weight;
                }
                if (totalWeight > 1e-6f && Math.Abs(totalWeight - 1f) > 1e-4f)
                    position /= totalWeight;
                result[vertex] = Vector3.Transform(position, _fitTransform);
            }
            return result;
        }

        public Vector3[] SkinNormals(double elapsedSeconds)
        {
            var skinMatrices = GetSkinMatrices(elapsedSeconds);
            var result = new Vector3[_sourceNormals.Length];
            for (var vertex = 0; vertex < result.Length; vertex++)
            {
                var normal = Vector3.Zero;
                var totalWeight = 0f;
                for (var influence = 0; influence < 4; influence++)
                {
                    var slot = vertex * 4 + influence;
                    var weight = _jointWeights[slot];
                    if (weight <= 0)
                        continue;
                    var joint = _jointIndices[slot];
                    normal += Vector3.TransformNormal(_sourceNormals[vertex], skinMatrices[joint]) * weight;
                    totalWeight += weight;
                }
                if (totalWeight > 1e-6f && normal.LengthSquared() > 1e-10f)
                    normal = Vector3.Normalize(normal);
                else
                    normal = Vector3.UnitZ;
                normal = Vector3.TransformNormal(normal, _axisRotation);
                result[vertex] = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            }
            return result;
        }

        private Matrix4x4[] GetSkinMatrices(double elapsedSeconds)
        {
            var translations = new Vector3[_nodes.Length];
            var rotations = new Quaternion[_nodes.Length];
            var scales = new Vector3[_nodes.Length];
            for (var i = 0; i < _nodes.Length; i++)
            {
                translations[i] = _nodes[i].Translation;
                rotations[i] = _nodes[i].Rotation;
                scales[i] = _nodes[i].Scale;
            }

            if (_animations.Length > 0)
            {
                var animation = _animations[0];
                var time = animation.StartTime;
                if (animation.Duration > 0)
                {
                    var wrapped = elapsedSeconds % animation.Duration;
                    if (wrapped < 0)
                        wrapped += animation.Duration;
                    time += wrapped;
                }

                foreach (var channel in animation.Channels)
                {
                    var sampled = Sample(animation.Samplers[channel.SamplerIndex], time, channel.Path);
                    switch (channel.Path)
                    {
                        case "translation":
                            translations[channel.NodeIndex] = new Vector3(sampled[0], sampled[1], sampled[2]);
                            break;
                        case "rotation":
                            rotations[channel.NodeIndex] = Quaternion.Normalize(new Quaternion(sampled[0], sampled[1], sampled[2], sampled[3]));
                            break;
                        case "scale":
                            scales[channel.NodeIndex] = new Vector3(sampled[0], sampled[1], sampled[2]);
                            break;
                    }
                }
            }

            var world = new Matrix4x4[_nodes.Length];
            var state = new byte[_nodes.Length];
            for (var i = 0; i < world.Length; i++)
                ComputeWorld(i, translations, rotations, scales, world, state);

            if (!Matrix4x4.Invert(world[_meshNodeIndex], out var inverseMeshWorld))
                throw new InvalidDataException("The skinned mesh node transform is not invertible.");

            var matrices = new Matrix4x4[_jointNodes.Length];
            for (var i = 0; i < matrices.Length; i++)
                matrices[i] = _inverseBindMatrices[i] * world[_jointNodes[i]] * inverseMeshWorld;
            return matrices;
        }

        private void ComputeWorld(
            int index,
            Vector3[] translations,
            Quaternion[] rotations,
            Vector3[] scales,
            Matrix4x4[] world,
            byte[] state)
        {
            if (state[index] == 2)
                return;
            if (state[index] == 1)
                throw new InvalidDataException("The glTF node hierarchy contains a cycle.");
            state[index] = 1;

            var node = _nodes[index];
            if (node.Matrix.HasValue)
            {
                if (_animations.Length > 0 && _animations[0].Channels.Length > 0 &&
                    node.Matrix.HasValue && (translations[index] != node.Translation ||
                                             rotations[index] != node.Rotation ||
                                             scales[index] != node.Scale))
                    throw new NotSupportedException("Animated glTF nodes must use TRS properties, not a fixed matrix.");
                world[index] = node.Matrix.Value;
            }
            else
            {
                var rotation = rotations[index].LengthSquared() < 1e-12f
                    ? Quaternion.Identity
                    : Quaternion.Normalize(rotations[index]);
                var local = Matrix4x4.CreateScale(scales[index]) *
                            Matrix4x4.CreateFromQuaternion(rotation) *
                            Matrix4x4.CreateTranslation(translations[index]);
                world[index] = node.ParentIndex >= 0
                    ? local * GetWorldParent(node.ParentIndex, translations, rotations, scales, world, state)
                    : local;
            }

            state[index] = 2;
        }

        private Matrix4x4 GetWorldParent(
            int parentIndex,
            Vector3[] translations,
            Quaternion[] rotations,
            Vector3[] scales,
            Matrix4x4[] world,
            byte[] state)
        {
            ComputeWorld(parentIndex, translations, rotations, scales, world, state);
            return world[parentIndex];
        }

        private static float[] Sample(AnimationSampler sampler, float time, string path)
        {
            var count = sampler.Inputs.Length;
            if (count == 0)
                throw new InvalidDataException("An animation sampler has no keyframes.");
            if (count == 1 || time <= sampler.Inputs[0])
                return CopyOutput(sampler, 0);
            if (time >= sampler.Inputs[^1])
                return CopyOutput(sampler, count - 1);

            var right = 1;
            while (right < count && sampler.Inputs[right] < time)
                right++;
            var left = right - 1;
            var span = sampler.Inputs[right] - sampler.Inputs[left];
            var fraction = span <= 1e-8f ? 0f : Math.Clamp((time - sampler.Inputs[left]) / span, 0f, 1f);
            if (sampler.Interpolation == "STEP")
                fraction = 0f;

            var leftValue = CopyOutput(sampler, left);
            var rightValue = CopyOutput(sampler, right);
            if (path == "rotation")
            {
                if (leftValue.Length != 4 || rightValue.Length != 4)
                    throw new InvalidDataException("A rotation animation output must be a quaternion.");
                var a = Quaternion.Normalize(new Quaternion(leftValue[0], leftValue[1], leftValue[2], leftValue[3]));
                var b = Quaternion.Normalize(new Quaternion(rightValue[0], rightValue[1], rightValue[2], rightValue[3]));
                if (Quaternion.Dot(a, b) < 0)
                    b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
                var q = Quaternion.Normalize(Quaternion.Slerp(a, b, fraction));
                return new[] { q.X, q.Y, q.Z, q.W };
            }

            var result = new float[leftValue.Length];
            for (var i = 0; i < result.Length; i++)
                result[i] = leftValue[i] + (rightValue[i] - leftValue[i]) * fraction;
            return result;
        }

        private static float[] CopyOutput(AnimationSampler sampler, int key)
        {
            var components = sampler.OutputComponents;
            var values = new float[components];
            Array.Copy(sampler.Outputs, key * components, values, 0, components);
            return values;
        }

        private static (Matrix4x4 Axis, Matrix4x4 Fit) CreateFitTransform(Vector3[] positions)
        {
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            foreach (var position in positions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            var range = max - min;
            var axis = range.Z > range.Y * 1.25f
                ? Matrix4x4.CreateRotationX(-MathF.PI / 2f)
                : range.X > range.Y * 1.25f
                    ? Matrix4x4.CreateRotationZ(MathF.PI / 2f)
                    : Matrix4x4.Identity;

            min = new Vector3(float.PositiveInfinity);
            max = new Vector3(float.NegativeInfinity);
            foreach (var position in positions)
            {
                var rotated = Vector3.Transform(position, axis);
                min = Vector3.Min(min, rotated);
                max = Vector3.Max(max, rotated);
            }

            var center = (min + max) * 0.5f;
            var height = Math.Max(0.001f, max.Y - min.Y);
            var scale = 2.16f / height;
            var translation = new Vector3(-center.X * scale, -center.Y * scale, -center.Z * scale);
            var fit = axis * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(translation);
            return (axis, fit);
        }

        private static BufferView[] ReadViews(JsonElement root)
        {
            var array = GetRequiredArray(root, "bufferViews");
            var result = new BufferView[array.GetArrayLength()];
            for (var i = 0; i < result.Length; i++)
            {
                var view = array[i];
                result[i] = new BufferView(
                    GetOptionalInt(view, "byteOffset", 0),
                    GetRequiredInt(view, "byteLength"),
                    GetOptionalInt(view, "byteStride", 0),
                    GetOptionalInt(view, "buffer", 0));
                if (result[i].BufferIndex != 0)
                    throw new NotSupportedException("Only one embedded GLB buffer is supported.");
            }
            return result;
        }

        private static Accessor[] ReadAccessors(JsonElement root)
        {
            var array = GetRequiredArray(root, "accessors");
            var result = new Accessor[array.GetArrayLength()];
            for (var i = 0; i < result.Length; i++)
            {
                var accessor = array[i];
                if (accessor.TryGetProperty("sparse", out _))
                    throw new NotSupportedException("Sparse glTF accessors are not supported by the preview renderer.");
                result[i] = new Accessor(
                    GetRequiredInt(accessor, "bufferView"),
                    GetOptionalInt(accessor, "byteOffset", 0),
                    GetRequiredInt(accessor, "componentType"),
                    GetRequiredInt(accessor, "count"),
                    GetComponents(GetRequiredString(accessor, "type")));
            }
            return result;
        }

        private static Node[] ReadNodes(JsonElement root)
        {
            var array = GetRequiredArray(root, "nodes");
            var nodes = new Node[array.GetArrayLength()];
            for (var i = 0; i < nodes.Length; i++)
            {
                var element = array[i];
                var translation = ReadJsonVector3(element, "translation", Vector3.Zero);
                var scale = ReadJsonVector3(element, "scale", Vector3.One);
                var rotation = ReadJsonQuaternion(element, "rotation", Quaternion.Identity);
                Matrix4x4? matrix = null;
                if (element.TryGetProperty("matrix", out var matrixElement))
                {
                    var values = ReadJsonFloatArray(matrixElement);
                    if (values.Length != 16)
                        throw new InvalidDataException("A glTF node matrix must contain 16 values.");
                    matrix = ReadGltfMatrix(values, 0);
                }

                nodes[i] = new Node(
                    GetOptionalString(element, "name", $"node-{i}"),
                    GetOptionalInt(element, "mesh", -1),
                    GetOptionalInt(element, "skin", -1),
                    ReadOptionalIntArray(element, "children"),
                    translation,
                    rotation,
                    scale,
                    matrix);
            }
            return nodes;
        }

        private static void BuildParents(Node[] nodes)
        {
            for (var parent = 0; parent < nodes.Length; parent++)
            {
                foreach (var child in nodes[parent].Children)
                {
                    if ((uint)child >= (uint)nodes.Length)
                        throw new InvalidDataException("A glTF node references a missing child.");
                    if (nodes[child].ParentIndex >= 0)
                        throw new InvalidDataException("A glTF node has more than one parent.");
                    nodes[child].ParentIndex = parent;
                }
            }
        }

        private static Animation[] ReadAnimations(JsonElement root, BufferView[] views, Accessor[] accessors, byte[] binary)
        {
            if (!TryGetArray(root, "animations", out var array))
                return Array.Empty<Animation>();

            var animations = new Animation[array.GetArrayLength()];
            for (var a = 0; a < animations.Length; a++)
            {
                var animationElement = array[a];
                var samplerElements = GetRequiredArray(animationElement, "samplers");
                var samplers = new AnimationSampler[samplerElements.GetArrayLength()];
                for (var s = 0; s < samplers.Length; s++)
                {
                    var samplerElement = samplerElements[s];
                    var interpolation = GetOptionalString(samplerElement, "interpolation", "LINEAR");
                    if (interpolation is not ("LINEAR" or "STEP"))
                        throw new NotSupportedException($"Animation interpolation '{interpolation}' is not supported.");
                    var inputs = ReadFloatAccessor(root, views, accessors, GetRequiredInt(samplerElement, "input"), binary, 1);
                    var outputAccessorIndex = GetRequiredInt(samplerElement, "output");
                    var outputComponents = accessors[outputAccessorIndex].Components;
                    var outputs = ReadFloatAccessor(root, views, accessors, outputAccessorIndex, binary, outputComponents);
                    if (inputs.Length == 0 || outputs.Length != inputs.Length * outputComponents)
                        throw new InvalidDataException("An animation sampler has inconsistent input and output key counts.");
                    for (var i = 1; i < inputs.Length; i++)
                    {
                        if (inputs[i] < inputs[i - 1])
                            throw new InvalidDataException("Animation key times must be sorted in ascending order.");
                    }
                    samplers[s] = new AnimationSampler(inputs, outputs, outputComponents, interpolation);
                }

                var channelElements = GetRequiredArray(animationElement, "channels");
                var channels = new AnimationChannel[channelElements.GetArrayLength()];
                for (var c = 0; c < channels.Length; c++)
                {
                    var channel = channelElements[c];
                    var target = GetRequiredElement(channel, "target");
                    var path = GetRequiredString(target, "path");
                    if (path is not ("translation" or "rotation" or "scale"))
                        throw new NotSupportedException($"Animation target path '{path}' is not supported.");
                    var nodeIndex = GetRequiredInt(target, "node");
                    if ((uint)nodeIndex >= (uint)GetRequiredArray(root, "nodes").GetArrayLength())
                        throw new InvalidDataException("An animation targets a missing node.");
                    channels[c] = new AnimationChannel(nodeIndex, GetRequiredInt(channel, "sampler"), path);
                    if ((uint)channels[c].SamplerIndex >= (uint)samplers.Length)
                        throw new InvalidDataException("An animation channel references a missing sampler.");
                    if (path == "rotation" && samplers[channels[c].SamplerIndex].OutputComponents != 4 ||
                        path != "rotation" && samplers[channels[c].SamplerIndex].OutputComponents != 3)
                        throw new InvalidDataException("The animation output component count does not match its target path.");
                }

                var start = float.PositiveInfinity;
                var end = float.NegativeInfinity;
                foreach (var sampler in samplers)
                {
                    start = Math.Min(start, sampler.Inputs[0]);
                    end = Math.Max(end, sampler.Inputs[^1]);
                }
                if (!float.IsFinite(start) || !float.IsFinite(end))
                    start = end = 0;
                animations[a] = new Animation(
                    GetOptionalString(animationElement, "name", $"animation-{a + 1}"),
                    samplers,
                    channels,
                    start,
                    end);
            }
            return animations;
        }

        private static Color ReadBaseColor(JsonElement root, JsonElement primitive)
        {
            var materialIndex = GetOptionalInt(primitive, "material", -1);
            if (materialIndex < 0 || !TryGetArray(root, "materials", out var materials) || materialIndex >= materials.GetArrayLength())
                return Color.FromRgb(205, 211, 224);
            var material = materials[materialIndex];
            if (!material.TryGetProperty("pbrMetallicRoughness", out var pbr) ||
                !pbr.TryGetProperty("baseColorFactor", out var factor) ||
                factor.ValueKind != JsonValueKind.Array || factor.GetArrayLength() != 4)
                return Color.FromRgb(205, 211, 224);
            var values = ReadJsonFloatArray(factor);
            return Color.FromArgb(ToByte(values[3]), ToByte(values[0]), ToByte(values[1]), ToByte(values[2]));
        }

        private static float[] ReadFloatAccessor(
            JsonElement root, BufferView[] views, Accessor[] accessors, int index, byte[] binary, int expectedComponents)
        {
            if ((uint)index >= (uint)accessors.Length)
                throw new InvalidDataException("A glTF attribute references a missing accessor.");
            var accessor = accessors[index];
            if (accessor.ComponentType != 5126)
                throw new NotSupportedException("This mesh reader expects float values for this accessor.");
            if (expectedComponents > 0 && accessor.Components != expectedComponents)
                throw new InvalidDataException("A glTF attribute has an unexpected component count.");
            var view = views[accessor.BufferViewIndex];
            var stride = view.ByteStride == 0 ? accessor.Components * 4 : view.ByteStride;
            var first = checked(view.ByteOffset + accessor.ByteOffset);
            var result = new float[checked(accessor.Count * accessor.Components)];
            var last = first + (long)Math.Max(0, accessor.Count - 1) * stride + accessor.Components * 4L;
            if (last > view.ByteOffset + (long)view.ByteLength || last > binary.Length)
                throw new InvalidDataException("A float accessor reads beyond its buffer view.");
            for (var row = 0; row < accessor.Count; row++)
            {
                var rowOffset = first + row * stride;
                for (var component = 0; component < accessor.Components; component++)
                    result[row * accessor.Components + component] =
                        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(binary.AsSpan(rowOffset + component * 4, 4)));
            }
            return result;
        }

        private static uint[] ReadUnsignedAccessor(
            JsonElement root, BufferView[] views, Accessor[] accessors, int index, byte[] binary, int expectedComponents)
        {
            if ((uint)index >= (uint)accessors.Length)
                throw new InvalidDataException("A glTF attribute references a missing accessor.");
            var accessor = accessors[index];
            var componentBytes = accessor.ComponentType switch
            {
                5121 => 1,
                5123 => 2,
                5125 => 4,
                _ => throw new NotSupportedException($"Unsigned accessor component type {accessor.ComponentType} is not supported.")
            };
            if (expectedComponents > 0 && accessor.Components != expectedComponents)
                throw new InvalidDataException("A glTF index/skin attribute has an unexpected component count.");
            var view = views[accessor.BufferViewIndex];
            var stride = view.ByteStride == 0 ? accessor.Components * componentBytes : view.ByteStride;
            var first = checked(view.ByteOffset + accessor.ByteOffset);
            var result = new uint[checked(accessor.Count * accessor.Components)];
            var last = first + (long)Math.Max(0, accessor.Count - 1) * stride + accessor.Components * componentBytes;
            if (last > view.ByteOffset + (long)view.ByteLength || last > binary.Length)
                throw new InvalidDataException("An unsigned accessor reads beyond its buffer view.");
            for (var row = 0; row < accessor.Count; row++)
            {
                var rowOffset = first + row * stride;
                for (var component = 0; component < accessor.Components; component++)
                {
                    var offset = rowOffset + component * componentBytes;
                    result[row * accessor.Components + component] = componentBytes switch
                    {
                        1 => binary[offset],
                        2 => BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(offset, 2)),
                        _ => BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(offset, 4))
                    };
                }
            }
            return result;
        }

        private static Vector3[] ReadVector3(float[] values)
        {
            if (values.Length % 3 != 0)
                throw new InvalidDataException("A VEC3 accessor's value count is not divisible by three.");
            var result = new Vector3[values.Length / 3];
            for (var i = 0; i < result.Length; i++)
                result[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            return result;
        }

        private static Vector3 ReadJsonVector3(JsonElement element, string property, Vector3 fallback)
        {
            if (!element.TryGetProperty(property, out var vector))
                return fallback;
            var values = ReadJsonFloatArray(vector);
            if (values.Length != 3)
                throw new InvalidDataException($"Node property '{property}' must contain three values.");
            return new Vector3(values[0], values[1], values[2]);
        }

        private static Quaternion ReadJsonQuaternion(JsonElement element, string property, Quaternion fallback)
        {
            if (!element.TryGetProperty(property, out var quaternion))
                return fallback;
            var values = ReadJsonFloatArray(quaternion);
            if (values.Length != 4)
                throw new InvalidDataException($"Node property '{property}' must contain four values.");
            return Quaternion.Normalize(new Quaternion(values[0], values[1], values[2], values[3]));
        }

        private static float[] ReadJsonFloatArray(JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Expected a JSON numeric array.");
            var values = new float[array.GetArrayLength()];
            var index = 0;
            foreach (var item in array.EnumerateArray())
                values[index++] = item.GetSingle();
            return values;
        }

        private static Matrix4x4 ReadGltfMatrix(float[] values, int offset) =>
            new(
                values[offset], values[offset + 4], values[offset + 8], values[offset + 12],
                values[offset + 1], values[offset + 5], values[offset + 9], values[offset + 13],
                values[offset + 2], values[offset + 6], values[offset + 10], values[offset + 14],
                values[offset + 3], values[offset + 7], values[offset + 11], values[offset + 15]);

        private static int[] ConvertToInt(uint[] values)
        {
            var result = new int[values.Length];
            for (var i = 0; i < values.Length; i++)
                result[i] = checked((int)values[i]);
            return result;
        }

        private static int GetComponents(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new NotSupportedException($"glTF accessor type '{type}' is not supported.")
        };

        private static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

        private static byte ToByte(float value) =>
            (byte)Math.Clamp((int)Math.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

        private static bool TryGetArray(JsonElement element, string property, out JsonElement array)
        {
            if (element.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array)
                return true;
            array = default;
            return false;
        }

        private static JsonElement GetRequiredArray(JsonElement element, string property)
        {
            if (!TryGetArray(element, property, out var array))
                throw new InvalidDataException($"The GLB is missing required array '{property}'.");
            return array;
        }

        private static JsonElement GetRequiredElement(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
                throw new InvalidDataException($"The GLB is missing required property '{property}'.");
            return value;
        }

        private static int GetRequiredInt(JsonElement element, string property) =>
            GetRequiredElement(element, property).GetInt32();

        private static int GetOptionalInt(JsonElement element, string property, int fallback) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : fallback;

        private static string GetRequiredString(JsonElement element, string property) =>
            GetRequiredElement(element, property).GetString()
            ?? throw new InvalidDataException($"Required string property '{property}' is null.");

        private static string GetOptionalString(JsonElement element, string property, string fallback) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        private static bool TryGetString(JsonElement element, string property, out string value)
        {
            if (element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String)
            {
                value = item.GetString() ?? string.Empty;
                return true;
            }
            value = string.Empty;
            return false;
        }

        private static int[] ReadOptionalIntArray(JsonElement element, string property) =>
            element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
                ? ReadIntArray(array)
                : Array.Empty<int>();

        private static int[] ReadIntArray(JsonElement array)
        {
            var result = new int[array.GetArrayLength()];
            var index = 0;
            foreach (var value in array.EnumerateArray())
                result[index++] = value.GetInt32();
            return result;
        }

        private sealed record BufferView(int ByteOffset, int ByteLength, int ByteStride, int BufferIndex);
        private sealed record Accessor(int BufferViewIndex, int ByteOffset, int ComponentType, int Count, int Components);

        private sealed class Node
        {
            public string Name { get; }
            public int MeshIndex { get; }
            public int SkinIndex { get; }
            public int[] Children { get; }
            public Vector3 Translation { get; }
            public Quaternion Rotation { get; }
            public Vector3 Scale { get; }
            public Matrix4x4? Matrix { get; }
            public int ParentIndex { get; set; } = -1;

            public Node(string name, int meshIndex, int skinIndex, int[] children,
                Vector3 translation, Quaternion rotation, Vector3 scale, Matrix4x4? matrix)
            {
                Name = name;
                MeshIndex = meshIndex;
                SkinIndex = skinIndex;
                Children = children;
                Translation = translation;
                Rotation = rotation;
                Scale = scale;
                Matrix = matrix;
            }
        }

        private sealed record AnimationSampler(float[] Inputs, float[] Outputs, int OutputComponents, string Interpolation);
        private sealed record AnimationChannel(int NodeIndex, int SamplerIndex, string Path);

        private sealed class Animation
        {
            public string Name { get; }
            public AnimationSampler[] Samplers { get; }
            public AnimationChannel[] Channels { get; }
            public float StartTime { get; }
            public float EndTime { get; }
            public float Duration => Math.Max(0, EndTime - StartTime);

            public Animation(string name, AnimationSampler[] samplers, AnimationChannel[] channels, float startTime, float endTime)
            {
                Name = name;
                Samplers = samplers;
                Channels = channels;
                StartTime = startTime;
                EndTime = endTime;
            }
        }
    }
}
