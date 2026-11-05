using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Veldrid
{
    /// <summary>One failed contract rule: which rule, which NAME, what each side said.</summary>
    public readonly record struct ShaderContractViolation(string Rule, string Name, string SideA, string SideB, string Detail)
    {
        /// <inheritdoc/>
        public override string ToString() => $"[{Rule}] {Name}: {SideA}  |  {SideB}  -  {Detail}";
    }

    /// <summary>
    /// Thrown when a shape, a pipeline description or a bind does not match, BY NAME, the compiled interface of its bundle (D0: a
    /// misalignment is a detected hard fail, never a visual). Never a warning. Spec: AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md.
    /// </summary>
    public sealed class ShaderContractException : VeldridException
    {
        /// <summary>Every violation found (the message lists them all).</summary>
        public IReadOnlyList<ShaderContractViolation> Violations { get; }
        /// <summary>The bundle (or shape) concerned.</summary>
        public string BundleName { get; }

        /// <summary>Create with the full violation table as the message.</summary>
        public ShaderContractException(string bundleName, IReadOnlyList<ShaderContractViolation> violations)
            : base(BuildMessage(bundleName, violations))
        {
            BundleName = bundleName;
            Violations = violations;
        }

        private static string BuildMessage(string bundleName, IReadOnlyList<ShaderContractViolation> violations)
        {
            var lines = new List<string> { $"Shader contract violated for '{bundleName}' ({violations.Count} violation(s)). A slot number is never evidence of agreement; every boundary joins by NAME." };
            lines.AddRange(violations.Select(v => "  " + v));
            lines.Add("  See AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Which bundle a <see cref="Shader"/> came from. Filled by <see cref="ResourceFactory.CreateFromBundle(VeldridShaderBundle, Func{string, byte[]}, string)"/>.</summary>
    public static class ShaderInterfaceRegistry
    {
        private static readonly ConditionalWeakTable<Shader, PrecompiledShaderResult> Table = new();

        internal static void Register(Shader shader, PrecompiledShaderResult result) => Table.AddOrUpdate(shader, result);

        /// <summary>The bundle result this shader was created from, or null for shaders not created through a bundle.</summary>
        public static PrecompiledShaderResult TryGetBundle(Shader shader) => Table.TryGetValue(shader, out var r) ? r : null;
    }

    /// <summary>
    /// The verifier behind <see cref="ShaderPipelineShape.Bind{TShape}"/> (D10) and the pipeline-creation backstop (V7). Pure over data.
    /// </summary>
    public static class ShaderContractVerifier
    {
        public const string Rule_V7a_VertexInputs = "V7a";
        public const string Rule_V7b_ResourceLayouts = "V7b";
        public const string Rule_V7c_UniformStructs = "V7c";
        public const string Rule_V7_ShaderSet = "V7";
        public const string Rule_Identifier = "ID";
        /// <summary>
        /// V9 (2026-11-04): on a backend that flattens (set, binding) to absolute registers (D3D11, Metal) the register of EVERY resource comes from
        /// the bundle's flat binding map, written by the shader compiler. A set with resources and no map entry, an element without an entry, or a
        /// fragment-stage UAV register below the pipeline's colour-attachment count is a violation - never a counted fallback.
        /// </summary>
        public const string Rule_V9_FlatBindingMap = "V9";
        /// <summary>
        /// V7e (V10, 2026-11-04): a <c>StorageBufferSlot&lt;TElement&gt;</c> struct against its compiled std430 storage block - element size == array
        /// stride (or whole-block size), members by name/offset/size both ways; a bound buffer's structure stride == the verified stride.
        /// </summary>
        public const string Rule_V7e_StorageStructs = "V7e";

        /// <summary>
        /// V7f (AN_Monsters _BUGFIX/70): on D3D11 a storage-buffer slot binds only a RAW buffer (<see cref="BufferDescription.RawBuffer"/>). SPIRV-Cross
        /// emits every GLSL <c>buffer</c> block as an HLSL <c>(RW)ByteAddressBuffer</c>; a STRUCTURED view bound to that declaration is undefined
        /// behaviour (observed: dynamic-index loads return zeros, constant-index loads and raw stores work - so a write-then-readback test passes).
        /// </summary>
        public const string Rule_V7f_StorageRawOnD3D11 = "V7f";

        // ───────────────────────── D10: shape <-> bundle, both directions, by NAME ─────────────────────────

        /// <summary>
        /// Verify a shape descriptor against a compiled bundle: declared bundle name; every compiled resource has a same-named slot of an
        /// accepting kind and every slot a compiled resource; every <c>UniformBlock&lt;T&gt;</c> struct matches its block member-by-member
        /// (name, offset, size, total); the concatenated vertex inputs match the compiled vertex inputs by name/location/format; every
        /// compiled name is a legal C# identifier (so it CAN be a field). No GPU objects.
        /// </summary>
        public static List<ShaderContractViolation> VerifyShape(ShapeDescriptor shape, string bundleName, VdShaderInterface itf, ResourceLayoutDescription[] compiledLayouts)
        {
            if (shape is null) throw new ArgumentNullException(nameof(shape));
            var violations = new List<ShaderContractViolation>();
            if (itf is null) { violations.Add(new ShaderContractViolation(Rule_V7_ShaderSet, bundleName ?? "?", "bundle without interface", "", "recompile with the current Veldrid.SPIRV (format version 2)")); return violations; }
            if (string.IsNullOrEmpty(shape.BundleName))
                violations.Add(new ShaderContractViolation(Rule_V7_ShaderSet, "(shape)", "no [ShaderBundle(\"...\")]", $"bundle '{bundleName}'", "a shape must name its bundle"));
            else if (!string.Equals(shape.BundleName, bundleName, StringComparison.Ordinal))
                violations.Add(new ShaderContractViolation(Rule_V7_ShaderSet, shape.BundleName, $"shape declares bundle '{shape.BundleName}'", $"bound to bundle '{bundleName}'", "wrong bundle for this shape"));

            // Resources (V7b by construction): compiled element <-> slot field.
            var slotsByName = shape.Slots.ToDictionary(s => s.Name, StringComparer.Ordinal);
            var compiledNames = new HashSet<string>(StringComparer.Ordinal);
            compiledLayouts ??= [];
            for (int set = 0; set < compiledLayouts.Length; set++)
            {
                var elements = compiledLayouts[set].Elements ?? [];
                for (int binding = 0; binding < elements.Length; binding++)
                {
                    var e = elements[binding];
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    compiledNames.Add(e.Name);
                    if (!IsLegalCSharpIdentifier(e.Name))
                        violations.Add(new ShaderContractViolation(Rule_Identifier, e.Name, $"GLSL resource at set {set} binding {binding}", "not a legal C# identifier", "rename the GLSL variable so it can be a shape field"));
                    if (!slotsByName.TryGetValue(e.Name, out var slot))
                    {
                        violations.Add(new ShaderContractViolation(Rule_V7b_ResourceLayouts, e.Name, "shape: no field", $"shader {e.Kind} at set {set} binding {binding}", $"declare a {SlotTypeFor(e.Kind)} field named '{e.Name}'"));
                        continue;
                    }
                    if (!slot.AcceptedKinds.Contains(e.Kind))
                        violations.Add(new ShaderContractViolation(Rule_V7b_ResourceLayouts, e.Name, $"shape field {slot.SlotType.Name}", $"shader {e.Kind}", $"field kind does not accept the shader resource kind (use {SlotTypeFor(e.Kind)})"));
                }
            }
            foreach (var slot in shape.Slots)
                if (!compiledNames.Contains(slot.Name))
                    violations.Add(new ShaderContractViolation(Rule_V7b_ResourceLayouts, slot.Name, $"shape field {slot.SlotType.Name} '{slot.Name}'", "shader: no such resource", "a shape field names a resource the compiled shader does not declare"));

            // Uniform structs (V7c by construction).
            foreach (var slot in shape.Slots.Where(s => s.StructType != null))
            {
                var block = itf.UniformBlocks.FirstOrDefault(b => string.Equals(b.Name, slot.Name, StringComparison.Ordinal));
                if (block is null)
                {
                    violations.Add(new ShaderContractViolation("METADATA", slot.Name, "uniform slot", "missing compiled member layout",
                        "cannot verify a uniform struct without its compiled block members"));
                    continue;
                }
                VerifyStructAgainstBlock(slot.StructType, block, violations);
            }

            // Storage structs (V7e, V10). A bundle compiled before V10 has no storageBlocks: that is a METADATA violation ("recompile"), never a guess.
            foreach (var slot in shape.Slots.Where(s => s.StorageElementType != null))
            {
                var block = (itf.StorageBlocks ?? []).FirstOrDefault(b => string.Equals(b.Name, slot.Name, StringComparison.Ordinal));
                if (block is null)
                {
                    violations.Add(new ShaderContractViolation("METADATA", slot.Name, "storage slot", "missing compiled storage block layout",
                        "cannot verify a storage element struct without its compiled std430 layout - recompile the bundle with Veldrid.SPIRV >= V10 (native ABI 3)"));
                    continue;
                }
                VerifyStorageStructAgainstBlock(slot.StorageElementType, block, violations);
            }

            // Vertex inputs (V7a by construction): field order = slot order; locations run across slots.
            VerifyVertexInputs(shape.VertexInputs.Select(v => v.Layout).ToArray(), itf.VertexInputs, violations);
            foreach (var vi in itf.VertexInputs)
                if (!IsLegalCSharpIdentifier(vi.Name))
                    violations.Add(new ShaderContractViolation(Rule_Identifier, vi.Name, $"GLSL vertex input at location {vi.Location}", "not a legal C# identifier", "rename the GLSL variable so it can be a vertex struct field"));
            return violations;
        }

        private static string SlotTypeFor(ResourceKind kind) => kind switch
        {
            ResourceKind.UniformBuffer => "UniformBlock<T>", ResourceKind.TextureReadOnly => "TextureSlot", ResourceKind.Sampler => "SamplerSlot",
            ResourceKind.StructuredBufferReadOnly or ResourceKind.StructuredBufferReadWrite => "StorageBufferSlot<TElement>", ResourceKind.TextureReadWrite => "StorageImageSlot", _ => kind.ToString(),
        };

        /// <summary>C# identifier rule (letters/digits/underscore, not starting with a digit, not a keyword). The GLSL name must be usable verbatim as a field name.</summary>
        public static bool IsLegalCSharpIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_')) return false;
            if (!name.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;
            return !CSharpKeywords.Contains(name);
        }
        private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override","params","private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
        };

        // ───────────────────────── pipeline-creation backstop (V7) ─────────────────────────

        /// <summary>
        /// Called by <see cref="ResourceFactory.CreateGraphicsPipeline(ref GraphicsPipelineDescription)"/>. Shaders not created from a
        /// verified bundle are REFUSED (the only exception is the internal legacy flag Veldrid.ImGui sets until its shaders move to a
        /// bundle). Bundle shaders are checked for vertex layouts and resource layouts by name (the shape already proved the structs).
        /// </summary>
        public static void ThrowIfViolated(ref GraphicsPipelineDescription description)
        {
            var shaders = description.ShaderSet.Shaders ?? [];
            PrecompiledShaderResult bundle = null;
            var violations = new List<ShaderContractViolation>();
            foreach (Shader shader in shaders)
            {
                var fromBundle = ShaderInterfaceRegistry.TryGetBundle(shader);
                if (fromBundle is null)
                {
                    violations.Add(new ShaderContractViolation(Rule_V7_ShaderSet, shader.Stage.ToString(), "shader not created from a .vdshader bundle", "", "all shaders must come from ResourceFactory.CreateFromBundle (ShaderBundlePipeline); raw CreateShader / CreateFromSpirv paths are not verifiable"));
                    continue;
                }
                if (bundle is null) bundle = fromBundle;
                else if (!ReferenceEquals(bundle, fromBundle) && !string.Equals(bundle.BundleName, fromBundle.BundleName, StringComparison.Ordinal))
                    violations.Add(new ShaderContractViolation(Rule_V7_ShaderSet, shader.Stage.ToString(), $"bundle '{bundle.BundleName}'", $"bundle '{fromBundle.BundleName}'", "all shaders of one pipeline must come from the same bundle"));
            }
            if (violations.Count > 0) throw new ShaderContractException(bundle?.BundleName ?? "(no bundle)", violations);
            if (bundle is null) return;   // no shaders at all: nothing to verify (backend validation rejects it)
            if (bundle.Interface is null)
                throw new ShaderContractException(bundle.BundleName ?? "?", [new ShaderContractViolation(Rule_V7_ShaderSet, bundle.BundleName ?? "?", "bundle without interface", "", "recompile the bundle with the current Veldrid.SPIRV (format version 2)")]);
            var layoutDescriptions = (description.ResourceLayouts ?? []).Select(l => l?.Description ?? default).ToArray();
            VerifyVertexInputs(description.ShaderSet.VertexLayouts, bundle.Interface.VertexInputs, violations);
            VerifyResourceLayouts(layoutDescriptions, bundle.ResourceLayouts, violations);
            if (violations.Count > 0) throw new ShaderContractException(bundle.BundleName ?? "?", violations);
        }

        /// <summary>Compute equivalent of <see cref="ThrowIfViolated(ref GraphicsPipelineDescription)"/>.</summary>
        public static void ThrowIfViolated(ref ComputePipelineDescription description)
        {
            if (description.ComputeShader is null) return;
            var bundle = ShaderInterfaceRegistry.TryGetBundle(description.ComputeShader);
            if (bundle is null)
                throw new ShaderContractException("(no bundle)", [new ShaderContractViolation(Rule_V7_ShaderSet, "Compute", "shader not created from a .vdshader bundle", "", "all shaders must come from ResourceFactory.CreateFromBundle")]);
            var violations = new List<ShaderContractViolation>();
            VerifyResourceLayouts((description.ResourceLayouts ?? []).Select(l => l?.Description ?? default).ToArray(), bundle.ResourceLayouts, violations);
            if (violations.Count > 0) throw new ShaderContractException(bundle.BundleName ?? "?", violations);
        }

        // ───────────────────────── the rules ─────────────────────────

        // V7a: flatten the vertex layouts in slot order (location = running index across layouts, Veldrid's rule); each element must be
        // the compiled input of that location BY NAME, with a component-compatible format.
        internal static void VerifyVertexInputs(VertexLayoutDescription[] layouts, VdShaderVertexInput[] compiled, List<ShaderContractViolation> violations)
        {
            compiled ??= [];
            var byLocation = compiled.ToDictionary(i => i.Location);
            uint location = 0;
            var seen = new HashSet<uint>();
            foreach (var layout in layouts ?? [])
            {
                foreach (var element in layout.Elements ?? [])
                {
                    if (!byLocation.TryGetValue(location, out var input))
                        violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, element.Name ?? "(unnamed)", $"vertex element at location {location} {element.Format}", "shader: no input at this location", "the vertex struct(s) declare more fields than the shader consumes"));
                    else
                    {
                        if (!string.Equals(element.Name, input.Name, StringComparison.Ordinal))
                            violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, input.Name, $"vertex field '{element.Name}' at location {location}", $"shader input '{input.Name}' at location {location}", "vertex struct field NAME must equal the GLSL input name (field order = location order)"));
                        if (!FormatsCompatible(element.Format, input.Format))
                            violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, input.Name, $"vertex format {element.Format}", $"shader type {input.Format}", "component count / integer-vs-float mismatch"));
                    }
                    seen.Add(location);
                    location++;
                }
            }
            foreach (var input in compiled.OrderBy(i => i.Location))
                if (!seen.Contains(input.Location))
                    violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, input.Name, "vertex structs: no field", $"shader input at location {input.Location} {input.Format}", "the shader consumes an input no vertex struct provides"));
        }

        /// <summary>A vertex format feeds a GLSL input when component counts agree and integer-ness agrees (normalised formats read as float).</summary>
        public static bool FormatsCompatible(VertexElementFormat pipeline, VertexElementFormat glsl)
        {
            if (FormatHelpers.GetElementCount(pipeline) != FormatHelpers.GetElementCount(glsl)) return false;
            return IsIntegerFormat(glsl) == IsIntegerFormat(pipeline);
        }

        private static bool IsIntegerFormat(VertexElementFormat f) => f switch
        {
            VertexElementFormat.Byte2 or VertexElementFormat.Byte4 or VertexElementFormat.SByte2 or VertexElementFormat.SByte4 or
            VertexElementFormat.UShort2 or VertexElementFormat.UShort4 or VertexElementFormat.Short2 or VertexElementFormat.Short4 or
            VertexElementFormat.UInt1 or VertexElementFormat.UInt2 or VertexElementFormat.UInt3 or VertexElementFormat.UInt4 or
            VertexElementFormat.Int1 or VertexElementFormat.Int2 or VertexElementFormat.Int3 or VertexElementFormat.Int4 => true,
            _ => false,
        };

        // V7b: ResourceLayouts[set].Elements[binding] must be the compiled resource at (set, binding) BY NAME, same kind.
        internal static void VerifyResourceLayouts(ResourceLayoutDescription[] layouts, ResourceLayoutDescription[] compiled, List<ShaderContractViolation> violations)
        {
            layouts ??= [];
            compiled ??= [];
            for (int set = 0; set < Math.Max(layouts.Length, compiled.Length); set++)
            {
                var pipelineElements = set < layouts.Length ? (layouts[set].Elements ?? []) : [];
                var compiledElements = set < compiled.Length ? (compiled[set].Elements ?? []) : [];
                for (int binding = 0; binding < Math.Max(pipelineElements.Length, compiledElements.Length); binding++)
                {
                    bool compiledUsed = binding < compiledElements.Length && !string.IsNullOrEmpty(compiledElements[binding].Name);
                    bool pipelineHas = binding < pipelineElements.Length && !string.IsNullOrEmpty(pipelineElements[binding].Name);
                    string slot = $"set {set} binding {binding}";
                    if (compiledUsed && !pipelineHas)
                        violations.Add(new ShaderContractViolation(Rule_V7b_ResourceLayouts, compiledElements[binding].Name, "pipeline: no element", $"shader {compiledElements[binding].Kind} '{compiledElements[binding].Name}' at {slot}", "the shader binds a resource the pipeline's ResourceLayouts do not declare"));
                    else if (compiledUsed && pipelineHas)
                    {
                        var p = pipelineElements[binding]; var c = compiledElements[binding];
                        if (!string.Equals(p.Name, c.Name, StringComparison.Ordinal))
                            violations.Add(new ShaderContractViolation(Rule_V7b_ResourceLayouts, c.Name, $"pipeline element '{p.Name}' at {slot}", $"shader resource '{c.Name}' at {slot}", "resource layout element NAME must equal the GLSL resource name"));
                        if (p.Kind != c.Kind)
                            violations.Add(new ShaderContractViolation(Rule_V7b_ResourceLayouts, c.Name, $"pipeline kind {p.Kind}", $"shader kind {c.Kind}", $"resource kind differs at {slot}"));
                    }
                }
            }
        }

        // ───────────────────────── V7d: one ResourceSet bound across pipelines of DIFFERENT bundles ─────────────────────────
        //
        // THE GOAL of the Veldrid / Veldrid.SPIRV / .vdshader system (user, 2026-11-01): the user's C# data lands at the GLSL
        // (set, binding) they asked for, NO MATTER what the platform shader compiler does. Names + (set, binding) are the contract;
        // the backend register a compiler assigns to that binding is an implementation detail resolved PER PIPELINE at bind time
        // (D3D11CommandList / MTLCommandList translate through the pipeline's own layout, never the set's).
        //
        // So a set built from shape A may be bound on shape B's pipeline iff both shaders DECLARE that descriptor set identically:
        // same names, kinds, stages and options at the same bindings (Vulkan's descriptor-set-layout compatibility rule). A shader
        // that reads two of six resources still declares all six, or its set-1 layout is not the material set's and this refuses it.

        public const string Rule_V7d_SharedSetBinding = "V7d";

        /// <summary>
        ///     V7d: may a set created against <paramref name="setLayout"/> (the compiled layout of the set's bundle) be bound at
        ///     descriptor set <paramref name="set"/> of a pipeline whose bundle compiled <paramref name="pipelineLayout"/>? Every binding
        ///     must be declared identically on both sides - name, kind, stages, options - including which bindings are unused. Pure over data.
        /// </summary>
        public static void VerifySharedSetBinding(uint set, ResourceLayoutDescription pipelineLayout, ResourceLayoutDescription setLayout, List<ShaderContractViolation> violations)
            => CheckSharedSetBinding(set, pipelineLayout, setLayout, violations);

        // HOT-PATH ZERO-ALLOCATION CONTRACT: resource sets are checked at bind time and again on draws. With a null
        // collector, this routine checks the same rules using only array access/comparisons and returns false at the first
        // mismatch. It must not create lists, strings, enumerators, closures or formatted diagnostics, even on rejection.
        // The caller reruns with a collector ONLY after failure; that cold pass accumulates every per-binding mismatch.
        // In particular, never hoist $"set {set}" out of the failure-reporting branch: that allocated on successful checks.
        // Both modes intentionally share this implementation so optimization cannot weaken validation or change its rules.
        internal static bool CheckSharedSetBinding(uint set, ResourceLayoutDescription pipelineLayout, ResourceLayoutDescription setLayout, List<ShaderContractViolation> violations)
        {
            var mine = pipelineLayout.Elements ?? [];
            var theirs = setLayout.Elements ?? [];
            if (mine.Length != theirs.Length)
            {
                violations?.Add(new ShaderContractViolation(Rule_V7d_SharedSetBinding, $"set {set}", $"pipeline bundle: {mine.Length} bindings", $"set's bundle: {theirs.Length} bindings", "a shared set requires both shaders to declare the same bindings of that set"));
                return false;
            }
            bool valid = true;
            for (int b = 0; b < mine.Length; b++)
            {
                var p = mine[b]; var s = theirs[b];
                if (p.Name != s.Name || p.Kind != s.Kind || p.Stages != s.Stages || p.Options != s.Options)
                {
                    if (violations is null) return false;
                    valid = false;
                    violations.Add(new ShaderContractViolation(Rule_V7d_SharedSetBinding, $"set {set} binding {b}", $"pipeline bundle: {Describe(p)}", $"set's bundle: {Describe(s)}", "a shared set requires identical declarations at every binding (a shader that reads part of a set still declares all of it)"));
                }
            }
            return valid;
        }

        private static string Describe(ResourceLayoutElementDescription e)
            => string.IsNullOrEmpty(e.Name) ? "(unused)" : $"{e.Name} {e.Kind} {e.Stages}{(e.Options != ResourceLayoutElementOptions.None ? " " + e.Options : "")}";

        /// <summary>
        /// V7c: a C# struct against a compiled block - EVERY C# field has a GLSL member of the same NAME at the same std140 offset and
        /// size, EVERY GLSL member has a C# field, and the struct size equals the 16-rounded block size. No name is exempt: a struct
        /// reaches the rounded size with <c>[StructLayout(LayoutKind.Sequential, Size = N)]</c>, never with fields the GLSL does not declare.
        /// </summary>
        public static void VerifyStructAgainstBlock(Type structType, VdShaderUniformBlock block, List<ShaderContractViolation> violations)
        {
            string structName = structType.Name; string glslBlockName = block.Name;
            if (!structType.IsValueType)
            {
                violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, glslBlockName, $"C# {structName} is a class", "uniform block", "UniformBlock<T> requires an unmanaged struct"));
                return;
            }
            int structSize = Marshal.SizeOf(structType);
            if (structSize != block.SizeBytes)
                violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, glslBlockName, $"C# {structName}: {structSize} bytes", $"shader block: {block.SizeBytes} bytes (std140, 16-rounded)", "struct size must equal the std140 block size - declare [StructLayout(LayoutKind.Sequential, Size = N)] to reach the rounded size"));
            var fields = structType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var membersByName = block.Members.ToDictionary(m => m.Name, StringComparer.Ordinal);
            foreach (var field in fields)
            {
                int offset = (int)Marshal.OffsetOf(structType, field.Name);
                int size = Marshal.SizeOf(field.FieldType);
                if (!membersByName.TryGetValue(field.Name, out var member))
                {
                    violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{field.Name}", $"C# field {structName}.{field.Name}", "shader: no such member", "struct field NAME must equal the GLSL member name"));
                    continue;
                }
                if (offset != member.OffsetBytes || size != member.SizeBytes)
                    violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{field.Name}", $"C# @ {offset} ({size} B)", $"shader {member.Type} @ {member.OffsetBytes} ({member.SizeBytes} B)", "field placement must equal the std140 member placement"));
            }
            foreach (var member in block.Members)
                if (!fields.Any(f => string.Equals(f.Name, member.Name, StringComparison.Ordinal)))
                    violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{member.Name}", $"C# {structName}: no such field", $"shader {member.Type} @ {member.OffsetBytes}", "every GLSL member must have a struct field of the same name"));
        }

        /// <summary>
        /// V7e (V10): a <c>StorageBufferSlot&lt;TElement&gt;</c> struct against a compiled storage block. A block that is ONLY a runtime array:
        /// <paramref name="elementType"/> is the array element - its size must equal the std430 array stride and, for a struct element, its
        /// fields must match the element members by name/offset/size both ways. A block with no runtime array: <paramref name="elementType"/>
        /// is the whole block - size equals the declared block size, fields match the members by name (same law as V7c, std430 placement).
        /// Fixed members plus a runtime array is refused: there is no single C# struct that is that block - split it into two buffers.
        /// </summary>
        public static void VerifyStorageStructAgainstBlock(Type elementType, VdShaderStorageBlock block, List<ShaderContractViolation> violations)
        {
            string typeName = elementType.Name; string blockName = block.Name;
            if (!elementType.IsValueType)
            {
                violations.Add(new ShaderContractViolation(Rule_V7e_StorageStructs, blockName, $"C# {typeName} is a class", "storage block", "StorageBufferSlot<TElement> requires an unmanaged struct"));
                return;
            }
            if (block.HasRuntimeArray && (block.Members?.Length ?? 0) > 0)
            {
                violations.Add(new ShaderContractViolation(Rule_V7e_StorageStructs, blockName, $"C# {typeName}", $"shader block: {block.Members.Length} fixed member(s) + runtime array {block.RuntimeArrayName}[]",
                    "a storage block with fixed members AND a runtime array has no single element struct - split the header and the array into two buffers"));
                return;
            }
            int size = Marshal.SizeOf(elementType);
            if (block.HasRuntimeArray)
            {
                if (size != block.RuntimeArrayStrideBytes)
                    violations.Add(new ShaderContractViolation(Rule_V7e_StorageStructs, $"{blockName}.{block.RuntimeArrayName}[]", $"C# {typeName}: {size} bytes", $"shader element {block.RuntimeArrayElementType}: stride {block.RuntimeArrayStrideBytes} bytes (std430)",
                        "the element struct size must equal the compiled array stride"));
                // A struct element: members by name. A scalar / vector element (uint, vec4): the size check above is the whole contract.
                if (block.RuntimeArrayElementMembers is { Length: > 0 })
                    VerifyFieldsAgainstMembers(elementType, $"{blockName}.{block.RuntimeArrayName}[]", block.RuntimeArrayElementMembers, violations);
            }
            else
            {
                if (size != block.FixedSizeBytes)
                    violations.Add(new ShaderContractViolation(Rule_V7e_StorageStructs, blockName, $"C# {typeName}: {size} bytes", $"shader block: {block.FixedSizeBytes} bytes (std430)", "struct size must equal the compiled block size"));
                VerifyFieldsAgainstMembers(elementType, blockName, block.Members ?? [], violations);
            }
        }

        private static void VerifyFieldsAgainstMembers(Type structType, string where, VdShaderUniformMember[] members, List<ShaderContractViolation> violations)
        {
            var fields = structType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var membersByName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);
            foreach (var field in fields)
            {
                int offset = (int)Marshal.OffsetOf(structType, field.Name);
                int fieldSize = Marshal.SizeOf(field.FieldType);
                if (!membersByName.TryGetValue(field.Name, out var member))
                { violations.Add(new ShaderContractViolation(Rule_V7e_StorageStructs, $"{where}.{field.Name}", $"C# field {structType.Name}.{field.Name}", "shader: no such member", "struct field NAME must equal the GLSL member name")); continue; }
                if (offset != member.OffsetBytes || fieldSize != member.SizeBytes)
                    violations.Add(new ShaderContractViolation(Rule_V7e_StorageStructs, $"{where}.{field.Name}", $"C# @ {offset} ({fieldSize} B)", $"shader {member.Type} @ {member.OffsetBytes} ({member.SizeBytes} B)", "field placement must equal the std430 member placement"));
            }
            foreach (var member in members)
                if (!fields.Any(f => string.Equals(f.Name, member.Name, StringComparison.Ordinal)))
                    violations.Add(new ShaderContractViolation(Rule_V7e_StorageStructs, $"{where}.{member.Name}", $"C# {structType.Name}: no such field", $"shader {member.Type} @ {member.OffsetBytes}", "every GLSL member must have a struct field of the same name"));
        }
    }
}
