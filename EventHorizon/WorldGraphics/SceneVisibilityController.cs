using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace EventHorizon.WorldGraphics;

internal sealed unsafe class SceneVisibilityController : IDisposable
{
    private readonly Configuration configuration;
    private readonly List<RendererSubmissionHook> rendererHooks = [];
    private bool disposed;

    private delegate void RendererSubmissionDelegate(nint renderer);

    public SceneVisibilityController(IGameInteropProvider gameInteropProvider, Configuration configuration)
    {
        this.configuration = configuration;

        var manager = Manager.Instance();
        if (manager == null)
        {
            throw new InvalidOperationException("Render.Manager is not available.");
        }

        AddRendererHook(gameInteropProvider, &manager->BGInstancingRenderer, () => configuration.HideBgParts);
        AddRendererHook(gameInteropProvider, &manager->TerrainRenderer, () => configuration.HideTerrain);
        AddRendererHook(gameInteropProvider, &manager->WaterRenderer, () => configuration.HideWater);
        AddRendererHook(gameInteropProvider, &manager->GrassRenderer, () => configuration.HideGrass);
    }

    public void Enable()
    {
        foreach (var hook in rendererHooks)
        {
            hook.Enable();
        }
    }

    public void Update()
    {
        var manager = Manager.Instance();
        if (manager != null)
        {
            manager->Is3DRenderingDisabled = configuration.HideAll3DScene;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var manager = Manager.Instance();
        if (manager != null)
        {
            manager->Is3DRenderingDisabled = false;
        }

        foreach (var hook in rendererHooks)
        {
            hook.Dispose();
        }
    }

    private void AddRendererHook(IGameInteropProvider gameInteropProvider, void* renderer, Func<bool> shouldHide)
    {
        var vtable = *(nint*)renderer;
        var renderAddress = *(nint*)(vtable + 0x10);
        rendererHooks.Add(new RendererSubmissionHook(gameInteropProvider, renderAddress, shouldHide));
    }

    private sealed class RendererSubmissionHook : IDisposable
    {
        private readonly Hook<RendererSubmissionDelegate> hook;
        private readonly Func<bool> shouldHide;

        public RendererSubmissionHook(IGameInteropProvider gameInteropProvider, nint address, Func<bool> shouldHide)
        {
            this.shouldHide = shouldHide;
            hook = gameInteropProvider.HookFromAddress<RendererSubmissionDelegate>(address, Detour);
        }

        public void Enable() => hook.Enable();

        public void Dispose() => hook.Dispose();

        private void Detour(nint renderer)
        {
            if (!shouldHide())
            {
                hook.Original(renderer);
            }
        }
    }
}
