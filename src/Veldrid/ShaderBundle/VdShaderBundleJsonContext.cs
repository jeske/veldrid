using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Veldrid
{
    /// <summary>
    /// System.Text.Json source generator context for AOT-compatible serialization
    /// of .vdshader bundle files.
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
    [JsonSerializable(typeof(VeldridShaderBundle))]
    [JsonSerializable(typeof(VdShaderBackendData))]
    [JsonSerializable(typeof(VdShaderResourceLayout))]
    [JsonSerializable(typeof(VdShaderResourceElement))]
    [JsonSerializable(typeof(VdShaderBindingEntry))]
    [JsonSerializable(typeof(VdShaderResourceLayout[]))]
    [JsonSerializable(typeof(VdShaderBindingEntry[]))]
    [JsonSerializable(typeof(Dictionary<string, VdShaderBackendData>))]
    internal partial class VdShaderBundleJsonContext : JsonSerializerContext
    {
    }
}
