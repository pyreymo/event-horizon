using System;

namespace EventHorizon.Features;

internal interface IFeature : IDisposable
{
    void Enable(FeatureScope scope);
    void Disable();
    void DrawSettings();
}

internal interface IFeatureSettings
{
    int Version { get; set; }
}

internal interface IFeatureDefinition
{
    string Id { get; }
    string TitleKey { get; }
    bool GetDefaultEnabled(FeatureConfigStore store);
    IFeature Create(FeatureConfigStore store);
}

internal abstract class Feature<TSettings>(TSettings settings, Action save) : IFeature
    where TSettings : class, IFeatureSettings
{
    protected TSettings Settings { get; } = settings;

    protected void Save() => save();

    public abstract void Enable(FeatureScope scope);

    public virtual void Disable() { }

    public abstract void DrawSettings();

    public virtual void Dispose() { }
}

internal sealed class FeatureDefinition<TSettings>(
    string id,
    string titleKey,
    Func<FeatureConfigStore, bool> getDefaultEnabled,
    Func<TSettings, Action, IFeature> create
) : IFeatureDefinition
    where TSettings : class, IFeatureSettings, new()
{
    public string Id { get; } = id;
    public string TitleKey { get; } = titleKey;

    public bool GetDefaultEnabled(FeatureConfigStore store) => getDefaultEnabled(store);

    public IFeature Create(FeatureConfigStore store)
    {
        var settings = store.Load<TSettings>(Id);
        store.Save(Id, settings);
        return create(settings, () => store.Save(Id, settings));
    }
}
