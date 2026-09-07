using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

// This is the Update stack record, NOT a field on GameObject. See docs/native-admission.md.
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct NativeDrawCandidate
{
    [FieldOffset(0)]
    public GameObject* Object;

    [FieldOffset(8)]
    public int Priority;

    [FieldOffset(12)]
    public uint Padding;
}

internal sealed unsafe class NativeDrawCandidateHook : IDisposable
{
    internal const string Signature = "E8 ?? ?? ?? ?? 49 8B CE 45 89 66 14 E8 ?? ?? ?? ?? 49 8B CE E8 ?? ?? ?? ?? 44 8B E0";
    internal delegate void ApplyPolicy(Span<NativeDrawCandidate> candidates);
    private delegate nint SortDelegate(NativeDrawCandidate* first, NativeDrawCandidate* last, long ideal, byte comparer);

    [ThreadStatic]
    private static int depth;
    private readonly Hook<SortDelegate> hook;
    private readonly ApplyPolicy apply;
    private readonly IPluginLog log;
    private bool failed;

    public NativeDrawCandidateHook(IGameInteropProvider interop, ISigScanner scanner, ApplyPolicy apply, IPluginLog log)
    {
        this.apply = apply;
        this.log = log;
        // ScanText resolves the leading E8 to the sort entry point.
        hook = interop.HookFromAddress<SortDelegate>(scanner.ScanText(Signature), Detour);
    }

    public bool Failed => failed;

    public void Enable() => hook.Enable();

    public void Dispose() => hook.Dispose();

    private nint Detour(NativeDrawCandidate* first, NativeDrawCandidate* last, long ideal, byte comparer)
    {
        depth++;
        try
        {
            var result = hook.Original(first, last, ideal, comparer);
            // The native introsort calls itself. Only touch the complete, already sorted range.
            if (depth != 1 || failed)
                return result;

            try
            {
                var bytes = (nint)last - (nint)first;
                if (first == null || bytes < 0 || bytes > 819 * 16 || bytes % 16 != 0)
                    throw new InvalidOperationException("Unexpected native draw candidate range.");

                var source = new Span<NativeDrawCandidate>(first, (int)(bytes / 16));
                Span<NativeDrawCandidate> working = stackalloc NativeDrawCandidate[source.Length];
                source.CopyTo(working);
                apply(working);
                // Publish only after all policy work succeeds; exceptions leave vanilla records intact.
                working.CopyTo(source);
            }
            catch (Exception exception)
            {
                failed = true;
                log.Error(exception, "Native draw admission failed; using game policy until plugin reload.");
            }
            return result;
        }
        finally
        {
            depth--;
        }
    }
}
