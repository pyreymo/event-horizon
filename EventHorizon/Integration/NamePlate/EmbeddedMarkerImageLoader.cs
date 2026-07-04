using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace EventHorizon.Integration.NamePlate;

/// <summary>
/// Performs the managed, asynchronous half of loading an embedded marker image.
///
/// This helper intentionally lives outside <see cref="EmbeddedMarkerTextureResources"/>:
/// that resource owner is an unsafe type because it manipulates Atk pointers, while C# does
/// not permit await expressions in an unsafe context. Keeping image I/O and decoding here also
/// makes the boundary clear: this class returns a managed texture wrap and never touches Atk.
/// </summary>
internal static class EmbeddedMarkerImageLoader
{
    public static async Task<IDalamudTextureWrap?> LoadAsync(
        ITextureProvider textureProvider,
        MarkerAssetDefinition definition,
        int generation,
        Func<int, bool> isCurrentGeneration,
        Action<Exception?, string> onFailure
    )
    {
        try
        {
            var bytes = await Task.Run(() => ReadEmbeddedImageBytes(definition)).ConfigureAwait(false);
            if (!isCurrentGeneration(generation))
            {
                return null;
            }

            var wrap = await textureProvider.CreateFromImageAsync(bytes, definition.DebugName).ConfigureAwait(false);
            if (!isCurrentGeneration(generation))
            {
                wrap.Dispose();
                return null;
            }

            return wrap;
        }
        catch (Exception exception)
        {
            if (isCurrentGeneration(generation))
            {
                onFailure(exception, "unknown error");
            }

            return null;
        }
    }

    private static byte[] ReadEmbeddedImageBytes(MarkerAssetDefinition definition)
    {
        var assembly = typeof(EmbeddedMarkerImageLoader).Assembly;
        var resourceName =
            assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(definition.ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded resource ending with '{definition.ResourceSuffix}' was not found.");

        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource stream is null: {resourceName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
