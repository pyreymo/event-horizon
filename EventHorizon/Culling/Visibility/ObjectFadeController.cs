using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling.Visibility;

internal sealed unsafe class ObjectFadeController(HiddenObjectTracker hiddenObjectTracker, VisibilityFlags hiddenFlags)
{
    private const int FadeInDurationMs = 220;
    private const int FadeOutDurationMs = 180;
    private const float OpaqueAlpha = 1f;

    private readonly HiddenObjectTracker hiddenObjectTracker = hiddenObjectTracker;
    private readonly VisibilityFlags hiddenFlags = hiddenFlags;
    private readonly Dictionary<nint, FadeRecord> fadeObjects = [];
    private readonly HashSet<nint> liveFadeAddresses = [];
    private readonly List<nint> staleAddresses = [];

    public bool HasActiveFades => fadeObjects.Count > 0;

    public bool IsFading(PlayerObjectIdentity identity)
    {
        return fadeObjects.TryGetValue(identity.Address, out var record) && record.IsSameObject(identity);
    }

    public bool Update(GameObject* gameObject, bool shouldHide, int objectIndex)
    {
        var address = (nint)gameObject;
        if (address == nint.Zero)
        {
            return false;
        }

        var isHidden = hiddenObjectTracker.IsHidden(gameObject);
        if (!fadeObjects.TryGetValue(address, out var record) || !record.IsSameObject(gameObject))
        {
            if (shouldHide && !isHidden)
            {
                FadeVisibleObjectHidden(
                    gameObject,
                    address,
                    FadeRecord.From(gameObject, desiredVisible: false, alpha: OpaqueAlpha, objectIndex)
                );
                return true;
            }

            if (!shouldHide && isHidden)
            {
                hiddenObjectTracker.RestoreIfHidden(gameObject);
                FadeHiddenObjectVisible(gameObject, address, FadeRecord.From(gameObject, desiredVisible: true, alpha: 0f, objectIndex));
                return true;
            }

            return false;
        }

        if (shouldHide)
        {
            FadeVisibleObjectHidden(gameObject, address, record);
        }
        else
        {
            hiddenObjectTracker.RestoreIfHidden(gameObject);
            FadeHiddenObjectVisible(gameObject, address, record);
        }

        return true;
    }

    public void Reset(GameObjectManager* manager)
    {
        if (fadeObjects.Count == 0)
        {
            return;
        }

        if (manager != null)
        {
            for (var i = 0; i < manager->Objects.IndexSorted.Length; i++)
            {
                var gameObject = manager->Objects.IndexSorted[i].Value;
                if (TryGetLiveRecord(gameObject, out _))
                {
                    SetAlpha(gameObject, OpaqueAlpha);
                }
            }
        }

        fadeObjects.Clear();
    }

    public void PruneMissing(GameObjectManager* manager)
    {
        if (fadeObjects.Count == 0)
        {
            return;
        }

        liveFadeAddresses.Clear();
        staleAddresses.Clear();

        if (manager != null)
        {
            for (var i = 0; i < manager->Objects.IndexSorted.Length; i++)
            {
                var gameObject = manager->Objects.IndexSorted[i].Value;
                if (TryGetLiveRecord(gameObject, out _))
                {
                    liveFadeAddresses.Add((nint)gameObject);
                }
            }
        }

        foreach (var address in fadeObjects.Keys)
        {
            if (!liveFadeAddresses.Contains(address))
            {
                staleAddresses.Add(address);
            }
        }

        foreach (var address in staleAddresses)
        {
            fadeObjects.Remove(address);
        }

        liveFadeAddresses.Clear();
        staleAddresses.Clear();
    }

    public void Clear()
    {
        fadeObjects.Clear();
    }

