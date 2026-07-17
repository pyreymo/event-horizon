using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Culling;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
#if DEBUG
using Dalamud.Plugin.Ipc;
#endif

namespace EventHorizon.Integration.Debug;

internal static unsafe class PlayerAdmissionDebugTrace
{
#if DEBUG
    private const string CreatingCharacterBaseLabel = "Penumbra.CreatingCharacterBase.V5";
    private const string CreatedCharacterBaseLabel = "Penumbra.CreatedCharacterBase.V5";
    private static readonly long RenderQuietThresholdTicks = 2 * Stopwatch.Frequency;

    private static readonly Lock StateLock = new();
    private static readonly ConcurrentDictionary<PlayerObjectIdentity, TrackedPlayerState> TrackedPlayers = new();
    private static readonly ConcurrentDictionary<nint, RenderState> RenderStates = new();

    private static IClientState? ClientState;
    private static IPlayerState? PlayerState;
    private static ITargetManager? TargetManager;
    private static ICallGateSubscriber<nint, Guid, nint, nint, nint, object?>? CreatingCharacterBase;
    private static ICallGateSubscriber<nint, Guid, nint, object?>? CreatedCharacterBase;
    private static Hook<OnRenderModelDelegate>? OnRenderModelHook;
    private static bool? LastPlayerLoaded;
    private static string? LastCullingMode;

    internal static event Action<nint, nint>? RenderModelObserved;

    private delegate void OnRenderModelDelegate(CharacterBase* characterBase, Model* model);
#endif

