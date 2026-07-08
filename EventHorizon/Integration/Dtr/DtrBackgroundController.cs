using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.Dtr;

internal sealed class DtrBackgroundController : IDisposable
{
    private const string DtrAddonName = "_DTR";
    private const uint BackgroundNodeId = 900003;

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly Configuration configuration;
    private readonly ChatLogBackgroundSkinProvider skinProvider;
    private readonly DtrBackgroundNode backgroundNode = new(BackgroundNodeId);

    public DtrBackgroundController(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IFramework framework,
        IClientState clientState,
        Configuration configuration
    )
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.framework = framework;
        this.clientState = clientState;
        this.configuration = configuration;
        skinProvider = new ChatLogBackgroundSkinProvider(gameGui);

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, DtrAddonName, OnDtrPostSetup);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, DtrAddonName, OnDtrPreDraw);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, DtrAddonName, OnDtrPreFinalize);
        framework.Update += OnFrameworkUpdate;

        Refresh();
    }

    public void Refresh()
    {
        if (!ShouldShowBackground())
        {
            RemoveBackground();
            return;
        }

        var addonPointer = gameGui.GetAddonByName(DtrAddonName);
        if (addonPointer != nint.Zero)
        {
            Apply(addonPointer);
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, DtrAddonName, OnDtrPostSetup);
        addonLifecycle.UnregisterListener(AddonEvent.PreDraw, DtrAddonName, OnDtrPreDraw);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, DtrAddonName, OnDtrPreFinalize);

        RemoveBackground();
    }

    private void OnDtrPostSetup(AddonEvent type, AddonArgs args) => Apply(args.Addon);

    private void OnDtrPreDraw(AddonEvent type, AddonArgs args) => Apply(args.Addon);

    private void OnDtrPreFinalize(AddonEvent type, AddonArgs args) => RemoveBackground(args.Addon);

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!clientState.IsLoggedIn && backgroundNode.IsCreated)
        {
            RemoveBackground();
        }
    }

    private unsafe void Apply(nint dtrPointer)
    {
        if (!ShouldShowBackground())
        {
            RemoveBackground(dtrPointer);
            return;
        }

        if (dtrPointer == nint.Zero)
        {
            RemoveBackground();
            return;
        }

        if (!skinProvider.TryGetChatLogBackgroundSkin(out var skin))
        {
            RemoveBackground(dtrPointer);
            return;
        }

        var unit = (AtkUnitBase*)dtrPointer;
        var root = unit->RootNode;
        if (root == null || !DtrBoundsProvider.TryGetBounds(unit, backgroundNode.ResourceNode, out var bounds))
        {
            RemoveBackground(dtrPointer);
            return;
        }

        if (!backgroundNode.EnsureAttached(unit, root))
        {
            return;
        }

        if (backgroundNode.Update(bounds, CreateStyle(), skin))
        {
            root->IsDirty = true;
        }
    }

    private void RemoveBackground()
    {
        var addonPointer = gameGui.GetAddonByName(DtrAddonName);
        RemoveBackground(addonPointer != nint.Zero ? addonPointer.Address : nint.Zero);
    }

    private unsafe void RemoveBackground(nint dtrPointer)
    {
        backgroundNode.Destroy(dtrPointer != nint.Zero ? (AtkUnitBase*)dtrPointer : null);
    }

    private DtrBackgroundStyle CreateStyle()
    {
        return new DtrBackgroundStyle(
            configuration.DtrBackgroundHorizontalPadding,
            configuration.DtrBackgroundHorizontalPadding,
            configuration.DtrBackgroundPaddingTop,
            configuration.DtrBackgroundPaddingBottom,
            configuration.DtrBackgroundAlpha
        );
    }

    private bool ShouldShowBackground() => configuration.EnableDtrBackground && clientState.IsLoggedIn;
}
