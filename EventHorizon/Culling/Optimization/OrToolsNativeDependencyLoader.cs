using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace EventHorizon.Culling.Optimization;

internal static class OrToolsNativeDependencyLoader
{
    private static readonly object Gate = new();
    private static readonly string[] NativeDependencyNames =
    [
        "abseil_dll.dll",
        "zlib1.dll",
        "bz2.dll",
        "re2.dll",
        "libutf8_validity.dll",
        "libprotobuf.dll",
        "highs.dll",
        "libscip.dll",
        "ortools.dll",
        "google-ortools-native.dll",
    ];

    private static bool loaded;

    public static bool EnsureLoaded(string pluginDirectory, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            if (loaded)
            {
                return false;
            }

            foreach (var dependencyName in NativeDependencyNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dependencyPath = Path.Combine(pluginDirectory, dependencyName);
                if (!File.Exists(dependencyPath))
                {
                    throw new FileNotFoundException("OR-Tools native dependency was not found.", dependencyPath);
                }

                NativeLibrary.Load(dependencyPath);
            }

            loaded = true;
            return true;
        }
    }
}
