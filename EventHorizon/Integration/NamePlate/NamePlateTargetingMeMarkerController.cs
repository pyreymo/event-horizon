using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Plugin.Services;
using EventHorizon.Integration.NativeUi;
using EventHorizon.Integration.Vfx;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.NamePlate;

internal sealed unsafe class NamePlateTargetingMeMarkerController : IDisposable
{
    private const string NamePlateAddonName = "NamePlate";
    private const int NamePlateSlotCount = 50;
    private const uint MarkerNodeIdBase = 930000;
    private const string TargetingMeVfxPath = "vfx/common/eff/m0904_stlppos01_0a1.avfx";
    private const double SlowFrameworkUpdateLogThresholdMs = 2.0;
    private const int SlowFrameworkUpdateLogCooldownMs = 1_000;

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
    private long nextSlowFrameworkUpdateLog;

    public NamePlateTargetingMeMarkerController(
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
        var start = Stopwatch.GetTimestamp();
        var phaseStart = start;
        if (disposed)
        {
            return;
        }

        var wasRefreshPending = refreshPending;
        if (refreshPending)
        {
            refreshPending = false;
            RefreshOnFrameworkThread();
        }
        var refreshTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        if (ShouldSuppressTargetingMeVfx() || ShouldClearTargetingMeVfx())
        {
            actorVfxController.ClearScope(ActorVfxScope.TargetingMeMarker);
        }
        var vfxClearTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        markerResources.UpdateLoadState();
        var textureLoadTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        var textureReady = markerResources.IsTextureReady();
        if (textureReady == lastTextureReady)
        {
            LogSlowFrameworkUpdate(
                start,
                refreshTicks,
                vfxClearTicks,
                textureLoadTicks,
                Stopwatch.GetTimestamp() - phaseStart,
                wasRefreshPending,
                textureChanged: false
            );
            return;
        }

        lastTextureReady = textureReady;
        MarkMarkerImagesDirty(textureReady);
        LogSlowFrameworkUpdate(
            start,
            refreshTicks,
            vfxClearTicks,
            textureLoadTicks,
            Stopwatch.GetTimestamp() - phaseStart,
            wasRefreshPending,
            textureChanged: true
        );
    }

    private void LogSlowFrameworkUpdate(
        long start,
        long refreshTicks,
        long vfxClearTicks,
        long textureLoadTicks,
        long textureReadyTicks,
        bool wasRefreshPending,
        bool textureChanged
    )
    {
        var totalTicks = Stopwatch.GetTimestamp() - start;
        if (ToMilliseconds(totalTicks) < SlowFrameworkUpdateLogThresholdMs)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextSlowFrameworkUpdateLog)
        {
            return;
        }

        nextSlowFrameworkUpdateLog = now + SlowFrameworkUpdateLogCooldownMs;
        log.Information(
            "[Perf] Slow NamePlateTargetingMeMarkerController.OnFrameworkUpdate total={TotalMs:F3}ms refresh={RefreshMs:F3}ms vfxClear={VfxClearMs:F3}ms textureLoad={TextureLoadMs:F3}ms textureReady={TextureReadyMs:F3}ms refreshPending={RefreshPending} textureChanged={TextureChanged} markerEnabled={MarkerEnabled} namePlate={NamePlateEnabled} vfx={VfxEnabled}",
            ToMilliseconds(totalTicks),
            ToMilliseconds(refreshTicks),
            ToMilliseconds(vfxClearTicks),
            ToMilliseconds(textureLoadTicks),
            ToMilliseconds(textureReadyTicks),
            wasRefreshPending,
            textureChanged,
            configuration.EnableTargetingMeMarker,
            configuration.EnableTargetingMeNamePlateMarker,
            configuration.EnableTargetingMeVfxMarker
        );
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
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

