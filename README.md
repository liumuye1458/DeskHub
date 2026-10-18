# DeskHub 桌面工作台

个人用的 Windows 桌面整理工具（当前版本 0.5.0）。

## 功能
1. 桌面分区，按事项和时间流转：今天 → 进行中 → 冷却区 → 自动归档（可撤回）；可自定义分区。
2. 文件查找（双击 Ctrl）、截图 + 文字识别（Ctrl+Alt+A）、计算器、右下角工具条，快捷键可改。
3. 密码箱（主密码加密、智能识别导入、本机对外接口）、备忘录（提醒、桌面便签）。
4. 「排列」：对齐现有摆法 / 推荐排版 / 恢复原样，拖动时自动吸附。
5. 7 种界面语言：简体中文、English、Bahasa Indonesia、ไทย、Tiếng Việt、Español、日本語。

## 编译
需要 .NET 8 SDK（Windows）。

```
dotnet publish src/DeskHub/DeskHub.csproj -c Release -o 发布/DeskHub
```

## 文档
- `桌面工具_方案V1_待确认.md`：整体方案
- `阶段①②③_*.md`、`0.4.0_*.md`、`0.5.0_*.md`：各版本说明
- `src/DeskHub/密码箱接口说明.md`：密码箱对外接口协议
