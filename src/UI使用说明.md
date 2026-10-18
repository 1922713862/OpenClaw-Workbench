# JFlash 工作台 v0.3.2 界面更新

本轮仅调整界面，网关认证、属主、启停与恢复规则沿用 v0.3.1。仅用于工作库与 P1，不包含生产交接或自动恢复部署。

工作台默认保留主要空间给原有 OpenClaw Control UI。左侧导航默认收起，悬停图标可见提示；展开后显示文字。状态和日志按需打开，关闭抽屉即可回到完整工作区。

按 **F11** 或 **Ctrl+Shift+F** 切换专注模式，再按一次恢复。专注模式只保留工作台内容；网关不会因此启动、停止或重启。

默认字号为：主标题 18–19pt，状态数值 16–17pt，正文/按钮 12pt，时间/辅助信息 11pt。仅标题与数值使用粗体。默认 uiScale=1，保留 0.9–1.5 范围。

P1 使用新版独立目录 `app-ui-v0.3.2`，原来的 `app` 保留。可以给 P1 或 demo 增加 `--ui-scale 1.2` 一类参数调整文字；默认快捷入口仍为 1.0。缩放不改变 64/220 导航与 56 顶栏的逻辑尺寸。

## 截图与尺寸口径

1440×900 默认、1100×720 最小均以 96 DPI 逻辑客户区为口径；截图 JSON 同时记录窗体外框、实际客户区像素与逻辑尺寸。工作区不足时窗口会适配可用屏幕，不隐瞒收敛后的实际尺寸。

默认布局为折叠导航、抽屉关闭，目标为 WebView 可见面积占客户区至少 70%；默认 1440×900 下另核对 WebView 至少 1000×700。最小窗口还需要顶部栏等区域，无法同时要求 WebView 高度 700；以正常适配、内容可用为准。专注模式可进一步扩大工作区。

占比从真实控件矩形计算：逐层扣除客户区裁剪、减去抽屉覆盖面积，除以窗体客户区面积。原有 Control UI 自己的导航属于 WebView 内容。离线截图测量同一承载控件中的保留视口，不伪造已连接的控制台。

## 可复现的离线验证

```powershell
dotnet publish .\JFWorkbench\JFWorkbench.csproj -c Release -o .\JFWorkbench\release-ui-v0.3.2
& .\JFWorkbench\Verify-Ui.ps1
```

单次示例：

```powershell
& .\JFWorkbench\release-ui-v0.3.2\JFWorkbench.exe --demo --ui-review --review-dpi 144 --review-size 1440x900 --review-state default --screenshot D:\chatgpt-codex专属工作库\JFWorkbench\verification\v0.3.2-ui\example.png
```

`--ui-review` 必须显式离线 demo；与 P1 或 smoke-test 混用会在初始化前拒绝。该入口不打开浏览器、不连接真实网关、不读取生产配置，完成截图后退出。界面正常运行时没有这些测量标注。

当前电脑原生显示为 144 DPI（150%）。JSON 中 `native-monitor-DPI` 为该环境实测，记录 DeviceDpi 与 GetDpiForWindow；其余 DPI 图明确标为 `equivalent-DPI-render-NOT-system-test`，只供观感对比，不验证真实系统 DPI 迁移或自动重排。没有改变 Windows 全局缩放，也没有将 uiScale 当作系统 DPI。100%/200% 原生验收需在相应显示环境重跑。

实际截图、占比与覆盖结论见工作库 `协作\Codex-给JF-1.md` 及 `verification\v0.3.2-ui\ui-measurements.json`。