    private void FadeHiddenObjectVisible(GameObject* gameObject, nint address, FadeRecord record)
    {
        var now = Environment.TickCount64;
        if (!record.DesiredVisible)
        {
            record = record.BeginTransition(desiredVisible: true, now);
        }

        var alpha = CalculateFadeAlpha(record, now);
        if (alpha >= OpaqueAlpha)
        {
            SetAlpha(gameObject, OpaqueAlpha);
            fadeObjects.Remove(address);
            return;
        }

        SetAlpha(gameObject, alpha);
        fadeObjects[address] = record with { Alpha = alpha, LastUpdate = now };
    }

    private void FadeVisibleObjectHidden(GameObject* gameObject, nint address, FadeRecord record)
    {
        var now = Environment.TickCount64;
        if (record.DesiredVisible)
        {
            record = record.BeginTransition(desiredVisible: false, now);
        }

        var alpha = CalculateFadeAlpha(record, now);
        if (alpha <= 0f)
        {
            hiddenObjectTracker.Hide(gameObject, hiddenFlags, record.ObjectIndex);
            SetAlpha(gameObject, OpaqueAlpha);
            fadeObjects.Remove(address);
            return;
        }

        hiddenObjectTracker.RestoreIfHidden(gameObject);
        SetAlpha(gameObject, alpha);
        fadeObjects[address] = record with { Alpha = alpha, LastUpdate = now };
    }

    private static void SetAlpha(GameObject* gameObject, float alpha)
    {
        if (gameObject == null)
        {
            return;
        }

        ((Character*)gameObject)->Alpha = Math.Clamp(alpha, 0f, 1f);
    }

    private static float CalculateFadeAlpha(FadeRecord record, long now)
    {
        var duration = record.DesiredVisible ? FadeInDurationMs : FadeOutDurationMs;
        var progress = Math.Clamp((now - record.TransitionStart) / (float)duration, 0f, 1f);
        var easedProgress = record.DesiredVisible ? EaseOutCubic(progress) : EaseInCubic(progress);
        var target = record.DesiredVisible ? OpaqueAlpha : 0f;

        return Lerp(record.StartAlpha, target, easedProgress);
    }

    private static float EaseOutCubic(float value)
    {
        var inverse = 1f - value;
        return 1f - (inverse * inverse * inverse);
    }

    private static float EaseInCubic(float value)
    {
        return value * value * value;
    }

    private static float Lerp(float start, float end, float progress)
    {
        if (progress <= 0f)
        {
            return start;
        }

        if (progress >= 1f)
        {
            return end;
        }

        return start + ((end - start) * progress);
    }

    private bool TryGetLiveRecord(GameObject* gameObject, out FadeRecord record)
    {
        if (gameObject == null)
        {
            record = default;
            return false;
        }

        return fadeObjects.TryGetValue((nint)gameObject, out record) && record.IsSameObject(gameObject);
    }

    private readonly record struct FadeRecord(
        ulong GameObjectId,
        uint EntityId,
        bool DesiredVisible,
        float StartAlpha,
        float Alpha,
        long TransitionStart,
        long LastUpdate,
        int ObjectIndex
    )
    {
        public static FadeRecord From(GameObject* gameObject, bool desiredVisible, float alpha, int objectIndex)
        {
            return new(
                (ulong)gameObject->GetGameObjectId(),
                gameObject->EntityId,
                desiredVisible,
                alpha,
                alpha,
                Environment.TickCount64,
                Environment.TickCount64,
                objectIndex
            );
        }

        public FadeRecord BeginTransition(bool desiredVisible, long now)
        {
            return this with { DesiredVisible = desiredVisible, StartAlpha = Alpha, TransitionStart = now, LastUpdate = now };
        }

        public bool IsSameObject(GameObject* gameObject) =>
            gameObject != null && ((ulong)gameObject->GetGameObjectId() == GameObjectId) && (gameObject->EntityId == EntityId);

        public bool IsSameObject(PlayerObjectIdentity identity) => identity.GameObjectId == GameObjectId && identity.EntityId == EntityId;
    }
}
