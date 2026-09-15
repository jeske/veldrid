#!/usr/bin/env -S dotnet run
// publish-local.cs - Cross-platform build+pack+deploy of the Veldrid packages to the local NuGet feed.
//
// ALWAYS RELEASE. A Debug-configured package is not "the same code with symbols": D3D11GraphicsDevice.cs has
//     #if DEBUG  flags |= DeviceCreationFlags.Debug;  #endif
// so a Debug AN.Veldrid.dll turns on the D3D11 validation layer (D3D11_3SDKLayers.dll, ~100 extra threads,
// per-call validation) in EVERY consuming app, regardless of GraphicsDeviceOptions.Debug. That is exactly what
// shipped as 5.2609.1417.1621 (AssemblyConfiguration="Debug") - see AN_Mirica/_BUGFIX/150_*.md. This script
// therefore has no configuration switch and verifies the produced assembly before deploying.
//
// Versioning is timestamp-based (v2) - every build gets a unique version via AN.Veldrid.Build.props. The
// timestamp is captured ONCE here and passed to MSBuild so every project gets the same version.
//
// Requires: LOCAL_NUGET_REPO environment variable (the local feed directory).
//
// Usage:
//   cmd\publish-local.cmd                # Windows launcher (cd's to the repo root first)
//   dotnet run --file cmd/publish-local.cs -- [--dry-run]
//   --dry-run | -n   show version + feed, build nothing

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// -- Disable MSBuild node reuse: orphaned nodes hold locks and corrupt later restores (esp. Linux after Ctrl+C) --
Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");

// -- Ctrl+C: kill the active child process tree so no zombie MSBuild survives ------------------------------------
Process? _activeChild = null;
var _cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    _cts.Cancel();
    var proc = _activeChild;
    if (proc is { HasExited: false })
    {
        WriteColor($"\n[Ctrl+C] Killing child process tree (PID {proc.Id})...", ConsoleColor.Yellow);
        try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }
    try
    {
        using var shutdown = Process.Start(new ProcessStartInfo("dotnet", "build-server shutdown") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        shutdown?.WaitForExit(5000);
    }
    catch { /* best effort */ }
    Environment.Exit(130);
};

// -- Arguments -------------------------------------------------------------------------------------------------
bool dryRun = args.Any(a => a is "--dry-run" or "-n");
if (args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase) || a.Equals("--release", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-c", StringComparison.OrdinalIgnoreCase)))
{
    WriteColor("ERROR: this script has no configuration switch - it ALWAYS publishes Release.", ConsoleColor.Red);
    WriteColor("  A Debug package enables the D3D11 validation layer in every consumer (see header comment).", ConsoleColor.Yellow);
    Environment.Exit(2);
}
const string Configuration = "Release";

// -- Repo root: walk up from cwd (the .cmd launcher cd's to the root; direct `dotnet run` from anywhere inside works too)
string repoRoot = FindRepoRoot(Directory.GetCurrentDirectory())
    ?? throw new InvalidOperationException("Cannot find the repo root (looked for AN.Veldrid.Build.props walking up from the current directory). Run via cmd/publish-local.cmd or from inside the repo.");
string solutionPath = Path.Combine(repoRoot, "Veldrid.sln");
string packageOutputDir = Path.Combine(repoRoot, "bin", "Packages", Configuration);

// -- LOCAL_NUGET_REPO ------------------------------------------------------------------------------------------
string? localNuGetFeedPath = Environment.GetEnvironmentVariable("LOCAL_NUGET_REPO");
if (string.IsNullOrEmpty(localNuGetFeedPath))
{
    WriteColor("ERROR: LOCAL_NUGET_REPO environment variable is not set.", ConsoleColor.Red);
    WriteColor("  Linux/macOS: export LOCAL_NUGET_REPO=/path/to/LocalNuGet", ConsoleColor.Yellow);
    WriteColor("  Windows:     $env:LOCAL_NUGET_REPO = 'C:\\PROJECTS\\LocalNuGet'", ConsoleColor.Yellow);
    Environment.Exit(1);
}
if (localNuGetFeedPath.StartsWith("~/") || localNuGetFeedPath == "~")   // neither C# nor MSBuild expand ~
    localNuGetFeedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), localNuGetFeedPath.Substring(Math.Min(2, localNuGetFeedPath.Length)));
localNuGetFeedPath = Path.GetFullPath(localNuGetFeedPath);
Environment.SetEnvironmentVariable("LOCAL_NUGET_REPO", localNuGetFeedPath);   // AN.Veldrid.Build.props reads it for DeployToLocalNuGet

// -- Version stamp, captured once ------------------------------------------------------------------------------
DateTime now = DateTime.Now;
string buildYYMM = now.ToString("yyMM"), buildDDHH = now.ToString("ddHH"), buildmmss = now.ToString("mmss");
string buildYYMMDD = now.ToString("yyMMdd"), buildHHmmss = now.ToString("HHmmss");
string versionProps = $"/p:_BuildYYMM={buildYYMM} /p:_BuildDDHH={buildDDHH} /p:_Buildmmss={buildmmss} /p:_BuildYYMMDD={buildYYMMDD} /p:_BuildHHmmss={buildHHmmss}";
string versionStamp = $"5.{buildYYMM}.{buildDDHH}.{buildmmss}";
string packageStamp = $"5.{int.Parse(buildYYMMDD)}.{int.Parse(buildHHmmss)}";   // NuGet strips leading zeros

