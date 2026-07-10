using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.NamePlate;

/// <summary>
/// Owns the runtime resources required to expose an embedded PNG as an <see cref="AtkUldPartsList"/>.
///
/// Responsibilities are deliberately limited to resource loading and lifetime management:
/// - read and decode the embedded PNG asynchronously;
/// - allocate the UI-space parts list, parts, and asset;
/// - bind the decoded texture to AtkTexture on the framework thread;
/// - release managed and unmanaged resources in the reverse order.
///
/// Marker identity, atlas coordinates, and layer selection live in <see cref="MarkerAssetDefinition"/>.
/// Do not subclass or copy this class when adding another PNG; create another definition and instance instead.
/// </summary>
internal sealed unsafe class EmbeddedMarkerTextureResources(
    ITextureProvider textureProvider,
    IPluginLog log,
    MarkerAssetDefinition definition
) : IDisposable
{
    private const ulong Alignment = 8;
    private static readonly ulong PartsListSize = (ulong)sizeof(AtkUldPartsList);
    private static readonly ulong AssetSize = (ulong)sizeof(AtkUldAsset);

    private readonly ITextureProvider textureProvider = textureProvider;
    private readonly IPluginLog log = log;

    private IDalamudTextureWrap? textureWrap;
    private Texture* kernelTexture;
    private Task<IDalamudTextureWrap?>? textureLoadTask;
    private volatile bool loadFailed;
    private int loadFailureLogged;
    private volatile bool disposed;

    // A generation separates successive create/dispose cycles. An old asynchronous load may
    // finish after a new cycle starts; it must dispose its result instead of attaching it.
    private int loadGeneration;

    public MarkerAssetDefinition Definition { get; } = definition;

    public AtkUldPartsList* PartsList { get; private set; }

    private AtkUldPart* Parts { get; set; }

    private AtkUldAsset* Asset { get; set; }

    private ulong PartsSize => checked((ulong)sizeof(AtkUldPart) * (ulong)Definition.PartCount);

    /// <summary>
    /// Lazily creates the unmanaged atlas structures and starts loading the PNG.
    /// Calling this repeatedly is safe.
    /// </summary>
    public bool EnsureCreated()
    {
        if (PartsList != null && Parts != null && Asset != null)
        {
            StartLoadIfNeeded();
            return true;
        }

        Dispose();
        disposed = false;

        var uiSpace = IMemorySpace.GetUISpace();
        if (uiSpace == null)
        {
            log.Error("TargetingMeMarker failed to allocate {MarkerName}: UI memory space is null.", Definition.Name);
            return false;
        }

        var partsList = (AtkUldPartsList*)uiSpace->Malloc(PartsListSize, Alignment);
        var parts = (AtkUldPart*)uiSpace->Malloc(PartsSize, Alignment);
        var asset = (AtkUldAsset*)uiSpace->Malloc(AssetSize, Alignment);
        if (partsList == null || parts == null || asset == null)
        {
            FreeAllocated(asset, parts, partsList);
            log.Error("TargetingMeMarker failed to allocate {MarkerName} resources.", Definition.Name);
            return false;
        }

        *partsList = default;
        *asset = default;
        asset->AtkTexture.Ctor();

        for (var index = 0; index < Definition.PartCount; index++)
        {
            parts[index] = default;
            SetPart(&parts[index], asset, Definition.GetPart(index));
        }

        partsList->Id = Definition.PartsListId;
        partsList->PartCount = (ushort)Definition.PartCount;
        partsList->Parts = parts;

        PartsList = partsList;
        Parts = parts;
        Asset = asset;
        StartLoadIfNeeded();
        return true;
    }

    /// <summary>
    /// Completes the framework-thread portion of loading. Call this from IFramework.Update;
    /// ConvertToKernelTexture and AtkTexture mutation must not happen in the background task.
    /// </summary>
    public void UpdateLoadState()
    {
        if (disposed || kernelTexture != null || textureLoadTask == null || !textureLoadTask.IsCompleted)
        {
            return;
        }

        if (!textureLoadTask.IsCompletedSuccessfully)
        {
            LogLoadFailure(textureLoadTask.Exception?.GetBaseException(), "Texture loading did not complete successfully.");
            return;
        }

        var wrap = textureLoadTask.Result;
        if (wrap == null)
        {
            loadFailed = true;
            return;
        }

        try
        {
            textureWrap = wrap;
            kernelTexture = (Texture*)textureProvider.ConvertToKernelTexture(textureWrap, true);
            if (kernelTexture == null)
            {
                LogLoadFailure(null, "ConvertToKernelTexture returned null.");
                return;
            }

            if (Asset == null)
            {
                LogLoadFailure(null, "The AtkUldAsset was released before texture binding completed.");
                return;
            }

            Asset->AtkTexture.Resource = null;
            Asset->AtkTexture.KernelTexture = kernelTexture;
            Asset->AtkTexture.TextureType = TextureType.KernelTexture;
        }
        catch (Exception exception)
        {
            LogLoadFailure(exception);
        }
    }

    public bool IsTextureReady()
    {
        return PartsList != null
            && Parts != null
            && Asset != null
            && kernelTexture != null
            && Asset->AtkTexture.KernelTexture == kernelTexture;
    }

    public void Dispose()
    {
        disposed = true;
        Interlocked.Increment(ref loadGeneration);

        if (Asset != null)
        {
            Asset->AtkTexture.KernelTexture = null;
            Asset->AtkTexture.TextureType = 0;
        }

        if (kernelTexture != null)
        {
            kernelTexture->DecRef();
            kernelTexture = null;
        }

        var disposedWrap = textureWrap;
        disposedWrap?.Dispose();
        textureWrap = null;

        // A completed task may hold a wrap that UpdateLoadState has not adopted yet.
        if (textureLoadTask is { IsCompletedSuccessfully: true })
        {
            var loadedWrap = textureLoadTask.Result;
            if (loadedWrap != null && !ReferenceEquals(loadedWrap, disposedWrap))
            {
                loadedWrap.Dispose();
            }
        }

        textureLoadTask = null;
        FreeAllocated(Asset, Parts, PartsList);
        Asset = null;
        Parts = null;
        PartsList = null;
        loadFailed = false;
        Interlocked.Exchange(ref loadFailureLogged, 0);
    }

    private void StartLoadIfNeeded()
    {
        if (textureLoadTask != null || textureWrap != null || kernelTexture != null || loadFailed)
        {
            return;
        }

        var generation = Volatile.Read(ref loadGeneration);

        // The async method lives in a safe helper outside this unsafe Atk resource owner.
        // Generation checks and failure reporting are supplied by the owner because they are
        // lifecycle policy, not image-loading behavior.
        textureLoadTask = EmbeddedMarkerImageLoader.LoadAsync(textureProvider, Definition, generation, IsCurrentGeneration, LogLoadFailure);
    }

    private bool IsCurrentGeneration(int generation)
    {
        return !disposed && generation == Volatile.Read(ref loadGeneration);
    }

    private static void SetPart(AtkUldPart* part, AtkUldAsset* asset, MarkerTexturePart definition)
    {
        *part = new AtkUldPart
        {
            UldAsset = asset,
            U = definition.U,
            V = definition.V,
            Width = definition.Width,
            Height = definition.Height,
        };
    }

    private void LogLoadFailure(Exception? exception, string message = "unknown error")
    {
        loadFailed = true;
        if (Interlocked.Exchange(ref loadFailureLogged, 1) != 0)
        {
            return;
        }
        if (exception == null)
        {
            log.Error("TargetingMeMarker {MarkerName} texture load failed: {Message}", Definition.Name, message);
        }
        else
        {
            log.Error(exception, "TargetingMeMarker {MarkerName} texture load failed: {Message}", Definition.Name, message);
        }
    }

    private void FreeAllocated(AtkUldAsset* asset, AtkUldPart* parts, AtkUldPartsList* partsList)
    {
        if (asset != null)
        {
            IMemorySpace.Free(asset, AssetSize);
        }
        if (parts != null)
        {
            IMemorySpace.Free(parts, PartsSize);
        }
        if (partsList != null)
        {
            IMemorySpace.Free(partsList, PartsListSize);
        }
    }
}
