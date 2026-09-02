# ImageSearch（功能增强 Fork）

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> 本仓库是 **[ldqk/ImageSearch](https://github.com/ldqk/ImageSearch)** 的 Fork，在原作者 **懒得勤快（ldqk）** 的开源作品基础上做了功能修复与体验增强。原项目协议为 [MIT License](LICENSE)，本 Fork 沿用同一协议并保留原作者版权声明。

本地硬盘以图搜图工具 + 图片 Exif 信息移除小工具，支持图片/GIF/视频相似检索，**千万级图片秒级检索**，灵感来源于 [DuplicateCleaner](https://masuit.org/1776)。

本 Fork 在上游基础上做了若干缺陷修复、稳定性加固与体验改进（详见[提交历史](https://github.com/Joftal/ImageSearch/commits/master)），并采用**便携模式**：索引、配置、临时文件全部保存在程序所在目录，可整体拷贝迁移。

---

## 功能

<img width="1165" height="840" alt="image" src="https://github.com/user-attachments/assets/9f295f3b-3edf-4227-bbd8-a4b386b59251" />

- 本地硬盘图片/GIF/视频以图搜图（差异哈希 + DCT 哈希 32/64 位 + 旋转/翻转变体匹配）
- 视频检索：ffmpeg 按 1 秒/帧采样 + ORB 特征深度搜索（支持视频内局部画面/裁切图定位与精确时间戳）
- 拖拽图片/URL/Base64/剪贴板直接搜索
- 集成 Everything 加速目录扫描（可选）
- 内置 HTTP API（本地回环，可选 ApiKey 鉴权）
- Straper：Exif 清除工具（JPEG 无损剥段、BMP 重编码、目录繁转简、右键菜单注册）

## 环境要求

- 运行时：.NET 10 Desktop（win-x64）
- 开发环境：Visual Studio 2026
- 处理器：4 核或更多；内存：8GB 或更多

## 下载与构建

- **Release 包**：见本仓库 [Releases](https://github.com/Joftal/ImageSearch/releases)（GitHub Actions 自动构建）
- **本地构建**：
  ```bash
  dotnet build 以图搜图.sln -c Release
  dotnet test tests/ImageSearch.Tests/ImageSearch.Tests.csproj
  dotnet publish 以图搜图/以图搜图.csproj -c Release -r win-x64 --self-contained false -o publish
  ```

## 特别说明

1. 如果电脑中安装有everything，软件会自动调取everything进行目录扫描，请确保要扫描的目录已经被everything索引，如果你想让软件不自动调取everything，把程序目录下的everything64.dll文件删掉即可
2. 软件不支持部分区域的图片检索，只能做相似检索
3. 相似度限定70是因为低于70的相似度肉眼看上去已经是完全不一样的图了
4. GIF检索只在GIF库内比对，不会匹配静态图片；视频检索按1秒/帧采样比对，视频内局部画面检索依赖ORB深度搜索
5. 便携模式：索引、配置等所有数据均保存在程序所在目录（临时文件在程序目录 temp\ 子目录），不写入其他盘；如需迁移直接整体拷贝程序文件夹
6. 视频检索功能依赖 ffmpeg/ffprobe：因体积超过 GitHub 单文件限制未随源码分发，请自行下载（如 https://www.gyan.dev/ffmpeg/builds/ 的 release essentials 版）并将 ffmpeg.exe、ffprobe.exe 放入程序目录 tools\ 下；缺失时仅视频索引不可用，图片/GIF检索不受影响（CI 打包已内置）

## License

[MIT License](LICENSE) — Copyright (c) 2018 ldqk

本 Fork 基于 MIT 协议对上游进行修改与再分发，保留原作者版权声明。原作者：[懒得勤快（ldqk）](https://github.com/ldqk) · 上游仓库：[github.com/ldqk/ImageSearch](https://github.com/ldqk/ImageSearch) · [gitee.com/masuit/ImageSearch](https://gitee.com/masuit/ImageSearch)

特别鸣谢原作者与 [Masuit.Tools](https://github.com/ldqk/Masuit.Tools)。

## 上游项目完全开源

上游作者声明其项目完全开源；若您在非官方链接为相关软件付费，请申请退款或向相关平台方投诉。
