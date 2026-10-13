using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmDirect;

/// <summary>
/// 内置 strm 直连代理（移植自验证成功的 strm302_proxy302.py）：
/// 单端口全包 —— 视频流请求 master 200（分片重写为 /hls-seg/）+ 分片 302 直连移动 CDN；
/// 其余请求（浏览/详情/图片/控制/普通影片流）透明转发 Jellyfin，普通影片完全走原生流程。
/// </summary>
public sealed class ProxyServer : IDisposable
{
    private static readonly Regex StreamRe = new(
        @"^/videos/([^/]+)/(?:stream|master\.m3u8|main\.m3u8)", RegexOptions.IgnoreCase);

    private static readonly Regex BypathRe = new(
        @"^/videos/bypath/([^/]+)/master\.m3u8", RegexOptions.IgnoreCase);

    private static readonly Regex HlsSegRe = new(@"^/hls-seg/([^/]+)/(.+)$");

    private static readonly Regex ImageRe = new(
        @"^/(Items/[^/]+/Images|Images|emby/Items/[^/]+/Images)", RegexOptions.IgnoreCase);

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
    private const int ImgCacheMax = 800;
    private const int ImgTtlSec = 86400;

    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly PluginConfiguration _config;
    private readonly string _jfHost;
    private readonly int _jfPort;

    private readonly HttpClient _proxyHttp;   // 透明转发 8096（不跟随 302、不走代理）
    private readonly HttpClient _fetchHttp;   // 拉 CDN（跟随 302、不走代理）

    private readonly ConcurrentDictionary<string, CdnInfo> _itemCdn = new();
    private readonly ConcurrentDictionary<string, ItemInfo> _itemCache = new();
    private readonly ConcurrentDictionary<string, ImgEntry> _imgCache = new();
    private readonly Dictionary<string, string> _strmCdnMap = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyServer"/> class.
    /// </summary>
    public ProxyServer(PluginConfiguration config, ILogger logger, ILibraryManager libraryManager)
    {
        _config = config;
        _logger = logger;
        _libraryManager = libraryManager;
        var u = new Uri(config.JfUrl);
        _jfHost = u.Host;
        _jfPort = u.Port > 0 ? u.Port : (u.Scheme == "https" ? 443 : 80);

        var ph = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        _proxyHttp = new HttpClient(ph) { Timeout = TimeSpan.FromSeconds(60) };

        var fh = new HttpClientHandler { AllowAutoRedirect = true, UseProxy = false, MaxAutomaticRedirections = 5 };
        _fetchHttp = new HttpClient(fh) { Timeout = TimeSpan.FromSeconds(20) };
        _fetchHttp.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        LoadBackupMap();
    }

