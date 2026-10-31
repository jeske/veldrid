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
    /// Thrown by <see cref="ResourceFactory.CreateGraphicsPipeline(ref GraphicsPipelineDescription)"/> when a pipeline description does
    /// not match, BY NAME, the compiled interface of the bundle its shaders came from (rules V7a vertex inputs, V7b resource layouts,
    /// V7c uniform structs). Never a warning. Spec: AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md.
    /// </summary>
    public sealed class ShaderContractException : VeldridException
    {
        /// <summary>Every violation found (the message lists them all).</summary>
        public IReadOnlyList<ShaderContractViolation> Violations { get; }
        /// <summary>The bundle the shaders came from.</summary>
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
            var lines = new List<string> { $"Shader contract violated creating a pipeline for bundle '{bundleName}' ({violations.Count} violation(s)). A slot number is never evidence of agreement; every boundary joins by NAME." };
            lines.AddRange(violations.Select(v => "  " + v));
            lines.Add("  See AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Marks a C# struct as the CPU side of GLSL <c>uniform {BlockName}</c> in bundle <c>{BundleName}</c> (D7). At pipeline creation the
    /// struct's fields are compared BY NAME to the compiled block members: <see cref="Marshal.OffsetOf(Type, string)"/> must equal the
    /// std140 offset, field size the member size, and <see cref="Marshal.SizeOf(Type)"/> the block size. Register the assembly once with
    /// <see cref="ShaderUniformBlockRegistry.Register(Assembly)"/> (explicit, AOT-friendly).
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
    public sealed class VdShaderUniformBlockAttribute(string bundleName, string glslBlockName) : Attribute
    {
        /// <summary>Bare bundle name (as in the .vdshader shaderName), e.g. <c>GBufferMesh</c>.</summary>
        public string BundleName { get; } = bundleName;
        /// <summary>GLSL block name, e.g. <c>Transforms</c>.</summary>
        public string GlslBlockName { get; } = glslBlockName;
    }

    /// <summary>The C# uniform structs that participate in V7c, by bundle name. Consumers register their assemblies at startup.</summary>
    public static class ShaderUniformBlockRegistry
    {
        private static readonly object Lock = new();
        private static readonly Dictionary<string, List<(Type StructType, string GlslBlockName)>> ByBundle = new(StringComparer.Ordinal);

        /// <summary>Scan <paramref name="assembly"/> for <see cref="VdShaderUniformBlockAttribute"/> structs. Idempotent.</summary>
        public static void Register(Assembly assembly)
        {
            if (assembly is null) throw new ArgumentNullException(nameof(assembly));
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            lock (Lock)
            {
                foreach (Type type in types)
                {
                    if (!type.IsValueType) continue;
                    foreach (var attr in type.GetCustomAttributes<VdShaderUniformBlockAttribute>(false))
                    {
                        if (!ByBundle.TryGetValue(attr.BundleName, out var list)) ByBundle[attr.BundleName] = list = new();
                        if (!list.Any(e => e.StructType == type && e.GlslBlockName == attr.GlslBlockName)) list.Add((type, attr.GlslBlockName));
                    }
                }
            }
        }

        /// <summary>Register one struct explicitly (tests, generated code).</summary>
        public static void Register(Type structType, string bundleName, string glslBlockName)
        {
            lock (Lock)
            {
                if (!ByBundle.TryGetValue(bundleName, out var list)) ByBundle[bundleName] = list = new();
                if (!list.Any(e => e.StructType == structType && e.GlslBlockName == glslBlockName)) list.Add((structType, glslBlockName));
            }
        }

        /// <summary>The structs registered for a bundle (empty when none).</summary>
        public static IReadOnlyList<(Type StructType, string GlslBlockName)> For(string bundleName)
        {
            lock (Lock) return ByBundle.TryGetValue(bundleName, out var list) ? list.ToArray() : Array.Empty<(Type, string)>();
        }

        /// <summary>Forget everything (tests).</summary>
        public static void Clear() { lock (Lock) ByBundle.Clear(); }
    }

    /// <summary>Which bundle a <see cref="Shader"/> came from. Filled by <see cref="ResourceFactory.CreateFromBundle(VeldridShaderBundle, Func{string, byte[]}, string)"/>.</summary>
    public static class ShaderInterfaceRegistry
    {
        private static readonly ConditionalWeakTable<Shader, PrecompiledShaderResult> Table = new();

        internal static void Register(Shader shader, PrecompiledShaderResult result) => Table.AddOrUpdate(shader, result);

        /// <summary>The bundle result this shader was created from, or null for shaders not created through a bundle.</summary>
        public static PrecompiledShaderResult? TryGetBundle(Shader shader) => Table.TryGetValue(shader, out var r) ? r : null;
    }

    /// <summary>
    /// The pipeline-creation half of the shader interface contract (V7). Pure over descriptions; <see cref="ResourceFactory"/> calls
    /// <see cref="ThrowIfViolated"/> before creating any graphics pipeline whose shaders came from a bundle.
    /// </summary>
    public static class ShaderContractVerifier
    {
        public const string Rule_V7a_VertexInputs = "V7a";
        public const string Rule_V7b_ResourceLayouts = "V7b";
        public const string Rule_V7c_UniformStructs = "V7c";
        public const string Rule_V7_ShaderSet = "V7";

        /// <summary>Verify and throw <see cref="ShaderContractException"/>. No-op when no shader in the set came from a bundle.</summary>
        public static void ThrowIfViolated(ref GraphicsPipelineDescription description)
        {
            var shaders = description.ShaderSet.Shaders;
            if (shaders is null || shaders.Length == 0) return;
            PrecompiledShaderResult? bundle = null;
            var violations = new List<ShaderContractViolation>();
            foreach (Shader shader in shaders)
            {
                var fromBundle = ShaderInterfaceRegistry.TryGetBundle(shader);
                if (fromBundle is null) continue;
                if (bundle is null) bundle = fromBundle;
                else if (!ReferenceEquals(bundle, fromBundle) && !string.Equals(bundle.BundleName, fromBundle.BundleName, StringComparison.Ordinal))
                    violations.Add(new ShaderContractViolation(Rule_V7_ShaderSet, shader.Stage.ToString(), $"bundle '{bundle.BundleName}'", $"bundle '{fromBundle.BundleName}'", "all shaders of one pipeline must come from the same bundle"));
            }
            if (bundle is null) return;   // legacy shaders (CreateFromSpirv / raw bytecode): nothing to verify against - D1 removes those paths
            if (bundle.Interface is null)
                throw new ShaderContractException(bundle.BundleName ?? "?", [new ShaderContractViolation(Rule_V7_ShaderSet, bundle.BundleName ?? "?", "bundle without interface", "", "recompile the bundle with the current Veldrid.SPIRV (format version 2)")]);

            violations.AddRange(Verify(ref description, bundle));
            if (violations.Count > 0) throw new ShaderContractException(bundle.BundleName ?? "?", violations);
        }

        /// <summary>All V7 violations of <paramref name="description"/> against <paramref name="bundle"/>. Pure.</summary>
        public static List<ShaderContractViolation> Verify(ref GraphicsPipelineDescription description, PrecompiledShaderResult bundle)
        {
            var layoutDescriptions = (description.ResourceLayouts ?? []).Select(l => l?.Description ?? default).ToArray();
            return VerifyCore(description.ShaderSet.VertexLayouts, layoutDescriptions, bundle.BundleName, Array.Empty<Type>(), bundle.Interface, bundle.ResourceLayouts);
        }

        /// <summary>
        /// S7: verify a <see cref="ShaderPipelineShape"/> (pure data) against a compiled bundle - NO device. This is what the per-assembly", "        /// test runs for every [VdShaderPipelineContract] shape; the same core the runtime check uses. The shape's UniformStructs are checked
        /// in addition to any structs registered globally for the bundle.
        /// </summary>
        public static List<ShaderContractViolation> Verify(ShaderPipelineShape shape, VeldridShaderBundle bundle)
        {
            if (shape is null) throw new ArgumentNullException(nameof(shape));
            if (bundle is null) throw new ArgumentNullException(nameof(bundle));
            if (bundle.Interface is null)
                return [new ShaderContractViolation(Rule_V7_ShaderSet, bundle.ShaderName ?? shape.BundleName, "bundle without interface", "", "recompile the bundle with the current Veldrid.SPIRV (format version 2)")];
            if (!string.Equals(bundle.ShaderName, shape.BundleName, StringComparison.Ordinal))
                return [new ShaderContractViolation(Rule_V7_ShaderSet, shape.BundleName, $"shape for '{shape.BundleName}'", $"bundle '{bundle.ShaderName}'", "the resolver returned a different bundle than the shape names")];
            return VerifyCore(shape.VertexLayouts, shape.ResourceLayouts, shape.BundleName, shape.UniformStructs, bundle.Interface, bundle.GetResourceLayouts());
        }

        private static List<ShaderContractViolation> VerifyCore(VertexLayoutDescription[] vertexLayouts, ResourceLayoutDescription[] resourceLayouts, string bundleName, Type[] explicitStructs, VdShaderInterface itf, ResourceLayoutDescription[] compiledLayouts)
        {
            var violations = new List<ShaderContractViolation>();
            VerifyVertexInputs(vertexLayouts, itf.VertexInputs, violations);
            VerifyResourceLayouts(resourceLayouts, compiledLayouts, violations);
            var structs = new List<(Type StructType, string GlslBlockName)>(ShaderUniformBlockRegistry.For(bundleName ?? ""));
            foreach (Type t in explicitStructs ?? [])
                foreach (var attr in t.GetCustomAttributes<VdShaderUniformBlockAttribute>(false))
                    if (string.Equals(attr.BundleName, bundleName, StringComparison.Ordinal) && !structs.Any(s => s.StructType == t && s.GlslBlockName == attr.GlslBlockName))
                        structs.Add((t, attr.GlslBlockName));
            foreach (Type t in explicitStructs ?? [])
                if (!t.GetCustomAttributes<VdShaderUniformBlockAttribute>(false).Any(a => string.Equals(a.BundleName, bundleName, StringComparison.Ordinal)))
                    violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, t.Name, $"shape UniformStructs: {t.Name}", $"no [VdShaderUniformBlock(\"{bundleName}\", ...)] on it", "a struct listed on a shape must be attributed to that shape's bundle"));
            VerifyUniformStructs(bundleName, itf.UniformBlocks, structs, violations);
            return violations;
        }

        // V7a: flatten the vertex layouts in slot order (location = running index across layouts, Veldrid's rule); each element must be
        // the compiled input of that location BY NAME, with a component-compatible format.
        private static void VerifyVertexInputs(VertexLayoutDescription[] layouts, VdShaderVertexInput[] compiled, List<ShaderContractViolation> violations)
        {
            var byLocation = compiled.ToDictionary(i => i.Location);
            uint location = 0;
            var seen = new HashSet<uint>();
            foreach (var layout in layouts ?? [])
            {
                foreach (var element in layout.Elements ?? [])
                {
                    if (!byLocation.TryGetValue(location, out var input))
                        violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, element.Name ?? "(unnamed)", $"pipeline vertex element at location {location} {element.Format}", "shader: no input at this location", "the vertex layouts declare more elements than the shader consumes"));
                    else
                    {
                        if (!string.Equals(element.Name, input.Name, StringComparison.Ordinal))
                            violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, input.Name, $"pipeline element '{element.Name}' at location {location}", $"shader input '{input.Name}' at location {location}", "vertex element NAME must equal the GLSL input name"));
                        if (!FormatsCompatible(element.Format, input.Format))
                            violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, input.Name, $"pipeline format {element.Format}", $"shader type {input.Format}", "component count / integer-vs-float mismatch"));
                    }
                    seen.Add(location);
                    location++;
                }
            }
            foreach (var input in compiled.OrderBy(i => i.Location))
                if (!seen.Contains(input.Location))
                    violations.Add(new ShaderContractViolation(Rule_V7a_VertexInputs, input.Name, "pipeline: no element", $"shader input at location {input.Location} {input.Format}", "the shader consumes an input the vertex layouts do not provide"));
        }

        /// <summary>A pipeline format feeds a GLSL input when component counts agree and integer-ness agrees (normalised formats read as float).</summary>
        public static bool FormatsCompatible(VertexElementFormat pipeline, VertexElementFormat glsl)
        {
            if (FormatHelpers.GetElementCount(pipeline) != FormatHelpers.GetElementCount(glsl)) return false;
            bool glslInteger = IsIntegerFormat(glsl);
            bool pipelineInteger = IsIntegerFormat(pipeline);
            return glslInteger == pipelineInteger;
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
        private static void VerifyResourceLayouts(ResourceLayoutDescription[] layouts, ResourceLayoutDescription[] compiled, List<ShaderContractViolation> violations)
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
                    // pipelineHas && !compiledUsed: an extra layout element the shader never binds - tolerated (shared layouts across shaders).
                }
            }
        }

        // V7c: every [VdShaderUniformBlock(bundle, block)] struct: field NAME + Marshal.OffsetOf + size == block member; struct size == block size.
        private static void VerifyUniformStructs(string bundleName, VdShaderUniformBlock[] blocks, IReadOnlyList<(Type StructType, string GlslBlockName)> structs, List<ShaderContractViolation> violations)
        {
            if (string.IsNullOrEmpty(bundleName)) return;
            foreach (var (structType, glslBlockName) in structs)
            {
                var block = blocks.FirstOrDefault(b => string.Equals(b.Name, glslBlockName, StringComparison.Ordinal));
                string structName = structType.Name;
                if (block is null)
                {
                    violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, glslBlockName, $"C# struct {structName}", "shader: no such uniform block", $"[VdShaderUniformBlock(\"{bundleName}\", \"{glslBlockName}\")] names a block the compiled shader does not declare"));
                    continue;
                }
                int structSize = Marshal.SizeOf(structType);
                if (structSize != block.SizeBytes)
                    violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, glslBlockName, $"C# {structName}: {structSize} bytes", $"shader block: {block.SizeBytes} bytes (std140)", "struct size must equal the std140 block size"));
                var fields = structType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var membersByName = block.Members.ToDictionary(m => m.Name, StringComparer.Ordinal);
                uint lastMemberEnd = block.Members.Length == 0 ? 0 : block.Members.Max(m => m.OffsetBytes + m.SizeBytes);
                foreach (var field in fields)
                {
                    if (IsPaddingField(field.Name))
                    {
                        // std140 pads a block to 16 bytes but names nothing there; a C# struct must fill it. Padding is legal ONLY in that
                        // tail: a "_pad" field overlapping a real member is a misaligned struct wearing a disguise.
                        int padOffset = (int)Marshal.OffsetOf(structType, field.Name);
                        int padSize = Marshal.SizeOf(field.FieldType);
                        if (padOffset < lastMemberEnd || padOffset + padSize > block.SizeBytes)
                            violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{field.Name}", $"C# padding @ {padOffset} ({padSize} B)", $"shader members end @ {lastMemberEnd}, block {block.SizeBytes} B", "padding fields may only occupy the std140 tail after the last member"));
                        continue;
                    }
                    if (!membersByName.TryGetValue(field.Name, out var member))
                    {
                        violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{field.Name}", $"C# field {structName}.{field.Name}", "shader: no such member", "struct field NAME must equal the GLSL member name"));
                        continue;
                    }
                    int offset = (int)Marshal.OffsetOf(structType, field.Name);
                    int size = Marshal.SizeOf(field.FieldType);
                    if (offset != member.OffsetBytes || size != member.SizeBytes)
                        violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{field.Name}", $"C# @ {offset} ({size} B)", $"shader {member.Type} @ {member.OffsetBytes} ({member.SizeBytes} B)", "field placement must equal the std140 member placement"));
                }
                foreach (var member in block.Members)
                    if (!fields.Any(f => string.Equals(f.Name, member.Name, StringComparison.Ordinal)))
                        violations.Add(new ShaderContractViolation(Rule_V7c_UniformStructs, $"{glslBlockName}.{member.Name}", $"C# {structName}: no such field", $"shader {member.Type} @ {member.OffsetBytes}", "every GLSL member must have a struct field of the same name"));
            }
        }

        /// <summary>A C# field that exists only to fill std140 tail padding: <c>_pad</c>, <c>_pad0</c>, <c>_padding</c>, <c>_padding1</c>...</summary>
        public static bool IsPaddingField(string fieldName)
            => fieldName.StartsWith("_pad", StringComparison.Ordinal);
    }
}