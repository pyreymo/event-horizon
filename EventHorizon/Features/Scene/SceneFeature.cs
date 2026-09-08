using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace EventHorizon.Features.Scene;

internal sealed class SceneFeature(SceneSettings settings, Action save, IGameInteropProvider interop)
    : Feature<SceneSettings>(settings, save)
{
    internal static IFeatureDefinition CreateDefinition(IGameInteropProvider interop) =>
        new FeatureDefinition<SceneSettings>(
            "scene",
            "Feature.Name.Scene",
            store =>
                store.LegacyEnabled("HideBgParts", false)
                || store.LegacyEnabled("HideTerrain", false)
                || store.LegacyEnabled("HideWater", false)
                || store.LegacyEnabled("HideGrass", false)
                || store.LegacyEnabled("HideAll3DScene", false),
            (settings, save) => new SceneFeature(settings, save, interop)
        );

    public override void Enable(FeatureScope scope)
    {
        var controller = new SceneVisibilityController(interop, Settings, scope);
        controller.Enable();
        scope.OnUpdate(controller.Update);
    }

    public override void DrawSettings()
    {
        var changed = FeatureUi.Checkbox("HideBgParts", Settings.HideBgParts, v => Settings.HideBgParts = v);
        changed |= FeatureUi.Checkbox("HideTerrain", Settings.HideTerrain, v => Settings.HideTerrain = v);
        changed |= FeatureUi.Checkbox("HideWater", Settings.HideWater, v => Settings.HideWater = v);
        changed |= FeatureUi.Checkbox("HideGrass", Settings.HideGrass, v => Settings.HideGrass = v);
        changed |= FeatureUi.Checkbox("HideAll3DScene", Settings.HideAll3DScene, v => Settings.HideAll3DScene = v);
        if (changed)
            Save();
    }

    private sealed unsafe class SceneVisibilityController : IDisposable
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
}

internal sealed class SceneSettings : IFeatureSettings
{
    public int Version { get; set; } = 1;
    public bool HideBgParts { get; set; } = false;
    public bool HideTerrain { get; set; } = false;
    public bool HideWater { get; set; } = false;
    public bool HideGrass { get; set; } = false;
    public bool HideAll3DScene { get; set; } = false;
}
