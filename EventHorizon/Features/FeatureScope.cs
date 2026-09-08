using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace EventHorizon.Features;

// One scope per activation. Callbacks become inert before teardown starts.
internal sealed class FeatureScope(IFramework framework, IDalamudPluginInterface pluginInterface, Action<Exception> fault) : IDisposable
{
    private readonly List<Action> cleanup = [];
    private bool active = true;

    public T Own<T>(T resource)
        where T : IDisposable
    {
        if (!active)
        {
            resource.Dispose();
            throw new ObjectDisposedException(nameof(FeatureScope));
        }
        Defer(resource.Dispose);
        return resource;
    }

    public void Defer(Action release)
    {
        if (!active)
            throw new ObjectDisposedException(nameof(FeatureScope));
        cleanup.Add(release);
    }

    public void Run(Action action)
    {
        if (!active)
            return;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            active = false;
            fault(exception);
        }
    }

    public void OnUpdate(Action callback)
    {
        void Handler(IFramework _) => Run(callback);
        Defer(() => framework.Update -= Handler);
        framework.Update += Handler;
    }

    public void OnDraw(Action callback)
    {
        void Handler() => Run(callback);
        Defer(() => pluginInterface.UiBuilder.Draw -= Handler);
        pluginInterface.UiBuilder.Draw += Handler;
    }

    public void Deactivate() => active = false;

    public void Dispose()
    {
        active = false;
        List<Exception> errors = [];
        for (var i = cleanup.Count - 1; i >= 0; i--)
        {
            try
            {
                cleanup[i]();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
        cleanup.Clear();
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }
}
