using System.Linq;
using System.Threading;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilityAppliedState
{
    private PlayerVisibilityFrameState? activeFrame;

    public PlayerVisibilityFrameState? ActiveFrame => Volatile.Read(ref activeFrame);
    public PlayerVisibilityTargetSet? ActiveTarget => ActiveFrame?.ActiveTarget;

    public void Publish(PlayerVisibilityFrameState frame) => Volatile.Write(ref activeFrame, frame);

    public bool IsExplicitlyVisible(PlayerObjectIdentity identity, int objectIndex)
    {
        var snapshot = ActiveFrame;
        return snapshot != null && snapshot.VisibleSlots.Contains((identity, objectIndex));
    }

    public bool IsExplicitlyVisible(PlayerObjectIdentity identity) =>
        ActiveFrame?.VisibleSlots.Any(key => key.Identity == identity) == true;

    public void Clear() => Volatile.Write(ref activeFrame, null);
}
