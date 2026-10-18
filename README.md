# New B Desk

![New B Desk](素材/New_B_Desk_图标_1024.png)

Windows 桌面整理工作台（当前版本 0.5.1）。

## 功能
1. 桌面分区，按事项和时间流转：今天 → 进行中 → 冷却区 → 自动归档（可撤回）；可自定义分区。
2. 文件查找（双击 Ctrl）、截图 + 文字识别（Ctrl+Alt+A）、计算器、右下角工具条，快捷键可改。
3. 密码箱（主密码加密、智能识别导入、本机对外接口）、备忘录（提醒、桌面便签）。
4. 「排列」：对齐现有摆法 / 推荐排版 / 恢复原样，拖动时自动吸附。
5. 7 种界面语言：简体中文、English、Bahasa Indonesia、ไทย、Tiếng Việt、Español、日本語。

## 安装
运行 `New_B_Desk_Setup_版本号.exe`：装在当前用户目录，不需要管理员权限；开始菜单、桌面快捷方式、开机自启可选；在 Windows「应用」里可以卸载（用户数据默认保留）。
需要电脑上有 .NET 8 桌面运行时，没有的话 Windows 会提示下载。

## 编译
需要 .NET 8 SDK（Windows）。版本号统一在 `src/Directory.Build.props`。

```
dotnet publish src/Setup -c Release -o 发布
```
会先发布主程序再打成单文件安装包 `发布/New_B_Desk_Setup_版本号.exe`。只要程序本身：

```
dotnet publish src/DeskHub/DeskHub.csproj -c Release -o 发布/DeskHub
```

## 文档
- `桌面工具_方案V1_待确认.md`：整体方案
- `阶段①②③_*.md`、`0.4.0_*.md`、`0.5.0_*.md`：各版本说明
- `src/DeskHub/密码箱接口说明.md`：密码箱对外接口协议
