# 片坞 Movie Dock — Windows 原生版

自 [fn-movie-dock](https://github.com/WANGjia8613/fn-movie-dock)（Python + Web UI）全量移植的 Windows 桌面应用：检索片源 → aria2 下载 → 自动整理入库 → 自动匹配中文字幕，一条链路完成。

## 功能

- **聚合检索**：内置索引源（TPB / YTS / DMHY，镜像轮询）+ qBittorrent 搜索 + 自定义索引 API + 大模型检索；中文片名可经大模型自动翻成英文再搜
- **候选评分**：按 HDR / DoVi / REMUX / 做种数 / 体积加权排序，最优候选高亮
- **下载引擎**：aria2（磁力/种子，应用可自托管拉起 aria2c）+ HTTP 直链降级；磁力元数据 followedBy 派生任务自动接管、占位文件防误判；任务暂停/继续/删除/批量清理、重启后任务列表不丢（tasks.json 持久化）
- **自动整理**：电影/剧集（S01E02、第X集）识别，按模板改名归档，支持 move/copy/硬链接、独立资料库目录
- **自动字幕**：SubHD 检索 + 简体/双语优先打分 + 大模型补中文译名；zip/rar 解压（7z 兜底）、GBK/Big5 编码规整为 UTF-8
- **配置**：YAML 配置文件（`%APPDATA%\MovieDock\config.yaml`），与 Python 版配置结构兼容，老版本配置自动迁移

## 运行

- **便携版**（`MovieDock-portable-win-x64.zip`）：解压后双击 `MovieDock.exe`，免安装、自带 .NET 8 运行时
- **框架依赖版**（`MovieDock-fd-win-x64.zip`）：需先安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)
- 磁力/种子下载需要 `aria2c.exe`（放程序目录或 PATH 即可，应用会自动拉起）；没有 aria2 时 HTTP 直链仍可下载

## 构建

```bash
dotnet build MovieDock.sln            # 需要 .NET 8 SDK
dotnet test tests/MovieDock.Core.Tests  # 80 项 xUnit 回归
dotnet publish src/MovieDock.App -c Release -o publish/fd
```

## 结构

```
src/MovieDock.Core    # 纯逻辑库：检索/下载/整理/字幕/配置（无任何 UI 依赖）
src/MovieDock.App     # WPF 界面（检索 / 任务 / 设置 三页）
tests/                # 80 项 xUnit 回归测试（移植自 Python 版 scripts/unit_checks.py）
dotnet-env.sh         # 本机构建环境脚本（NuGet 离线还原等环境修复）
```

## 许可证

本项目以 **MIT** 许可证发布，见 [LICENSE](LICENSE)：可自由使用、修改、再分发（保留版权与许可声明）。

许可证与 Python 版 [fn-movie-dock](https://github.com/WANGjia8613/fn-movie-dock) 保持一致。