    [Conditional("DEBUG")]
    public static void Initialize(
        IDalamudPluginInterface pluginInterface,
        IGameInteropProvider gameInteropProvider,
        IClientState currentClientState,
        IPlayerState currentPlayerState,
        ITargetManager currentTargetManager
    )
    {
#if DEBUG
        lock (StateLock)
        {
            if (OnRenderModelHook != null)
            {
                return;
            }

            try
            {
                ClientState = currentClientState;
                PlayerState = currentPlayerState;
                TargetManager = currentTargetManager;
                CreatingCharacterBase = pluginInterface.GetIpcSubscriber<nint, Guid, nint, nint, nint, object?>(CreatingCharacterBaseLabel);
                CreatedCharacterBase = pluginInterface.GetIpcSubscriber<nint, Guid, nint, object?>(CreatedCharacterBaseLabel);

                CreatingCharacterBase.Subscribe(OnCreatingCharacterBase);
                CreatedCharacterBase.Subscribe(OnCreatedCharacterBase);
                ClientState.TerritoryChanged += OnTerritoryChanged;

                var onRenderModelAddress = (nint)CharacterBase.StaticVirtualTablePointer->OnRenderModel;
                OnRenderModelHook = gameInteropProvider.HookFromAddress<OnRenderModelDelegate>(onRenderModelAddress, OnRenderModelDetour);
                OnRenderModelHook.Enable();

                DebugFileLog.Information(
                    "AdmissionTrace",
                    "Trace initialized: OnRenderModel=0x{Address:X}, Territory={Territory}, ClientLoggedIn={ClientLoggedIn}, PlayerLoaded={PlayerLoaded}",
                    onRenderModelAddress.ToInt64(),
                    ClientState.TerritoryType,
                    ClientState.IsLoggedIn,
                    PlayerState.IsLoaded
                );
            }
            catch (Exception exception)
            {
                Unsubscribe();
                DebugFileLog.Error("AdmissionTrace", exception, "Failed to initialize player admission trace");
            }
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void Update(CullingStatus cullingStatus)
    {
#if DEBUG
        LogLifecycleChanges(cullingStatus);
        var now = Stopwatch.GetTimestamp();
        foreach (var pair in TrackedPlayers)
        {
            var identity = pair.Key;
            var tracked = pair.Value;
            var gameObject = (GameObject*)identity.Address;
            if (gameObject == null || PlayerObjectIdentity.From(gameObject) != identity)
            {
                TrackedPlayers.TryRemove(identity, out _);
                continue;
            }

            var characterBase = (CharacterBase*)gameObject->DrawObject;
            var characterBaseAddress = (nint)characterBase;
            if (tracked.CharacterBase != characterBaseAddress)
            {
                var previous = tracked.CharacterBase;
                tracked.CharacterBase = characterBaseAddress;
                tracked.RenderQuietLogged = false;
                DebugFileLog.Debug(
                    "AdmissionTrace",
                    "CharacterBase association changed: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, Previous=0x{Previous:X}, Current=0x{Current:X}, RenderFlags=0x{RenderFlags:X}",
                    gameObject->ObjectIndex,
                    identity.Address.ToInt64(),
                    identity.EntityId,
                    previous.ToInt64(),
                    characterBaseAddress.ToInt64(),
                    (ulong)gameObject->RenderFlags
                );
            }

            if (characterBase == null)
            {
                continue;
            }

            var render = AssociateRenderState(characterBaseAddress, identity, gameObject->ObjectIndex);
            var lastCallbackAt = Volatile.Read(ref render.LastCallbackAt);
            if (
                !tracked.RenderQuietLogged
                && Volatile.Read(ref render.CallbackCount) != 0
                && lastCallbackAt != 0
                && now - lastCallbackAt >= RenderQuietThresholdTicks
            )
            {
                tracked.RenderQuietLogged = true;
                DebugFileLog.Debug(
                    "AdmissionTrace",
                    "OnRenderModel became quiet for 2s: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, CharacterBase=0x{CharacterBase:X}, CallbackTotal={CallbackTotal}, ModelsPresent={ModelsPresent}, LoadState={LoadState}, IsVisible={IsVisible}, RenderFlags=0x{RenderFlags:X}",
                    gameObject->ObjectIndex,
                    identity.Address.ToInt64(),
                    identity.EntityId,
                    characterBaseAddress.ToInt64(),
                    Volatile.Read(ref render.CallbackCount),
                    CountPresentModels(characterBase),
                    characterBase->LoadState,
                    characterBase->IsVisible,
                    (ulong)gameObject->RenderFlags
                );
            }
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void OnEnableDrawSuppressed(GameObject* gameObject)
    {
#if DEBUG
        if (!TryGetRemotePlayer(gameObject, out var identity, out var objectIndex))
        {
            return;
        }

        var tracked = TrackedPlayers.GetOrAdd(identity, _ => new TrackedPlayerState());
        if (Interlocked.CompareExchange(ref tracked.HoldLogged, 1, 0) != 0)
        {
            return;
        }

        tracked.HeldAt = Stopwatch.GetTimestamp();
        DebugFileLog.Debug(
            "AdmissionTrace",
            "EnableDraw held: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, Territory={Territory}, RenderFlags=0x{RenderFlags:X}, TargetableStatus=0x{TargetableStatus:X2}, DrawObject=0x{DrawObject:X}",
            objectIndex,
            identity.Address.ToInt64(),
            identity.EntityId,
            ClientState?.TerritoryType ?? 0,
            (ulong)gameObject->RenderFlags,
            (byte)gameObject->TargetableStatus,
            ((nint)gameObject->DrawObject).ToInt64()
        );
#endif
    }

    [Conditional("DEBUG")]
    public static void OnEnableDrawOriginalEntering(GameObject* gameObject)
    {
#if DEBUG
        if (!TryGetTrackedRemotePlayer(gameObject, out var identity, out var objectIndex, out var tracked))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref tracked.OriginalEntered, 1, 0) != 0)
        {
            return;
        }

        DebugFileLog.Debug(
            "AdmissionTrace",
            "EnableDraw released: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, HeldMs={HeldMs:F3}, RenderFlags=0x{RenderFlags:X}, TargetableStatus=0x{TargetableStatus:X2}, DrawObject=0x{DrawObject:X}",
            objectIndex,
            identity.Address.ToInt64(),
            identity.EntityId,
            Stopwatch.GetElapsedTime(tracked.HeldAt).TotalMilliseconds,
            (ulong)gameObject->RenderFlags,
            (byte)gameObject->TargetableStatus,
            ((nint)gameObject->DrawObject).ToInt64()
        );
#endif
    }

    [Conditional("DEBUG")]
    public static void OnEnableDrawOriginalReturned(GameObject* gameObject)
    {
#if DEBUG
        if (!TryGetTrackedRemotePlayer(gameObject, out var identity, out var objectIndex, out var tracked))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref tracked.OriginalReturned, 1, 0) != 0)
        {
            return;
        }

        DebugFileLog.Debug(
            "AdmissionTrace",
            "EnableDraw returned: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, CharacterBase=0x{CharacterBase:X}, RenderFlags=0x{RenderFlags:X}, TargetableStatus=0x{TargetableStatus:X2}",
            objectIndex,
            identity.Address.ToInt64(),
            identity.EntityId,
            ((nint)gameObject->DrawObject).ToInt64(),
            (ulong)gameObject->RenderFlags,
            (byte)gameObject->TargetableStatus
        );
#endif
    }

