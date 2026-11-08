# LL Craft Launcher

一个面向 Windows 的 Minecraft 启动器，使用 WinUI 3 与 .NET 10 构建。

> 本项目是个人学习与自用项目。开发过程中参考了 [PCL-CE](https://github.com/PCL-Community/PCL-CE) 的公开实现，
> 但代码为独立编写，不包含其源码。

## 功能

**下载**
- 全部原版版本，支持 BMCLAPI / 官方源 / 自定义镜像，可按文件类型分别配置
- 模组加载器：Fabric、Quilt、Forge、NeoForge
- 加载器版本自包含：jar 与依赖库全部放入加载器目录，不再依赖原版版本
- 资源文件支持多源交叉并发、断点续传、SHA1 校验与自动重试
- 社区资源搜索：Modrinth 与 CurseForge 双源合并，支持按版本与加载器筛选

**启动**
- 直接启动版本，不引入"实例"概念
- 版本隔离：可配置为仅隔离可装 Mod 的版本、仅隔离非正式版，或全部隔离
- 启动过程分阶段提示，等待游戏窗口真正出现才结束
- 崩溃自动识别：内存不足、Java 版本不符、模组冲突、显卡驱动等，
  并给出对应的解决办法

**账号**
- 离线账号
- 微软正版账号：OAuth 设备码流程，令牌过期自动续期
- 第三方皮肤站：Yggdrasil 协议（LittleSkin 等），自动注入 authlib-injector

**联机**
- 基于 EasyTier 的虚拟局域网，房间码一键加入
- 局域网世界自动扫描
- 中继节点动态获取，多源降级

## 系统要求

- Windows 10 1809 及以上 / Windows 11
- x64
- 无需预装 .NET 运行时（自包含发布）

## 安装

从 [Releases](../../releases) 下载安装包：

- `LLCL_x.y.z_x64.msi` — 安装到 `%LOCALAPPDATA%\LL Craft Launcher`，无需管理员权限
- `LLCL_x.y.z_x64_portable.zip` — 解压即用

## 数据位置

| 内容 | 路径 |
|---|---|
| 游戏数据 | `%USERPROFILE%\.minecraft`（与官方启动器、PCL 共用） |
| 启动器配置与日志 | `%APPDATA%\LLCL` |
| 账号 | `%APPDATA%\LLCL\accounts.json` |

将 `portable.txt` 放在程序目录可启用便携模式，所有数据存于程序目录下的 `LLCL` 文件夹。

## 从源码构建

需要 .NET 10 SDK 与 Windows 10 SDK。

```powershell
# 编译到 bin 目录（开发用）
dotnet build src/LLCL.App/LLCL.App.csproj -p:Platform=x64

# 发布安装包（会产出 msi 与 portable zip）
pwsh build/publish.ps1 -Stage beta -StageNumber 1
```

版本号统一在 `build/version.json` 中维护，构建时自动注入。

### 版本阶段

| 阶段 | 用途 |
|---|---|
| `dev` | 内部开发，仅编译到 bin，不产出安装包 |
| `beta` | 内测，产出安装包并发布到 GitHub |
| `preview` / `rc` / `release` | 公测与正式版，0.x 阶段不使用 |

## 目录结构

```
src/LLCL.Core/      核心逻辑：下载、启动、账号、联机、资源
src/LLCL.App/       WinUI 3 界面
build/              版本定义与打包脚本
tools/              图标生成、冒烟测试
```

## 已知问题

- 微软登录使用的客户端 ID 来自 PrismLauncher 的公开配置，
  若该应用被停用则需要更换。

## 致谢

- [PCL-CE](https://github.com/PCL-Community/PCL-CE)：功能设计与交互参考
- [PrismLauncher](https://github.com/PrismLauncher/PrismLauncher)：微软认证流程参考
- [EasyTier](https://github.com/EasyTier/EasyTier)：联机内核
- [BMCLAPI](https://bmclapi2.bangbang93.com)：下载镜像
- [authlib-injector](https://github.com/yushijinhun/authlib-injector)：第三方皮肤支持
- [Modrinth](https://modrinth.com) / [CurseForge](https://curseforge.com)：资源数据

## 许可

MIT License，详见 [LICENSE](LICENSE)。
