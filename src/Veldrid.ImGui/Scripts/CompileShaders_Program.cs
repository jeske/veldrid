// ImGui shader precompiler - a thin caller of Veldrid.SPIRV's ShaderBundlePipeline (one compile -> verify -> cache path, D1).
using Veldrid.SPIRV;

string imguiDir = args.Length > 0 ? args[0] : Directory.Exists("Shaders/Source") ? "." : Directory.Exists("../Shaders/Source") ? ".." : "src/Veldrid.ImGui";
string sourceDir = Path.Combine(imguiDir, "Shaders", "Source");
string compiledDir = Path.Combine(imguiDir, "Shaders", "Compiled");
Console.WriteLine($"=== ImGui shader precompiler ({ShaderBundlePipeline.CompilerVersionTag}) ===");
if (!Directory.Exists(sourceDir)) { Console.Error.WriteLine($"ERROR: {sourceDir} not found"); return 1; }
try
{
    int compiled = ShaderBundlePipeline.GetOrBuildAll(sourceDir, compiledDir, [new ShaderBundle_SourcePair("ImGui", "ImGui.vert.glsl", "ImGui.frag.glsl")], deleteOrphans: true, Console.WriteLine);
    Console.WriteLine($"=== Done: {compiled} bundle(s) compiled ===");
    return 0;
}
catch (ShaderInterfaceContractException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (SpirvCompilationException ex) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }