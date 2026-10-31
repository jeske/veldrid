using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Veldrid
{
    /// <summary>Names the .vdshader bundle a <see cref="ShaderPipelineShape"/> binds to - documentation turned into checked data. <c>Bind</c> asserts the bundle's shaderName equals it.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ShaderBundleAttribute(string bundleName) : Attribute
    {
        /// <summary>The bundle's shaderName (bare, no prefix).</summary>
        public string BundleName { get; } = bundleName;
    }

    /// <summary>On a <see cref="VertexInput{TVertex}"/> field: the buffer advances per instance (step rate 1), not per vertex.</summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class PerInstanceAttribute : Attribute { }

    /// <summary>On a vertex struct field: the GPU format when it is not the default for the C# type (e.g. <see cref="VertexElementFormat.Byte4_Norm"/> for a packed colour).</summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class VertexFormatAttribute(VertexElementFormat format) : Attribute
    {
        /// <summary>The format the buffer holds for this field.</summary>
        public VertexElementFormat Format { get; } = format;
    }

    // ───────────────────────────────── slots: the ONLY handles renderer code touches ─────────────────────────────────

    /// <summary>A shader-visible resource slot owned by a shape. Its NAME is the C# field name, which is the GLSL name (D10).</summary>
    public abstract class ShapeSlot
    {
        /// <summary>GLSL resource name == C# field name.</summary>
        public string Name { get; internal set; } = "";
        /// <summary>Descriptor set, from the bundle.</summary>
        public uint Set { get; internal set; }
        /// <summary>Binding within the set, from the bundle.</summary>
        public uint Binding { get; internal set; }
        /// <summary>The bound resource, or null until <c>Set</c>.</summary>
        internal IBindableResource Bound { get; set; }
        internal abstract ResourceKind[] AcceptedKinds { get; }
        internal void MarkChanged()
        {
            if (Owner is null) throw new InvalidOperationException("Slot was not created through ShaderPipelineShape.Bind.");
            Owner.EnsureBound();
            Owner.InvalidateSet(Set);
        }
        internal ShaderPipelineShape Owner { get; set; }
        internal bool UsesDynamicUniform { get; set; }
        internal uint DynamicUniformOffset { get; set; }

        internal void SetResource(IBindableResource resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            RequireLiveResource(resource);
            MarkChanged();
            Bound = resource;
        }

        internal static void RequireLiveResource(IBindableResource resource)
        {
            bool isDisposed = resource switch
            {
                Texture t => t.IsDisposed,
                TextureView v => v.IsDisposed,
                Sampler s => s.IsDisposed,
                DeviceBuffer b => b.IsDisposed,
                DeviceBufferRange range => range.Buffer.IsDisposed,
                _ => false,
            };
            if (isDisposed)
                throw new ObjectDisposedException((resource as IDeviceResource)?.Name ?? resource.GetType().Name);
            if (resource is TextureView view && view.Target.IsDisposed)
                throw new ObjectDisposedException(view.Target.Name ?? "texture view target");
        }
    }

    /// <summary>A <c>uniform Block { ... }</c>: owns its buffer; written only through typed <see cref="Update"/> / <see cref="UpdateMember{TMember}"/>.</summary>
    public sealed class UniformBlock<T> : ShapeSlot, IDisposable where T : unmanaged
    {
        internal DeviceBuffer Buffer { get; set; }
        internal VdShaderUniformBlock Compiled { get; set; }
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.UniformBuffer];

        public ShaderUniformArena<T> CreateArena(GraphicsDevice device, uint capacity)
        {
            if (Owner is null) throw new InvalidOperationException("Unbound uniform block.");
            Owner.EnsureBound();
            if (!UsesDynamicUniform) throw new InvalidOperationException("Declare this field with [DynamicUniform] before using an arena.");
            if (!ReferenceEquals(device.ResourceFactory, Owner.BoundFactory)) throw new InvalidOperationException("Uniform arena device differs from shape device.");
            return new ShaderUniformArena<T>(device, Name, Compiled.SizeBytes, capacity);
        }

        public void Select(ShaderUniformArena<T> arena, UniformArenaElement element)
        {
            ArgumentNullException.ThrowIfNull(arena);
            if (Owner is null) throw new InvalidOperationException("Unbound uniform block.");
            Owner.EnsureBound();
            if (!UsesDynamicUniform) throw new InvalidOperationException("This field is not a dynamic uniform block.");
            // One verified T may supply differently named blocks (Silky PaintParams/GradientParams/ImagePatternParams).
            // Each destination block was independently checked against every T field at shape construction.
            if (!ReferenceEquals(arena.Factory, Owner.BoundFactory))
                throw new InvalidOperationException("Uniform arena device does not match this slot.");
            uint offset = arena.OffsetOf(element);
            SetResource(new DeviceBufferRange(arena.Buffer, 0, Compiled.SizeBytes));
            DynamicUniformOffset = offset;
        }

        /// <summary>Write the whole block. <typeparamref name="T"/> was verified field-by-field against the compiled block at <c>Bind</c>.</summary>
        public void Update(CommandList cl, in T value)
        {
            if (Buffer is null) throw new InvalidOperationException($"UniformBlock '{Name}' is not bound (shape not created through ShaderPipelineShape.Bind).");
            Owner.EnsureBound();
            if (!ReferenceEquals(Bound, Buffer)) throw new InvalidOperationException("Write selected arena elements through the arena, not Update.");
            cl.UpdateBuffer(Buffer, 0, value);
        }

        /// <summary>Write one member, named by EXPRESSION (not a string): <c>block.UpdateMember(cl, b => b.LightMVP, in m)</c>. Offset and size come from the compiled block.</summary>
        public void UpdateMember<TMember>(CommandList cl, Expression<Func<T, TMember>> member, in TMember value) where TMember : unmanaged
        {
            if (member is null) throw new ArgumentNullException(nameof(member));
            if (member.Body is not MemberExpression me || me.Member is not FieldInfo field
                || me.Expression != member.Parameters[0] || field.DeclaringType != typeof(T) || field.IsStatic)
                throw new ArgumentException("UpdateMember takes a direct field access expression, e.g. b => b.LightMVP", nameof(member));
            if (Buffer is null || Compiled is null)
                throw new InvalidOperationException($"UniformBlock '{Name}' is not bound (shape not created through ShaderPipelineShape.Bind).");
            var compiledMember = Compiled.Members.FirstOrDefault(m => m.Name == field.Name)
                ?? throw new ShaderContractException(Name, [new ShaderContractViolation(ShaderContractVerifier.Rule_V7c_UniformStructs, $"{Name}.{field.Name}", "C# field", "shader: no such member", "UpdateMember names a field the compiled block does not have")]);
            if (Marshal.SizeOf<TMember>() != compiledMember.SizeBytes)
                throw new ShaderContractException(Name, [new ShaderContractViolation(ShaderContractVerifier.Rule_V7c_UniformStructs, $"{Name}.{field.Name}", $"value {Marshal.SizeOf<TMember>()} B", $"member {compiledMember.SizeBytes} B", "size mismatch")]);
            Owner.EnsureBound();
            if (!ReferenceEquals(Bound, Buffer)) throw new InvalidOperationException("Write selected arena elements through the arena, not UpdateMember.");
            cl.UpdateBuffer(Buffer, compiledMember.OffsetBytes, value);
        }

        /// <summary>Dispose the owned buffer (the shape disposes its blocks).</summary>
        public void Dispose() => Buffer?.Dispose();
    }

    /// <summary>A <c>uniform texture2D</c> / texture slot.</summary>
    public sealed class TextureSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.TextureReadOnly];
        /// <summary>Bind a view; the set is rebuilt lazily at the next <see cref="ShaderPipelineShape.BindResourceSets"/>.</summary>
        public void Set(TextureView view) { SetResource(view); }
        /// <summary>Bind a texture (its default view).</summary>
        public void Set(Texture texture) { SetResource(texture); }
        /// <summary>Evict sets referencing a removed view; does not dispose the view.</summary>
        public void Forget(TextureView view) { if (Owner is null) throw new InvalidOperationException("Unbound texture slot."); Owner.EvictResource(view); }
        /// <summary>Evict sets referencing a removed texture or any of its views; does not dispose the texture.</summary>
        public void Forget(Texture texture) { if (Owner is null) throw new InvalidOperationException("Unbound texture slot."); Owner.EvictResource(texture); }
    }

    /// <summary>A <c>uniform sampler</c> slot.</summary>
    public sealed class SamplerSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.Sampler];
        /// <summary>Bind a sampler.</summary>
        public void Set(Sampler sampler) { Bound = sampler ?? throw new ArgumentNullException(nameof(sampler)); MarkChanged(); }
    }

    /// <summary>A storage (SSBO) buffer slot; read-only or read-write per the shader.</summary>
    public sealed class StorageBufferSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.StructuredBufferReadOnly, ResourceKind.StructuredBufferReadWrite];
        /// <summary>Bind a buffer created with structured-buffer usage.</summary>
        public void Set(DeviceBuffer buffer) { Bound = buffer ?? throw new ArgumentNullException(nameof(buffer)); MarkChanged(); }
    }

    /// <summary>A storage image slot.</summary>
    public sealed class StorageImageSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.TextureReadWrite];
        /// <summary>Bind a storage texture view.</summary>
        public void Set(TextureView view) { Bound = view ?? throw new ArgumentNullException(nameof(view)); MarkChanged(); }
    }

    /// <summary>A vertex buffer slot: <typeparamref name="TVertex"/>'s fields ARE the GLSL <c>in</c> names; the layout is generated from the struct.</summary>
    public sealed class VertexInput<TVertex> where TVertex : unmanaged
    {
        internal ShaderPipelineShape Owner { get; set; }
        internal ResourceFactory Factory { get; set; }

        /// <summary>Create vertex storage whose upload type matches this verified input.</summary>
        public ShaderVertexBuffer<TVertex> CreateBuffer(uint vertexCapacity)
        {
            EnsureBound();
            if (Layout.Stride != System.Runtime.CompilerServices.Unsafe.SizeOf<TVertex>())
                throw new ShaderContractException(typeof(TVertex).Name, [new ShaderContractViolation("STRIDE", typeof(TVertex).Name,
                    $"managed size {System.Runtime.CompilerServices.Unsafe.SizeOf<TVertex>()}", $"verified stride {Layout.Stride}",
                    "managed and marshalled vertex layouts differ")]);
            return new ShaderVertexBuffer<TVertex>(Factory, vertexCapacity);
        }

        /// <summary>Bind by the named field, never by a positional buffer array. Only the exact vertex type is accepted.</summary>
        public void Bind(CommandList commands, ShaderVertexBuffer<TVertex> buffer)
        {
            EnsureBound();
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.EnsureAlive();
            if (!ReferenceEquals(Factory, buffer.Factory)) throw new InvalidOperationException("Vertex buffer belongs to a different device.");
            commands.GraphicsShapeBindings.RequireOwner(Owner);
            commands.SetVertexBuffer(SlotIndex, buffer.Buffer);
            commands.GraphicsShapeBindings.MarkVertex(Owner, SlotIndex);
        }

        private void EnsureBound()
        {
            if (Owner is null) throw new InvalidOperationException("Vertex input was not created through ShaderPipelineShape.Bind.");
            Owner.EnsureBound();
        }

        /// <summary>The generated layout (for inspection/tests).</summary>
        public VertexLayoutDescription Layout { get; internal set; }
        /// <summary>Slot index = field order among the shape's VertexInput fields.</summary>
        public uint SlotIndex { get; internal set; }
    }

    // ───────────────────────────────── the descriptor: verification INPUT as data ─────────────────────────────────

    /// <summary>One resource slot as declared by the shape (built by reflection today; a generator may supply it statically later).</summary>
    public sealed record ShapeSlotDescriptor(string Name, Type SlotType, ResourceKind[] AcceptedKinds, Type StructType);
    /// <summary>One vertex input as declared: the struct's fields in offset order become the elements.</summary>
    public sealed record ShapeVertexInputDescriptor(string FieldName, Type VertexType, bool PerInstance, VertexLayoutDescription Layout);
    /// <summary>Everything the verifier needs about a shape, without GPU objects.</summary>
    public sealed record ShapeDescriptor(string BundleName, ShapeSlotDescriptor[] Slots, ShapeVertexInputDescriptor[] VertexInputs);

    /// <summary>
    /// D10 (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md 5.3): a graphics or compute pipeline as a C# TYPE whose FIELD
    /// NAMES are the GLSL names. <see cref="Bind{TShape}"/> is the only constructor: it verifies every field against the compiled bundle in
    /// both directions and creates the layouts, buffers and slots; the instance that results is the proof. Renderer code then touches
    /// resources only through the typed fields - a misspelt name is a compile error, a renamed GLSL variable fails at Bind.
    /// Derive a <c>sealed partial</c> class, mark it <c>[ShaderBundle("Name")]</c>, declare the fields. Nothing else.
    /// </summary>
    public abstract class ShaderPipelineShape : IDisposable
    {
        private ResourceLayout[] layouts = Array.Empty<ResourceLayout>();
        private ResourceSet[] sets = Array.Empty<ResourceSet>();
        private bool[] setDirty = Array.Empty<bool>();
        private readonly List<ShapeSlot> slots = new();
        private readonly List<(FieldInfo Field, bool PerInstance, VertexLayoutDescription Layout)> vertexInputs = new();
        private ResourceFactory factory;
        internal ResourceFactory BoundFactory => factory;
        private bool disposed;
        private readonly List<(FieldInfo Field, object Handle)> boundFields = new();

        /// <summary>The bundle this instance was bound to.</summary>
        public PrecompiledShaderResult Bundle { get; private set; }
        /// <summary>The bundle name from <see cref="ShaderBundleAttribute"/>.</summary>
        public string BundleName => GetType().GetCustomAttribute<ShaderBundleAttribute>()?.BundleName
            ?? throw new ShaderContractException(GetType().Name, [new ShaderContractViolation(ShaderContractVerifier.Rule_V7_ShaderSet, GetType().Name, "shape class", "no [ShaderBundle(\"...\")]", "a shape must name its bundle")]);

        // No overridable descriptor hook: validation must see the actual fields. Generated descriptors (deferred) must preserve this invariant.
        private ShapeDescriptor Describe() => ShapeDescriptorBuilder.FromReflection(GetType());

        /// <summary>THE constructor. Verifies the shape against the bundle (throws <see cref="ShaderContractException"/>) and creates layouts, uniform buffers and slots.</summary>
        public static TShape Bind<TShape>(PrecompiledShaderResult bundle, ResourceFactory factory) where TShape : ShaderPipelineShape, new()
        {
            if (bundle is null) throw new ArgumentNullException(nameof(bundle));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            var shape = new TShape();
            var descriptor = shape.Describe();
            var violations = ShaderContractVerifier.VerifyShape(descriptor, bundle.BundleName, bundle.Interface, bundle.ResourceLayouts);
            if (violations.Count > 0) throw new ShaderContractException(descriptor.BundleName, violations);
            shape.Materialize(descriptor, bundle, factory);
            return shape;
        }

        /// <summary>Test-time verification: the same check as <see cref="Bind{TShape}"/>, no device, no GPU objects.</summary>
        public static IReadOnlyList<ShaderContractViolation> Verify<TShape>(VeldridShaderBundle bundle) where TShape : ShaderPipelineShape, new()
        {
            if (bundle is null) throw new ArgumentNullException(nameof(bundle));
            var descriptor = new TShape().Describe();
            if (bundle.Interface is null) return [new ShaderContractViolation(ShaderContractVerifier.Rule_V7_ShaderSet, bundle.ShaderName ?? "?", "bundle without interface", "", "recompile with the current Veldrid.SPIRV")];
            return ShaderContractVerifier.VerifyShape(descriptor, bundle.ShaderName, bundle.Interface, bundle.GetResourceLayouts());
        }

        /// <summary>Test-time verification by type (for enumerating every shape in an assembly).</summary>
        public static IReadOnlyList<ShaderContractViolation> Verify(Type shapeType, VeldridShaderBundle bundle)
        {
            var shape = (ShaderPipelineShape)Activator.CreateInstance(shapeType)!;
            var descriptor = shape.Describe();
            if (bundle.Interface is null) return [new ShaderContractViolation(ShaderContractVerifier.Rule_V7_ShaderSet, bundle.ShaderName ?? "?", "bundle without interface", "", "recompile with the current Veldrid.SPIRV")];
            return ShaderContractVerifier.VerifyShape(descriptor, bundle.ShaderName, bundle.Interface, bundle.GetResourceLayouts());
        }

        /// <summary>Every concrete <see cref="ShaderPipelineShape"/> in an assembly with its declared bundle name - the test enumerates these.</summary>
        public static IReadOnlyList<(Type ShapeType, string BundleName)> EnumerateShapes(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            return types.Where(t => !t.IsAbstract && typeof(ShaderPipelineShape).IsAssignableFrom(t))
                        .Select(t => (t, t.GetCustomAttribute<ShaderBundleAttribute>()?.BundleName ?? "")).ToList();
        }

        private void Materialize(ShapeDescriptor descriptor, PrecompiledShaderResult bundle, ResourceFactory f)
        {
            Bundle = bundle; factory = f;
            var compiledLayouts = bundle.ResourceLayouts ?? [];
            layouts = new ResourceLayout[compiledLayouts.Length];
            for (uint set = 0; set < compiledLayouts.Length; set++)
            {
                var description = compiledLayouts[set];
                description.Elements = (ResourceLayoutElementDescription[])description.Elements.Clone();
                for (int b = 0; b < description.Elements.Length; b++)
                {
                    var element = description.Elements[b];
                    if (string.IsNullOrEmpty(element.Name)) continue;
                    var field = GetType().GetField(element.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                    if (field.IsDefined(typeof(DynamicUniformAttribute)))
                    {
                        if (element.Kind != ResourceKind.UniformBuffer)
                            throw new ShaderContractException(BundleName, [new ShaderContractViolation("DYNAMIC", element.Name, "DynamicUniform", element.Kind.ToString(), "only uniform blocks may use a dynamic uniform range")]);
                        description.Elements[b].Options |= ResourceLayoutElementOptions.DynamicBinding;
                    }
                }
                var map = bundle.FlatBindingMap?.Where(e => e.Set == set).ToArray();
                layouts[set] = map is { Length: > 0 } ? f.CreateResourceLayout(ref description, set, map) : f.CreateResourceLayout(ref description);
            }
            sets = new ResourceSet[layouts.Length]; setDirty = new bool[layouts.Length]; setCache = Enumerable.Range(0, layouts.Length).Select(_ => new Dictionary<BoundResourcesKey, ResourceSet>()).ToArray();
            for (int set = 0; set < compiledLayouts.Length; set++)
            {
                var elements = compiledLayouts[set].Elements ?? [];
                for (int binding = 0; binding < elements.Length; binding++)
                {
                    if (string.IsNullOrEmpty(elements[binding].Name)) continue;
                    var slotDesc = descriptor.Slots.First(s => s.Name == elements[binding].Name);
                    var field = GetType().GetField(slotDesc.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var slot = (ShapeSlot)Activator.CreateInstance(slotDesc.SlotType)!;
                    slot.Name = slotDesc.Name; slot.Set = (uint)set; slot.Binding = (uint)binding; slot.Owner = this;
                    slot.UsesDynamicUniform = field.IsDefined(typeof(DynamicUniformAttribute));
                    if (slotDesc.StructType != null)
                    {
                        var block = bundle.Interface.UniformBlocks.First(b => b.Name == slotDesc.Name);
                        var buffer = f.CreateBuffer(new BufferDescription(block.SizeBytes, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
                        slotDesc.SlotType.GetProperty("Buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(slot, buffer);
                        slotDesc.SlotType.GetProperty("Compiled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(slot, block);
                        slot.Bound = buffer;
                    }
                    field.SetValue(this, slot);
                    boundFields.Add((field, slot));
                    slots.Add(slot);
                }
            }
            uint slotIndex = 0;
            foreach (var vi in descriptor.VertexInputs)
            {
                var field = GetType().GetField(vi.FieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                var input = Activator.CreateInstance(field.FieldType)!;
                field.FieldType.GetProperty("Layout")!.SetValue(input, vi.Layout);
                field.FieldType.GetProperty("SlotIndex")!.SetValue(input, slotIndex);
                field.FieldType.GetProperty("Owner", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(input, this);
                field.FieldType.GetProperty("Factory", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(input, f);
                field.SetValue(this, input);
                boundFields.Add((field, input));
                vertexInputs.Add((field, vi.PerInstance, vi.Layout));
                slotIndex++;
            }
            for (int i = 0; i < setDirty.Length; i++) setDirty[i] = true;
        }

        internal ShapeResourceBindingRevision ResourceBindingRevision { get; private set; }
        internal void InvalidateSet(uint set)
        {
            ResourceBindingRevision = new(checked(ResourceBindingRevision.Value + 1));
            if (set < setDirty.Length) setDirty[set] = true;
        }

        /// <summary>Create the graphics pipeline for this shape: shaders, layouts and vertex layouts come FROM the shape; the caller supplies only fixed-function state (and specialization constants, if the GLSL declares any).</summary>
        public Pipeline CreateGraphicsPipeline(BlendStateDescription blend, DepthStencilStateDescription depthStencil, RasterizerStateDescription rasterizer, PrimitiveTopology topology, OutputDescription outputs, SpecializationConstant[] specializations = null)
        {
            EnsureBound();
            var desc = new GraphicsPipelineDescription
            {
                BlendState = blend, DepthStencilState = depthStencil, RasterizerState = rasterizer, PrimitiveTopology = topology,
                ShaderSet = new ShaderSetDescription(vertexInputs.Select(v => v.Layout).ToArray(), Bundle.Shaders, specializations),
                ResourceLayouts = layouts, Outputs = outputs,
            };
            var pipeline = factory.CreateGraphicsPipeline(ref desc);
            pipeline.VerifiedShape = this;
            pipeline.VerifiedVertexInputCount = vertexInputs.Count;
            pipeline.VerifiedHasResources = slots.Count != 0;
            return pipeline;
        }

        /// <summary>Create the compute pipeline for this shape (compute shapes declare no <see cref="VertexInput{TVertex}"/>).</summary>
        public Pipeline CreateComputePipeline(uint threadGroupSizeX, uint threadGroupSizeY, uint threadGroupSizeZ)
        {
            EnsureBound();
            var desc = new ComputePipelineDescription(Bundle.Shaders[0], layouts, threadGroupSizeX, threadGroupSizeY, threadGroupSizeZ);
            var pipeline = factory.CreateComputePipeline(ref desc);
            pipeline.VerifiedShape = this;
            pipeline.VerifiedHasResources = slots.Count != 0;
            return pipeline;
        }

        /// <summary>
        /// Bind every resource set. Sets are CACHED by the identity of the bound resources, so switching a slot between a handful of
        /// textures (ImGui per draw command, sprite batching, material switching) creates each combination once and is free afterwards.
        /// A slot never <c>Set</c> is an exception naming it.
        /// </summary>
        public void BindResourceSets(CommandList cl, bool compute = false)
        {
            EnsureBound();
            ArgumentNullException.ThrowIfNull(cl);
            var bindings = compute ? cl.ComputeShapeBindings : cl.GraphicsShapeBindings;
            bindings.RequireOwner(this);
            foreach (var slot in slots) if (slot.Bound != null) ShapeSlot.RequireLiveResource(slot.Bound);
            for (int set = 0; set < layouts.Length; set++)
            {
                if (setDirty[set] || sets[set] is null)
                {
                    var elements = layouts[set].Description.Elements ?? [];
                    var bound = new IBindableResource[elements.Length];
                    for (int b = 0; b < elements.Length; b++)
                    {
                        if (string.IsNullOrEmpty(elements[b].Name)) continue;
                        var slot = slots.First(s => s.Set == set && s.Binding == b);
                        bound[b] = slot.Bound ?? throw new ShaderContractException(BundleName, [new ShaderContractViolation("BIND", slot.Name, "slot never Set()", $"set {set} binding {b} {elements[b].Kind}", "every shader resource must be bound before drawing")]);
                    }
                    var key = new BoundResourcesKey(bound);
                    if (!setCache[set].TryGetValue(key, out var cached))
                    {
                        cached = factory.CreateResourceSet(new ResourceSetDescription(layouts[set], bound));
                        setCache[set][key] = cached;
                    }
                    sets[set] = cached;
                    setDirty[set] = false;
                }
                var offsets = slots.Where(s => s.Set == set && s.UsesDynamicUniform)
                    .OrderBy(s => s.Binding).Select(s => s.DynamicUniformOffset).ToArray();
                if (compute) cl.SetComputeResourceSet((uint)set, sets[set], offsets);
                else cl.SetGraphicsResourceSet((uint)set, sets[set], offsets);
            }
            bindings.MarkResources(this);
        }

        private Dictionary<BoundResourcesKey, ResourceSet>[] setCache = Array.Empty<Dictionary<BoundResourcesKey, ResourceSet>>();

        internal int CachedResourceSetCount => setCache.Sum(cache => cache.Count);

        internal void EvictResource(IBindableResource resource)
        {
            EnsureBound();
            ArgumentNullException.ThrowIfNull(resource);
            for (int set = 0; set < setCache.Length; set++)
            {
                foreach (var entry in setCache[set].Where(e => e.Key.References(resource)).ToArray())
                {
                    if (ReferenceEquals(sets[set], entry.Value))
                    {
                        sets[set] = null;
                        setDirty[set] = true;
                    }
                    entry.Value.Dispose();
                    setCache[set].Remove(entry.Key);
                }
            }
            foreach (var slot in slots)
                if (ReferencesResource(slot.Bound, resource))
                {
                    slot.Bound = null;
                    InvalidateSet(slot.Set);
                }
        }

        private static bool ReferencesResource(IBindableResource bound, IBindableResource resource)
            => ReferenceEquals(bound, resource) || (resource is Texture texture && bound is TextureView view && ReferenceEquals(view.Target, texture));

        /// <summary>Identity of the resources bound to one set (reference equality per element).</summary>
        private readonly struct BoundResourcesKey(IBindableResource[] bound) : IEquatable<BoundResourcesKey>
        {
            private readonly IBindableResource[] bound = bound;
            internal bool References(IBindableResource resource) => bound.Any(b => ReferencesResource(b, resource));
            public bool Equals(BoundResourcesKey other)
            {
                if (bound.Length != other.bound.Length) return false;
                for (int i = 0; i < bound.Length; i++) if (!Equals(bound[i], other.bound[i])) return false;
                return true;
            }
            public override bool Equals(object obj) => obj is BoundResourcesKey k && Equals(k);
            public override int GetHashCode() { var h = new HashCode(); foreach (var b in bound) h.Add(b); return h.ToHashCode(); }
        }

        internal void EnsureBound()
        {
            if (disposed) throw new ObjectDisposedException(GetType().Name);
            if (Bundle is null) throw new InvalidOperationException($"{GetType().Name} was not created through ShaderPipelineShape.Bind.");
            ValidateFieldIdentity(this, boundFields);
        }

        // Replacing a public field handle cannot silently redirect writes to a different slot.
        internal static void ValidateFieldIdentity(object shape, IReadOnlyList<(FieldInfo Field, object Handle)> fields)
        {
            foreach (var (field, handle) in fields)
                if (!ReferenceEquals(field.GetValue(shape), handle))
                    throw new ShaderContractException(shape.GetType().Name, [new ShaderContractViolation("OWNERSHIP", field.Name,
                        "shape field was reassigned", "expected the handle installed by Bind",
                        "slot handles cannot be replaced or exchanged between shapes")]);
        }

        /// <summary>Dispose owned sets, layouts and uniform buffers (shaders belong to the bundle source).</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var cache in setCache) foreach (var s in cache.Values) s.Dispose();
            foreach (var l in layouts) l?.Dispose();
            foreach (var slot in slots) if (slot is IDisposable d) d.Dispose();
        }
    }

    /// <summary>Builds a <see cref="ShapeDescriptor"/> from a shape type by reflection (the default <c>Describe()</c>).</summary>
    public static class ShapeDescriptorBuilder
    {
        /// <summary>Reflect the shape's fields: slot fields (ShapeSlot subclasses) and VertexInput fields, in declaration order.</summary>
        public static ShapeDescriptor FromReflection(Type shapeType)
        {
            if (shapeType is null) throw new ArgumentNullException(nameof(shapeType));
            // Until generated descriptors exist, actual fields are the sole authority. Restrict inheritance so no field is silently
            // skipped by DeclaredOnly and no subclass can substitute a different descriptor after verification.
            if (!shapeType.IsSealed || shapeType.IsAbstract || shapeType.BaseType != typeof(ShaderPipelineShape))
                throw new ShaderContractException(shapeType.Name, [new ShaderContractViolation("SHAPE", shapeType.Name,
                    "shape declaration", "requires a sealed direct ShaderPipelineShape subclass",
                    "shape fields must be completely visible to verification")]);
            string bundleName = shapeType.GetCustomAttribute<ShaderBundleAttribute>()?.BundleName ?? "";
            var slots = new List<ShapeSlotDescriptor>();
            var inputs = new List<ShapeVertexInputDescriptor>();
            foreach (var field in shapeType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(f => f.MetadataToken))
            {
                Type ft = field.FieldType;
                if (typeof(ShapeSlot).IsAssignableFrom(ft))
                {
                    var probe = (ShapeSlot)Activator.CreateInstance(ft)!;
                    Type structType = ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(UniformBlock<>) ? ft.GetGenericArguments()[0] : null;
                    slots.Add(new ShapeSlotDescriptor(field.Name, ft, probe.AcceptedKinds, structType));
                }
                else if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(VertexInput<>))
                {
                    Type vertexType = ft.GetGenericArguments()[0];
                    bool perInstance = field.GetCustomAttribute<PerInstanceAttribute>() != null;
                    inputs.Add(new ShapeVertexInputDescriptor(field.Name, vertexType, perInstance, VertexLayoutFor(vertexType, perInstance)));
                }
            }
            return new ShapeDescriptor(bundleName, slots.ToArray(), inputs.ToArray());
        }

        /// <summary>The vertex layout a struct implies: fields in offset order; names are the GLSL input names; formats from the C# type or <see cref="VertexFormatAttribute"/>.</summary>
        public static VertexLayoutDescription VertexLayoutFor(Type vertexType, bool perInstance)
        {
            var fields = vertexType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(f => (int)Marshal.OffsetOf(vertexType, f.Name)).ToArray();
            var elements = fields.Select(f => new VertexElementDescription(f.Name, VertexElementSemantic.TextureCoordinate, f.GetCustomAttribute<VertexFormatAttribute>()?.Format ?? DefaultFormat(f.FieldType, vertexType.Name, f.Name), (uint)(int)Marshal.OffsetOf(vertexType, f.Name))).ToArray();
            var layout = new VertexLayoutDescription((uint)Marshal.SizeOf(vertexType), elements) { InstanceStepRate = perInstance ? 1u : 0u };
            return layout;
        }

        private static VertexElementFormat DefaultFormat(Type t, string owner, string field)
        {
            if (t == typeof(float)) return VertexElementFormat.Float1;
            if (t == typeof(Vector2)) return VertexElementFormat.Float2;
            if (t == typeof(Vector3)) return VertexElementFormat.Float3;
            if (t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(RgbaFloat)) return VertexElementFormat.Float4;
            if (t == typeof(int)) return VertexElementFormat.Int1;
            if (t == typeof(uint)) return VertexElementFormat.UInt1;
            if (t == typeof(RgbaByte)) return VertexElementFormat.Byte4_Norm;
            throw new ShaderContractException(owner, [new ShaderContractViolation(ShaderContractVerifier.Rule_V7a_VertexInputs, $"{owner}.{field}", $"C# type {t.Name}", "no default vertex format", "add [VertexFormat(...)] to the field")]);
        }
    }
}