    [Conditional("DEBUG")]
    public static void DumpCurrentTarget()
    {
#if DEBUG
        var address = TargetManager?.Target?.Address ?? nint.Zero;
        var gameObject = (GameObject*)address;
        if (gameObject == null)
        {
            DebugFileLog.Information("AdmissionTrace", "Debug target snapshot: no current target");
            return;
        }

        if (gameObject->ObjectKind != ObjectKind.Pc)
        {
            DebugFileLog.Information(
                "AdmissionTrace",
                "Debug target snapshot skipped: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, ObjectKind={ObjectKind}",
                gameObject->ObjectIndex,
                address.ToInt64(),
                gameObject->ObjectKind
            );
            return;
        }

        var identity = PlayerObjectIdentity.From(gameObject);
        var tracked = TrackedPlayers.GetOrAdd(identity, _ => new TrackedPlayerState());
        var characterBase = (CharacterBase*)gameObject->DrawObject;
        var characterBaseAddress = (nint)characterBase;
        tracked.CharacterBase = characterBaseAddress;
        var render = characterBase == null ? null : AssociateRenderState(characterBaseAddress, identity, gameObject->ObjectIndex);
        DebugFileLog.Information(
            "AdmissionTrace",
            "Debug target snapshot: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, ObjectKind={ObjectKind}, CharacterBase=0x{CharacterBase:X}, CallbackTotal={CallbackTotal}, LastCallbackAgoMs={LastCallbackAgoMs:F3}, SlotCount={SlotCount}, Models={Models}, HasModelLoaded=0x{HasModelLoaded:X}, HasModelFilesLoaded=0x{HasModelFilesLoaded:X}, LoadState={LoadState}, IsVisible={IsVisible}, RenderFlags=0x{RenderFlags:X}, TargetableStatus=0x{TargetableStatus:X2}",
            gameObject->ObjectIndex,
            address.ToInt64(),
            identity.EntityId,
            gameObject->ObjectKind,
            characterBaseAddress.ToInt64(),
            render == null ? 0 : Volatile.Read(ref render.CallbackCount),
            GetLastCallbackAgoMilliseconds(render),
            characterBase == null ? 0 : characterBase->SlotCount,
            BuildModelSnapshot(characterBase),
            characterBase == null ? 0 : characterBase->HasModelInSlotLoaded,
            characterBase == null ? 0 : characterBase->HasModelFilesInSlotLoaded,
            characterBase == null ? 0 : characterBase->LoadState,
            characterBase != null && characterBase->IsVisible,
            (ulong)gameObject->RenderFlags,
            (byte)gameObject->TargetableStatus
        );
#endif
    }

    [Conditional("DEBUG")]
    public static void Close()
    {
#if DEBUG
        lock (StateLock)
        {
            Unsubscribe();
            TrackedPlayers.Clear();
            RenderStates.Clear();
            LastPlayerLoaded = null;
            LastCullingMode = null;
        }
#endif
    }

#if DEBUG
    private static void OnTerritoryChanged(uint territoryId) =>
        DebugFileLog.Information(
            "AdmissionTrace",
            "Territory changed: Territory={Territory}, ClientLoggedIn={ClientLoggedIn}, PlayerLoaded={PlayerLoaded}, TrackedPlayers={TrackedPlayers}, RenderStates={RenderStates}",
            territoryId,
            ClientState?.IsLoggedIn ?? false,
            PlayerState?.IsLoaded ?? false,
            TrackedPlayers.Count,
            RenderStates.Count
        );

