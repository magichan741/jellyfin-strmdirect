# Jellyfin.StrmDirect

随 Jellyfin 服务器自动运行的 **strm 影片秒播直连插件**（内置代理，无需外部反代进程）。

针对「strm 文件内是运营商专用 m3u8（如移动 CDN），手机/电视在移动网络下可直接播放，但服务器端无法播放」的场景：
手机 app 连接插件端口即可——**master 清单返 200 + 分片 302 直连 CDN**，实现秒播秒切；浏览、刮削、详情、控制透明转发 Jellyfin；普通影片走原生流程。**strm 文件内容从未改写**。

## 原理

```
手机 app ──(局域网/Tailscale)──> 8098(插件, Jellyfin 进程内)
                                   │
                       分片 302 直连 ▼（手机自己连）
                              CDN  ←── 手机 DNS 解析最优节点
```

- **master.m3u8 返 200**：插件拉 CDN 主清单 → 选最低码率子清单 → 分片重写为 `http://<请求Host>/hls-seg/{itemId}/{分片}`。播放器收到 200 清单不会回退网页。
- **分片 302 直连**：`/hls-seg/{itemId}/{seg}` → 302 到原始 CDN 域名（strm 内的地址），query 与子清单最终 URL 合并。
- 手机 ↔ 服务器只传清单与控制信令，**视频数据全走 手机 ↔ CDN**（同运营商网络下秒播）。
- 透明转发保留请求 Host，Jellyfin 生成的回流 URL 自动指向手机所连端口 → 单端口全包。
- 探测劫持：UA 含 `lavf/ffprobe/ffmpeg` 的请求（含代理绝对 URL）返回本地假 HLS + 假 ts，避免服务端探测拖慢进播放页。
- 循环防护：strm 内容若以反代前缀开头（历史遗留改写），回退备份映射文件拿原 CDN 地址。

## 安装

1. 编译（需 .NET SDK 10+）：

   ```bash
   dotnet build -c Release
   ```

2. 把 `bin/Release/net10.0/Jellyfin.Plugin.StrmDirect.dll` 复制到 Jellyfin 的 `plugins/StrmDirect_1.0.0.0/`，并写入 `meta.json`：

   ```json
   {
     "category": "Other",
     "guid": "9e2a7c41-6b8f-4d3e-9a1c-2f5b8d7e4a60",
     "name": "StrmDirect",
     "description": "strm 秒播通道：master 200 + 分片 302 直连 CDN",
     "owner": "jellyfin-strmdirect",
     "targetAbi": "12.2.0.0",
     "version": "1.0.0.0",
     "status": "Active",
     "autoUpdate": false,
     "assemblies": ["Jellyfin.Plugin.StrmDirect.dll"]
   }
   ```

   同时把 `probe_dummy.ts`（任意极小假 ts 分片，可手动生成空 ts）放入同目录，并在配置里指定路径。

3. 重启 Jellyfin。插件随服务器自动监听 8098（无需任何手动启动步骤）。

## 配置（插件设置页，中文界面）

| 配置项 | 默认值 | 说明 |
|---|---|---|
| 启用插件（总开关） | 开 | 关闭后不监听端口 |
| 代理监听端口 | 8098 | 手机 app 服务器地址 = `http://<服务器IP>:8098` |
| Jellyfin 地址 | http://localhost:8096 | 转发目标 |
| API Token | （必填） | Jellyfin 设置 → 高级 → API 密钥 |
| 用户 ID | （必填） | Jellyfin 用户 ID |
| CDN 标识 | chinamobile | strm 内地址匹配该关键字才走直连通道；其他地址透明转发 |
| 探测劫持 | 开 | ffprobe 探测返回假清单 |
| 图片缓存 | 开 | 图片请求缓存（800 条 / 24h） |
| 备份映射文件 | strm_backup.json | 循环防护：历史反代地址回退原 CDN 地址 |
| 假分片文件 | probe_dummy.ts | 探测假分片 |
| 反代前缀 | http://IP:8098/videos/bypath/ | 识别历史改写地址 |

> 修改配置后需重启 Jellyfin 生效。

## 说明

- 依赖 Jellyfin 12.2（`Jellyfin.Controller` / `Jellyfin.Model` 12.2.0）。
- 插件通过 `IPluginServiceRegistrator` + `IHostedService` 随服务器启停（12.x 已无 `IServerEntryPoint`）。
- 仅在 CDN 与客户端同运营商网络时效果最佳；跨网时请关闭 `CDN 标识` 匹配或直接停用插件走原生流程。
- 本项目为个人留档/分享用途，请自行评估使用环境与网络合规性。