    private void LoadBackupMap()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.BackupJsonPath) || !File.Exists(_config.BackupJsonPath))
            {
                return;
            }

            var json = File.ReadAllText(_config.BackupJsonPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var v = prop.Value.GetString()?.Trim().TrimStart('\uFEFF');
                    if (!string.IsNullOrEmpty(v))
                    {
                        _strmCdnMap[prop.Name] = v;
                    }
                }
            }

            _logger.LogInformation("[StrmDirect] 备份映射加载 {Count} 条", _strmCdnMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StrmDirect] 加载备份映射失败");
        }
    }

    /// <summary>
    /// 启动监听（随服务器入口点 Run 调用）。
    /// </summary>
    public void Start()
    {
        if (_listener != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _config.ListenPort);
        _listener.Start(256);
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _logger.LogInformation("[StrmDirect] 监听 0.0.0.0:{Port}", _config.ListenPort);
    }

    /// <summary>
    /// 停止监听。
    /// </summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 停止监听异常");
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts!.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmDirect] Accept 异常");
                await Task.Delay(100);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    // ============ HTTP 处理 ============

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                stream.ReadTimeout = 30000;

                // 首个字节：TLS ClientHello -> 400（与 Jellyfin/Kestrel 一致，纯 HTTP）
                var first = new byte[1];
                var got = await ReadSomeAsync(stream, first, 0, 1);
                if (got == 0)
                {
                    return;
                }

                if (first[0] == 0x16)
                {
                    await WriteHeadAsync(stream, 400, "Bad Request", null, 0);
                    return;
                }

                var reader = new ConnReader(stream);
                reader.PushBack(first, 0, 1);

                while (!_cts!.IsCancellationRequested)
                {
                    var headBytes = await ReadHeadBytesAsync(reader);
                    if (headBytes == null)
                    {
                        break;
                    }

                    var (method, rawTarget, version, headers) = ParseHead(headBytes);
                    var body = await ReadBodyAsync(reader, headers);

                    var handled = await RouteAsync(stream, method, rawTarget, version, headers, body);
                    if (version == "HTTP/1.0")
                    {
                        break;
                    }

                    // keep-alive：继续下一条请求（除流式响应关闭外）
                    if (handled == RouteResult.CloseConnection)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmDirect] 连接处理异常");
            }
        }
    }

    private static async Task<int> ReadSomeAsync(Stream s, byte[] buf, int off, int cnt)
    {
        return await s.ReadAsync(buf.AsMemory(off, cnt)).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadHeadBytesAsync(ConnReader reader)
    {
        var list = new List<byte>(8192);
        var tmp = new byte[4096];
        while (true)
        {
            var n = await reader.ReadAsync(tmp, 0, tmp.Length);
            if (n <= 0)
            {
                return list.Count == 0 ? null : throw new InvalidDataException("unexpected eof in head");
            }

            list.AddRange(tmp.AsSpan(0, n).ToArray());
            if (FindCrlfCrlf(list) >= 0)
            {
                return list.ToArray();
            }

            if (list.Count > 65536)
            {
                throw new InvalidDataException("head too large");
            }
        }
    }

    private static int FindCrlfCrlf(List<byte> data)
    {
        for (var i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
            {
                return i;
            }
        }

        return -1;
    }

    private (string Method, string RawTarget, string Version, Dictionary<string, string> Headers) ParseHead(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var method = requestLine[0];
        var target = requestLine.Length > 1 ? requestLine[1] : "/";
        var version = requestLine.Length > 2 ? requestLine[2] : "HTTP/1.1";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        return (method, target, version, headers);
    }

    private async Task<byte[]> ReadBodyAsync(ConnReader reader, Dictionary<string, string> headers)
    {
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0)
        {
            var body = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = await reader.ReadAsync(body, read, len - read);
                if (n <= 0)
                {
                    throw new InvalidDataException("eof in body");
                }

                read += n;
            }

            return body;
        }

        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = await reader.ReadLineAsync();
                if (sizeLine == null)
                {
                    break;
                }

                var sizeText = sizeLine.Trim().Split(';')[0];
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
                {
                    break;
                }

                if (size == 0)
                {
                    await reader.ReadLineAsync(); // trailing CRLF
                    break;
                }

                var chunk = new byte[size];
                var read = 0;
                while (read < size)
                {
                    var n = await reader.ReadAsync(chunk, read, size - read);
                    if (n <= 0)
                    {
                        throw new InvalidDataException("eof in chunk");
                    }

                    read += n;
                }

                ms.Write(chunk, 0, size);
                await reader.ReadLineAsync(); // chunk CRLF
            }

            return ms.ToArray();
        }

        return Array.Empty<byte>();
    }

    // ============ 路由 ============

    private enum RouteResult { Continue, CloseConnection }

    private async Task<RouteResult> RouteAsync(
        NetworkStream stream,
        string method,
        string rawTarget,
        string version,
        Dictionary<string, string> headers,
        byte[] body)
    {
        // ===== 探测劫持（必须在 URI 解析之前）：Jellyfin 的 ffprobe 经 http_proxy 走本端口探测时，
        // 请求行是代理绝对 URL（如 GET http://cdn.example.com/.../index.m3u8），无法用 stash 拼接解析。
        // 对 Lavf/ffprobe/ffmpeg UA 一律返回本地假 HLS（秒回），避免服务端探测拖慢 PlaybackInfo。
        var ua = headers.TryGetValue("User-Agent", out var uav) ? uav : string.Empty;
        var uaL = ua.ToLower(CultureInfo.InvariantCulture);
        var isProbe = uaL.Contains("lavf") || uaL.Contains("ffprobe") || uaL.Contains("ffmpeg");
        if (_config.ProbeHijack && isProbe)
        {
            string pp = rawTarget;
            if (pp.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || pp.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    pp = new Uri(pp).AbsolutePath;
                }
                catch (Exception)
                {
                    pp = rawTarget;
                }
            }

            if (pp == "/__probe_dummy.ts")
            {
                await RespondDummyTsAsync(stream);
            }
            else
            {
                _logger.LogDebug("[StrmDirect] 探测劫持: {Ua} -> 假清单", ua[..Math.Min(ua.Length, 60)]);
                await RespondFakeMasterAsync(stream);
            }

            return RouteResult.Continue;
        }

        string path;
        string query;
        try
        {
            var uri = new Uri("http://stash" + rawTarget);
            path = Uri.UnescapeDataString(uri.AbsolutePath);
            query = uri.Query; // 含 '?'
        }
        catch (Exception)
        {
            await WriteHeadAsync(stream, 400, "Bad Request", null, 0);
            return RouteResult.Continue;
        }

        var upgrade = headers.TryGetValue("Upgrade", out var up) ? up : string.Empty;
        if (upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            await WsProxyAsync(stream, method, rawTarget, headers);
            return RouteResult.Continue;
        }

        if (path == "/__health")
        {
            await RespondAsync(stream, 200, "OK", new Dictionary<string, string> { { "Content-Type", "text/plain" } }, Encoding.UTF8.GetBytes("ok"));
            return RouteResult.Continue;
        }

        var mb = BypathRe.Match(path);
        if (mb.Success)
        {
            var b64 = mb.Groups[1].Value;
            string spath;
            try
            {
                spath = DecodeB64(b64);
            }
            catch (Exception)
            {
                await WriteHeadAsync(stream, 400, "Bad Request", null, 0);
                return RouteResult.Continue;
            }

            if (isProbe)
            {
                await RespondFakeMasterAsync(stream);
                return RouteResult.Continue;
            }

            var url = ReadStrmByPath(spath);
            if (url != null && IsHttpUrl(url))
            {
                var key = "bypath:" + b64;
                if (await ServeMasterAsync(stream, key, url, headers))
                {
                    return RouteResult.Continue;
                }
            }

            await WriteHeadAsync(stream, 404, "Not Found", null, 0);
            return RouteResult.Continue;
        }

        var m3 = HlsSegRe.Match(path);
        if (m3.Success)
        {
            var itemKey = m3.Groups[1].Value;
            var seg = m3.Groups[2].Value;
            if (Seg302(stream, itemKey, seg, query))
            {
                return RouteResult.Continue;
            }
        }

        var m = StreamRe.Match(path);
        if (m.Success)
        {
            var itemId = m.Groups[1].Value;
            var url = await ResolveStrmUrlAsync(itemId);
            if (url != null)
            {
                if (await ServeMasterAsync(stream, itemId, url, headers))
                {
                    return RouteResult.Continue;
                }
            }
        }

        return await ProxyAsync(stream, method, rawTarget, headers, body) ? RouteResult.Continue : RouteResult.CloseConnection;
    }

    // ============ strm 解析 ============

    private static bool IsHttpUrl(string s)
    {
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeB64(string b64)
    {
        var padded = b64 + new string('=', (4 - (b64.Length % 4)) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
    }

    private string? ReadStrmByPath(string path)
    {
        try
        {
            var content = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (content.StartsWith(_config.ProxyPrefix, StringComparison.Ordinal))
            {
                if (_strmCdnMap.TryGetValue(path, out var cdn))
                {
                    return cdn;
                }

                _logger.LogWarning("[StrmDirect] bypath 循环防护: {Path} 内容已是反代且无备份", path);
                return null;
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 读取 strm 失败: {Path}", path);
            return null;
        }
    }

    private async Task<string?> ResolveStrmUrlAsync(string itemId)
    {
        var info = await GetItemInfoAsync(itemId);
        var mu = info.MediaUrl ?? string.Empty;
        if (mu.StartsWith(_config.ProxyPrefix, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(info.Path) && _strmCdnMap.TryGetValue(info.Path, out var cdn))
            {
                return cdn;
            }
        }

        if (IsHttpUrl(mu))
        {
            return mu;
        }

        var path = info.Path ?? string.Empty;
        if (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return ReadStrmByPath(path);
        }

        return null;
    }

    private async Task<ItemInfo> GetItemInfoAsync(string itemId)
    {
        if (_itemCache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var info = new ItemInfo();
        try
        {
            var url = $"{_config.JfUrl}/Users/{_config.UserId}/Items/{itemId}?Fields=Path,MediaSources";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{_config.Token}\"");
            using var resp = await _proxyHttp.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    info.Path = p.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("MediaSources", out var ms) && ms.ValueKind == JsonValueKind.Array
                    && ms.GetArrayLength() > 0 && ms[0].TryGetProperty("Path", out var mp)
                    && mp.ValueKind == JsonValueKind.String)
                {
                    info.MediaUrl = mp.GetString() ?? string.Empty;
                }

                if (info.Path.Length > 0 || info.MediaUrl.Length > 0)
                {
                    _itemCache[itemId] = info;
                    return info;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] item API 失败: {Id}", itemId);
        }

        // fallback：进程内直接取条目
        try
        {
            var item = _libraryManager.GetItemById(Guid.Parse(itemId));
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                info.Path = item.Path;
                if (info.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    info.MediaUrl = ReadStrmByPath(info.Path) ?? string.Empty;
                }
                else
                {
                    info.MediaUrl = info.Path;
                }

                _itemCache[itemId] = info;
                return info;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 进程内取条目失败: {Id}", itemId);
        }

        return info;
    }

    // ============ master 200 + 分片重写 ============

    private async Task<bool> ServeMasterAsync(NetworkStream stream, string itemKey, string url, Dictionary<string, string> reqHeaders)
    {
        string data;
        try
        {
            data = await FetchTextAsync(url);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] master 拉取失败 {Url}", url[..Math.Min(url.Length, 80)]);
            return false;
        }

        var base0 = url[..(url.LastIndexOf('/') + 1)];
        string? sub = null;
        foreach (var line in data.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length > 0 && !l.StartsWith("#"))
            {
                sub = l;
                break;
            }
        }

        if (sub == null)
        {
            _logger.LogDebug("[StrmDirect] master 无子清单变体: {Url}", url[..Math.Min(url.Length, 80)]);
            return false;
        }

        var subUrl = sub.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? sub : base0 + sub;
        string subData;
        string finalUrl;
        try
        {
            (subData, finalUrl) = await FetchTextWithFinalAsync(subUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 子清单拉取失败 {Url}", subUrl[..Math.Min(subUrl.Length, 80)]);
            return false;
        }

        var pu = new Uri(finalUrl);
        _itemCdn[itemKey] = new CdnInfo
        {
            Base = base0,
            Query = pu.Query.TrimStart('?'),
        };

        var host = reqHeaders.TryGetValue("Host", out var h) && !string.IsNullOrEmpty(h)
            ? h
            : $"localhost:{_config.ListenPort}";

        var sb = new StringBuilder();
        foreach (var line in subData.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0 || l.StartsWith("#"))
            {
                sb.Append(line).Append('\n');
                continue;
            }

            if (IsHttpUrl(l))
            {
                var pu2 = new Uri(l);
                var uri2 = pu2.AbsolutePath.TrimStart('/');
                if (!string.IsNullOrEmpty(pu2.Query))
                {
                    sb.Append("http://").Append(host).Append("/hls-seg/").Append(itemKey).Append('/').Append(uri2).Append(pu2.Query).Append('\n');
                }
                else
                {
                    sb.Append("http://").Append(host).Append("/hls-seg/").Append(itemKey).Append('/').Append(uri2).Append('\n');
                }

                continue;
            }

            var uri = l.Split(',')[0];
            sb.Append("http://").Append(host).Append("/hls-seg/").Append(itemKey).Append('/').Append(uri).Append('\n');
        }

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        await RespondAsync(stream, 200, "OK", new Dictionary<string, string>
        {
            { "Content-Type", "application/vnd.apple.mpegurl" },
            { "Access-Control-Allow-Origin", "*" },
            { "Cache-Control", "no-store" },
        }, body);
        _logger.LogInformation("[StrmDirect] master 200: {Key} | CDN {Base}", itemKey, _itemCdn[itemKey].Base[..Math.Min(_itemCdn[itemKey].Base.Length, 60)]);
        return true;
    }

    private bool Seg302(NetworkStream stream, string itemKey, string seg, string segQuery)
    {
        if (!_itemCdn.TryGetValue(itemKey, out var info))
        {
            return false;
        }

        var target = info.Base + seg;
        var ps = new List<string>();
        if (!string.IsNullOrEmpty(segQuery))
        {
            ps.Add(segQuery.TrimStart('?'));
        }

        if (!string.IsNullOrEmpty(info.Query))
        {
            ps.Add(info.Query);
        }

        if (ps.Count > 0)
        {
            target += "?" + string.Join("&", ps);
        }

        // 302 响应
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 302 Found\r\n");
        sb.Append("Location: ").Append(target).Append("\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Content-Length: 0\r\n\r\n");
        try
        {
            var bytes = Encoding.Latin1.GetBytes(sb.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            _logger.LogInformation("[StrmDirect] 分片 302: {Seg} -> {Target}", seg[..Math.Min(seg.Length, 40)], target[..Math.Min(target.Length, 100)]);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 302 发送失败");
            return false;
        }
    }

    private async Task<string> FetchTextAsync(string url)
    {
        var resp = await _fetchHttp.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<(string Text, string FinalUrl)> FetchTextWithFinalAsync(string url)
    {
        var resp = await _fetchHttp.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        return (text, final);
    }

    // ============ 探测假清单 ============

    private async Task RespondDummyTsAsync(NetworkStream stream)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_config.DummyTsPath) && File.Exists(_config.DummyTsPath))
            {
                var data = await File.ReadAllBytesAsync(_config.DummyTsPath);
                await RespondAsync(stream, 200, "OK", new Dictionary<string, string> { { "Content-Type", "video/mp2t" } }, data);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 读取假 ts 失败");
        }

        await WriteHeadAsync(stream, 500, "Internal Server Error", null, 0);
    }

    private async Task RespondFakeMasterAsync(NetworkStream stream)
    {
        var fake = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:7200\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:7200.0,\nhttp://127.0.0.1:" + _config.ListenPort + "/__probe_dummy.ts\n#EXT-X-ENDLIST\n";
        await RespondAsync(stream, 200, "OK", new Dictionary<string, string> { { "Content-Type", "application/vnd.apple.mpegurl" } }, Encoding.UTF8.GetBytes(fake));
    }

    // ============ 透明转发 ============

    private async Task<bool> ProxyAsync(NetworkStream stream, string method, string rawTarget, Dictionary<string, string> headers, byte[] body)
    {
        string path;
        try
        {
            path = new Uri("http://stash" + rawTarget).AbsolutePath;
        }
        catch (Exception)
        {
            path = rawTarget;
        }

        // ---- usersettings 写库外键 bug 拦截 ----
        if (method == "GET" && path.TrimEnd('/') == "/DisplayPreferences/usersettings")
        {
            var dummy = "{\"Id\":\"usersettings\",\"SortBy\":\"SortName\",\"RememberIndexing\":true,\"PrimaryImageHeight\":0,\"PrimaryImageWidth\":0,\"CustomPrefs\":{},\"ScrollDirection\":\"Horizontal\",\"ShowBackdrop\":true,\"RememberSorting\":true,\"SortOrder\":\"Ascending\",\"ShowSidebar\":true,\"Client\":\"emby\"}";
            await RespondAsync(stream, 200, "OK", new Dictionary<string, string> { { "Content-Type", "application/json; charset=utf-8" } }, Encoding.UTF8.GetBytes(dummy));
            return true;
        }

        // ---- 图片缓存 ----
        if (method == "GET" && _config.ImageCache && ImageRe.IsMatch(path))
        {
            var imgKey = rawTarget;
            if (_imgCache.TryGetValue(imgKey, out var hit)
                && DateTime.UtcNow.Subtract(hit.Ts).TotalSeconds < ImgTtlSec)
            {
                await RespondAsync(stream, 200, "OK", new Dictionary<string, string>
                {
                    { "Content-Type", hit.ContentType },
                    { "Content-Length", hit.Body.Length.ToString(CultureInfo.InvariantCulture) },
                    { "Cache-Control", "public, max-age=86400" },
                }, hit.Body);
                return true;
            }

            var fetched = await ForwardBufferedAsync(method, rawTarget, headers, body);
            if (fetched != null && fetched.Status == 200 && fetched.Body.Length > 0)
            {
                if (_imgCache.Count >= ImgCacheMax)
                {
                    _imgCache.Clear();
                }

                _imgCache[imgKey] = new ImgEntry { Body = fetched.Body, ContentType = fetched.ContentType, Ts = DateTime.UtcNow };
            }

            if (fetched != null)
            {
                await RespondAsync(stream, fetched.Status, fetched.Reason, fetched.Headers, fetched.Body);
                return true;
            }

            await WriteHeadAsync(stream, 502, "Bad Gateway", new Dictionary<string, string> { { "Content-Type", "text/plain" } }, 0);
            return true;
        }

        // ---- 普通请求：流式转发（支持大流/普通影片播放，不做全量缓冲） ----
        return await ForwardStreamingAsync(stream, method, rawTarget, headers, body);
    }

    private HttpRequestMessage BuildForwardRequest(string method, string rawTarget, Dictionary<string, string> headers, byte[] body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), _config.JfUrl + rawTarget);
        if (body.Length > 0)
        {
            req.Content = new ByteArrayContent(body);
        }

        foreach (var (k, v) in headers)
        {
            var kl = k.ToLowerInvariant();
            if (kl is "host" or "connection" or "accept-encoding" or "transfer-encoding" or "content-length")
            {
                continue;
            }

            if (!req.Headers.TryAddWithoutValidation(k, v))
            {
                if (req.Content != null)
                {
                    req.Content.Headers.TryAddWithoutValidation(k, v);
                }
            }
        }

        req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{_config.Token}\"");
        return req;
    }

    private async Task<FetchedResponse?> ForwardBufferedAsync(string method, string rawTarget, Dictionary<string, string> headers, byte[] body)
    {
        try
        {
            var req = BuildForwardRequest(method, rawTarget, headers, body);
            using var resp = await _proxyHttp.SendAsync(req);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var outHeaders = new Dictionary<string, string>();
            foreach (var h in resp.Headers)
            {
                var kl = h.Key.ToLowerInvariant();
                if (kl is "transfer-encoding" or "connection" or "content-length")
                {
                    continue;
                }

                outHeaders[h.Key] = string.Join(", ", h.Value);
            }

            outHeaders["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture);
            var ct = resp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            return new FetchedResponse((int)resp.StatusCode, resp.ReasonPhrase ?? string.Empty, outHeaders, bytes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 缓冲转发失败: {Target}", rawTarget[..Math.Min(rawTarget.Length, 70)]);
            return null;
        }
    }

    private async Task<bool> ForwardStreamingAsync(NetworkStream stream, string method, string rawTarget, Dictionary<string, string> headers, byte[] body)
    {
        HttpResponseMessage? resp = null;
        try
        {
            var req = BuildForwardRequest(method, rawTarget, headers, body);
            // 响应头读完即返回，正文流式复制
            resp = await _proxyHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var code = (int)resp.StatusCode;
            var reason = resp.ReasonPhrase ?? string.Empty;

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            long? contentLength = null;
            foreach (var h in resp.Headers)
            {
                var kl = h.Key.ToLowerInvariant();
                if (kl is "transfer-encoding" or "connection" or "content-length")
                {
                    continue;
                }

                sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append("\r\n");
            }

            if (resp.Content.Headers.ContentLength.HasValue)
            {
                contentLength = resp.Content.Headers.ContentLength.Value;
                sb.Append("Content-Length: ").Append(contentLength.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            else
            {
                // 上游无长度（chunked/ffmpeg 实时流）：关闭连接方式流式透传
                sb.Append("Connection: close\r\n");
            }

            sb.Append("\r\n");
            var head = Encoding.Latin1.GetBytes(sb.ToString());
            await stream.WriteAsync(head.AsMemory(0, head.Length));
            await resp.Content.CopyToAsync(stream);

            if (!contentLength.HasValue)
            {
                return false; // 需要关闭连接（无长度）
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] 流式转发失败: {Target}", rawTarget[..Math.Min(rawTarget.Length, 70)]);
            try
            {
                await WriteHeadAsync(stream, 502, "Bad Gateway", null, 0);
            }
            catch (Exception)
            {
            }

            return false;
        }
        finally
        {
            resp?.Dispose();
        }
    }

    // ============ WebSocket 代理 ============

    private async Task WsProxyAsync(NetworkStream clientStream, string method, string rawTarget, Dictionary<string, string> headers)
    {
        TcpClient upstream;
        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync(_jfHost, _jfPort);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmDirect] WS 上游连接失败");
            await WriteHeadAsync(clientStream, 502, "Bad Gateway", null, 0);
            return;
        }

        using (upstream)
        {
            var us = upstream.GetStream();
            var req = new StringBuilder();
            req.Append(method).Append(' ').Append(rawTarget).Append(" HTTP/1.1\r\n");
            foreach (var (k, v) in headers)
            {
                if (k.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                req.Append(k).Append(": ").Append(v).Append("\r\n");
            }

            req.Append("Host: ").Append(_jfHost).Append(':').Append(_jfPort).Append("\r\n\r\n");
            try
            {
                var reqBytes = Encoding.Latin1.GetBytes(req.ToString());
                await us.WriteAsync(reqBytes.AsMemory(0, reqBytes.Length));
                await us.FlushAsync();

                // 读上游响应头并转发
                var head = await ReadHeadFromStreamAsync(us);
                if (head == null)
                {
                    return;
                }

                await clientStream.WriteAsync(head.AsMemory(0, head.Length));
                await clientStream.FlushAsync();

                var t1 = Task.Run(() => PipeAsync(us, clientStream));
                var t2 = Task.Run(() => PipeAsync(clientStream, us));
                await Task.WhenAll(t1, t2);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmDirect] WS 代理异常");
            }
        }
    }

    private static async Task<byte[]?> ReadHeadFromStreamAsync(Stream s)
    {
        var list = new List<byte>(8192);
        var tmp = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(tmp.AsMemory(0, tmp.Length));
            if (n <= 0)
            {
                return list.Count == 0 ? null : list.ToArray();
            }

            list.AddRange(tmp.AsSpan(0, n).ToArray());
            if (FindCrlfCrlf(list) >= 0)
            {
                return list.ToArray();
            }

            if (list.Count > 65536)
            {
                return null;
            }
        }
    }

    private static async Task PipeAsync(Stream src, Stream dst)
    {
        try
        {
            await src.CopyToAsync(dst);
        }
        catch (Exception)
        {
        }

        try
        {
            dst.Close();
        }
        catch (Exception)
        {
        }
    }

    // ============ 响应辅助 ============

    private static async Task RespondAsync(NetworkStream stream, int code, string reason, Dictionary<string, string>? headers, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
            }
        }

        if (body.Length > 0 && !sb.ToString().Contains("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        sb.Append("\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        await stream.WriteAsync(head.AsMemory(0, head.Length));
        if (body.Length > 0)
        {
            await stream.WriteAsync(body.AsMemory(0, body.Length));
        }

        await stream.FlushAsync();
    }

    private static async Task WriteHeadAsync(NetworkStream stream, int code, string reason, Dictionary<string, string>? headers, long contentLength)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
            }
        }

        sb.Append("Content-Length: ").Append(contentLength).Append("\r\n\r\n");
        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length));
        await stream.FlushAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _proxyHttp.Dispose();
        _fetchHttp.Dispose();
    }

    // ============ 内部类型 ============

    private sealed class CdnInfo
    {
        public string Base { get; set; } = string.Empty;

        public string Query { get; set; } = string.Empty;
    }

    private sealed class ItemInfo
    {
        public string Path { get; set; } = string.Empty;

        public string MediaUrl { get; set; } = string.Empty;
    }

    private sealed class ImgEntry
    {
        public byte[] Body { get; set; } = Array.Empty<byte>();

        public string ContentType { get; set; } = "image/jpeg";

        public DateTime Ts { get; set; }
    }

    private sealed class FetchedResponse
    {
        public FetchedResponse(int status, string reason, Dictionary<string, string> headers, byte[] body, string contentType)
        {
            Status = status;
            Reason = reason;
            Headers = headers;
            Body = body;
            ContentType = contentType;
        }

        public int Status { get; }

        public string Reason { get; }

        public Dictionary<string, string> Headers { get; }

        public byte[] Body { get; }

        public string ContentType { get; }
    }

    /// <summary>
    /// 连接读取器：支持 pushback（首字节 TLS 检测与跨请求残余数据）。
    /// </summary>
    private sealed class ConnReader
    {
        private readonly Stream _stream;
        private byte[] _extra = Array.Empty<byte>();

        public ConnReader(Stream stream)
        {
            _stream = stream;
        }

        public void PushBack(byte[] data, int start, int count)
        {
            if (count <= 0)
            {
                return;
            }

            var all = new byte[_extra.Length + count];
            if (_extra.Length > 0)
            {
                Buffer.BlockCopy(_extra, 0, all, 0, _extra.Length);
            }

            Buffer.BlockCopy(data, start, all, _extra.Length, count);
            _extra = all;
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count)
        {
            if (_extra.Length > 0)
            {
                var n = Math.Min(_extra.Length, count);
                Buffer.BlockCopy(_extra, 0, buffer, offset, n);
                if (n == _extra.Length)
                {
                    _extra = Array.Empty<byte>();
                }
                else
                {
                    var rest = new byte[_extra.Length - n];
                    Buffer.BlockCopy(_extra, n, rest, 0, rest.Length);
                    _extra = rest;
                }

                return n;
            }

            return await _stream.ReadAsync(buffer.AsMemory(offset, count));
        }

        public async Task<string?> ReadLineAsync()
        {
            var list = new List<byte>(128);
            var one = new byte[1];
            while (true)
            {
                var n = await ReadAsync(one, 0, 1);
                if (n <= 0)
                {
                    return list.Count == 0 ? null : Encoding.Latin1.GetString(list.ToArray());
                }

                if (one[0] == 10)
                {
                    break;
                }

                if (one[0] != 13)
                {
                    list.Add(one[0]);
                }
            }

            return Encoding.Latin1.GetString(list.ToArray());
        }
    }
}