    private static void LogLifecycleChanges(CullingStatus cullingStatus)
    {
        var loaded = PlayerState?.IsLoaded ?? false;
        if (LastPlayerLoaded != loaded)
        {
            LastPlayerLoaded = loaded;
            DebugFileLog.Information(
                "AdmissionTrace",
                "Player load state changed: PlayerLoaded={PlayerLoaded}, ClientLoggedIn={ClientLoggedIn}, Territory={Territory}",
                loaded,
                ClientState?.IsLoggedIn ?? false,
                ClientState?.TerritoryType ?? 0
            );
        }

        var mode = GetCullingMode(cullingStatus, loaded);
        if (LastCullingMode == mode)
        {
            return;
        }

        var previous = LastCullingMode ?? "Uninitialized";
        LastCullingMode = mode;
        DebugFileLog.Information(
            "AdmissionTrace",
            "Culling mode changed: Previous={Previous}, Current={Current}, OtherPlayerCount={OtherPlayerCount}, Territory={Territory}",
            previous,
            mode,
            cullingStatus.OtherPlayerCount,
            ClientState?.TerritoryType ?? 0
        );
    }

    private static string GetCullingMode(CullingStatus status, bool loaded)
    {
        if (!status.Enabled)
        {
            return "Disabled";
        }

        if (!loaded)
        {
            return "PlayerUnavailable";
        }

        if (status.SuspendedByTemporaryReveal)
        {
            return "SuspendedTemporaryReveal";
        }

        if (status.SuspendedInDuty)
        {
            return "SuspendedDuty";
        }

        if (status.SuspendedByLowPlayerCount)
        {
            return "SuspendedLowPlayerCount";
        }

        return "Active";
    }

    private static void OnCreatingCharacterBase(nint address, Guid collectionId, nint modelId, nint customize, nint equipment)
    {
        if (!TryGetTrackedRemotePlayer((GameObject*)address, out var identity, out var objectIndex, out _))
        {
            return;
        }

        var modelCharaId = modelId == nint.Zero ? 0 : *(ushort*)modelId;
        DebugFileLog.Debug(
            "AdmissionTrace",
            "Penumbra CreatingCharacterBase: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, CollectionId={CollectionId}, ModelCharaId={ModelCharaId}, CurrentDrawObject=0x{DrawObject:X}",
            objectIndex,
            identity.Address.ToInt64(),
            identity.EntityId,
            collectionId,
            modelCharaId,
            ((nint)((GameObject*)address)->DrawObject).ToInt64()
        );
    }

    private static void OnCreatedCharacterBase(nint address, Guid collectionId, nint characterBase)
    {
        if (!TryGetTrackedRemotePlayer((GameObject*)address, out var identity, out var objectIndex, out _))
        {
            return;
        }

        if (characterBase != nint.Zero)
        {
            AssociateRenderState(characterBase, identity, objectIndex);
        }

        DebugFileLog.Debug(
            "AdmissionTrace",
            "Penumbra CreatedCharacterBase: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, CollectionId={CollectionId}, CharacterBase=0x{CharacterBase:X}, GameObjectDrawObject=0x{DrawObject:X}",
            objectIndex,
            identity.Address.ToInt64(),
            identity.EntityId,
            collectionId,
            characterBase.ToInt64(),
            ((nint)((GameObject*)address)->DrawObject).ToInt64()
        );
    }

    private static void OnRenderModelDetour(CharacterBase* characterBase, Model* model)
    {
        try
        {
            RenderModelObserved?.Invoke((nint)characterBase, (nint)model);
            if (characterBase != null && RenderStates.TryGetValue((nint)characterBase, out var state))
            {
                var total = Interlocked.Increment(ref state.CallbackCount);
                Volatile.Write(ref state.LastCallbackAt, Stopwatch.GetTimestamp());
                if (total == 1)
                {
                    DebugFileLog.Debug(
                        "AdmissionTrace",
                        "CharacterBase first OnRenderModel: ObjectIndex={ObjectIndex}, Address=0x{Address:X}, EntityId=0x{EntityId:X}, CharacterBase=0x{CharacterBase:X}, Model=0x{Model:X}, Slot={Slot}",
                        state.ObjectIndex,
                        state.Identity.Address.ToInt64(),
                        state.Identity.EntityId,
                        ((nint)characterBase).ToInt64(),
                        ((nint)model).ToInt64(),
                        FindModelSlot(characterBase, model)
                    );
                }
            }
        }
        catch (Exception exception)
        {
            DebugFileLog.Error("AdmissionTrace", exception, "CharacterBase.OnRenderModel trace failed");
        }
        finally
        {
            OnRenderModelHook!.Original(characterBase, model);
        }
    }

