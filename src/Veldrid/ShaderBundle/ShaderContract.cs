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
                    if (e.Kind is ResourceKind.StructuredBufferReadOnly or ResourceKind.StructuredBufferReadWrite)
                        violations.Add(new ShaderContractViolation("UNSUPPORTED_LAYOUT", e.Name,
                            "storage buffer", "storage member/array-stride reflection not implemented",
                            "storage binding is refused until its C# layout can be verified; resource kind alone is insufficient"));
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
            ResourceKind.StructuredBufferReadOnly or ResourceKind.StructuredBufferReadWrite => "StorageBufferSlot", ResourceKind.TextureReadWrite => "StorageImageSlot", _ => kind.ToString(),
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

        /// <summary>V7c: a C# struct against a compiled block - field NAME == member name, Marshal.OffsetOf == std140 offset, size == size, struct size == block size; <c>_pad*</c> fields only in the std140 tail.</summary>
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
                violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, glslBlockName, $"C# {structName}: {structSize} bytes", $"shader block: {block.SizeBytes} bytes (std140)", "struct size must equal the std140 block size"));
            var fields = structType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var membersByName = block.Members.ToDictionary(m => m.Name, StringComparer.Ordinal);
            uint lastMemberEnd = block.Members.Length == 0 ? 0 : block.Members.Max(m => m.OffsetBytes + m.SizeBytes);
            foreach (var field in fields)
            {
                int offset = (int)Marshal.OffsetOf(structType, field.Name);
                int size = Marshal.SizeOf(field.FieldType);
                if (IsPaddingField(field.Name))
                {
                    if (offset < lastMemberEnd || offset + size > block.SizeBytes)
                        violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{field.Name}", $"C# padding @ {offset} ({size} B)", $"shader members end @ {lastMemberEnd}, block {block.SizeBytes} B", "padding fields may only occupy the std140 tail after the last member"));
                    continue;
                }
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

        /// <summary>A C# field that exists only to fill std140 tail padding: <c>_pad</c>, <c>_pad0</c>, <c>_padding</c>, <c>_padding1</c>...</summary>
        public static bool IsPaddingField(string fieldName) => fieldName.StartsWith("_pad", StringComparison.Ordinal);
    }
}