    private readonly record struct TargetingMeMarkerStyle(
        MarkerAssetDefinition Marker,
        float OffsetX,
        float OffsetY,
        float Scale,
        byte Opacity,
        byte GlowOpacity,
        bool UseCustomColor,
        ByteColor Color
    )
    {
        public static TargetingMeMarkerStyle FromConfiguration(Configuration configuration, MarkerAssetDefinition marker)
        {
            return new TargetingMeMarkerStyle(
                marker,
                Math.Clamp(configuration.TargetingMeMarkerOffsetX, -500f, 500f),
                Math.Clamp(configuration.TargetingMeMarkerOffsetY, -500f, 500f),
                Math.Clamp(configuration.TargetingMeMarkerScale, 0.01f, 5.0f),
                configuration.TargetingMeMarkerOpacity,
                configuration.TargetingMeMarkerGlowOpacity,
                configuration.UseCustomTargetingMeMarkerColor,
                new ByteColor
                {
                    R = configuration.TargetingMeMarkerColorRed,
                    G = configuration.TargetingMeMarkerColorGreen,
                    B = configuration.TargetingMeMarkerColorBlue,
                    A = 255,
                }
            );
        }
    }

    private sealed class NamePlateMarkerSlot(int namePlateIndex, uint nodeId)
    {
        private nint markerRoot;
        private nint glowNode;
        private nint outlineNode;
        private nint owningComponent;
        private bool seenThisFrame;
        private bool lastVisible;
        private int appliedStyleRevision = -1;

        public bool HasMarker => markerRoot != nint.Zero && glowNode != nint.Zero && outlineNode != nint.Zero;

        private AtkResNode* MarkerRootNode => (AtkResNode*)markerRoot;
        private AtkImageNode* GlowImageNode => (AtkImageNode*)glowNode;
        private AtkImageNode* OutlineImageNode => (AtkImageNode*)outlineNode;

        public bool EnsureAttached(
            AtkComponentBase* component,
            AddonNamePlate.NamePlateObject* namePlateObject,
            TargetingMeMarkerStyle style,
            int styleRevision,
            AtkUldPartsList* partsList
        )
        {
            var parent = namePlateObject == null ? null : namePlateObject->NameContainer;
            if (component == null || parent == null || partsList == null)
            {
                return false;
            }

            var uldManager = &component->UldManager;
            if (HasMarker)
            {
                if (owningComponent == (nint)component && MarkerRootNode->ParentNode == parent && ReferencesPartsList(partsList))
                {
                    ApplyStyle(style, styleRevision, parent);
                    return false;
                }

                Destroy(null);
            }

            owningComponent = (nint)component;
            if (!CreateMarkerTree(uldManager, parent, style, styleRevision, partsList))
            {
                ClearReference();
                return false;
            }

            NativeAtkNodeTree.MarkDirty(uldManager, parent);
            return true;
        }

        public void ApplyLayout(TargetingMeMarkerStyle style, AddonNamePlate.NamePlateObject* namePlateObject)
        {
            if (!HasMarker || namePlateObject == null)
            {
                return;
            }

            var parent = namePlateObject->NameContainer;
            var nameText = namePlateObject->NameText;
            if (parent == null || nameText == null || MarkerRootNode->ParentNode != parent)
            {
                return;
            }

            var textNode = &nameText->AtkResNode;
            var textTopCenter = NativeAtkGeometry.ConvertLocalPoint(textNode, parent, textNode->Width / 2f, 0f);

            var inheritedScale = NativeAtkGeometry.GetInheritedScale(parent);
            var root = MarkerRootNode;

            // No scale on this marker
            root->ScaleX = NativeAtkGeometry.DivideByScale(style.Scale, inheritedScale.ScaleX);
            root->ScaleY = NativeAtkGeometry.DivideByScale(style.Scale, inheritedScale.ScaleY);

            root->X = textTopCenter.X + style.OffsetX - (style.Marker.Width * root->ScaleX / 2f);
            root->Y = textTopCenter.Y + style.OffsetY - (style.Marker.Height * root->ScaleY / 2f);

            root->IsDirty = true;
            parent->IsDirty = true;
        }

        public void SetVisible(bool visible, bool textureReady)
        {
            seenThisFrame = true;
            lastVisible = visible;
            ApplyActualVisibility(textureReady);
        }

        public void MarkUnseen()
        {
            seenThisFrame = false;
        }

        public void HideIfUnseen(bool textureReady)
        {
            if (!seenThisFrame)
            {
                lastVisible = false;
                ApplyActualVisibility(textureReady);
            }
        }

