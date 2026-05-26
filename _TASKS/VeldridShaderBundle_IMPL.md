# IMPL: Veldrid Shader Bundle Loader (AN_Veldrid)

**Status:** Ready to implement  
**Last Updated:** 2026-05-26  
**Depends on:** AN_VeldridSpirv Phase 1-3 (complete — produces .vdshader files)  
**Validated with:** AN_SilkyNvg, AN_Monsters

---

## Summary

Add a `.vdshader` bundle loader to core Veldrid. This is the ONE API for loading precompiled shaders. It replaces direct `factory.CreateShader(ShaderDescription)` calls for precompiled shader bytes. The bundle contains compiled shader data for ALL backends plus the `ResourceLayoutDescription[]` needed to create matching `ResourceLayout` objects.

---

## The Problem (Why This Exists)

SilkyNvg (and any code using precompiled shaders) currently calls:
```csharp
var desc = new ShaderDescription(ShaderStages.Vertex, hlslBytes, "main");
var shader = factory.CreateShader(ref desc);
```

This works on Vulkan (SPIR-V passes through unchanged) and OpenGL (name-based binding). It BREAKS on D3D11 and Metal because the flat register/argument indices baked into the compiled shader don't match what Veldrid's `ResourceLayout` constructors compute independently.

The fix: load shaders from a `.vdshader` bundle that includes the `ResourceLayoutDescription[]` from compile time. The game uses those layouts — guaranteed correct on every backend.

---

## What a .vdshader File Contains

```json
{
  "purpose": "Veldrid precompiled shader bundle...",
  "version": 1,
  "shaderName": "SolidFill",
  "vertexSource": "fill.vert",
  "fragmentSource": "fill.frag",
  "compiledAt": "2026-05-26T13:00:00-06:00",
  "compiledAtEpoch": 1779918000,
  "inputHash": "abc123...",
  "resourceLayoutDescriptions": [
    { "elements": [{ "name": "FragUniforms", "kind": "UniformBuffer", "stages": "Fragment" }] },
    { "elements": [{ "name": "Texture", "kind": "TextureReadOnly", "stages": "Fragment" }, { "name": "Sampler", "kind": "Sampler", "stages": "Fragment" }] }
  ],
  "flatBindingMap": [...],
  "backends": {
    "Vulkan": { "shaderFormat": "spirv", "vertexEntryPoint": "main", "fragmentEntryPoint": "main", "vertexShaderFile": "SolidFill_Vertex.spv", "fragmentShaderFile": "SolidFill_Fragment.spv" },
    "Direct3D11": { "shaderFormat": "hlsl_text", "vertexEntryPoint": "main", "fragmentEntryPoint": "main", "vertexShaderFile": "SolidFill_Vertex.hlsl", "fragmentShaderFile": "SolidFill_Fragment.hlsl" },
    "Metal": { "shaderFormat": "msl_text", "vertexEntryPoint": "main0", "fragmentEntryPoint": "main0", "vertexShaderFile": "SolidFill_Vertex.metal", "fragmentShaderFile": "SolidFill_Fragment.metal" },
    "OpenGL": { "shaderFormat": "glsl_text", "vertexEntryPoint": "main", "fragmentEntryPoint": "main", "vertexShaderFile": "SolidFill_Vertex.glsl", "fragmentShaderFile": "SolidFill_Fragment.glsl" },
    "OpenGLES": { "shaderFormat": "glsl_text", "vertexEntryPoint": "main", "fragmentEntryPoint": "main", "vertexShaderFile": "SolidFill_Vertex.essl", "fragmentShaderFile": "SolidFill_Fragment.essl" }
  }
}
```

Shader data can be external (filenames) or inline (base64). The `resourceLayoutDescriptions` is the SAME for all backends.

---

## Implementation Checklist

### Step 1: Add VeldridShaderBundle to core Veldrid

