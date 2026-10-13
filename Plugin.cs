using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StrmDirect;

/// <summary>
/// StrmDirect 插件：随服务器运行的移动 CDN strm 影片直连通道（master 200 + 分片 302）。
/// 手机 app 连接本插件端口即可：strm 影片秒播（分片 302 直连移动 CDN），
/// 浏览/刮削/详情/控制透明转发 Jellyfin，普通影片走原生流程。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("9e2a7c41-6b8f-4d3e-9a1c-2f5b8d7e4a60");

    /// <inheritdoc />
    public override string Name => "StrmDirect";

    /// <inheritdoc />
    public override string Description => "随服务器运行的 strm 秒播通道：master 清单 200 + 分片 302 直连移动 CDN，服务器零流量转发，普通影片走原生流程。";

    /// <summary>
    /// 提供中文配置页（Jellyfin 插件设置页显示该页面而非通用英文编辑器）。
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "StrmDirect",
                DisplayName = "StrmDirect 设置",
                EmbeddedResourcePath = GetType().Namespace + ".Config.configpage.html",
                EnableInMainMenu = false,
                MenuSection = "server",
            },
        };
    }
}
