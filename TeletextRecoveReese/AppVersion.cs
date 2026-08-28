using System.Reflection;

namespace TeletextRecoveReese;

/// <summary>
/// Application identity backed by the version metadata in TeletextRecoveReese.csproj.
/// Change the Version property there; all UI version labels read the same value here.
/// </summary>
internal static class AppVersion
{
    public const string ProductName = "TeletextRecoveReese";

    public static string InformationalVersion { get; } =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "unknown";

    public static string DisplayVersion => InformationalVersion
        .Split('+', 2)[0]
        .Replace('-', ' ');

    public static string DisplayName => $"{ProductName} {DisplayVersion}";
}
