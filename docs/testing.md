# 构建与测试

## 默认检查

在 Windows x64 / .NET Framework 4.8 环境执行：

```powershell
.\build.ps1 -OutputDirectory .\dist
.\test.ps1
```

`test.ps1` 编译生产客户端加测试入口，以及解析/传输测试入口。只运行内存模拟和纯数据检查，不启动 Agent、不连接真实服务器、不读取配对凭据、不发送 Claude 消息。

0.2.8 的基线为 26 项客户端回归、6 项解析/传输检查。测试输出保存在 `test-build`，该目录不进入版本控制。已有公开记录位于 `reports`。

## 弹窗预览

`preview/AuthorNoticePreview.cs` 是独立预览入口，与 `src/ApplicationInfo.cs` 一同编译。程序接收一个输出报告路径，检查准确的作者文字及弹窗关闭前后的消息循环；点击“确定”后退出。它不是 Agent 自测入口，不访问 Claude 或用户数据。

## 真实环境诊断

`LiveClientFormCheck.cs`、`LiveClientCheck.cs`、`BlankLineInspection.cs` 等源码用于人工选择的真实环境诊断，可能访问已配对服务器，某些模式会提交真实消息。不要把它们纳入默认持续集成，也不要在普通用户会话中随意运行。

实发检查应使用自己的专用测试会话、唯一虚构标记，核对同一会话的新用户消息、真实回复、发送按钮恢复和无重复提交。测试无远程桌面场景时，应读取服务器会话遥测，不能只以本地窗口已关闭为判断依据。

CI 的构建/回归通过不代表 Claude 界面兼容或真实消息提交验收通过。
