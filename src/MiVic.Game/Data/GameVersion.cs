using System.Reflection;

namespace MiVic.Game.Data;

/// <summary>
/// The version this build calls itself.
/// <para>
/// Read from the assembly rather than kept as a constant here, so that the number on
/// screen is the number that was built: <c>Directory.Build.props</c> holds it, MSBuild
/// stamps it into the informational version, and this reads it back. A version typed
/// into the source as well is a version that will disagree with the tag.
/// </para>
/// </summary>
public static class GameVersion
{
    /// <summary>The version as displayed, with a leading v, e.g. <c>v0.01</c>.</summary>
    public static string Display { get; } = Read();

    private static string Read()
    {
        Assembly assembly = typeof(GameVersion).Assembly;

        string? version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3);

        if (string.IsNullOrWhiteSpace(version))
        {
            // A build with no version stamped is still a build; saying so beats
            // showing an empty string next to the word "version".
            return "v0.00";
        }

        // Source control metadata, when a build adds it, is not part of the number.
        int plus = version.IndexOf('+');

        if (plus >= 0)
        {
            version = version[..plus];
        }

        return version.StartsWith('v') ? version : $"v{version}";
    }
}
