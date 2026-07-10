using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Plugin.Services;
using EventHorizon.Interop.Atk;
using EventHorizon.Interop.Vfx;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.TargetingMarker;

internal sealed unsafe class TargetingMarkerController : IDisposable
{
    private const string NamePlateAddonName = "NamePlate";
    private const int NamePlateSlotCount = 50;
    private const uint MarkerNodeIdBase = 930000;
    private const string TargetingMeVfxPath = "vfx/common/eff/m0904_stlppos01_0a1.avfx";

    // Marker-specific data belongs here. To add another embedded PNG:
    // 1. include it as an EmbeddedResource, 2. add another definition,
    // 3. create one EmbeddedMarkerTextureResources instance and select it.
    // Do not duplicate the loading or unmanaged-resource lifecycle code.
    private static readonly MarkerAssetDefinition GazeMarker = new(
        name: "Gaze Marker",
        resourceSuffix: ".Assets.gaze-marker.png",
        debugName: "EventHorizon gaze-marker.png",
        partsListId: MarkerNodeIdBase + 10_000,
        glowPartId: 0,
        outlinePartId: 1,
        width: 64,
        height: 64,
        parts: [new MarkerTexturePart(U: 0, V: 0, Width: 64, Height: 64), new MarkerTexturePart(U: 64, V: 0, Width: 64, Height: 64)]
    );

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly INamePlateGui namePlateGui;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly NamePlateMarkerSlot[] slots = new NamePlateMarkerSlot[NamePlateSlotCount];
    private readonly EmbeddedMarkerTextureResources markerResources;
    private readonly ActorVfxController actorVfxController;
    private readonly IPluginLog log;

    private TargetingMeMarkerStyle appliedStyle;
    private int styleRevision;
    private bool lastTextureReady;
    private bool refreshPending;
    private bool disposed;

    public TargetingMarkerController(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        INamePlateGui namePlateGui,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        Configuration configuration,
        IFramework framework,
        ITextureProvider textureProvider,
        ActorVfxController actorVfxController,
        IPluginLog log
    )
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.namePlateGui = namePlateGui;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.configuration = configuration;
        this.framework = framework;
        this.actorVfxController = actorVfxController;
        this.log = log;
        markerResources = new EmbeddedMarkerTextureResources(textureProvider, log, GazeMarker);

