using Dalamud.Plugin.Services;

namespace PictomancyDemo;

internal static class DemoPlugin
{
    public static IObjectTable Objects => EventHorizon.Plugin.ObjectTable;
    public static ITargetManager TargetManager => EventHorizon.Plugin.TargetManager;
    public static IDataManager DataManager => EventHorizon.Plugin.DataManager;
    public static ITextureProvider TextureProvider => EventHorizon.Plugin.TextureProvider;
}
