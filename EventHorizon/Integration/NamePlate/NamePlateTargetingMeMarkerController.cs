using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Plugin.Services;
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

    // Marker-specific data belongs here. To add another embedded PNG:
    // 1. include it as an EmbeddedResource, 2. add another definition,
    // 3. create one EmbeddedMarkerTextureResources instance and select it.
    // Do not duplicate the loading or unmanaged-resource lifecycle code.
    private static readonly MarkerAssetDefinition AlertEyeMarker = new(
        name: "Alert Eye",
        resourceSuffix: ".Assets.alert-eye.png",
        debugName: "EventHorizon alert-eye.png",
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
    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly NamePlateMarkerSlot[] slots = new NamePlateMarkerSlot[NamePlateSlotCount];
    private readonly EmbeddedMarkerTextureResources markerResources;

    private TargetingMeMarkerStyle appliedStyle;
    private int styleRevision;
    private bool lastTextureReady;
    private bool refreshPending;
    private bool disposed;

    public NamePlateTargetingMeMarkerController(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        INamePlateGui namePlateGui,
        IObjectTable objectTable,
        ITargetManager targetManager,
        Configuration configuration,
        IFramework framework,
        ITextureProvider textureProvider,
        IPluginLog log
    )
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.namePlateGui = namePlateGui;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.configuration = configuration;
        this.framework = framework;
        markerResources = new EmbeddedMarkerTextureResources(textureProvider, log, AlertEyeMarker);

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
        if (!configuration.EnableTargetingMeMarker)
        {
            RemoveAllMarkerNodes();
            ReleaseMarkerResources();
            namePlateGui.RequestRedraw();
            return;
        }

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
            return;
        }

        RefreshStyleRevision();

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
                slot.SetNamePlateColor(nameText->TextColor, nameText->EdgeColor);
            }
        }

        for (var index = 0; index < slots.Length; index++)
        {
            slots[index].HideIfUnseen(textureReady);
        }
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
        byte GlowOpacity
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
                configuration.TargetingMeMarkerGlowOpacity
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

            MarkDirty(uldManager, parent);
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
            var textTopCenter = ConvertLocalPoint(textNode, parent, textNode->Width / 2f, 0f);

            var inheritedScale = GetInheritedScale(parent);
            var root = MarkerRootNode;

            // No scale on this marker
            root->ScaleX = DivideByScale(style.Scale, inheritedScale.ScaleX);
            root->ScaleY = DivideByScale(style.Scale, inheritedScale.ScaleY);

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
            if (!HasMarker)
            {
                return;
            }

            var glow = &GlowImageNode->AtkResNode;
            glow->AddRed = (short)(textColor.R - 160);
            glow->AddGreen = (short)(textColor.G - 160);
            glow->AddBlue = (short)(textColor.B - 160);
            glow->IsDirty = true;

            var outline = &OutlineImageNode->AtkResNode;
            outline->AddRed = edgeColor.R;
            outline->AddGreen = edgeColor.G;
            outline->AddBlue = edgeColor.B;
            outline->IsDirty = true;
        }

        public void MarkImagesDirty(bool textureReady)
        {
            if (!HasMarker)
            {
                return;
            }

            MarkImageDirty(GlowImageNode);
            MarkImageDirty(OutlineImageNode);
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
                    glowNode != nint.Zero && GlowImageNode->PartsList == partsList
                    || outlineNode != nint.Zero && OutlineImageNode->PartsList == partsList
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
            MarkDirty(uldManager, parent);
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

            InitializeVisualDefaults(root);
            InitializeVisualDefaults(&glow->AtkResNode);
            InitializeVisualDefaults(&outline->AtkResNode);
            ConfigureRoot(root, nodeId);
            ConfigureImage(glow, nodeId + 1000, partsList);
            ConfigureImage(outline, nodeId + 2000, partsList);

            InsertFirstChild(parent, root);
            InsertLastChild(root, (AtkResNode*)glow);
            InsertLastChild(root, (AtkResNode*)outline);

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
            ApplyAlpha(root, style.Opacity);
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
                DetachNode(&outline->AtkResNode);
                outline->AtkResNode.Destroy(true);
                outlineNode = nint.Zero;
            }

            if (glowNode != nint.Zero)
            {
                var glow = GlowImageNode;
                glow->PartsList = null;
                glow->PartId = 0;
                DetachNode(&glow->AtkResNode);
                glow->AtkResNode.Destroy(true);
                glowNode = nint.Zero;
            }

            if (markerRoot != nint.Zero)
            {
                DetachNode(MarkerRootNode);
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

        private static (float X, float Y) ConvertLocalPoint(AtkResNode* fromNode, AtkResNode* toAncestor, float x, float y)
        {
            const int MaxParents = 16;
            var node = fromNode;
            for (var index = 0; node != null && node != toAncestor && index < MaxParents; index++)
            {
                var scaleX = SafeScale(node->ScaleX);
                var scaleY = SafeScale(node->ScaleY);
                x = node->X + node->OriginX + ((x - node->OriginX) * scaleX);
                y = node->Y + node->OriginY + ((y - node->OriginY) * scaleY);
                node = node->ParentNode;
            }

            return (x, y);
        }

        private static (float ScaleX, float ScaleY) GetInheritedScale(AtkResNode* node)
        {
            const int MaxParents = 16;
            var scaleX = 1f;
            var scaleY = 1f;
            for (var index = 0; node != null && index < MaxParents; index++)
            {
                scaleX *= SafeScale(node->ScaleX);
                scaleY *= SafeScale(node->ScaleY);
                node = node->ParentNode;
            }

            return (SafeScale(scaleX), SafeScale(scaleY));
        }

        private static float SafeScale(float scale)
        {
            return MathF.Abs(scale) < 0.001f ? 1f : scale;
        }

        private static float DivideByScale(float value, float scale)
        {
            return value / SafeScale(scale);
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
            ApplyAlpha(node, opacity);
            node->ToggleVisibility(true);
            node->IsDirty = true;
            imageNode->PartId = partId;
        }

        private static void InitializeVisualDefaults(AtkResNode* node)
        {
            node->Color = new ByteColor
            {
                R = 255,
                G = 255,
                B = 255,
                A = 255,
            };
            node->AddRed = 0;
            node->AddGreen = 0;
            node->AddBlue = 0;
            node->MultiplyRed = 100;
            node->MultiplyGreen = 100;
            node->MultiplyBlue = 100;
            node->ScaleX = 1f;
            node->ScaleY = 1f;
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

        private static void MarkDirty(AtkUldManager* uldManager, AtkResNode* root)
        {
            if (root != null)
            {
                root->IsDirty = true;
            }
            if (uldManager != null)
            {
                uldManager->UpdateDrawNodeList();
            }
        }

        private static void MarkImageDirty(AtkImageNode* imageNode)
        {
            if (imageNode == null)
            {
                return;
            }

            imageNode->AtkResNode.IsDirty = true;
            if (imageNode->AtkResNode.ParentNode != null)
            {
                imageNode->AtkResNode.ParentNode->IsDirty = true;
            }
        }

        private static void InsertFirstChild(AtkResNode* parent, AtkResNode* child)
        {
            var lastChild = parent->ChildNode;
            AtkResNode* firstChild = null;
            if (lastChild != null)
            {
                firstChild = lastChild;
                var siblingCount = 0;
                while (firstChild->PrevSiblingNode != null && firstChild->PrevSiblingNode->ParentNode == parent && siblingCount++ < 256)
                {
                    firstChild = firstChild->PrevSiblingNode;
                }
            }

            child->ParentNode = parent;
            child->PrevSiblingNode = null;
            child->NextSiblingNode = firstChild;
            child->ChildNode = null;
            child->ChildCount = 0;

            if (firstChild != null)
            {
                firstChild->PrevSiblingNode = child;
            }
            if (lastChild == null)
            {
                parent->ChildNode = child;
            }

            parent->ChildCount++;
        }

        private static void InsertLastChild(AtkResNode* parent, AtkResNode* child)
        {
            var lastChild = parent->ChildNode;
            if (lastChild != null)
            {
                var siblingCount = 0;
                while (lastChild->NextSiblingNode != null && lastChild->NextSiblingNode->ParentNode == parent && siblingCount++ < 256)
                {
                    lastChild = lastChild->NextSiblingNode;
                }
            }

            child->ParentNode = parent;
            child->PrevSiblingNode = lastChild;
            child->NextSiblingNode = null;
            child->ChildNode = null;
            child->ChildCount = 0;

            if (lastChild != null)
            {
                lastChild->NextSiblingNode = child;
            }

            parent->ChildNode = child;
            parent->ChildCount++;
        }

        private static void DetachNode(AtkResNode* node)
        {
            if (node == null)
            {
                return;
            }

            var parent = node->ParentNode;
            var previousSibling = node->PrevSiblingNode;
            var nextSibling = node->NextSiblingNode;
            var previousIsChild = previousSibling != null && previousSibling->ParentNode == parent;
            var nextIsChild = nextSibling != null && nextSibling->ParentNode == parent;

            if (previousIsChild)
            {
                previousSibling->NextSiblingNode = nextIsChild ? nextSibling : null;
            }
            if (nextIsChild)
            {
                nextSibling->PrevSiblingNode = previousIsChild ? previousSibling : null;
            }
            if (parent != null && parent->ChildNode == node)
            {
                parent->ChildNode =
                    previousIsChild ? previousSibling
                    : nextIsChild ? nextSibling
                    : null;
            }
            if (parent != null && parent->ChildCount > 0)
            {
                parent->ChildCount--;
            }

            node->ParentNode = null;
            node->PrevSiblingNode = null;
            node->NextSiblingNode = null;
        }

        private static void ApplyAlpha(AtkResNode* node, byte alpha)
        {
            node->Color.A = alpha;
            node->SetAlpha(alpha);
            node->Alpha_2 = alpha;
        }
    }
}
