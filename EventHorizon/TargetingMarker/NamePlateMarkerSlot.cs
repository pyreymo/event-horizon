using System;
using EventHorizon.Interop.Atk;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.TargetingMarker;

internal readonly record struct TargetingMeMarkerStyle(
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

internal sealed unsafe class NamePlateMarkerSlot(int namePlateIndex, uint nodeId)
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
        if (addon == null || (uint)namePlateIndex >= 50 || addon->NamePlateObjectArray == null)
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