    private static RenderState AssociateRenderState(nint characterBase, PlayerObjectIdentity identity, int objectIndex) =>
        RenderStates.AddOrUpdate(
            characterBase,
            _ => new RenderState(identity, objectIndex),
            (_, current) => current.Identity == identity ? current : new RenderState(identity, objectIndex)
        );

    private static bool TryGetTrackedRemotePlayer(
        GameObject* gameObject,
        out PlayerObjectIdentity identity,
        out int objectIndex,
        out TrackedPlayerState tracked
    )
    {
        tracked = null!;
        if (!TryGetRemotePlayer(gameObject, out identity, out objectIndex))
        {
            return false;
        }

        if (!TrackedPlayers.TryGetValue(identity, out var current))
        {
            return false;
        }

        tracked = current;
        return true;
    }

    private static bool TryGetRemotePlayer(GameObject* gameObject, out PlayerObjectIdentity identity, out int objectIndex)
    {
        identity = default;
        objectIndex = gameObject == null ? -1 : gameObject->ObjectIndex;
        if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc || !CharacterObjectSlots.IsEvenSlot(objectIndex))
        {
            return false;
        }

        identity = PlayerObjectIdentity.From(gameObject);
        return true;
    }

    private static int FindModelSlot(CharacterBase* characterBase, Model* model)
    {
        if (characterBase->Models == null)
        {
            return -1;
        }

        for (var slot = 0; slot < characterBase->SlotCount; slot++)
        {
            if (characterBase->Models[slot] == model)
            {
                return slot;
            }
        }

        return -1;
    }

    private static int CountPresentModels(CharacterBase* characterBase)
    {
        if (characterBase == null || characterBase->Models == null)
        {
            return 0;
        }

        var count = 0;
        for (var slot = 0; slot < characterBase->SlotCount; slot++)
        {
            if (characterBase->Models[slot] != null)
            {
                count++;
            }
        }

        return count;
    }

    private static string BuildModelSnapshot(CharacterBase* characterBase)
    {
        if (characterBase == null || characterBase->Models == null)
        {
            return "none";
        }

        var result = new System.Text.StringBuilder();
        for (var slot = 0; slot < characterBase->SlotCount; slot++)
        {
            var model = characterBase->Models[slot];
            if (model == null)
            {
                continue;
            }

            if (result.Length != 0)
            {
                result.Append(',');
            }

            result.Append(slot).Append(":0x").Append(((nint)model).ToString("X"));
        }

        return result.Length == 0 ? "none" : result.ToString();
    }

    private static double GetLastCallbackAgoMilliseconds(RenderState? render)
    {
        var timestamp = render == null ? 0 : Volatile.Read(ref render.LastCallbackAt);
        return timestamp == 0 ? -1 : Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    }

    private static void Unsubscribe()
    {
        if (ClientState != null)
        {
            ClientState.TerritoryChanged -= OnTerritoryChanged;
        }

        CreatingCharacterBase?.Unsubscribe(OnCreatingCharacterBase);
        CreatedCharacterBase?.Unsubscribe(OnCreatedCharacterBase);
        OnRenderModelHook?.Dispose();
        ClientState = null;
        PlayerState = null;
        TargetManager = null;
        CreatingCharacterBase = null;
        CreatedCharacterBase = null;
        OnRenderModelHook = null;
        RenderModelObserved = null;
    }

    private sealed class TrackedPlayerState
    {
        public long HeldAt;
        public int HoldLogged;
        public int OriginalEntered;
        public int OriginalReturned;
        public nint CharacterBase;
        public bool RenderQuietLogged;
    }

    private sealed class RenderState(PlayerObjectIdentity identity, int objectIndex)
    {
        public readonly PlayerObjectIdentity Identity = identity;
        public readonly int ObjectIndex = objectIndex;
        public long CallbackCount;
        public long LastCallbackAt;
    }
#endif
}