        public void SetNamePlateColor(ByteColor textColor, ByteColor edgeColor)
        {
            SetMarkerColor(textColor, edgeColor);
        }

        public void SetMarkerColor(ByteColor color)
        {
            SetMarkerColor(color, color);
        }

        private void SetMarkerColor(ByteColor glowColor, ByteColor outlineColor)
        {
            if (!HasMarker)
            {
                return;
            }

            var glow = &GlowImageNode->AtkResNode;
            glow->AddRed = (short)(glowColor.R - 160);
            glow->AddGreen = (short)(glowColor.G - 160);
            glow->AddBlue = (short)(glowColor.B - 160);
            glow->IsDirty = true;

            var outline = &OutlineImageNode->AtkResNode;
            outline->AddRed = outlineColor.R;
            outline->AddGreen = outlineColor.G;
            outline->AddBlue = outlineColor.B;
            outline->IsDirty = true;
        }

        public void MarkImagesDirty(bool textureReady)
        {
            if (!HasMarker)
            {
                return;
            }

            NativeAtkNodeTree.MarkImageDirty(GlowImageNode);
            NativeAtkNodeTree.MarkImageDirty(OutlineImageNode);
            MarkerRootNode->IsDirty = true;
            ApplyActualVisibility(textureReady);
            if (MarkerRootNode->ParentNode != null)
            {
                MarkerRootNode->ParentNode->IsDirty = true;
            }
        }

        public bool ReferencesPartsList(AtkUldPartsList* partsList)
        {
            return partsList != null
                && (
                    (glowNode != nint.Zero && GlowImageNode->PartsList == partsList)
                    || (outlineNode != nint.Zero && OutlineImageNode->PartsList == partsList)
                );
        }

        public void Destroy(AddonNamePlate* addon)
        {
            if (!HasMarker)
            {
                ClearReference();
                return;
            }

            var parent = MarkerRootNode->ParentNode;
            var uldManager = TryGetUldManager(addon);
            DestroyMarkerTree();
            NativeAtkNodeTree.MarkDirty(uldManager, parent);
            ClearReference();
        }

        public void ClearReference()
        {
            markerRoot = nint.Zero;
            glowNode = nint.Zero;
            outlineNode = nint.Zero;
            owningComponent = nint.Zero;
            seenThisFrame = false;
            lastVisible = false;
            appliedStyleRevision = -1;
        }

        private bool CreateMarkerTree(
            AtkUldManager* uldManager,
            AtkResNode* parent,
            TargetingMeMarkerStyle style,
            int styleRevision,
            AtkUldPartsList* partsList
        )
        {
            var root = uldManager->CreateAtkNode(NodeType.Res);
            var glow = (AtkImageNode*)uldManager->CreateAtkNode(NodeType.Image);
            var outline = (AtkImageNode*)uldManager->CreateAtkNode(NodeType.Image);
            if (root == null || glow == null || outline == null)
            {
                DestroyCreatedNode((AtkResNode*)outline);
                DestroyCreatedNode((AtkResNode*)glow);
                DestroyCreatedNode(root);
                return false;
            }

            markerRoot = (nint)root;
            glowNode = (nint)glow;
            outlineNode = (nint)outline;

            NativeAtkNodeStyle.InitializeVisualDefaults(root);
            NativeAtkNodeStyle.InitializeVisualDefaults(&glow->AtkResNode);
            NativeAtkNodeStyle.InitializeVisualDefaults(&outline->AtkResNode);
            ConfigureRoot(root, nodeId);
            ConfigureImage(glow, nodeId + 1000, partsList);
            ConfigureImage(outline, nodeId + 2000, partsList);

            NativeAtkNodeTree.InsertFirstChild(parent, root);
            NativeAtkNodeTree.InsertLastChild(root, (AtkResNode*)glow);
            NativeAtkNodeTree.InsertLastChild(root, (AtkResNode*)outline);

            ApplyVisualStyle(style);
            root->ToggleVisibility(false);
            appliedStyleRevision = styleRevision;
            return true;
        }

