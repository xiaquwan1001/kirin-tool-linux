// Guard: the version a user sees must be the version in the csproj.
//
// v2.4.13 pinned <FileVersion>/<InformationalVersion> at 2.4.13 and never bumped them, so
// v2.4.13/v2.4.14/v2.4.15 all reported "2.4.13+<commit>" from `doctor` and `info --json`.
// The 2.4.15 real-device smoke test found it. This harness fails if anyone re-pins the
// metadata or if the built assembly disagrees with the project file.
//   dotnet run --project tests/VersionMetadata -c Release

using System.Reflection;
using System.Text.RegularExpressions;
using Kirin_Tool.Services;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

string coreProject = Path.Combine(root, "Kirin-Tool.csproj");
string cliProject = Path.Combine(root, "KirinTool.Cli", "KirinTool.Cli.csproj");
string coreXml = File.ReadAllText(coreProject);
string cliXml = File.ReadAllText(cliProject);

static string VersionOf(string xml)
{
    Match match = Regex.Match(xml, @"<Version>([^<]+)</Version>");
    return match.Success ? match.Groups[1].Value.Trim() : "";
}

string coreVersion = VersionOf(coreXml);
string cliVersion = VersionOf(cliXml);
Check(coreVersion.Length > 0, "Kirin-Tool.csproj declares <Version> (" + coreVersion + ")");
Check(coreVersion == cliVersion, "CLI project declares the same <Version> (" + cliVersion + ")");
Check(!Regex.IsMatch(coreXml, @"<FileVersion>"), "Kirin-Tool.csproj does not pin <FileVersion> (it would drift)");
Check(!Regex.IsMatch(coreXml, @"<InformationalVersion>"), "Kirin-Tool.csproj does not pin <InformationalVersion>");
Check(!Regex.IsMatch(cliXml, @"<InformationalVersion>"), "KirinTool.Cli.csproj does not pin <InformationalVersion>");

Assembly core = typeof(FastbootClient).Assembly;
string? informational = core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
string reported = (informational ?? "").Split('+')[0];
Check(reported == coreVersion,
    "the built assembly reports the csproj version (assembly=" + reported + ", csproj=" + coreVersion + ")");

string? fileVersion = core.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
Check(!string.IsNullOrEmpty(fileVersion) && fileVersion!.StartsWith(coreVersion + ".", StringComparison.Ordinal),
    "the built assembly file version tracks the csproj version (file=" + fileVersion + ")");

Console.WriteLine(checks + " version-metadata checks passed; no device was contacted.");