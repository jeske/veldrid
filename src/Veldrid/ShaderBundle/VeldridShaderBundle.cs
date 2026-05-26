using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veldrid
{
    /// <summary>
    /// Represents a Veldrid shader bundle (.vdshader) file.
    /// ONE file per shader variant, containing compiled shader data for ALL backends,
    /// plus the single <see cref="ResourceLayoutDescription"/>[] that is identical across all backends.
    /// This is the ONE way to load precompiled shaders in Veldrid.
    /// </summary>
    public sealed class VeldridShaderBundle
    {
        private const string PurposeText =
            "Veldrid precompiled shader bundle. Contains compiled shader bytecode for all supported " +
            "graphics backends plus the ResourceLayoutDescription[] needed to create matching " +
            "ResourceLayout objects. Load this file with Veldrid's shader loading API to get " +
            "correct resource bindings on every backend.";

        /// <summary>Human-readable explanation of what this file is.</summary>
        [JsonPropertyName("purpose")]
        public string Purpose { get; set; } = PurposeText;

        /// <summary>Format version number.</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>The shader variant name (e.g., "Terrain", "NvgFill", "SolidFill").</summary>
        [JsonPropertyName("shaderName")]
        public string ShaderName { get; set; }

        /// <summary>Original vertex shader source filename (informational).</summary>
        [JsonPropertyName("vertexSource")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VertexSource { get; set; }

        /// <summary>Original fragment shader source filename (informational).</summary>
        [JsonPropertyName("fragmentSource")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string FragmentSource { get; set; }

        /// <summary>Original compute shader source filename (informational).</summary>
        [JsonPropertyName("computeSource")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ComputeSource { get; set; }

        /// <summary>ISO 8601 compilation timestamp (human-readable).</summary>
        [JsonPropertyName("compiledAt")]
        public string CompiledAt { get; set; }

        /// <summary>Unix epoch seconds compilation timestamp (machine-comparable).</summary>
        [JsonPropertyName("compiledAtEpoch")]
        public long CompiledAtEpoch { get; set; }

        /// <summary>SHA256 hash of the SPIR-V input bytes (vertex+fragment concatenated).</summary>
        [JsonPropertyName("inputHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string InputHash { get; set; }

        /// <summary>
        /// The ResourceLayoutDescription[] — the authoritative data for creating ResourceLayout objects.
        /// Identical across all backends. Array index = descriptor set number.
        /// </summary>
        [JsonPropertyName("resourceLayoutDescriptions")]
        public VdShaderResourceLayout[] ResourceLayoutDescriptions { get; set; }

        /// <summary>
        /// The flat binding map from the cross-compiler (validation data).
        /// </summary>
        [JsonPropertyName("flatBindingMap")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public VdShaderBindingEntry[] FlatBindingMap { get; set; }

        /// <summary>
        /// Per-backend shader data. Keys are backend names: "Vulkan", "Direct3D11", "Metal", "OpenGL", "OpenGLES".
        /// </summary>
        [JsonPropertyName("backends")]
        public Dictionary<string, VdShaderBackendData> Backends { get; set; } = new();

        // ─── Serialization ──────────────────────────────────────────────────────

        /// <summary>Serialize this shader bundle to JSON.</summary>
        public string Serialize()
            => JsonSerializer.Serialize(this, VdShaderBundleJsonContext.Default.VeldridShaderBundle);

        /// <summary>Serialize this shader bundle to a .vdshader file.</summary>
        public void SerializeToFile(string path)
            => File.WriteAllText(path, Serialize());

        /// <summary>Deserialize a shader bundle from JSON.</summary>
        public static VeldridShaderBundle Deserialize(string json)
            => JsonSerializer.Deserialize(json, VdShaderBundleJsonContext.Default.VeldridShaderBundle)
                ?? throw new VeldridException("Failed to deserialize .vdshader file: null result");

        /// <summary>Deserialize a shader bundle from a file.</summary>
        public static VeldridShaderBundle DeserializeFromFile(string path)
            => Deserialize(File.ReadAllText(path));

        // ─── Runtime API ────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the shader bytes for the specified backend.
        /// For inline data, resolves from base64. For external files, uses the provided resolver
        /// function (which may load from filesystem, WAD, embedded resources, etc.).
        /// If no resolver is provided and data is external, falls back to filesystem using basePath.
        /// </summary>
        /// <param name="backend">The graphics backend to get shader data for.</param>
        /// <param name="fileResolver">
        /// Optional function to resolve external shader filenames to byte arrays.
        /// Use this for loading from WAD files, embedded resources, or any non-filesystem source.
        /// </param>
        /// <param name="basePath">
        /// Optional base directory path for resolving external shader files from the filesystem.
        /// Only used when <paramref name="fileResolver"/> is null and shader data is external.
        /// </param>
        /// <returns>A tuple of (vertexBytes, fragmentBytes, vertexEntryPoint, fragmentEntryPoint).</returns>
        public (byte[] vertexBytes, byte[] fragmentBytes, string vertexEntry, string fragmentEntry)
            GetVertexFragmentShaderData(
                GraphicsBackend backend,
                Func<string, byte[]> fileResolver = null,
                string basePath = null)
        {
            string key = GetBackendKey(backend);
            if (!Backends.TryGetValue(key, out var data))
                throw new VeldridException(
                    $"Shader bundle '{ShaderName}' does not contain data for backend '{key}'. " +
                    $"Available backends: {string.Join(", ", Backends.Keys)}");

            byte[] vsBytes = ResolveShaderBytes(data.VertexShaderFile, data.VertexShaderData, fileResolver, basePath);
            byte[] fsBytes = ResolveShaderBytes(data.FragmentShaderFile, data.FragmentShaderData, fileResolver, basePath);

            if (vsBytes == null)
                throw new VeldridException(
                    $"Shader bundle '{ShaderName}' backend '{key}' has no vertex shader data " +
                    "(neither inline nor external file specified).");
            if (fsBytes == null)
                throw new VeldridException(
                    $"Shader bundle '{ShaderName}' backend '{key}' has no fragment shader data " +
                    "(neither inline nor external file specified).");

            return (vsBytes, fsBytes, data.VertexEntryPoint ?? "main", data.FragmentEntryPoint ?? "main");
        }

        /// <summary>
        /// Gets the <see cref="ResourceLayoutDescription"/>[] for creating <see cref="ResourceLayout"/> objects.
        /// These layouts are the authoritative source of truth — they guarantee correct resource binding
        /// on every backend because they were produced by the cross-compiler at the same time as the shader bytecode.
        /// </summary>
        public ResourceLayoutDescription[] GetResourceLayouts()
        {
            if (ResourceLayoutDescriptions == null || ResourceLayoutDescriptions.Length == 0)
                return [];

            var layouts = new ResourceLayoutDescription[ResourceLayoutDescriptions.Length];
            for (int i = 0; i < ResourceLayoutDescriptions.Length; i++)
            {
                var src = ResourceLayoutDescriptions[i];
                if (src.Elements == null || src.Elements.Length == 0)
                {
                    layouts[i].Elements = [];
                    continue;
                }

                layouts[i].Elements = new ResourceLayoutElementDescription[src.Elements.Length];
                for (int j = 0; j < src.Elements.Length; j++)
                {
                    layouts[i].Elements[j] = new ResourceLayoutElementDescription(
                        src.Elements[j].Name,
                        src.Elements[j].Kind,
                        src.Elements[j].Stages);
                }
            }
            return layouts;
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static byte[] ResolveShaderBytes(
            string externalFile,
            string inlineData,
            Func<string, byte[]> fileResolver,
            string basePath)
        {
            // Prefer inline base64 data
            if (!string.IsNullOrEmpty(inlineData))
                return Convert.FromBase64String(inlineData);

            // Fall back to external file
            if (!string.IsNullOrEmpty(externalFile))
            {
                // Use caller-provided resolver (for WAD files, embedded resources, etc.)
                if (fileResolver != null)
                    return fileResolver(externalFile);

                // Fallback to filesystem
                string fullPath = basePath != null
                    ? Path.Combine(basePath, externalFile)
                    : externalFile;
                if (!File.Exists(fullPath))
                    throw new VeldridException($"External shader file not found: {fullPath}");
                return File.ReadAllBytes(fullPath);
            }

            return null;
        }

        /// <summary>
        /// Gets the backend key string for a <see cref="GraphicsBackend"/> enum value.
        /// </summary>
        public static string GetBackendKey(GraphicsBackend backend) => backend switch
        {
            GraphicsBackend.Vulkan => "Vulkan",
            GraphicsBackend.Direct3D11 => "Direct3D11",
            GraphicsBackend.Metal => "Metal",
            GraphicsBackend.OpenGL => "OpenGL",
            GraphicsBackend.OpenGLES => "OpenGLES",
            _ => throw new VeldridException($"Unsupported backend: {backend}")
        };

        /// <summary>
        /// Computes a SHA256 hash of the given data, returned as a lowercase hex string.
        /// </summary>
        public static string ComputeSha256(byte[] data)
        {
            byte[] hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
