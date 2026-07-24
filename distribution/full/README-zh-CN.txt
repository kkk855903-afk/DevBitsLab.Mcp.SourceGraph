SourceGraph MCP Full 离线版（Windows x64）
==========================================

用途
----
此安装包同时包含 SourceGraph MCP 工具和代码语义搜索模型。安装期间不需要
访问 NuGet 或 Hugging Face。

要求
----
- 64 位 Windows
- .NET 10 SDK
- 需要分析的项目根目录中有一个 .slnx 或 .sln 文件

安装
----
1. 解压整个 ZIP，不能只复制安装脚本。
2. 在需要接入 SourceGraph MCP 的项目根目录打开 PowerShell。
3. 运行：

   powershell -ExecutionPolicy Bypass -File "解压目录\install-sourcegraph-mcp.ps1"

脚本会：
- 从包内的本地 NuGet 源安装或更新 sourcegraph-mcp；
- 校验并安装内置的 jina-embeddings-v2-base-code 模型；
- 为当前项目生成 .codex/config.toml；
- 预热源码索引，并执行 demo 健康检查。

如果项目根目录中有多个解决方案：

   powershell -ExecutionPolicy Bypass -File "解压目录\install-sourcegraph-mcp.ps1" `
     -Solution ".\需要使用的解决方案.slnx"

只配置、不立即建立索引：

   powershell -ExecutionPolicy Bypass -File "解压目录\install-sourcegraph-mcp.ps1" `
     -SkipPrewarm

只校验下载的离线包，不安装任何内容：

   powershell -ExecutionPolicy Bypass -File ".\install-sourcegraph-mcp.ps1" `
     -VerifyBundleOnly

安装完成后
----------
关闭并重新打开 Codex 项目，然后运行 /mcp，确认 sourcegraph 已连接。

模型说明
----------
内置的模型用于本地代码语义搜索，不是对话式大语言模型。模型文件不会上传，
许可证和来源信息见 THIRD-PARTY-NOTICES.txt 与 APACHE-2.0.txt。
