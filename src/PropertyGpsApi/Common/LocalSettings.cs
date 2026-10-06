using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace PropertyGpsApi.Common;

/// <summary>
/// appsettings.Local.json: a git-ignored local overrides file, layered on top of
/// appsettings.json.
///
/// User secrets are the usual answer for developer credentials, but they only load in the
/// Development environment AND only for the Windows profile that created them, which makes
/// them quietly invisible when the app is launched from an IDE running as another user, or
/// from the published exe. This file has none of those failure modes. It is listed in
/// .gitignore and must never be committed.
///
/// Where it sits matters. It used to be appended after every default source, environment
/// variables and the command line included, so the file beat both: a Database__B2A or
/// RequestEncryption__Mode set for one run was silently ignored and the run used whatever
/// server the file named (defects L1 / N6). It now goes directly after the last JSON file
/// source the host added (appsettings.json, appsettings.{Environment}.json and, in
/// Development, user secrets), so the order is:
///
///     appsettings.json &lt; appsettings.{Environment}.json &lt; user secrets
///         &lt; appsettings.Local.json &lt; environment variables &lt; command line
///
/// It is still resolved against the content root, so a Visual Studio run (content root =
/// the project folder) and a run from bin (where the csproj copies it) both find it.
/// </summary>
internal static class LocalSettings
{
    internal const string FileName = "appsettings.Local.json";

    internal static void Add(IConfigurationBuilder configuration)
    {
        // AddJsonFile resolves the file provider (the content root) exactly as for the
        // host's own files; the source is then moved to its place.
        configuration.AddJsonFile(FileName, optional: true, reloadOnChange: true);

        var sources = configuration.Sources;
        var local = sources[^1];
        sources.RemoveAt(sources.Count - 1);
        sources.Insert(InsertionIndex(sources), local);
    }

    /// <summary>
    /// After the last JSON file source; failing that, before the first unprefixed
    /// environment-variable source; failing that, last.
    /// </summary>
    private static int InsertionIndex(IList<IConfigurationSource> sources)
    {
        for (var i = sources.Count - 1; i >= 0; i--)
            if (sources[i] is JsonConfigurationSource) return i + 1;

        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" }) return i;

        return sources.Count;
    }
}
