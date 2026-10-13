using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StrmDirect;

/// <summary>
/// 插件配置：随服务器运行的移动 CDN strm 直连代理。
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// 是否启用内置代理。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 代理监听端口（手机 app 服务器地址 = http://TailscaleIP:ListenPort）。
    /// </summary>
    public int ListenPort { get; set; } = 8098;

    /// <summary>
    /// Jellyfin 服务端地址（转发目标）。
    /// </summary>
    public string JfUrl { get; set; } = "http://localhost:8096";

    /// <summary>
    /// Jellyfin API Token。
    /// </summary>
    public string Token { get; set; } = "YOUR_API_TOKEN";

    /// <summary>
    /// 用户 Id（PlaybackInfo 缺省注入用）。
    /// </summary>
    public string UserId { get; set; } = "YOUR_USER_ID";

    /// <summary>
    /// strm 识别标记（strm 内容包含此子串视为移动 CDN strm）。
    /// </summary>
    public string CdnMarker { get; set; } = "chinamobile";

    /// <summary>
    /// 探测劫持：Lavf/ffprobe/ffmpeg 请求返回假 HLS 清单（避免服务器端探测拖慢起播）。
    /// </summary>
    public bool ProbeHijack { get; set; } = true;

    /// <summary>
    /// 图片缓存（减少 Tailscale 往返，24h TTL）。
    /// </summary>
    public bool ImageCache { get; set; } = true;

    /// <summary>
    /// strm 原值备份映射（strm 内容被改写为反代前缀时回退查原 CDN URL）。
    /// </summary>
    public string BackupJsonPath { get; set; } = @"./strm_backup.json";

    /// <summary>
    /// 探测假分片文件（本地 ts，供 ffprobe 秒解析）。
    /// </summary>
    public string DummyTsPath { get; set; } = @"./probe_dummy.ts";

    /// <summary>
    /// 反代前缀（strm 内容若以此开头视为已被改写，回退备份映射）。
    /// </summary>
    public string ProxyPrefix { get; set; } = "http://YOUR_TAILSCALE_IP:8098/videos/bypath/";
}