        private void ApplyStyle(TargetingMeMarkerStyle style, int styleRevision, AtkResNode* parent)
        {
            if (!HasMarker || appliedStyleRevision == styleRevision)
            {
                return;
            }

            ApplyVisualStyle(style);
            appliedStyleRevision = styleRevision;
            parent->IsDirty = true;
        }

        private void ApplyVisualStyle(TargetingMeMarkerStyle style)
        {
            var marker = style.Marker;
            var root = MarkerRootNode;
            root->Width = marker.Width;
            root->Height = marker.Height;
            root->OriginX = 0;
            root->OriginY = 0;
            NativeAtkNodeStyle.ApplyAlpha(root, style.Opacity);
            root->IsDirty = true;

            ApplyImageStyle(GlowImageNode, marker.GlowPartId, marker.Width, marker.Height, style.GlowOpacity);
            ApplyImageStyle(OutlineImageNode, marker.OutlinePartId, marker.Width, marker.Height, 255);
        }

        private void ApplyActualVisibility(bool textureReady)
        {
            if (HasMarker)
            {
                MarkerRootNode->ToggleVisibility(lastVisible && textureReady);
                MarkerRootNode->IsDirty = true;
            }
        }

        private void DestroyMarkerTree()
        {
            if (outlineNode != nint.Zero)
            {
                var outline = OutlineImageNode;
                outline->PartsList = null;
                outline->PartId = 0;
                NativeAtkNodeTree.Detach(&outline->AtkResNode);
                outline->AtkResNode.Destroy(true);
                outlineNode = nint.Zero;
            }

            if (glowNode != nint.Zero)
            {
                var glow = GlowImageNode;
                glow->PartsList = null;
                glow->PartId = 0;
                NativeAtkNodeTree.Detach(&glow->AtkResNode);
                glow->AtkResNode.Destroy(true);
                glowNode = nint.Zero;
            }

            if (markerRoot != nint.Zero)
            {
                NativeAtkNodeTree.Detach(MarkerRootNode);
                MarkerRootNode->Destroy(true);
                markerRoot = nint.Zero;
            }
        }

        private AtkUldManager* TryGetUldManager(AddonNamePlate* addon)
        {
            if (addon == null || (uint)namePlateIndex >= NamePlateSlotCount || addon->NamePlateObjectArray == null)
            {
                return null;
            }

            var rootComponentNode = addon->NamePlateObjectArray[namePlateIndex].RootComponentNode;
            if (rootComponentNode == null || rootComponentNode->Component == null || (nint)rootComponentNode->Component != owningComponent)
            {
                return null;
            }

            return &rootComponentNode->Component->UldManager;
        }

        private static void ApplyImageStyle(AtkImageNode* imageNode, ushort partId, ushort width, ushort height, byte opacity)
        {
            var node = &imageNode->AtkResNode;
            node->X = 0;
            node->Y = 0;
            node->Width = width;
            node->Height = height;
            node->ScaleX = 1f;
            node->ScaleY = 1f;
            node->OriginX = 0;
            node->OriginY = 0;
            NativeAtkNodeStyle.ApplyAlpha(node, opacity);
            node->ToggleVisibility(true);
            node->IsDirty = true;
            imageNode->PartId = partId;
        }

        private static void ConfigureRoot(AtkResNode* root, uint rootNodeId)
        {
            root->NodeId = rootNodeId;
            root->Type = NodeType.Res;
            root->NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft | NodeFlags.Visible | NodeFlags.Enabled;
            root->DrawFlags = 0;
            root->IsDirty = true;
        }

        private static void ConfigureImage(AtkImageNode* imageNode, uint imageNodeId, AtkUldPartsList* partsList)
        {
            var node = &imageNode->AtkResNode;
            node->NodeId = imageNodeId;
            node->Type = NodeType.Image;
            node->NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft | NodeFlags.Visible | NodeFlags.Enabled;
            node->DrawFlags = 0;
            node->IsDirty = true;
            imageNode->PartsList = partsList;
            imageNode->WrapMode = 1;
            imageNode->Flags = 0;
        }

        private static void DestroyCreatedNode(AtkResNode* node)
        {
            if (node != null)
            {
                node->Destroy(true);
            }
        }
    }
}
