using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Veldrid
{
    /// <summary>
    /// The C# side of a graphics pipeline as DATA (AN_Veldrid/_BUGFIX/BUGFIX_ShaderInterfaceContract_NameVerified.md S7): which bundle,
    /// the vertex layouts, the resource layout DESCRIPTIONS and the uniform structs. A renderer builds its
    /// <see cref="GraphicsPipelineDescription"/> FROM its shape (<see cref="ToGraphicsPipelineDescription"/>), and a unit test verifies
    /// the same shape against the compiled bundle with <see cref="ShaderContractVerifier.Verify(ShaderPipelineShape, VeldridShaderBundle)"/>
    /// - no GraphicsDevice, no window. Runtime V7 (at CreateGraphicsPipeline) remains the backstop for anything that bypasses shapes.
    /// </summary>
    public sealed record ShaderPipelineShape(
        string BundleName,
        VertexLayoutDescription[] VertexLayouts,
        ResourceLayoutDescription[] ResourceLayouts,
        Type[] UniformStructs)
    {
        /// <summary>A shape with no uniform structs declared (V7c still runs for structs registered globally for the bundle).</summary>
        public ShaderPipelineShape(string bundleName, VertexLayoutDescription[] vertexLayouts, ResourceLayoutDescription[] resourceLayouts)
            : this(bundleName, vertexLayouts, resourceLayouts, Array.Empty<Type>()) { }

        /// <summary>
        /// The runtime description for this shape: the shaders and the ResourceLayout OBJECTS created from <see cref="ResourceLayouts"/>
        /// (in order), plus the caller's fixed-function state. Using this is what keeps the tested shape and the created pipeline identical.
        /// </summary>
        public GraphicsPipelineDescription ToGraphicsPipelineDescription(
            Shader[] shaders,
            ResourceLayout[] resourceLayouts,
            BlendStateDescription blendState,
            DepthStencilStateDescription depthStencilState,
            RasterizerStateDescription rasterizerState,
            PrimitiveTopology primitiveTopology,
            OutputDescription outputs,
            SpecializationConstant[] specializations = null)
        {
            if (resourceLayouts.Length != ResourceLayouts.Length)
                throw new VeldridException($"ShaderPipelineShape '{BundleName}': {ResourceLayouts.Length} resource layout description(s) but {resourceLayouts.Length} ResourceLayout object(s) were supplied.");
            return new GraphicsPipelineDescription
            {
                BlendState = blendState,
                DepthStencilState = depthStencilState,
                RasterizerState = rasterizerState,
                PrimitiveTopology = primitiveTopology,
                ShaderSet = new ShaderSetDescription(VertexLayouts, shaders, specializations),
                ResourceLayouts = resourceLayouts,
                Outputs = outputs,
            };
        }
    }

    /// <summary>
    /// Marks a static, parameterless method returning <see cref="ShaderPipelineShape"/> or <see cref="IEnumerable{T}"/> of it. Every
    /// pipeline a renderer creates should have one; <see cref="ShaderPipelineContractRegistry.Enumerate"/> finds them for the test that
    /// verifies each against its bundle at BUILD/TEST time - the app never needs to run to learn that a layout and a shader disagree.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class VdShaderPipelineContractAttribute : Attribute { }

    /// <summary>Finds every <see cref="VdShaderPipelineContractAttribute"/> shape in an assembly and verifies them against their bundles.</summary>
    public static class ShaderPipelineContractRegistry
    {
        /// <summary>All declared shapes in <paramref name="assembly"/> (static methods marked <see cref="VdShaderPipelineContractAttribute"/>).</summary>
        public static IReadOnlyList<(string DeclaredBy, ShaderPipelineShape Shape)> Enumerate(Assembly assembly)
        {
            if (assembly is null) throw new ArgumentNullException(nameof(assembly));
            var result = new List<(string, ShaderPipelineShape)>();
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (Type type in types)
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<VdShaderPipelineContractAttribute>() is null) continue;
                    if (method.GetParameters().Length != 0)
                        throw new VeldridException($"[VdShaderPipelineContract] {type.FullName}.{method.Name} must be parameterless.");
                    object value = method.Invoke(null, null);
                    string declaredBy = $"{type.FullName}.{method.Name}";
                    switch (value)
                    {
                        case ShaderPipelineShape shape: result.Add((declaredBy, shape)); break;
                        case IEnumerable<ShaderPipelineShape> shapes: foreach (var s in shapes) result.Add((declaredBy, s)); break;
                        default: throw new VeldridException($"[VdShaderPipelineContract] {declaredBy} must return ShaderPipelineShape or IEnumerable<ShaderPipelineShape> (returned {value?.GetType().Name ?? "null"}).");
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Verify every declared shape in <paramref name="assembly"/> against the bundle <paramref name="resolveBundle"/> returns for its name
        /// (embedded Compiled/*.vdshader, or ShaderBundlePipeline.GetOrBuild for runtime-compiled GLSL - the SAME compiler the app uses).
        /// Returns one line per violation prefixed by the declaring method; empty = contract holds. A test asserts the list is empty.
        /// </summary>
        public static IReadOnlyList<string> VerifyAll(Assembly assembly, Func<string, VeldridShaderBundle> resolveBundle)
        {
            if (resolveBundle is null) throw new ArgumentNullException(nameof(resolveBundle));
            var lines = new List<string>();
            var shapes = Enumerate(assembly);
            if (shapes.Count == 0) lines.Add($"{assembly.GetName().Name}: no [VdShaderPipelineContract] shapes declared - every pipeline the assembly creates should have one.");
            foreach (var (declaredBy, shape) in shapes)
            {
                VeldridShaderBundle bundle;
                try { bundle = resolveBundle(shape.BundleName); }
                catch (Exception ex) { lines.Add($"{declaredBy}: bundle '{shape.BundleName}' could not be resolved: {ex.Message}"); continue; }
                if (bundle is null) { lines.Add($"{declaredBy}: bundle '{shape.BundleName}' could not be resolved (null)"); continue; }
                foreach (var v in ShaderContractVerifier.Verify(shape, bundle))
                    lines.Add($"{declaredBy} [{shape.BundleName}] {v}");
            }
            return lines;
        }

        /// <summary>Bundle names that have no declared shape in <paramref name="assembly"/> - a pipeline the tests cannot see (runtime V7 is its only guard).</summary>
        public static IReadOnlyList<string> BundlesWithoutShapes(Assembly assembly, IEnumerable<string> bundleNames)
        {
            var covered = new HashSet<string>(Enumerate(assembly).Select(s => s.Shape.BundleName), StringComparer.Ordinal);
            return bundleNames.Where(n => !covered.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
    }
}