using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.Integration.Debug;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class EnableDrawHook : IDisposable
{
    private const string CallsiteSignature = "E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9 74 33 45 33 C0";
    private readonly PlayerAdmissionGate admissionGate;
    private readonly Hook<EnableDrawDelegate> hook;
    private bool disposed;

    private delegate void EnableDrawDelegate(GameObject* gameObject);

    public EnableDrawHook(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, PlayerAdmissionGate admissionGate)
    {
        this.admissionGate = admissionGate;
        // ScanText applies Dalamud's ReadJmpCallSig behavior when a signature begins
        // with CALL/JMP, so this is already the resolved function entry, not the E8 callsite.
        var target = sigScanner.ScanText(CallsiteSignature);
        hook = gameInteropProvider.HookFromAddress<EnableDrawDelegate>(target, Detour);
    }

    public void Enable() => hook.Enable();

    public void Disable()
    {
        if (hook.IsEnabled)
        {
            hook.Disable();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Disable();
        hook.Dispose();
    }

    private void Detour(GameObject* gameObject)
    {
        if (admissionGate.ShouldSuppressEnableDraw(gameObject))
        {
            PlayerAdmissionDebugTrace.OnEnableDrawSuppressed(gameObject);
            return;
        }

        PlayerAdmissionDebugTrace.OnEnableDrawOriginalEntering(gameObject);
        hook.Original(gameObject);
        PlayerAdmissionDebugTrace.OnEnableDrawOriginalReturned(gameObject);
    }
}
