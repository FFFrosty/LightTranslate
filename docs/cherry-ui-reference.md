# 划词翻译界面参考

轻译是独立的 Windows 翻译工具，不是 Cherry Studio 的官方版本或插件。

本轮界面设计参考 Cherry Studio 官方仓库在 `dd0767e1e7ecd8e37f44376382f35cb7ba04bea8` 的划词翻译布局和交互。轻译使用自己的 C# / Windows Forms 实现，没有引入 Cherry Studio 的 TypeScript、CSS、品牌图片或图标文件。

## 对照来源

- [SelectionToolbarView.tsx](https://github.com/CherryHQ/cherry-studio/blob/dd0767e1e7ecd8e37f44376382f35cb7ba04bea8/src/renderer/components/selection/SelectionToolbarView.tsx)：紧凑圆角工具条、悬停反馈。
- [ActionWindow.tsx](https://github.com/CherryHQ/cherry-studio/blob/dd0767e1e7ecd8e37f44376382f35cb7ba04bea8/src/renderer/windows/selection/action/ActionWindow.tsx)：紧凑标题栏、置顶、透明度和窗口按钮。
- [ActionTranslate.tsx](https://github.com/CherryHQ/cherry-studio/blob/dd0767e1e7ecd8e37f44376382f35cb7ba04bea8/src/renderer/windows/selection/action/components/ActionTranslate.tsx)：语言方向、目标语言选择、原文折叠、逐步显示译文。
- [WindowFooter.tsx](https://github.com/CherryHQ/cherry-studio/blob/dd0767e1e7ecd8e37f44376382f35cb7ba04bea8/src/renderer/windows/selection/action/components/WindowFooter.tsx)：停止、关闭、重新翻译和复制操作。
- [SelectionService.ts](https://github.com/CherryHQ/cherry-studio/blob/dd0767e1e7ecd8e37f44376382f35cb7ba04bea8/src/main/services/selection/SelectionService.ts)：500 × 400 的默认结果窗口尺寸、屏幕工作区边界。

Cherry Studio 源码使用 [AGPL-3.0](https://github.com/CherryHQ/cherry-studio/blob/dd0767e1e7ecd8e37f44376382f35cb7ba04bea8/LICENSE)。本文件记录参考来源，不将上游源码重新标记为轻译的 MIT 许可。

## 范围

对齐的是划词翻译的主要界面与交互。轻译保留自己的名称和图标；不会加入 Cherry Studio 的聊天、知识库、搜索或其他 Agent 功能。原生控件的字体栅格化、系统阴影和弹出菜单会随 Windows 版本及缩放设置变化，因此不能保证逐像素一致。

语言判断在本机完成，是粗略判断；不会为了识别语种额外发送一次模型请求。实际译文仍由已配置的服务生成。
