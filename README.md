# jhnote v0.1.0

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey.svg)
[![Release](https://img.shields.io/github/v/release/uranusuperman/jhnote?sort=semver)](https://github.com/uranusuperman/jhnote/releases/latest ) **👈 下载**

华为云空间备忘录导出为 Markdown 笔记，**C# + WebView2 原生实现**：
一个 66KB 的 exe + 两个 WebView2 DLL 即可运行。

 **使用方法**：解压后运行jhnote.exe → 先点「登录」扫码 → 状态变「已登录（笔记 N 条）」→ 再点「开始导出」→ 等待导出、渲染完成。

协议：MIT　|　本项目在 **AI** 辅助下完成

尝试过用手机直连电脑HiSuite导出备忘录，发现导出文件有加密，从外部存储导出的文件同样情况，解密会很麻烦甚至不可能。
故参考b站 @FPV-FIT https://www.bilibili.com/video/BV1qnnT6eEw6/  思路，借助华为云空间cookie。

### ·手写笔迹覆盖在文字上的笔记暂时无法完全复原，只能文字和手写笔迹分开展示

---

## 免责声明

- 本项目**与华为公司无任何关联**，非官方工具，未获得华为授权或认可；
- 仅用于导出**使用者本人**的备忘录数据，请勿用于获取他人数据；
- 通过网页版接口访问云空间，可能受**华为云服务条款**约束，使用前请自行评估合规性；
- 软件按 MIT 协议“**按原样**”提供，不附带任何担保；因使用本工具造成的数据丢失、账号异常或其他后果，由使用者自行承担；
- 仓库中不包含任何用户数据、账号信息或 cookie，导出结果与缓存均已通过 `.gitignore` 排除。

---

## 一、这个工具解决什么问题

华为手机备忘录（`com.example.android.notepad`）没有导出 Markdown 的官方途径。本项目直接调用**华为云空间网页版**的接口，把账号里的笔记拉下来，转成本地 Markdown + 附件：

- 保留 **创建时间 / 修改时间 / 分类（文件夹）/ 标签**；
- 正文 HTML → Markdown（含图片、文档、视频、音频附件）；
- **手写笔迹**单独渲染成长图（`手写笔迹.png`），并自动替换 md 里的缩略图；
- 输出可直接放进 Obsidian 等笔记库。

---

## 二、从实战中总结的关键结论

这些是踩坑后确认的事实，也是整个实现的设计前提：

| 结论                          | 说明                                                                                   |
| --------------------------- | ------------------------------------------------------------------------------------ |
| **登录只能靠浏览器 cookie**         | 云空间没有 OAuth / API Key，唯一凭证是登录后的 cookie                                               |
| **cookie 约 15 分钟失效**        | 一次完整导出 10–12 分钟，基本“每次运行都要扫码”，做持久化意义不大                                                |
| **下载附件需要“登记”**              | 下载网关要求会话先真实请求过一次附件，否则 403：`The settings <loginID> is not found in the token service` |
| **登记方式**                    | 登录后在浏览器里打开一条带附件的笔记，并用 `<img>` 真发一次请求；再用 curl 探测到 200 才算成功                            |
| **手写笔迹是私有格式**               | 附件 `.txt` 以 `HWGRAFFI` 开头（矢量笔迹），云端预览图只有 540×540（被裁切）                                 |
| **笔迹可用画布导出**                | 笔记详情页用 `<canvas>` 渲染，`canvas.toDataURL()` 能拿到整幅（实测 320×343 ~ 320×4215）               |
| **隐藏窗口无法截图**？？              | 窗口移出屏幕 / 隐藏后 Chromium 停止绘制，截图全黑；渲染时窗口必须可见                                            |
| **Markdown 可能会误识别一些字符为转义符** | 空格被压缩、`4 空格` 变代码块、`1.` 被当成有序列表、alt 里的 `$` 触发公式                                       |
| **附件路径要 URL 编码**            | 目录名含空格时链接会在空格处截断，Obsidian 报“找不到”                                                     |
| **文字和手写笔迹分开展示**             | 带文字和手写笔迹的笔记无法完全复原，只能文字和手写笔迹分开展示                                                      |
？？为未验证不确定

---

## 三、功能

### 图形界面

双击 `jhnote.exe`：（路径名仅为示例）

```
状态：未登录                      [登录] [退出登录]
导出到  [C:\Users\<你的用户名>\Documents\jhnote-export] [浏览…]
☐ 只导文字（不下载附件）  ☑ 同时渲染手写笔迹
[开始导出] [取消]
进度条 / 状态行 / 日志
```

顺序：**先点「登录」扫码 → 状态变「已登录（笔记 N 条）」→ 再点「开始导出」**。

- 「退出登录」关闭会话窗口并清空内存中的 cookie；
- 导出期间「退出登录」自动禁用，避免会话被抽走；
- 导出完成后弹窗显示完整输出路径。

### 命令行

```powershell
jhnote.exe                             # 图形界面
jhnote.exe export --out DIR            # 登录后导出（--no-media / --match 关键字）
jhnote.exe all --out DIR               # 登录 + 导出 + 渲染手写笔迹
jhnote.exe render --out DIR [--export DIR]   # 只渲染手写笔迹
```

退出码：`0` 成功，`1` 出错，`2` 未登录/中断。

---

## 四、目录结构

```
jhnote\
├── src\
│   ├── Program.cs          CLI 入口、路径解析、TLS 设置、控制台挂接
│   ├── MainForm.cs         图形界面与导出编排
│   ├── LoginForm.cs        WebView2 登录窗口（登录 + 预热 + 可选导出/渲染）
│   ├── NotepadRenderer.cs  手写笔迹画布导出、md 图片替换
│   ├── Api.cs              云空间接口（列表/详情/标签/下载/探测）
│   ├── Render.cs           HTML → Markdown（路径编码、空行与空格保留）
│   ├── Export.cs           导出编排、附件去重、CSV/报告、笔迹清单
│   └── ExportProgress.cs   进度数据结构
├── tests\SelfTest.cs       13 项离线自测（不联网）
├── build.ps1               Roslyn csc 一键编译
├── LICENSE                 MIT 协议
├── THIRD-PARTY-NOTICES.md  第三方组件声明
├── .gitattributes          换行符规范
├── .gitignore              忽略 bin\、lib\ 与运行期数据
├── README.md               使用说明（本文件）
├── lib\                    首次编译自动下载的 WebView2 SDK（不入库）
└── bin\                    编译产物 + 运行期数据（data\ 浏览器缓存、export\ 导出结果）
```

---

## 五、编译

### 环境要求

| 依赖 | 说明 |
|---|---|
| Windows 10 / 11 | 需自带 **.NET Framework 4.8** |
| **Roslyn csc** | 满足其一即可：安装 Visual Studio，或安装 **.NET SDK**（脚本会自动找到 `dotnet` 里的 csc） |
| WebView2 SDK 程序集 | 优先用本机 Visual Studio 自带的；**没有则首次编译自动从 nuget.org 下载**到 `lib\`，之后走本地缓存 |

### 编译

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

若本机没有 Visual Studio 的 WebView2 程序集，首次运行会看到：

```
[..] downloading WebView2 SDK 1.0.3485.44 from nuget.org
[OK] WebView2 SDK saved to ...\jhnote\lib
[OK] built ...\jhnote\bin\jhnote.exe
```

下载的 DLL 缓存在 `lib\`（已加入 `.gitignore`，不会提交）；有缓存后编译**不再联网**。已经装 Visual Studio 的机器则直接复用它的程序集，不会下载。

如果需要强制重新下载（例如缓存损坏）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -DownloadWebView2
```

### 产物

```
bin\jhnote.exe                       ~66 KB
bin\Microsoft.Web.WebView2.Core.dll  ~635 KB
bin\WebView2Loader.dll               ~156 KB
bin\selftest.exe                     自测程序（离线，13 项断言）
```

运行还需要 **WebView2 Runtime**（Win11 自带；Win10 多数已随 Edge 安装）。

> 只想要可执行文件、不想编译：直接下载 Release 里的 zip。

---

## 六、输出结构

```
<out>\
├── notes\<日期>_<标题>.md      每篇一篇 Markdown（YAML front-matter）
├── assets\<日期>_<标题>\       该笔记的图片/文档/视频/音频
│   └── 手写笔迹.png            手写笔记的整幅画布（如有）
├── handwriting.json           真正含 HWGRAFFI 笔迹的笔记清单
├── index.csv                  全量索引（标题、时间、分类、字数、附件数）
└── export_report.md           导出报告
```

Markdown 头部示例：

```yaml
---
title: "示例标题"
created: 2024-01-01 12:00:00
modified: 2024-01-01 12:30:00
folder: "示例分类"
tag: "示例分类|示例标签"
note_id: "EXAMPLE0000000000000000000000000000"
source: huawei-cloud-notepad
attachments:
  - assets/20240101_示例标题/sample.webp
---
```

---

## 七、实现要点

### 登录与预热

1. 内嵌 WebView2 打开 `cloud.huawei.com/home`，用户扫码登录；
2. 轮询 cookie，齐备（`loginID`/`token`/`userId`/`isLogin`）且页面在 `/home` 才继续；
3. 选一条带附件的笔记（优先带图片），直接按 GUID 导航打开；
4. 页面内用 `<img>` 请求一次附件，再用 `curl` 探测 HTTP 200，最多换 3 条笔记重试；
5. 成功后保存 cookie 到内存，窗口保持打开供后续渲染使用。

### 导出

列表 → 详情 → 附件下载（curl 通道，规避 .NET 对 `%2F` 的处理差异）→ 渲染 Markdown。
附件按“同版本组”去重（优先 `_original`），按内容嗅探真实扩展名。

### 手写笔迹

`handwriting.json` 记录含 `HWGRAFFI` 的笔记；渲染时逐条打开笔记页，取页面中最大的 `<canvas>`
并 `toDataURL('image/png')`，存入该笔记自己的 `assets` 目录并替换 md 中的第一张图片链接。

### Markdown 处理（不完全）

- 行首/连续空格 → NBSP，避免被压缩或误判为代码块；
- 行首 `1.` → `1\.`，避免被当作有序列表；
- 图片 alt 去掉 `$`、`[]` 等字符（`$` 会被当作行内公式）；
- 链接路径做百分号编码（空格、`#`、`?`、括号）。

---

## 八、已知限制

| 限制                     | 原因                                             |
| ---------------------- | ---------------------------------------------- |
| 每次运行都要扫码               | 会话只保留在内存（不落盘），且云端会话本身约 15 分钟                   |
| 笔迹图约 320 px 宽          | 网页版画布的原生分辨率，不是导出时被压缩                           |
| 首次运行可能被 SmartScreen 拦截 | exe 未签名，点“仍要运行”即可                              |
| 没有「打开文件夹」按钮            | 当前机器 `explorer.exe` 启动即 `0xc0000142`，该功能不可用已移除 |
| 笔迹渲染时浏览器窗口必须可见？？       | 隐藏窗口后 Chromium 停止绘制，截图为全黑                      |
| 渲染依赖网页版结构              | 若华为改版（URL/画布结构），需要同步调整                         |
？？为未验证不确定

---

## 九、自测

```powershell
.\bin\selftest.exe
```

13 项离线断言：HTML 解析、图片原位渲染、文档/视频链接、空行与空格保留、文件名清洗、
`$` 清洗、路径编码、序号转义等。全部通过。

---

## 十、版本

**v0.1.0** —— 首个可用版本：

- 完整导出（文字 + 附件 + 分类/时间/标签）；
- 手写笔迹画布导出与 md 自动替换；
- 图形界面（登录状态、目录选择、进度、日志、取消）；
- 会话仅内存保存，退出即失效；
- 三命令 CLI（`export` / `all` / `render`）。

---

## 十一、协议

本项目以 **MIT License** 发布：

```
Copyright (c) 2026 uranusuperman
```

可以自由使用、修改、分发、商用，只需**保留版权声明与许可声明**，且作者不承担任何担保责任。
完整条款见 [LICENSE](LICENSE)。

随程序分发的 Microsoft WebView2 SDK 组件遵循其自身许可，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

---

## 十二、致谢

- 思路参考了社区华为备份解密方案与 B 站 FPV-FIT 的 `HWNOTE.py`（链接见文首）
- 感谢所有提供帮助的ai^_^
