using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using GameFileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;

namespace EventHorizon.Interop.Vfx;

internal sealed unsafe class StaticVfxResourceRedirector : IDisposable
{
    public const string HiddenPlayerGroundMarkerPath = "vfx/common/eff/x6d8_stlp_01_c0x1.avfx";

    private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(2);
    private const string LocalHiddenPlayerGroundMarkerAssetPath = "Assets/no-binder.avfx";
    private const string ReadFileSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 63 42";
    private const string ReadSqpackSig = "40 56 41 56 48 83 EC ?? 0F BE 02";

    private readonly IPluginLog log;
    private readonly HookCallbackTracker callbackTracker;
    private readonly string localHiddenPlayerGroundMarkerPath;
    private readonly Hook<ReadSqpackDelegate>? readSqpackHook;
    private ReadSqpackDelegate? readSqpackOriginal;
    private int loggedMissingAsset;
    private int disposed;

    [Signature(ReadFileSig)]
    private readonly ReadFileDelegate? readFile = null;

    public StaticVfxResourceRedirector(IDalamudPluginInterface pluginInterface, IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.log = log;
        callbackTracker = new HookCallbackTracker(nameof(StaticVfxResourceRedirector), log);

        var assemblyDirectory =
            pluginInterface.AssemblyLocation.Directory?.FullName
            ?? Path.GetDirectoryName(typeof(StaticVfxResourceRedirector).Assembly.Location)
            ?? AppContext.BaseDirectory;
        localHiddenPlayerGroundMarkerPath = Path.Combine(assemblyDirectory, LocalHiddenPlayerGroundMarkerAssetPath);

        Hook<ReadSqpackDelegate>? createdHook = null;
        try
        {
            gameInteropProvider.InitializeFromAttributes(this);
            if (readFile is null)
            {
                throw new InvalidOperationException("ReadFile signature was not resolved.");
            }

            createdHook = gameInteropProvider.HookFromSignature<ReadSqpackDelegate>(ReadSqpackSig, ReadSqpackDetour);
            Volatile.Write(ref readSqpackOriginal, createdHook.Original);
            readSqpackHook = createdHook;
            callbackTracker.MarkReady();
            createdHook.Enable();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "initialize static VFX resource redirector");
            callbackTracker.BeginStop();
            try
            {
                createdHook?.Dispose();
            }
            catch (Exception disposeException)
            {
                callbackTracker.ReportException(disposeException, "dispose partially initialized ReadSqpack hook");
            }

            callbackTracker.MarkStopped();
        }
    }

    private delegate byte ReadFileDelegate(nint fileHandler, FileDescriptor* fileDesc, int priority, bool isSync);

    private delegate byte ReadSqpackDelegate(nint fileHandler, FileDescriptor* fileDesc, int priority, bool isSync);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        callbackTracker.BeginStop();
        try
        {
            readSqpackHook?.Disable();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "disable ReadSqpack hook");
        }

        callbackTracker.WaitForDrain(CallbackDrainTimeout);

        try
        {
            readSqpackHook?.Dispose();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "dispose ReadSqpack hook");
        }
        finally
        {
            callbackTracker.MarkStopped();
        }
    }

    private byte ReadSqpackDetour(nint fileHandler, FileDescriptor* fileDesc, int priority, bool isSync)
    {
        using var callback = callbackTracker.Enter();

        var callOriginal = Volatile.Read(ref readSqpackOriginal);
        if (callOriginal is null)
        {
            // Fail this one read rather than dereferencing an unpublished hook during construction.
            callbackTracker.ReportMissingOriginal();
            return 0;
        }

        if (
            !callbackTracker.ShouldRunPluginLogic
            || Volatile.Read(ref disposed) != 0
            || fileDesc == null
            || fileDesc->ResourceHandle == null
        )
        {
            return callOriginal(fileHandler, fileDesc, priority, isSync);
        }

        var unpackedRead = readFile;
        if (unpackedRead is null)
        {
            return callOriginal(fileHandler, fileDesc, priority, isSync);
        }

        byte[] utfPath;
        try
        {
            var actualPath = GetResourcePath(fileDesc->ResourceHandle);
            if (!actualPath.Equals(HiddenPlayerGroundMarkerPath, StringComparison.OrdinalIgnoreCase))
            {
                return callOriginal(fileHandler, fileDesc, priority, isSync);
            }

            if (!File.Exists(localHiddenPlayerGroundMarkerPath))
            {
                if (Interlocked.Exchange(ref loggedMissingAsset, 1) == 0)
                {
                    SafeWarning("Static VFX asset missing. path={Path}", localHiddenPlayerGroundMarkerPath);
                }

                return callOriginal(fileHandler, fileDesc, priority, isSync);
            }

            utfPath = Encoding.Unicode.GetBytes(localHiddenPlayerGroundMarkerPath);
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "prepare static VFX resource redirect");
            return callOriginal(fileHandler, fileDesc, priority, isSync);
        }

        try
        {
            var fileInterfaceLength = checked(0x20 + utfPath.Length + 0x16);
            var fileInterface = stackalloc byte[fileInterfaceLength];
            new Span<byte>(fileInterface, fileInterfaceLength).Clear();
            Marshal.Copy(utfPath, 0, (nint)fileInterface + 0x21, utfPath.Length);

            fileDesc->FileMode = GameFileMode.LoadUnpackedResource;
            fileDesc->FilePathString = localHiddenPlayerGroundMarkerPath;
            fileDesc->FileInterface = (FileInterface*)fileInterface;
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "build static VFX file interface");
            return callOriginal(fileHandler, fileDesc, priority, isSync);
        }

        return unpackedRead(fileHandler, fileDesc, priority, isSync);
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

    private void SafeWarning(string messageTemplate, params object[] values)
    {
        try
        {
            log.Warning(messageTemplate, values);
        }
        catch
        {
            // Never allow logging failures to escape a native callback.
        }
    }
}
