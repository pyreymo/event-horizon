using System;
using EventHorizon.Integration.NativeUi;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.Dtr;

internal sealed unsafe class DtrBackgroundNode(uint nodeId)
{
    private nint node;

    public bool IsCreated => node != nint.Zero;

    public AtkResNode* ResourceNode => (AtkResNode*)node;

    public bool IsAttachedTo(AtkResNode* root) => node != nint.Zero && ResourceNode->ParentNode == root;

    public bool EnsureAttached(AtkUnitBase* unit, AtkResNode* root)
    {
        if (node != nint.Zero)
        {
            if (ResourceNode->ParentNode != root)
            {
                Destroy(unit);
            }
            else
            {
                MoveToBack(unit, root);
                return true;
            }
        }

        var nineGridNode = (AtkNineGridNode*)unit->UldManager.CreateAtkNode(NodeType.NineGrid);
        if (nineGridNode == null)
        {
            return false;
        }

        node = (nint)nineGridNode;
        nineGridNode->AtkResNode.NodeId = nodeId;
        nineGridNode->AtkResNode.Type = NodeType.NineGrid;
        NativeAtkNodeTree.InsertLastChild(root, (AtkResNode*)nineGridNode);
        NativeAtkNodeTree.MarkDirty(unit, root);
        return true;
    }

    public bool Update(NativeNodeBounds bounds, DtrBackgroundStyle style, DtrBackgroundSkin skin)
    {
        var nineGridNode = (AtkNineGridNode*)node;
        if (nineGridNode == null)
        {
            return false;
        }

        var resNode = &nineGridNode->AtkResNode;
        var x = MathF.Floor(bounds.X - style.PaddingLeft);
        var y = MathF.Floor(bounds.Y - style.PaddingTop);
        var width = (ushort)MathF.Ceiling(bounds.Width + style.PaddingLeft + style.PaddingRight);
        var height = (ushort)MathF.Ceiling(bounds.Height + style.PaddingTop + style.PaddingBottom);
        const NodeFlags nodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft | NodeFlags.Visible;
        const uint drawFlags = 0x8;

        if (IsCurrent(nineGridNode, x, y, width, height, nodeFlags, drawFlags, style, skin))
        {
            return false;
        }

        resNode->NodeId = nodeId;
        resNode->Type = NodeType.NineGrid;
        resNode->X = x;
        resNode->Y = y;
        resNode->Width = width;
        resNode->Height = height;
        resNode->ScaleX = 1.0f;
        resNode->ScaleY = 1.0f;
        resNode->Color = new ByteColor
        {
            R = 0,
            G = 0,
            B = 0,
            A = style.Alpha,
        };
        resNode->SetAlpha(style.Alpha);
        resNode->Alpha_2 = style.Alpha;
        resNode->NodeFlags = nodeFlags;
        resNode->DrawFlags = drawFlags;
        resNode->IsDirty = true;

        nineGridNode->PartsList = skin.PartsList;
        nineGridNode->PartId = skin.PartId;
        nineGridNode->TopOffset = skin.TopOffset;
        nineGridNode->RightOffset = skin.RightOffset;
        nineGridNode->BottomOffset = skin.BottomOffset;
        nineGridNode->LeftOffset = skin.LeftOffset;
        nineGridNode->BlendMode = skin.BlendMode;
        nineGridNode->PartsTypeRenderType = skin.RenderType;
        return true;
    }

    public void Destroy(AtkUnitBase* unit)
    {
        if (node == nint.Zero)
        {
            return;
        }

        var resNode = ResourceNode;
        var root = resNode->ParentNode;
        NativeAtkNodeTree.Detach(resNode);
        NativeAtkNodeTree.MarkDirty(unit, root);
        resNode->Destroy(true);
        node = nint.Zero;
    }

    private void MoveToBack(AtkUnitBase* unit, AtkResNode* root)
    {
        if (ResourceNode->NextSiblingNode == null)
        {
            return;
        }

        NativeAtkNodeTree.Detach(ResourceNode);
        NativeAtkNodeTree.InsertLastChild(root, ResourceNode);
        NativeAtkNodeTree.MarkDirty(unit, root);
    }

    private bool IsCurrent(
        AtkNineGridNode* nineGridNode,
        float x,
        float y,
        ushort width,
        ushort height,
        NodeFlags nodeFlags,
        uint drawFlags,
        DtrBackgroundStyle style,
        DtrBackgroundSkin skin
    )
    {
        var resNode = &nineGridNode->AtkResNode;
        return resNode->NodeId == nodeId
            && resNode->Type == NodeType.NineGrid
            && resNode->X == x
            && resNode->Y == y
            && resNode->Width == width
            && resNode->Height == height
            && resNode->ScaleX == 1.0f
            && resNode->ScaleY == 1.0f
            && resNode->Color.R == 0
            && resNode->Color.G == 0
            && resNode->Color.B == 0
            && resNode->Color.A == style.Alpha
            && resNode->Alpha_2 == style.Alpha
            && resNode->NodeFlags == nodeFlags
            && resNode->DrawFlags == drawFlags
            && nineGridNode->PartsList == skin.PartsList
            && nineGridNode->PartId == skin.PartId
            && nineGridNode->TopOffset == skin.TopOffset
            && nineGridNode->RightOffset == skin.RightOffset
            && nineGridNode->BottomOffset == skin.BottomOffset
            && nineGridNode->LeftOffset == skin.LeftOffset
            && nineGridNode->BlendMode == skin.BlendMode
            && nineGridNode->PartsTypeRenderType == skin.RenderType;
    }
}

internal readonly unsafe struct DtrBackgroundSkin(
    AtkUldPartsList* partsList,
    uint partId,
    short topOffset,
    short rightOffset,
    short bottomOffset,
    short leftOffset,
    uint blendMode,
    byte renderType
)
{
    public AtkUldPartsList* PartsList { get; } = partsList;
    public uint PartId { get; } = partId;
    public short TopOffset { get; } = topOffset;
    public short RightOffset { get; } = rightOffset;
    public short BottomOffset { get; } = bottomOffset;
    public short LeftOffset { get; } = leftOffset;
    public uint BlendMode { get; } = blendMode;
    public byte RenderType { get; } = renderType;
}

internal readonly record struct DtrBackgroundStyle(
    float PaddingLeft,
    float PaddingRight,
    float PaddingTop,
    float PaddingBottom,
    byte Alpha
);