Console.WriteLine();
WriteColor($"=== Veldrid publish-local ({Configuration}) ===", ConsoleColor.Cyan);
WriteColor($"Repo root:     {repoRoot}", ConsoleColor.DarkGray);
WriteColor($"Version stamp: {versionStamp} (pkg: {packageStamp})", ConsoleColor.DarkGray);
WriteColor($"Local feed:    {localNuGetFeedPath}", ConsoleColor.DarkGray);

if (dryRun)
{
    Console.WriteLine();
    WriteColor($"[DRY RUN] Would build {Configuration}, pack, and deploy version {packageStamp} to {localNuGetFeedPath}", ConsoleColor.Yellow);
    return 0;
}

// -- Clean stale packages so a failed pack cannot redeploy an old .nupkg ---------------------------------------
if (Directory.Exists(packageOutputDir))
    foreach (string stale in Directory.GetFiles(packageOutputDir, "*.nupkg")) File.Delete(stale);
DateTime deployStartTime = DateTime.Now;

// -- [1/3] Build ------------------------------------------------------------------------------------------------
Console.WriteLine();
WriteColor("[1/3] Building solution (Release)...", ConsoleColor.Green);
int buildExit = RunProcess("dotnet", $"build \"{solutionPath}\" -c {Configuration} /p:UseLocalVeldrid=true /nodeReuse:false {versionProps}");
if (buildExit != 0) return Fail($"dotnet build exited with code {buildExit}");

// -- [2/3] Verify the produced assembly really is a Release build -----------------------------------------------
// Reads AssemblyConfigurationAttribute's string out of the metadata blob heap: no reflection load, no TFM issues.
Console.WriteLine();
WriteColor("[2/3] Verifying AN.Veldrid.dll AssemblyConfiguration == Release...", ConsoleColor.Green);
string binDir = Path.Combine(repoRoot, "bin", Configuration);
string[] veldridDlls = Directory.Exists(binDir) ? Directory.GetFiles(binDir, "AN.Veldrid.dll", SearchOption.AllDirectories) : [];
if (veldridDlls.Length == 0) return Fail($"no AN.Veldrid.dll found under {binDir} after build");
foreach (string dll in veldridDlls)
{
    string cfg = ReadAssemblyConfiguration(dll);
    if (cfg != "Release") return Fail($"{dll} has AssemblyConfiguration '{cfg}' (expected Release) - refusing to deploy");
    WriteColor($"  OK  {Path.GetRelativePath(repoRoot, dll)}", ConsoleColor.DarkGray);
}

// -- [3/3] Pack (+ DeployToLocalNuGet target in AN.Veldrid.Build.props copies to the feed) ----------------------
Console.WriteLine();
WriteColor("[3/3] Packing + deploying...", ConsoleColor.Green);
int packExit = RunProcess("dotnet", $"pack \"{solutionPath}\" -c {Configuration} /p:UseLocalVeldrid=true /p:LocalNuGetFeedPath=\"{localNuGetFeedPath}\" --no-build /nodeReuse:false {versionProps}");
if (packExit != 0) return Fail($"dotnet pack exited with code {packExit}");

// -- Report ------------------------------------------------------------------------------------------------------
var deployed = Directory.Exists(localNuGetFeedPath)
    ? new DirectoryInfo(localNuGetFeedPath).GetFiles("*.nupkg").Where(f => f.LastWriteTime >= deployStartTime).OrderBy(f => f.Name).ToArray()
    : [];
Console.WriteLine();
if (deployed.Length == 0) return Fail($"pack succeeded but no .nupkg newer than {deployStartTime:HH:mm:ss} appeared in {localNuGetFeedPath}");
WriteColor("PUBLISH SUCCEEDED (Release)", ConsoleColor.Green);
WriteColor("Deployed packages:", ConsoleColor.Cyan);
foreach (var pkg in deployed) WriteColor($"  {pkg.Name}  ({Math.Round(pkg.Length / 1024.0, 1)} KB)", ConsoleColor.Green);
Console.WriteLine();
return 0;

// -- Helpers -----------------------------------------------------------------------------------------------------

static string? FindRepoRoot(string startDir)
{
    for (string? dir = startDir; dir != null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "AN.Veldrid.Build.props"))) return dir;
    return null;
}

/// <summary>AssemblyConfigurationAttribute's argument as stored in the #Blob heap: a length-prefixed UTF-8 string.
/// We look for a short printable token equal to Debug/Release bounded by control bytes - good enough for a guard.</summary>
static string ReadAssemblyConfiguration(string dllPath)
{
    string s = Encoding.Latin1.GetString(File.ReadAllBytes(dllPath));
    var m = Regex.Match(s, "[\\x01-\\x1f](Debug|Release)[\\x00-\\x1f]");
    return m.Success ? m.Groups[1].Value : "<unknown>";
}

static void WriteColor(string message, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}

static int Fail(string message)
{
    Console.WriteLine();
    WriteColor("PUBLISH FAILED", ConsoleColor.Red);
    WriteColor($"  x {message}", ConsoleColor.Red);
    Console.WriteLine();
    return 1;
}

int RunProcess(string fileName, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false })!;
    _activeChild = process;
    try { process.WaitForExit(); }
    finally { _activeChild = null; }
    return _cts.IsCancellationRequested ? -1 : process.ExitCode;
}