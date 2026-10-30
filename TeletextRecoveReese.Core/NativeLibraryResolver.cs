using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TeletextRecoveReese.Core;

/// <summary>
/// .NET allows a single DllImport resolver per assembly, so every native
/// binding in this assembly registers its library here instead.
/// </summary>
internal static class NativeLibraryResolver
{
    private static readonly ConcurrentDictionary<string, DllImportResolver> Resolvers = new();

    static NativeLibraryResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    public static void Register(string libraryName, DllImportResolver resolver) =>
        Resolvers[libraryName] = resolver;

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        Resolvers.TryGetValue(libraryName, out DllImportResolver? resolver)
            ? resolver(libraryName, assembly, searchPath)
            : IntPtr.Zero;
}
