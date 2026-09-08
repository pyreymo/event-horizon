using System;
using EventHorizon.Localization;

namespace EventHorizon.Features;

internal interface IFeature : IDisposable
{
    void Enable(FeatureScope scope);
    void Disable();
    void DrawSettings();
}

internal abstract class Feature<TSettings>(TSettings settings, Action save) : IFeature
    where TSettings : class
{
    protected TSettings Settings { get; } = settings;

    protected void Save() => save();

    public abstract void Enable(FeatureScope scope);

    public virtual void Disable() { }

    public abstract void DrawSettings();

    public virtual void Dispose() { }
}

internal enum FeatureState
{
    Disabled,
    Enabling,
    Enabled,
    Disabling,
    Faulted,
    Disposed,
}

internal sealed class FeatureRegistration(string id, string titleKey, bool defaultEnabled, Func<IFeature> create)
{
    public string Id { get; } = id;
    public string Title => Loc.Text(titleKey);
    public string Description => Loc.Text(titleKey + ".Description");
    public bool DefaultEnabled { get; } = defaultEnabled;
    public Func<IFeature> Create { get; } = create;
    public IFeature? Instance { get; set; }
    public FeatureScope? Scope { get; set; }
    public FeatureState State { get; set; }
    public Exception? Error { get; set; }
    public bool? PendingEnabled { get; set; }
}
