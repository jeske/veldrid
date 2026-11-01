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

    // ───────────────────────────────── slots: typed IDENTIFIERS that replace every uint in the public API ─────────────────────────────────
    //
    // BUGFIX_ShaderInterfaceContract_TypedSlotArguments.md: a slot is a verified NAME + set/binding + accepted resource type. It holds
    // NO bound-resource state and has NO verbs. Consumers keep their CommandList / ResourceFactory calls with the same names; where
    // an int slot or a raw layout used to be, a slot (or a shape) goes. Binding a resource to a slot is `slot.With(resource)`, consumed
    // by ResourceFactory.CreateResourceSet(shape, ...). Storage CREATION stays on the slot because that is where the type is checked.

    /// <summary>A shader-visible resource slot owned by a shape. Its NAME is the C# field name, which is the GLSL name (D10).</summary>
    public abstract class ShapeSlot
    {
        /// <summary>GLSL resource name == C# field name.</summary>
        public string Name { get; internal set; } = "";
        /// <summary>Descriptor set, from the bundle.</summary>
        public uint Set { get; internal set; }
        /// <summary>Binding within the set, from the bundle.</summary>
        public uint Binding { get; internal set; }
        internal abstract ResourceKind[] AcceptedKinds { get; }
        internal ShaderPipelineShape Owner { get; set; }
        internal bool UniformBindingOffsetSelectable { get; set; }

        internal void EnsureOwned()
        {
            if (Owner is null) throw new InvalidOperationException($"Slot '{Name}' was not created through ShaderPipelineShape.Bind.");
            Owner.EnsureBound();
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

        internal ShapeResourceBinding BindingOf(IBindableResource resource, IUniformArenaOffsets arena = null)
        {
            EnsureOwned();
            ArgumentNullException.ThrowIfNull(resource);
            RequireLiveResource(resource);
            return new ShapeResourceBinding(this, resource, arena);
        }
    }

    /// <summary>One NAMED resource assignment for <see cref="ResourceFactory.CreateResourceSet(ShaderPipelineShape, ShapeResourceBinding[])"/>: produced only by a slot's <c>With(...)</c>, so the resource type was checked against the slot kind.</summary>
    public readonly struct ShapeResourceBinding
    {
        internal ShapeSlot Slot { get; }
        internal IBindableResource Resource { get; }
        /// <summary>Non-null when the slot selects an arena element at bind time (<see cref="CommandList.SetGraphicsResourceSet(ResourceSet, UniformArenaElement[])"/>).</summary>
        internal IUniformArenaOffsets Arena { get; }

        internal ShapeResourceBinding(ShapeSlot slot, IBindableResource resource, IUniformArenaOffsets arena)
        {
            Slot = slot; Resource = resource; Arena = arena;
        }
    }

    /// <summary>Byte-offset source for offset-selectable uniform bindings; implemented by <see cref="ShaderUniformArena{T}"/>.</summary>
    internal interface IUniformArenaOffsets
    {
        uint OffsetOf(UniformArenaElement element);
        void EnsureAlive();
    }

    /// <summary>A <c>uniform Block { ... }</c>. Storage is created here (explicitly, typed); it is written through <see cref="CommandList.UpdateBuffer{T}(UniformBlock{T}, in T)"/> and bound through <see cref="With()"/> / <see cref="With(ShaderUniformBuffer{T})"/> / <see cref="With(ShaderUniformArena{T})"/>.</summary>
    public sealed class UniformBlock<T> : ShapeSlot, IDisposable where T : unmanaged
    {
        // The block-private allocation from CreateBuffer. Null when the block is fed by shared storage or an arena instead.
        private DeviceBuffer privateBuffer;
        internal VdShaderUniformBlock Compiled { get; set; }
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.UniformBuffer];

        /// <summary>Create this block's PRIVATE buffer with explicit native usage. No allocation occurs at shape binding, and no CPU shadow is introduced.</summary>
        public void CreateBuffer(BufferUsage usage)
        {
            EnsureOwned();
            if (privateBuffer != null) throw new InvalidOperationException($"UniformBlock '{Name}' already has a private buffer.");
            if (UniformBindingOffsetSelectable) throw new InvalidOperationException($"UniformBlock '{Name}' declares [UniformBufferBindingTimeOffsetSelectable]; use CreateArena, not whole-block storage.");
            TypedBufferUsageValidation.RequireUniform(usage);
            if (System.Runtime.CompilerServices.Unsafe.SizeOf<T>() != Compiled.SizeBytes)
                throw new InvalidOperationException($"UniformBlock '{Name}' managed size differs from the verified block size.");
            privateBuffer = Owner.BoundFactory.CreateBuffer(new BufferDescription(Compiled.SizeBytes, usage));
        }

        /// <summary>
        ///     Create ONE typed uniform buffer from this verified block for use by every block verified against the same <typeparamref name="T"/>
        ///     (bind it with each block's <see cref="With(ShaderUniformBuffer{T})"/>). One <see cref="CommandList.UpdateBuffer{T}(ShaderUniformBuffer{T}, in T)"/>
        ///     feeds every set that binds it, in command order. Allocation usage is explicit.
        /// </summary>
        public ShaderUniformBuffer<T> CreateSharedBuffer(BufferUsage usage)
        {
            EnsureOwned();
            if (UniformBindingOffsetSelectable) throw new InvalidOperationException($"UniformBlock '{Name}' declares [UniformBufferBindingTimeOffsetSelectable]; use CreateArena, not shared whole-block storage.");
            return new ShaderUniformBuffer<T>(Owner.BoundFactory, Name, Compiled.SizeBytes, usage);
        }

        /// <summary>Create aligned typed arena storage with an explicit allocation usage; offset selection is a separate field declaration.</summary>
        public ShaderUniformArena<T> CreateArena(GraphicsDevice device, uint capacity, BufferUsage usage)
        {
            EnsureOwned();
            ArgumentNullException.ThrowIfNull(device);
            if (!UniformBindingOffsetSelectable) throw new InvalidOperationException("Declare this field with [UniformBufferBindingTimeOffsetSelectable] before using an arena.");
            if (!ReferenceEquals(device.ResourceFactory, Owner.BoundFactory)) throw new InvalidOperationException("Uniform arena device differs from shape device.");
            return new ShaderUniformArena<T>(device, Name, Compiled.SizeBytes, capacity, usage);
        }

        /// <summary>Bind this block's PRIVATE buffer (from <see cref="CreateBuffer"/>).</summary>
        public ShapeResourceBinding With()
        {
            EnsureOwned();
            if (privateBuffer is null) throw new InvalidOperationException($"UniformBlock '{Name}' has no private buffer. Call CreateBuffer first, or bind shared storage / an arena explicitly.");
            return BindingOf(privateBuffer);
        }

        /// <summary>Bind shared storage created by any block verified against the same <typeparamref name="T"/>. Legal because THIS block was independently name/type-verified at <c>Bind</c>.</summary>
        public ShapeResourceBinding With(ShaderUniformBuffer<T> storage)
        {
            ArgumentNullException.ThrowIfNull(storage);
            EnsureOwned();
            if (UniformBindingOffsetSelectable) throw new InvalidOperationException($"UniformBlock '{Name}' declares [UniformBufferBindingTimeOffsetSelectable]; bind an arena, not whole-block storage.");
            if (!ReferenceEquals(storage.Factory, Owner.BoundFactory)) throw new InvalidOperationException("Shared uniform storage device differs from this shape's device.");
            if (Compiled.SizeBytes != storage.SizeInBytes) throw new InvalidOperationException($"UniformBlock '{Name}' verified size {Compiled.SizeBytes} B differs from shared storage size {storage.SizeInBytes} B.");
            storage.EnsureAlive();
            return BindingOf(storage.Buffer);
        }

        /// <summary>Bind a one-block window of a typed arena; the element is chosen per draw by <see cref="CommandList.SetGraphicsResourceSet(ResourceSet, UniformArenaElement[])"/>.</summary>
        public ShapeResourceBinding With(ShaderUniformArena<T> arena)
        {
            ArgumentNullException.ThrowIfNull(arena);
            EnsureOwned();
            if (!UniformBindingOffsetSelectable) throw new InvalidOperationException($"UniformBlock '{Name}' does not declare [UniformBufferBindingTimeOffsetSelectable]; it cannot bind an arena.");
            if (!ReferenceEquals(arena.Factory, Owner.BoundFactory)) throw new InvalidOperationException("Uniform arena device does not match this slot.");
            arena.EnsureAlive();
            // One verified T may supply differently named blocks (Silky PaintParams/GradientParams/ImagePatternParams): each destination
            // block was independently checked against every T field at shape construction.
            return BindingOf(new DeviceBufferRange(arena.Buffer, 0, Compiled.SizeBytes), arena);
        }

        /// <summary>
        ///     The block-private buffer from <see cref="CreateBuffer"/>. Throws if the block has none (it is fed by shared storage or an
        ///     arena, which are written through their own handles) or if it has been disposed.
        /// </summary>
        internal DeviceBuffer PrivateBuffer
        {
            get
            {
                EnsureOwned();
                if (privateBuffer is null) throw new InvalidOperationException($"UniformBlock '{Name}' has no private buffer. Call CreateBuffer, or write the shared storage / arena it is bound to.");
                RequireLiveResource(privateBuffer);
                return privateBuffer;
            }
        }

        /// <summary>Resolve a member named by EXPRESSION (not a string) to its compiled byte offset, checking size and the backend's partial-write support.</summary>
        internal uint ResolveMemberOffset<TMember>(Expression<Func<T, TMember>> member) where TMember : unmanaged
        {
            if (member is null) throw new ArgumentNullException(nameof(member));
            if (member.Body is not MemberExpression me || me.Member is not FieldInfo field
                || me.Expression != member.Parameters[0] || field.DeclaringType != typeof(T) || field.IsStatic)
                throw new ArgumentException("Member update takes a direct field access expression, e.g. b => b.LightMVP", nameof(member));
            if (Compiled is null)
                throw new InvalidOperationException($"UniformBlock '{Name}' is not bound (shape not created through ShaderPipelineShape.Bind).");
            var compiledMember = Compiled.Members.FirstOrDefault(m => m.Name == field.Name)
                ?? throw new ShaderContractException(Name, [new ShaderContractViolation(ShaderContractVerifier.Rule_V7c_UniformStructs, $"{Name}.{field.Name}", "C# field", "shader: no such member", "member update names a field the compiled block does not have")]);
            if (Marshal.SizeOf<TMember>() != compiledMember.SizeBytes)
                throw new ShaderContractException(Name, [new ShaderContractViolation(ShaderContractVerifier.Rule_V7c_UniformStructs, $"{Name}.{field.Name}", $"value {Marshal.SizeOf<TMember>()} B", $"member {compiledMember.SizeBytes} B", "size mismatch")]);
            var buffer = PrivateBuffer;
            // D3D11's current UpdateBuffer routes partial constant-buffer writes through a copy, not UpdateSubresource.
            // Do not silently widen the write or invent a CPU shadow.
            if (Owner.BoundFactory.BackendType == GraphicsBackend.Direct3D11
                && (compiledMember.OffsetBytes != 0 || compiledMember.SizeBytes != buffer.SizeInBytes))
                throw new NotSupportedException($"UniformBlock '{Name}': this D3D11 update path requires the whole constant buffer; a member update cannot issue a partial write. Update the complete typed value.");
            return compiledMember.OffsetBytes;
        }

        /// <summary>Dispose the OWNED buffer (the shape disposes its blocks). Shared storage and arenas belong to their own handles.</summary>
        public void Dispose() => privateBuffer?.Dispose();
    }

    /// <summary>A <c>uniform texture2D</c> / texture slot.</summary>
    public sealed class TextureSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.TextureReadOnly];
        /// <summary>Bind a view.</summary>
        public ShapeResourceBinding With(TextureView view) => BindingOf(view);
        /// <summary>Bind a texture (its default view).</summary>
        public ShapeResourceBinding With(Texture texture) => BindingOf(texture);
    }

    /// <summary>A <c>uniform sampler</c> slot.</summary>
    public sealed class SamplerSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.Sampler];
        /// <summary>Bind a sampler.</summary>
        public ShapeResourceBinding With(Sampler sampler) => BindingOf(sampler);
    }

    /// <summary>A storage (SSBO) buffer slot; read-only or read-write per the shader.</summary>
    public sealed class StorageBufferSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.StructuredBufferReadOnly, ResourceKind.StructuredBufferReadWrite];
        /// <summary>Bind a buffer created with structured-buffer usage.</summary>
        public ShapeResourceBinding With(DeviceBuffer buffer) => BindingOf(buffer);
    }

    /// <summary>A storage image slot.</summary>
    public sealed class StorageImageSlot : ShapeSlot
    {
        internal override ResourceKind[] AcceptedKinds => [ResourceKind.TextureReadWrite];
        /// <summary>Bind a storage texture view.</summary>
        public ShapeResourceBinding With(TextureView view) => BindingOf(view);
    }

    /// <summary>Non-generic view of a <see cref="VertexInput{TVertex}"/>: what <see cref="ShaderSetDescription"/> takes where a positional vertex layout used to go.</summary>
    public abstract class VertexInput
    {
        internal ShaderPipelineShape Owner { get; set; }
        internal ResourceFactory Factory { get; set; }
        /// <summary>The generated layout (for inspection/tests).</summary>
        public VertexLayoutDescription Layout { get; internal set; }
        /// <summary>Slot index = field order among the shape's VertexInput fields.</summary>
        public uint SlotIndex { get; internal set; }
        /// <summary>The verified vertex struct - the identity that lets a bound vertex buffer survive a pipeline switch.</summary>
        internal abstract Type VertexType { get; }

        internal void EnsureBound()
        {
            if (Owner is null) throw new InvalidOperationException("Vertex input was not created through ShaderPipelineShape.Bind.");
            Owner.EnsureBound();
        }
    }

    /// <summary>A vertex buffer slot: <typeparamref name="TVertex"/>'s fields ARE the GLSL <c>in</c> names; the layout is generated from the struct. Passed to <see cref="CommandList.SetVertexBuffer{TVertex}(VertexInput{TVertex}, ShaderVertexBuffer{TVertex})"/> where the slot index used to go.</summary>
    public sealed class VertexInput<TVertex> : VertexInput where TVertex : unmanaged
    {
        internal override Type VertexType => typeof(TVertex);

        /// <summary>Create vertex storage whose upload type matches this verified input.</summary>
        public ShaderVertexBuffer<TVertex> CreateBuffer(uint vertexCapacity, BufferUsage usage)
        {
            EnsureBound();
            if (Layout.Stride != System.Runtime.CompilerServices.Unsafe.SizeOf<TVertex>())
                throw new ShaderContractException(typeof(TVertex).Name, [new ShaderContractViolation("STRIDE", typeof(TVertex).Name,
                    $"managed size {System.Runtime.CompilerServices.Unsafe.SizeOf<TVertex>()}", $"verified stride {Layout.Stride}",
                    "managed and marshalled vertex layouts differ")]);
            return new ShaderVertexBuffer<TVertex>(Factory, vertexCapacity, usage);
        }
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
    /// both directions and creates the layouts and slots. The shape then carries NO bound-resource state: it is the typed argument that
    /// replaces raw layouts/shader sets in <see cref="GraphicsPipelineDescription"/> and <see cref="ResourceFactory.CreateResourceSet(ShaderPipelineShape, ShapeResourceBinding[])"/>,
    /// and its slots replace every slot/binding integer in <see cref="CommandList"/>.
    /// Derive a <c>sealed partial</c> class, mark it <c>[ShaderBundle("Name")]</c>, declare the fields. Nothing else.
    /// </summary>
    public abstract class ShaderPipelineShape : IDisposable
    {
        private ResourceLayoutDescription[] compiledLayouts = Array.Empty<ResourceLayoutDescription>();
        private readonly List<ShapeSlot> slots = new();
        private readonly List<(FieldInfo Field, bool PerInstance, VertexLayoutDescription Layout, Type VertexType)> vertexInputs = new();
        private ResourceFactory factory;
        internal ResourceFactory BoundFactory => factory;
        private bool disposed;
        private readonly List<(FieldInfo Field, object Handle)> boundFields = new();

        /// <summary>The bundle this instance was bound to.</summary>
        public PrecompiledShaderResult Bundle { get; private set; }
        /// <summary>The bundle name from <see cref="ShaderBundleAttribute"/>.</summary>
        public string BundleName => GetType().GetCustomAttribute<ShaderBundleAttribute>()?.BundleName
            ?? throw new ShaderContractException(GetType().Name, [new ShaderContractViolation(ShaderContractVerifier.Rule_V7_ShaderSet, GetType().Name, "shape class", "no [ShaderBundle(\"...\")]", "a shape must name its bundle")]);

        internal int VertexInputCount => vertexInputs.Count;
        internal bool HasResources => slots.Count != 0;
        internal int SetCount => compiledLayouts.Length;
        internal VertexLayoutDescription[] VertexLayouts => vertexInputs.Select(v => v.Layout).ToArray();
        /// <summary>The verified vertex struct at a slot index (null if none) - the identity that lets a bound vertex buffer survive a pipeline switch.</summary>
        internal Type VertexTypeAt(uint slot) => slot < vertexInputs.Count ? vertexInputs[(int)slot].VertexType : null;
        /// <summary>The bundle's layout for one descriptor set, with offset-selectable options already applied from the field attributes.</summary>
        internal ResourceLayoutDescription CompiledLayoutFor(uint set)
        {
            if (set >= compiledLayouts.Length) throw new ArgumentOutOfRangeException(nameof(set));
            return compiledLayouts[set];
        }
        internal IEnumerable<ShapeSlot> SlotsOfSet(uint set) => slots.Where(s => s.Set == set);

        // Per-set slot arrays in BINDING order, built once on first use. Set construction runs per transient set (clip masks, filters),
        // so it must not pay for a LINQ iterator per call; the shape's slots never change after Bind.
        private ShapeSlot[][] slotsBySetInBindingOrder;
        /// <summary>The slots of one descriptor set, ordered by binding index. Allocation-free after the first call.</summary>
        internal ReadOnlySpan<ShapeSlot> SlotsOfSetSpan(uint set)
        {
            if (slotsBySetInBindingOrder is null)
            {
                var bySet = new ShapeSlot[compiledLayouts.Length][];
                for (uint s = 0; s < bySet.Length; s++) bySet[s] = slots.Where(x => x.Set == s).OrderBy(x => x.Binding).ToArray();
                slotsBySetInBindingOrder = bySet;
            }
            return set < slotsBySetInBindingOrder.Length ? slotsBySetInBindingOrder[set] : ReadOnlySpan<ShapeSlot>.Empty;
        }

        /// <summary>
        ///     Whether a verified <see cref="ResourceSet"/> may be bound at <paramref name="set"/> for THIS shape's pipelines. True when the
        ///     set was created from this instance, or from any shape whose compiled layout for that set is element-wise identical (name,
        ///     kind, stages, options - V7d). That is the GPU's rule (descriptor-set layout compatibility) and it is name-sound: the set's
        ///     resources were checked against the same names and kinds this shape declares. The backend register each compiler gave a
        ///     binding is NOT part of the rule: D3D11 / Metal translate (set, binding) through the PIPELINE's layout at bind time.
        ///     Any declaration difference => refused, with the binding named.
        /// </summary>
        internal List<ShaderContractViolation> ResourceSetBindingViolations(ResourceSet rs, uint set)
        {
            var violations = new List<ShaderContractViolation>();
            if (rs.VerifiedShape is null)
            { violations.Add(new ShaderContractViolation("BIND", $"set {set}", "resource set without a verified shape", "a set from ResourceSetDescription(layout, slot bindings)", "only verified sets can be bound")); return violations; }
            if (set >= compiledLayouts.Length)
            { violations.Add(new ShaderContractViolation("BIND", $"set {set}", $"{GetType().Name} declares {compiledLayouts.Length} set(s)", $"set index {set}", "the selected pipeline has no such descriptor set")); return violations; }
            if (ReferenceEquals(rs.VerifiedShape, this)) return violations;
            if (rs.VerifiedSetIndex != set)
            { violations.Add(new ShaderContractViolation("BIND", $"set {set}", $"set was created for descriptor set {rs.VerifiedSetIndex}", $"bound at descriptor set {set}", "a set binds only at the descriptor set it was created for")); return violations; }
            ShaderContractVerifier.VerifySharedSetBinding(set, compiledLayouts[set], rs.VerifiedShape.CompiledLayoutFor(rs.VerifiedSetIndex), violations);
            return violations;
        }

        internal bool AcceptsResourceSet(ResourceSet rs, uint set) => ResourceSetBindingViolations(rs, set).Count == 0;

        // No overridable descriptor hook: validation must see the actual fields. Generated descriptors (deferred) must preserve this invariant.
        private ShapeDescriptor Describe() => ShapeDescriptorBuilder.FromReflection(GetType());

        /// <summary>Verifies the shape and creates layouts and typed slots. Buffer storage requires explicit creation through the verified fields.</summary>
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
            // The bundle's layouts, with offset-selectable options applied from the FIELD attributes. Consumers create ResourceLayout
            // objects themselves through factory.CreateResourceLayout(new ResourceLayoutDescription(slots...)) - the same call as before.
            var bundleLayouts = bundle.ResourceLayouts ?? [];
            compiledLayouts = new ResourceLayoutDescription[bundleLayouts.Length];
            for (uint set = 0; set < compiledLayouts.Length; set++)
            {
                var description = bundleLayouts[set];
                description.Elements = (ResourceLayoutElementDescription[])description.Elements.Clone();
                for (int b = 0; b < description.Elements.Length; b++)
                {
                    var element = description.Elements[b];
                    if (string.IsNullOrEmpty(element.Name)) continue;
                    var field = GetType().GetField(element.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                    if (field.IsDefined(typeof(UniformBufferBindingTimeOffsetSelectableAttribute)))
                    {
                        if (element.Kind != ResourceKind.UniformBuffer)
                            throw new ShaderContractException(BundleName, [new ShaderContractViolation("OFFSET_BINDING", element.Name, "UniformBufferBindingTimeOffsetSelectable", element.Kind.ToString(), "only uniform blocks may select a binding-time offset")]);
                        description.Elements[b].Options |= ResourceLayoutElementOptions.DynamicBinding;
                    }
                }
                compiledLayouts[set] = description;
            }
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
                    slot.UniformBindingOffsetSelectable = field.IsDefined(typeof(UniformBufferBindingTimeOffsetSelectableAttribute));
                    if (slotDesc.StructType != null)
                    {
                        var block = bundle.Interface.UniformBlocks.First(b => b.Name == slotDesc.Name);
                        slotDesc.SlotType.GetProperty("Compiled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(slot, block);
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
                var input = (VertexInput)Activator.CreateInstance(field.FieldType)!;
                input.Layout = vi.Layout; input.SlotIndex = slotIndex; input.Owner = this; input.Factory = f;
                field.SetValue(this, input);
                boundFields.Add((field, input));
                vertexInputs.Add((field, vi.PerInstance, vi.Layout, vi.VertexType));
                slotIndex++;
            }
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

        /// <summary>Dispose layouts and OWNED uniform buffers. Shaders belong to the bundle; resource sets, shared storage and arenas belong to the consumer.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Layouts, sets, shared storage and arenas are consumer-owned; only the OWNED block buffers are disposed here.
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