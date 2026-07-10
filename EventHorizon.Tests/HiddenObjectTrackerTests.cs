using EventHorizon.Culling.Visibility;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHorizon.Tests;

[TestClass]
public sealed unsafe class HiddenObjectTrackerTests
{
    private const VisibilityFlags HiddenFlags = VisibilityFlags.Nameplate | VisibilityFlags.Model;

    [TestMethod]
    public void HiddenObjectMovedToAnotherSlot_RemainsHiddenAndRecordMoves()
    {
        var tracker = CreateTracker();
        var manager = default(GameObjectManager);
        var gameObject = CreateObject(100);
        manager.Objects.IndexSorted[20] = &gameObject;
        tracker.Hide(&gameObject, HiddenFlags, 20);

        manager.Objects.IndexSorted[20] = null;
        manager.Objects.IndexSorted[40] = &gameObject;
        tracker.PruneMissing(&manager);

        Assert.AreEqual(HiddenFlags, gameObject.RenderFlags & HiddenFlags);
        Assert.AreEqual(40, tracker.GetRecordedObjectIndex(Identity(&gameObject)));
    }

    [TestMethod]
    public void AdmissionReassertAtNewSlot_UpdatesRecordedIndexAndFlags()
    {
        var tracker = CreateTracker();
        var gameObject = CreateObject(101);
        tracker.Hide(&gameObject, HiddenFlags, 20);
        gameObject.RenderFlags = 0;

        tracker.Hide(&gameObject, HiddenFlags, 40);

        Assert.AreEqual(HiddenFlags, gameObject.RenderFlags & HiddenFlags);
        Assert.AreEqual(40, tracker.GetRecordedObjectIndex(Identity(&gameObject)));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddressReusedByDifferentIdentity_DropsRecordWithoutChangingNewFlags(bool startsWithSharedFlag)
    {
        var tracker = CreateTracker();
        var manager = default(GameObjectManager);
        var storage = CreateObject(102);
        manager.Objects.IndexSorted[20] = &storage;
        tracker.Hide(&storage, HiddenFlags, 20);

        storage.EntityId = 202;
        storage.RenderFlags = startsWithSharedFlag ? VisibilityFlags.Nameplate : (VisibilityFlags)0x4000;
        var expected = storage.RenderFlags;
        tracker.PruneMissing(&manager);

        Assert.AreEqual(expected, storage.RenderFlags);
        Assert.IsFalse(tracker.IsHidden(&storage));
    }

    private static GameObject CreateObject(uint entityId) =>
        new()
        {
            EntityId = entityId,
            ObjectKind = ObjectKind.Pc,
            RenderFlags = 0,
        };

    private static HiddenObjectTracker CreateTracker() => new(Identity);

    private static PlayerObjectIdentity Identity(GameObject* gameObject) =>
        new((nint)gameObject, gameObject->EntityId, gameObject->EntityId);
}
