using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.NativeUi;

internal static unsafe class NativeNodeBoundsScanner
{
    public static void AddChildTreeBounds(
        AtkResNode* root,
        AtkResNode* excludedNode,
        NodeBoundsPredicate shouldUseNode,
        ref NativeNodeBoundsCollector collector
    )
    {
        VisitChildren(root, excludedNode, shouldUseNode, 0, 0, 1, 1, ref collector, 0);
    }

    public static void AddNodeListBounds(
        AtkUnitBase* unit,
        AtkResNode* excludedNode,
        NodeBoundsPredicate shouldUseNode,
        ref NativeNodeBoundsCollector collector
    )
    {
        if (unit == null || unit->RootNode == null || unit->UldManager.NodeList == null)
        {
            return;
        }

        for (var i = 0; i < unit->UldManager.NodeListCount; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (!shouldUseNode(node, excludedNode) || !TryGetNodeBoundsRelativeToRoot(unit->RootNode, node, out var bounds))
            {
                continue;
            }

            collector.Add(bounds);
        }
    }

    private static void VisitChildren(
        AtkResNode* parent,
        AtkResNode* excludedNode,
        NodeBoundsPredicate shouldUseNode,
        float parentX,
        float parentY,
        float parentScaleX,
        float parentScaleY,
        ref NativeNodeBoundsCollector collector,
        int depth
    )
    {
        const int MaxDepth = 8;
        const int MaxSiblings = 256;

        if (parent == null || depth >= MaxDepth)
        {
            return;
        }

        var sibling = GetFirstSibling(parent->ChildNode);
        var siblingCount = 0;
        while (sibling != null && siblingCount++ < MaxSiblings)
        {
            var nodeX = parentX + (sibling->X * parentScaleX);
            var nodeY = parentY + (sibling->Y * parentScaleY);
            var nodeScaleX = parentScaleX * sibling->ScaleX;
            var nodeScaleY = parentScaleY * sibling->ScaleY;

            if (shouldUseNode(sibling, excludedNode))
            {
                GetVisualNodeSize(sibling, out var nodeWidth, out var nodeHeight);
                collector.Add(CreateBoundsFromScaledRect(nodeX, nodeY, nodeWidth * nodeScaleX, nodeHeight * nodeScaleY));
            }

            if (sibling->ChildNode != null)
            {
                VisitChildren(sibling, excludedNode, shouldUseNode, nodeX, nodeY, nodeScaleX, nodeScaleY, ref collector, depth + 1);
            }

            sibling = sibling->NextSiblingNode;
        }
    }

    private static bool TryGetNodeBoundsRelativeToRoot(AtkResNode* root, AtkResNode* node, out NativeNodeBounds bounds)
    {
        bounds = default;
        if (root == null || node == null || node == root)
        {
            return false;
        }

        const int MaxDepth = 16;
        var x = node->X;
        var y = node->Y;
        var scaleX = node->ScaleX;
        var scaleY = node->ScaleY;
        var parent = node->ParentNode;
        var depth = 0;

        while (parent != null && parent != root && depth++ < MaxDepth)
        {
            x = parent->X + (x * parent->ScaleX);
            y = parent->Y + (y * parent->ScaleY);
            scaleX *= parent->ScaleX;
            scaleY *= parent->ScaleY;
            parent = parent->ParentNode;
        }

        if (parent != root)
        {
            return false;
        }

        GetVisualNodeSize(node, out var width, out var height);
        bounds = CreateBoundsFromScaledRect(x, y, width * scaleX, height * scaleY);
        return true;
    }

    private static void GetVisualNodeSize(AtkResNode* node, out ushort width, out ushort height)
    {
        width = node->Width;
        height = node->Height;
        if (node->Type != NodeType.Text)
        {
            return;
        }

        var textNode = (AtkTextNode*)node;
        var text = textNode->NodeText.StringPtr;
        if (text.Value == null)
        {
            return;
        }

        ushort drawWidth = 0;
        ushort drawHeight = 0;
        textNode->GetTextDrawSize(&drawWidth, &drawHeight, text, 0, -1, false);
        width = drawWidth > 0 ? drawWidth : width;
        height = drawHeight > 0 ? drawHeight : height;
    }

    private static NativeNodeBounds CreateBoundsFromScaledRect(float x, float y, float width, float height)
    {
        var minX = MathF.Min(x, x + width);
        var minY = MathF.Min(y, y + height);
        var maxX = MathF.Max(x, x + width);
        var maxY = MathF.Max(y, y + height);
        var boundsX = MathF.Floor(minX);
        var boundsY = MathF.Floor(minY);
        return new NativeNodeBounds(boundsX, boundsY, (ushort)MathF.Ceiling(maxX - boundsX), (ushort)MathF.Ceiling(maxY - boundsY));
    }

    private static AtkResNode* GetFirstSibling(AtkResNode* node)
    {
        const int MaxSiblings = 256;

        var sibling = node;
        var siblingCount = 0;
        while (sibling != null && sibling->PrevSiblingNode != null && siblingCount++ < MaxSiblings)
        {
            sibling = sibling->PrevSiblingNode;
        }

        return sibling;
    }
}

internal unsafe delegate bool NodeBoundsPredicate(AtkResNode* node, AtkResNode* excludedNode);

internal struct NativeNodeBoundsCollector
{
    private float minX;
    private float minY;
    private float maxX;
    private float maxY;
    private bool hasBounds;

    public void Add(NativeNodeBounds bounds)
    {
        if (!hasBounds)
        {
            minX = bounds.X;
            minY = bounds.Y;
            maxX = bounds.X + bounds.Width;
            maxY = bounds.Y + bounds.Height;
            hasBounds = true;
            return;
        }

        minX = MathF.Min(minX, bounds.X);
        minY = MathF.Min(minY, bounds.Y);
        maxX = MathF.Max(maxX, bounds.X + bounds.Width);
        maxY = MathF.Max(maxY, bounds.Y + bounds.Height);
    }

    public readonly bool TryBuild(out NativeNodeBounds bounds)
    {
        if (!hasBounds || maxX <= minX || maxY <= minY)
        {
            bounds = default;
            return false;
        }

        var x = MathF.Floor(minX);
        var y = MathF.Floor(minY);
        bounds = new NativeNodeBounds(x, y, (ushort)MathF.Ceiling(maxX - x), (ushort)MathF.Ceiling(maxY - y));
        return true;
    }
}

internal readonly record struct NativeNodeBounds(float X, float Y, ushort Width, ushort Height);
