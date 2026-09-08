using System;
using Dalamud.Plugin.Services;
using EventHorizon.Application;

namespace EventHorizon.Features.Dtr;

internal sealed class DtrFeature(
    DtrSettings settings,
    Action save,
    IDtrBar dtrBar,
    ICullingReader reader,
    ICullingCommands commands,
    Action openSettings
) : Feature<DtrSettings>(settings, save)
{
    private DtrBar? bar;

    public override void Enable(FeatureScope scope)
    {
        bar = scope.Own(new DtrBar(dtrBar, Settings, reader.GetStatus, commands.SetEnabled, openSettings, () => commands.Enabled, scope));
        bar.RefreshNow();
        scope.OnUpdate(bar.Update);
    }

    public override void Disable() => bar = null;

    public override void DrawSettings()
    {
        if (FeatureUi.Checkbox("ShowFrameRateInDtrBar", Settings.ShowFrameRateInDtrBar, value => Settings.ShowFrameRateInDtrBar = value))
            Save();
    }
}