- [ ] Create `src/Veldrid/ShaderBundle/VeldridShaderBundle.cs` — the JSON model class (copy from AN_VeldridSpirv's `VeldridShaderBundle.cs`, remove the SPIRV-specific dependencies)
- [ ] Create `src/Veldrid/ShaderBundle/VdShaderBackendData.cs` — per-backend data model
- [ ] Create `src/Veldrid/ShaderBundle/VdShaderResourceLayout.cs` — serializable resource layout
- [ ] Create `src/Veldrid/ShaderBundle/VdShaderBindingEntry.cs` — serializable binding map entry
- [ ] These classes need: `System.Text.Json` (built into .NET 8), `System.Security.Cryptography` for hash verification
- [ ] The bundle class needs `Deserialize(string json)`, `DeserializeFromFile(string path)`, `GetResourceLayouts()`, and `GetVertexFragmentShaderData(GraphicsBackend, Func<string, byte[]> fileResolver, string basePath)`

### Step 2: Add ResourceFactory.CreateFromBundle()

- [ ] Add to `ResourceFactory.cs`:
  ```csharp
  /// <summary>
  /// Creates shaders from a .vdshader bundle. This is the required way to load precompiled shaders.
  /// Returns both the Shader objects and the ResourceLayoutDescription[] that MUST be used
  /// to create ResourceLayout objects for correct binding on all backends.
  /// </summary>
  public PrecompiledShaderResult CreateFromBundle(
      VeldridShaderBundle bundle,
      Func<string, byte[]> fileResolver = null,
      string basePath = null)
  {
      var (vertexBytes, fragmentBytes, vertexEntry, fragmentEntry) =
          bundle.GetVertexFragmentShaderData(BackendType, fileResolver, basePath);
      
      var vsDesc = new ShaderDescription(ShaderStages.Vertex, vertexBytes, vertexEntry);
      var fsDesc = new ShaderDescription(ShaderStages.Fragment, fragmentBytes, fragmentEntry);
      
      return new PrecompiledShaderResult(
          new[] { CreateShader(ref vsDesc), CreateShader(ref fsDesc) },
          bundle.GetResourceLayouts());
  }
  ```

- [ ] Add `PrecompiledShaderResult` class:
  ```csharp
  public class PrecompiledShaderResult
  {
      public Shader[] Shaders { get; }
      public ResourceLayoutDescription[] ResourceLayouts { get; }
  }
  ```

- [ ] Add convenience overload that takes JSON string:
  ```csharp
  public PrecompiledShaderResult CreateFromBundle(
      string vdshaderJson,
      Func<string, byte[]> fileResolver = null,
      string basePath = null)
  ```

### Step 3: The fileResolver Pattern

The `fileResolver` parameter allows loading shader bytes from ANY source without touching the filesystem:

```csharp
// Loading from filesystem (default):
var result = factory.CreateFromBundle(bundle, basePath: "/path/to/shaders/");

// Loading from a WAD/pack file:
var result = factory.CreateFromBundle(bundle, fileResolver: filename => wadFile.ReadEntry(filename));

// Loading from embedded resources:
var result = factory.CreateFromBundle(bundle, fileResolver: filename => {
    var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MyApp.Shaders.{filename}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
});

// Inline mode (no resolver needed — data is in the JSON):
var result = factory.CreateFromBundle(bundle); // base64 data decoded automatically
```

### Step 4: Usage in SilkyNvg (example of how client code changes)

**Before (broken on Metal/D3D11):**
```csharp
var vertexShaderDesc = new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, vertexEntryPoint);
var vertexShader = factory.CreateShader(ref vertexShaderDesc);
var fragmentShaderDesc = new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, fragmentEntryPoint);
var fragmentShader = factory.CreateShader(ref fragmentShaderDesc);
// Then manually create ResourceLayout objects... which may not match the shader
```

**After (correct on all backends):**
```csharp
var bundle = VeldridShaderBundle.Deserialize(vdshaderJson);
var result = factory.CreateFromBundle(bundle, fileResolver: LoadShaderFile);
Shader[] shaders = result.Shaders;
ResourceLayoutDescription[] layouts = result.ResourceLayouts;
// Create ResourceLayout objects from layouts — guaranteed correct
```

### Step 5: Deprecate raw CreateShader for precompiled bytes

- [ ] Add `[Obsolete]` attribute to `ResourceFactory.CreateShader(ShaderDescription)` with message: "For precompiled shaders, use CreateFromBundle() with a .vdshader file. Direct CreateShader() with raw HLSL/MSL bytes does not guarantee correct resource bindings."
- [ ] OR: keep it but add a runtime warning when non-SPIR-V bytes are passed (using the `ShaderBindingMismatchAction` enum from the spec)

---

## Files to Create/Modify in AN_Veldrid

| File | Action |
|------|--------|
| `src/Veldrid/ShaderBundle/VeldridShaderBundle.cs` | Create — JSON model + deserialization |
| `src/Veldrid/ShaderBundle/VdShaderBackendData.cs` | Create — per-backend data |
| `src/Veldrid/ShaderBundle/VdShaderResourceLayout.cs` | Create — serializable layout |
| `src/Veldrid/ShaderBundle/VdShaderBindingEntry.cs` | Create — binding map entry |
| `src/Veldrid/ShaderBundle/PrecompiledShaderResult.cs` | Create — return type |
| `src/Veldrid/ResourceFactory.cs` | Modify — add `CreateFromBundle()` methods |

---

## Key Principles

1. **ONE file, ALL backends** — A `.vdshader` file contains shader data for every backend. The loader picks the right one at runtime.
2. **ONE API** — `factory.CreateFromBundle()` is the only way to load precompiled shaders correctly.
3. **Same on every backend** — No backend-specific logic in the API. The `ResourceLayoutDescription[]` is identical everywhere.
4. **No filesystem required at load time** — The `fileResolver` pattern allows loading from WADs, embedded resources, or any byte source. Inline mode (base64) requires no external files at all.
5. **The game uses the bundle's layouts** — `result.ResourceLayouts` is what you pass to `factory.CreateResourceLayout()`. This guarantees the bindings match the compiled shader.
