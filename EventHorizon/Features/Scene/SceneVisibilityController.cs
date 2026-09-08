using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace EventHorizon.Features.Scene;

internal sealed unsafe class SceneVisibilityController : IDisposable
{
    private readonly SceneSettings configuration;
    private readonly List<RendererSubmissionHook> rendererHooks = [];
    private bool disposed;
    private bool changedScene;
    private bool previousSceneDisabled;
    private bool lastWritten;

    private delegate void RendererSubmissionDelegate(nint renderer);

    public SceneVisibilityController(IGameInteropProvider gameInteropProvider, SceneSettings configuration, FeatureScope scope)
    {
        this.configuration = configuration;

        var manager = Manager.Instance();
        if (manager == null)
        {
            throw new InvalidOperationException("Render.Manager is not available.");
        }

        scope.Own(this);
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
            if (configuration.HideAll3DScene)
            {
                if (!changedScene)
                    previousSceneDisabled = manager->Is3DRenderingDisabled;
                changedScene = true;
                lastWritten = true;
                manager->Is3DRenderingDisabled = true;
            }
            else
                RestoreScene(manager);
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
            RestoreScene(manager);
        }

        List<Exception> errors = [];
        foreach (var hook in rendererHooks)
        {
            try
            {
                hook.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

    private void RestoreScene(Manager* manager)
    {
        if (changedScene && manager->Is3DRenderingDisabled == lastWritten)
            manager->Is3DRenderingDisabled = previousSceneDisabled;
        changedScene = false;
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