        for (var index = 0; index < slots.Length; index++)
        {
            slots[index] = new NamePlateMarkerSlot(index, MarkerNodeIdBase + (uint)index);
        }

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, NamePlateAddonName, OnNamePlatePostSetup);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, NamePlateAddonName, OnNamePlatePreFinalize);
        namePlateGui.OnDataUpdate += OnNamePlateDataUpdate;
        framework.Update += OnFrameworkUpdate;

        RequestRefresh();
    }

    public void RequestRefresh()
    {
        if (!disposed)
        {
            refreshPending = true;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        namePlateGui.OnDataUpdate -= OnNamePlateDataUpdate;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, NamePlateAddonName, OnNamePlatePostSetup);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, NamePlateAddonName, OnNamePlatePreFinalize);

        RemoveAllMarkerNodes();
        actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
        ReleaseMarkerResources();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed)
        {
            return;
        }

        if (refreshPending)
        {
            refreshPending = false;
            RefreshOnFrameworkThread();
        }
        if (ShouldSuppressTargetingMeVfx() || ShouldClearTargetingMeVfx())
        {
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
        }
        markerResources.UpdateLoadState();
        var textureReady = markerResources.IsTextureReady();
        if (textureReady == lastTextureReady)
        {
            return;
        }

        lastTextureReady = textureReady;
        MarkMarkerImagesDirty(textureReady);
    }

    private void RefreshOnFrameworkThread()
    {
        if (disposed)
        {
            return;
        }

        RefreshStyleRevision();
        if (!ShouldUseNamePlateMarker())
        {
            RemoveAllMarkerNodes();
            ReleaseMarkerResources();
            ClearTargetingMeVfxIfDisabled();
            namePlateGui.RequestRedraw();
            return;
        }

        ClearTargetingMeVfxIfDisabled();

        var addonPointer = gameGui.GetAddonByName(NamePlateAddonName);
        if (addonPointer != nint.Zero)
        {
            EnsureMarkers((AddonNamePlate*)addonPointer.Address);
        }

        namePlateGui.RequestRedraw();
    }

    private void OnNamePlatePostSetup(AddonEvent _, AddonArgs __)
    {
        if (!disposed && configuration.EnableTargetingMeMarker)
        {
            RequestRefresh();
        }
    }

    private void OnNamePlatePreFinalize(AddonEvent _, AddonArgs args)
    {
        if (!disposed)
        {
            RemoveMarkerNodes((AddonNamePlate*)args.Addon.Address);
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
        }
    }

    private void OnNamePlateDataUpdate(INamePlateUpdateContext _, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (disposed)
        {
            return;
        }

        if (!configuration.EnableTargetingMeMarker)
        {
            HideAllMarkers();
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
            return;
        }

        RefreshStyleRevision();
        if (ShouldUseNamePlateMarker())
        {
            UpdateTargetingMeNamePlateMarkers(handlers);
        }
        else
        {
            RemoveAllMarkerNodes();
            ReleaseMarkerResources();
        }

        if (ShouldUseTargetingMeVfx())
        {
            UpdateTargetingMeVfx(handlers);
        }
        else
        {
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
        }
    }

    private void UpdateTargetingMeNamePlateMarkers(IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        var addonPointer = gameGui.GetAddonByName(NamePlateAddonName);
        var addon = addonPointer == nint.Zero ? null : (AddonNamePlate*)addonPointer.Address;
        if (addon == null || !EnsureMarkers(addon))
        {
            return;
        }

        var textureReady = markerResources.IsTextureReady();
        for (var index = 0; index < slots.Length; index++)
        {
            slots[index].MarkUnseen();
        }

        foreach (var handler in handlers)
        {
            var namePlateIndex = handler.NamePlateIndex;
            if ((uint)namePlateIndex >= slots.Length)
            {
                continue;
            }

            var namePlateObject = &addon->NamePlateObjectArray[namePlateIndex];
            var rootComponentNode = namePlateObject->RootComponentNode;
            if (rootComponentNode == null || rootComponentNode->Component == null)
            {
                continue;
            }

            var slot = slots[namePlateIndex];
            slot.SetVisible(IsTargetingLocalPlayer(handler.PlayerCharacter), textureReady);
            slot.ApplyLayout(appliedStyle, namePlateObject);

            var nameText = namePlateObject->NameText;
            if (nameText != null)
            {
                if (appliedStyle.UseCustomColor)
                {
                    slot.SetMarkerColor(appliedStyle.Color);
                }
                else
                {
                    slot.SetNamePlateColor(nameText->TextColor, nameText->EdgeColor);
                }
            }
        }

        for (var index = 0; index < slots.Length; index++)
        {
            slots[index].HideIfUnseen(textureReady);
        }
    }

    private void UpdateTargetingMeVfx(IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (objectTable.LocalPlayer is null)
        {
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
            return;
        }

        if (ShouldSuppressTargetingMeVfx())
        {
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
            return;
        }

        var activePlayerIds = new HashSet<ulong>();
        foreach (var handler in handlers)
        {
            var player = handler.PlayerCharacter;
            if (player is not null && IsTargetingLocalPlayer(player))
            {
                var gameObjectId = player.GameObjectId;
                activePlayerIds.Add(gameObjectId);
                actorVfxController.Show(ActorVfxScope.TargetingMeMarker, gameObjectId, player.Address, TargetingMeVfxPath);
            }
        }

        actorVfxController.PruneScopeExcept(ActorVfxScope.TargetingMeMarker, activePlayerIds);
    }

    private bool IsTargetingLocalPlayer(IPlayerCharacter? player)
    {
        var localPlayer = objectTable.LocalPlayer;
        return player is not null
            && localPlayer is not null
            && player.GameObjectId != localPlayer.GameObjectId
            && (player.TargetObjectId == localPlayer.GameObjectId || IsCurrentTargetTestInjected(player));
    }

    private bool IsCurrentTargetTestInjected(IPlayerCharacter player)
    {
        var target = targetManager.Target;
        return configuration.EnableTargetingMeMarkerCurrentTargetTest && target is not null && target.GameObjectId == player.GameObjectId;
    }

    private bool EnsureMarkers(AddonNamePlate* addon)
    {
        if (addon == null || addon->NamePlateObjectArray == null || !markerResources.EnsureCreated())
        {
            return false;
        }

        var partsList = markerResources.PartsList;
        if (partsList == null)
        {
            return false;
        }

        var createdAny = false;
        for (var index = 0; index < slots.Length; index++)
        {
            var namePlateObject = &addon->NamePlateObjectArray[index];
            var rootComponentNode = namePlateObject->RootComponentNode;
            if (
                rootComponentNode == null
                || rootComponentNode->Component == null
                || namePlateObject->NameContainer == null
                || namePlateObject->NameText == null
            )
            {
                slots[index].Destroy(addon);
                continue;
            }

            createdAny |= slots[index]
                .EnsureAttached(rootComponentNode->Component, namePlateObject, appliedStyle, styleRevision, partsList);
        }

        return createdAny || HasAnyMarker();
    }

    private void RefreshStyleRevision()
    {
        var style = TargetingMeMarkerStyle.FromConfiguration(configuration, markerResources.Definition);
        if (style != appliedStyle)
        {
            appliedStyle = style;
            styleRevision++;
            lastTextureReady = false;
        }
    }

    private bool ShouldUseNamePlateMarker()
    {
        return configuration.EnableTargetingMeMarker && configuration.EnableTargetingMeNamePlateMarker;
    }

    private bool ShouldUseTargetingMeVfx()
    {
        return configuration.EnableTargetingMeMarker && configuration.EnableTargetingMeVfxMarker;
    }

    private bool ShouldSuppressTargetingMeVfx()
    {
        return ShouldUseTargetingMeVfx()
            && configuration.DisableTargetingMeMarkerVfxInDuty
            && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]);
    }

    private bool ShouldClearTargetingMeVfx()
    {
        return ShouldUseTargetingMeVfx() && (objectTable.LocalPlayer is null || gameGui.GetAddonByName(NamePlateAddonName) == nint.Zero);
    }

    private void ClearTargetingMeVfxIfDisabled()
    {
        if (!ShouldUseTargetingMeVfx())
        {
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
        }
    }

    private void MarkMarkerImagesDirty(bool textureReady)
    {
        for (var index = 0; index < slots.Length; index++)
        {
            slots[index].MarkImagesDirty(textureReady);
        }
    }

    private bool HasAnyMarker()
    {
        for (var index = 0; index < slots.Length; index++)
        {
            if (slots[index].HasMarker)
            {
                return true;
            }
        }

        return false;
    }

    private void HideAllMarkers()
    {
        for (var index = 0; index < slots.Length; index++)
        {
            slots[index].SetVisible(false, false);
        }
    }

    private void RemoveAllMarkerNodes()
    {
        var addonPointer = gameGui.GetAddonByName(NamePlateAddonName);
        if (addonPointer == nint.Zero)
        {
            for (var index = 0; index < slots.Length; index++)
            {
                slots[index].ClearReference();
            }
            return;
        }

        RemoveMarkerNodes((AddonNamePlate*)addonPointer.Address);
    }

    private void RemoveMarkerNodes(AddonNamePlate* addon)
    {
        for (var index = 0; index < slots.Length; index++)
        {
            slots[index].Destroy(addon);
        }
    }

    private void ReleaseMarkerResources()
    {
        if (!HasAnySharedPartsListReference(markerResources.PartsList))
        {
            markerResources.Dispose();
        }

        lastTextureReady = false;
    }

    private bool HasAnySharedPartsListReference(AtkUldPartsList* partsList)
    {
        if (partsList == null)
        {
            return false;
        }

        for (var index = 0; index < slots.Length; index++)
        {
            if (slots[index].ReferencesPartsList(partsList))
            {
                return true;
            }
        }

        return false;
    }
}
