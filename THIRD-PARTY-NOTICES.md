# 第三方组件声明（THIRD-PARTY NOTICES）

本项目（jhnote）自身代码以 MIT 协议发布，见 [LICENSE](LICENSE)。
运行时还依赖以下第三方组件，它们**不属于**本项目，遵循各自的许可条款。

---

## 1. Microsoft WebView2 SDK（随程序分发）

| 文件 | 说明 |
|---|---|
| `bin\Microsoft.Web.WebView2.Core.dll` | WebView2 托管封装（.NET） |
| `bin\WebView2Loader.dll` | WebView2 加载器 |

- 版权：© Microsoft Corporation
- 许可：Microsoft 的 WebView2 SDK 许可条款
- 文档：https://learn.microsoft.com/microsoft-edge/webview2/
- 说明：本项目仅按官方允许的方式分发 SDK 的**托管封装与加载器**；WebView2 运行时（Runtime）由用户系统安装，不随本项目分发。

## 2. WebView2 Runtime（不随程序分发）

程序运行依赖系统中已安装的 WebView2 Runtime：

- Windows 11 通常自带；
- Windows 10 多随 Microsoft Edge 安装；
- 缺失时程序无法启动，可从微软官网安装 “Microsoft Edge WebView2 Runtime”。

## 3. .NET Framework 4.8（不随程序分发）

- 版权：© Microsoft Corporation
- 说明：Windows 10/11 系统组件，本项目仅依赖其运行时，不附带任何二进制文件。

## 4. 思路参考

早期实现参考过社区项目与视频教程（如 华为备份解密方案、`HWNOTE.py`）的思路，
但本仓库的接口调用、登录预热、画布渲染、Markdown 处理均为独立实现，未包含其代码。

---

## 5. 数据与内容

本工具处理的是**用户自己的备忘录数据**。仓库中不包含任何用户的笔记内容、账号信息或 cookie；
导出结果（`bin\export\`）与浏览器缓存（`bin\data\`）已通过 `.gitignore` 排除。
