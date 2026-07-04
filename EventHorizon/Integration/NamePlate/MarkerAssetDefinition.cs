using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.NamePlate;

/// <summary>
/// Describes one rectangular region in the PNG atlas used by an <see cref="AtkImageNode"/>.
/// This type contains only atlas coordinates; it owns no texture or unmanaged memory.
/// </summary>
internal readonly record struct MarkerTexturePart(ushort U, ushort V, ushort Width, ushort Height);

/// <summary>
/// Immutable, marker-specific data shared by the controller and the generic resource loader.
///
/// Keep PNG layout details here rather than in resource-management code. A new marker image
/// should normally require only another definition plus one resource instance in the controller.
/// </summary>
internal sealed class MarkerAssetDefinition
{
    private readonly MarkerTexturePart[] parts;

    public MarkerAssetDefinition(
        string name,
        string resourceSuffix,
        string debugName,
        uint partsListId,
        ushort glowPartId,
        ushort outlinePartId,
        ushort width,
        ushort height,
        params MarkerTexturePart[] parts
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceSuffix);
        ArgumentException.ThrowIfNullOrWhiteSpace(debugName);
        ArgumentNullException.ThrowIfNull(parts);

        if (width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Marker width must be greater than zero.");
        }
        if (height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Marker height must be greater than zero.");
        }
        if (parts.Length == 0 || parts.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(parts), "A marker atlas must contain between 1 and 65535 parts.");
        }
        if (glowPartId >= parts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(glowPartId));
        }
        if (outlinePartId >= parts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(outlinePartId));
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Width == 0 || parts[index].Height == 0)
            {
                throw new ArgumentException($"Atlas part {index} has an empty rectangle.", nameof(parts));
            }
        }

        ValidateLayerSize(parts[glowPartId], width, height, nameof(glowPartId));
        ValidateLayerSize(parts[outlinePartId], width, height, nameof(outlinePartId));

        Name = name;
        ResourceSuffix = resourceSuffix;
        DebugName = debugName;
        PartsListId = partsListId;
        GlowPartId = glowPartId;
        OutlinePartId = outlinePartId;
        Width = width;
        Height = height;
        this.parts = (MarkerTexturePart[])parts.Clone();
    }

    public string Name { get; }

    public string ResourceSuffix { get; }

    public string DebugName { get; }

    /// <summary>
    /// ID assigned to the unmanaged AtkUldPartsList. Keep this unique among simultaneously loaded marker assets.
    /// </summary>
    public uint PartsListId { get; }

    public ushort GlowPartId { get; }

    public ushort OutlinePartId { get; }

    public ushort Width { get; }

    public ushort Height { get; }

    public int PartCount => parts.Length;

    public MarkerTexturePart GetPart(int index)
    {
        return parts[index];
    }

    private static void ValidateLayerSize(MarkerTexturePart part, ushort width, ushort height, string parameterName)
    {
        if (part.Width != width || part.Height != height)
        {
            throw new ArgumentException("Glow and outline parts must match the marker's logical dimensions.", parameterName);
        }
    }
}
