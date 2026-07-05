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
    private readonly Configuration configuration;
    private readonly ChatLogBackgroundSkinProvider skinProvider;
    private readonly DtrBackgroundNode backgroundNode = new(BackgroundNodeId);

    public DtrBackgroundController(IAddonLifecycle addonLifecycle, IGameGui gameGui, Configuration configuration)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.configuration = configuration;
        skinProvider = new ChatLogBackgroundSkinProvider(gameGui);

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, DtrAddonName, OnDtrPostSetup);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, DtrAddonName, OnDtrPreDraw);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, DtrAddonName, OnDtrPreFinalize);

        Refresh();
    }

    public void Refresh()
    {
        if (!configuration.EnableDtrBackground)
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
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, DtrAddonName, OnDtrPostSetup);
        addonLifecycle.UnregisterListener(AddonEvent.PreDraw, DtrAddonName, OnDtrPreDraw);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, DtrAddonName, OnDtrPreFinalize);

        RemoveBackground();
    }

    private void OnDtrPostSetup(AddonEvent type, AddonArgs args) => Apply(args.Addon);

    private void OnDtrPreDraw(AddonEvent type, AddonArgs args) => Apply(args.Addon);

    private void OnDtrPreFinalize(AddonEvent type, AddonArgs args) => RemoveBackground();

    private unsafe void Apply(nint dtrPointer)
    {
        if (!configuration.EnableDtrBackground)
        {
            RemoveBackground();
            return;
        }

        if (dtrPointer == nint.Zero || !skinProvider.TryGetChatLogBackgroundSkin(out var skin))
        {
            return;
        }

        var unit = (AtkUnitBase*)dtrPointer;
        var root = unit->RootNode;
        if (
            root == null
            || !backgroundNode.EnsureAttached(unit, root)
            || !DtrBoundsProvider.TryGetBounds(unit, backgroundNode.ResourceNode, out var bounds)
        )
        {
            return;
        }

        if (backgroundNode.Update(bounds, CreateStyle(), skin))
        {
            root->IsDirty = true;
        }
    }

    private unsafe void RemoveBackground()
    {
        var addonPointer = gameGui.GetAddonByName(DtrAddonName);
        backgroundNode.Destroy(addonPointer != nint.Zero ? (AtkUnitBase*)addonPointer.Address : null);
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
}
