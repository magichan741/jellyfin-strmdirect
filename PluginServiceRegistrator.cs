using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StrmDirect;

/// <summary>
/// Registers plugin services into the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost appHost)
    {
        // 内置代理注册为 HostedService：随 Jellyfin Host 启动/停止自动运行。
        services.AddHostedService<StrmDirectEntryPoint>();
    }
}
