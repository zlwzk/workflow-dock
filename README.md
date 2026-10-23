# 工作流坞 (workflow-dock)

WinIsland 灵动岛插件：快捷启动栏 + 命令框。

- 小岛常驻一排常用 App 图标（最多 6 个），点击直接启动
- 展开后多一个命令框：输入网址、文件/文件夹路径、或快捷列表里的名称，回车执行
- 设置页：显示开关 + 快捷启动列表编辑（一行一个：`名称|目标`）

## 构建

```powershell
cd 本目录
dotnet build
```

构建完成会自动把插件拷进本机 WinIsland 的 `plugins\workflow-dock\`（目录写在 csproj 的 `WinIslandPluginsDir`）。
`global.json` 把构建 SDK 钉在 .NET 10，不要删。

## 设置格式

每行一个快捷方式：

```
名称|目标
微信|C:\Program Files\Tencent\WeChat\WeChat.exe
B站|https://www.bilibili.com
下载|C:\Users\me\Downloads
```
