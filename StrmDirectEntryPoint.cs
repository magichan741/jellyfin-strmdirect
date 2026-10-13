using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmDirect;

/// <summary>
/// 服务器托管服务：Jellyfin（ASP.NET Core Host）启动时自动启动内置代理，停止时自动回收。
/// </summary>
public class StrmDirectEntryPoint : IHostedService
{
    private readonly ILogger<StrmDirectEntryPoint> _logger;
    private readonly ILibraryManager _libraryManager;
    private ProxyServer? _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmDirectEntryPoint"/> class.
    /// </summary>
    public StrmDirectEntryPoint(ILogger<StrmDirectEntryPoint> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            _logger.LogInformation("[StrmDirect] 插件未启用，内置代理跳过");
            return Task.CompletedTask;
        }

        try
        {
            _server = new ProxyServer(config, _logger, _libraryManager);
            _server.Start();
            _logger.LogInformation("[StrmDirect] 内置代理已随服务器启动，监听 0.0.0.0:{Port}", config.ListenPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StrmDirect] 内置代理启动失败（端口 {Port} 可能被占用，请先停掉外部 8098 反代）", config.ListenPort);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _server?.Stop();
            _server?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 回收代理异常");
        }

        _server = null;
        return Task.CompletedTask;
    }
}
