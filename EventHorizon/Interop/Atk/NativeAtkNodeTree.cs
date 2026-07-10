using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Interop.Atk;

internal static unsafe class NativeAtkNodeTree
{
    private const int MaxSiblings = 256;

    public static void InsertFirstChild(AtkResNode* parent, AtkResNode* child)
    {
        var lastChild = parent->ChildNode;
        AtkResNode* firstChild = null;
        if (lastChild != null)
        {
            firstChild = lastChild;
            var siblingCount = 0;
            while (firstChild->PrevSiblingNode != null && firstChild->PrevSiblingNode->ParentNode == parent && siblingCount++ < MaxSiblings)
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

    public static void InsertLastChild(AtkResNode* parent, AtkResNode* child)
    {
        var lastChild = parent->ChildNode;
        if (lastChild != null)
        {
            var siblingCount = 0;
            while (lastChild->NextSiblingNode != null && lastChild->NextSiblingNode->ParentNode == parent && siblingCount++ < MaxSiblings)
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

    public static void Detach(AtkResNode* node)
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

    public static void MarkDirty(AtkUnitBase* unit, AtkResNode* root)
    {
        MarkDirty(unit == null ? null : &unit->UldManager, root);
    }

    public static void MarkDirty(AtkUldManager* uldManager, AtkResNode* root)
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

    public static void MarkImageDirty(AtkImageNode* imageNode)
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
}
