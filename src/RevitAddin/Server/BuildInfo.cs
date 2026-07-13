using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RevitMCPAddin.Server;

/// <summary>
/// Build-truth surfaced at runtime. Version, git commit/branch/state and build
/// timestamp are stamped into the assembly at compile time (see
/// RevitMCPAddin.csproj → StampBuildInfo). /health reads them from the loaded
/// assembly, so it reports what this dll actually is — a build that lost or
/// gained commands can no longer masquerade behind a hand-typed version.
/// </summary>
internal static class BuildInfo
{
    private static readonly Assembly Self = typeof(BuildInfo).Assembly;

    /// <summary>Semver from AssemblyInformationalVersion (csproj &lt;Version&gt;).</summary>
    public static string Version { get; } = ReadInformationalVersion();

    /// <summary>Short git commit the dll was built from, or "unknown".</summary>
    public static string GitCommit { get; } = ReadMetadata("GitCommit");

    /// <summary>Git branch at build time, or "unknown".</summary>
    public static string GitBranch { get; } = ReadMetadata("GitBranch");

    /// <summary>"clean" / "dirty" working tree at build time, or "unknown".</summary>
    public static string GitState { get; } = ReadMetadata("GitState");

    /// <summary>ISO-8601 UTC timestamp of the build, or "unknown".</summary>
    public static string BuildTimestampUtc { get; } = ReadMetadata("BuildTimestampUtc");

    private static string ReadInformationalVersion()
    {
        var info = Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return "unknown";
        // The SDK may append "+<sourceRevisionId>"; keep just the semver.
        var plus = info.IndexOf('+');
        return plus >= 0 ? info.Substring(0, plus) : info;
    }

    private static string ReadMetadata(string key)
    {
        foreach (var a in Self.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (a.Key == key)
                return string.IsNullOrWhiteSpace(a.Value) ? "unknown" : a.Value;
        return "unknown";
    }

    /// <summary>
    /// Stable, order-independent fingerprint of the registered command surface.
    /// Same set of commands → same hash; a build that gained or dropped a command
    /// → different hash. Lets a consumer verify capability straight from /health
    /// (no auth, no /commands diff) and catch a version that outranks its build.
    /// </summary>
    public static string CapabilityHash(IEnumerable<string> commandNames)
    {
        var joined = string.Join("\n",
            commandNames.Distinct().OrderBy(n => n, StringComparer.Ordinal));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString(); // 64-bit prefix, 16 hex chars — plenty to spot drift
    }
}
