using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using GameFileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;

namespace EventHorizon.Integration.Vfx;

internal sealed unsafe class StaticVfxResourceRedirector : IDisposable
{
    public const string HiddenPlayerGroundMarkerPath = "vfx/common/eff/x6d8_stlp_01_c0x1.avfx";

    private const string LocalHiddenPlayerGroundMarkerAssetPath = "Assets/no-binder.avfx";
    private const string ReadFileSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 63 42";
    private const string ReadSqpackSig = "40 56 41 56 48 83 EC ?? 0F BE 02";

    private readonly IPluginLog log;
    private readonly string localHiddenPlayerGroundMarkerPath;
    private bool loggedMissingAsset;
    private bool disposed;

    [Signature(ReadSqpackSig, DetourName = nameof(ReadSqpackDetour))]
    private readonly Hook<ReadSqpackDelegate>? readSqpackHook = null;

    [Signature(ReadFileSig)]
    private readonly ReadFileDelegate? readFile = null;

    public StaticVfxResourceRedirector(IDalamudPluginInterface pluginInterface, IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.log = log;
        var assemblyDirectory =
            pluginInterface.AssemblyLocation.Directory?.FullName
            ?? Path.GetDirectoryName(typeof(StaticVfxResourceRedirector).Assembly.Location)
            ?? AppContext.BaseDirectory;
        localHiddenPlayerGroundMarkerPath = Path.Combine(assemblyDirectory, LocalHiddenPlayerGroundMarkerAssetPath);

        try
        {
            gameInteropProvider.InitializeFromAttributes(this);
            readSqpackHook?.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to initialize static VFX resource redirector.");
        }
    }

    private delegate byte ReadFileDelegate(nint fileHandler, FileDescriptor* fileDesc, int priority, bool isSync);

    private delegate byte ReadSqpackDelegate(nint fileHandler, FileDescriptor* fileDesc, int priority, bool isSync);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        readSqpackHook?.Dispose();
        disposed = true;
    }

    private byte ReadSqpackDetour(nint fileHandler, FileDescriptor* fileDesc, int priority, bool isSync)
    {
        if (fileDesc == null || fileDesc->ResourceHandle == null || readSqpackHook is null || readFile is null)
        {
            return readSqpackHook!.Original(fileHandler, fileDesc, priority, isSync);
        }

        var actualPath = GetResourcePath(fileDesc->ResourceHandle);
        if (!actualPath.Equals(HiddenPlayerGroundMarkerPath, StringComparison.OrdinalIgnoreCase))
        {
            return readSqpackHook.Original(fileHandler, fileDesc, priority, isSync);
        }

        if (!File.Exists(localHiddenPlayerGroundMarkerPath))
        {
            if (!loggedMissingAsset)
            {
                log.Warning("Static VFX asset missing. path={Path}", localHiddenPlayerGroundMarkerPath);
                loggedMissingAsset = true;
            }

            return readSqpackHook.Original(fileHandler, fileDesc, priority, isSync);
        }

        fileDesc->FileMode = GameFileMode.LoadUnpackedResource;
        fileDesc->FilePathString = localHiddenPlayerGroundMarkerPath;

        var utfPath = Encoding.Unicode.GetBytes(localHiddenPlayerGroundMarkerPath);
        var fileInterface = stackalloc byte[0x20 + utfPath.Length + 0x16];
        Marshal.Copy(utfPath, 0, (nint)fileInterface + 0x21, utfPath.Length);
        fileDesc->FileInterface = (FileInterface*)fileInterface;

        return readFile(fileHandler, fileDesc, priority, isSync);
    }

    private static string GetResourcePath(ResourceHandle* resourceHandle)
    {
        if (resourceHandle == null)
        {
            return string.Empty;
        }

        var bytes = resourceHandle->FileName.AsSpan();
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
