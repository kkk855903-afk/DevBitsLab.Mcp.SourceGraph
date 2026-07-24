# MedInteropLens 复用与演进分析

> 状态日期：2026-07-24
> 本文是已实施后的复用台账。Phase 0 对“候选上游”的原始判断保留在末节；当前仓库已不再是空工作区或仅有候选设计。

## 1. 结论

最初选择
`Jak3b0/DevBitsLab.Mcp.SourceGraph@6b32a8b9b353c30322889d5ab644c2d19bca779a`
作为 MIT 基线是正确的：Roslyn、SQLite/FTS5、scope、stdio MCP、watcher 和测试骨架
均被保留，并在同一分层内扩展。MedInteropLens 没有复制第二套 server、图数据库或
Roslyn symbol model。

最终策略是：

- **原样或小改复用**：hosting、scope routing、Roslyn 基础索引、SQLite/FTS5、
  watcher、history/embeddings、Tree-sitter 扩展和大量上游测试；
- **演进式复用**：XAML、增量删除、storage schema、SDK canonical key、查询 DTO、
  隐私路径、插件/执行信任；
- **新增领域模块**：protoc descriptor、gRPC linker/baseline、Clang extractor、
  isolated native worker、P/Invoke/ABI matcher、Interop 规则和跨域 execution trace；
- **明确不引入**：第二个面向用户的 MCP、第二套事实数据库、运行时下载 CodeGraph、
  手写 `.proto` parser，以及把第三方 AST 类型当公开合同。

## 2. 上游模块的实际去向

| 基线模块 | 当前去向 | 主要演进 |
|---|---|---|
| `Core` | 保留 | 增加证据、Interop/ABI 事实、医疗路径和用户执行信任合同 |
| `Sdk` | 保留并发布 2.5.0 | 开放 kebab-case kind，增加 evidence、`proto/c/cpp` key 和新跨域关系 |
| `Indexing` | 保留并扩展 | 原子文件发布、managed interop/layout/usage、protobuf、Command 和 WPF 风险 |
| `Indexing.Xaml` | 深度增强 | Roslyn 语义 DataContext/Binding/Command、资源快照、明确 unresolved outcome |
| `Storage` | 附加式演进至 v14 | `edge_evidence`、producer 投影、native snapshot、gRPC baseline、read version |
| `Watcher` | 扩展 | 从 `.cs` 扩展到 XAML/proto/native 输入，并统一删除和隐私语义 |
| `Server` | 保留唯一宿主 | 新增 interop/gRPC/WPF 工具、完整性状态、native coordinator 和 execution profile |
| `Tests` | 大量保留 | 新增医疗跨层 fixture、Clang/worker/ABI/proto/gRPC/WPF/隐私/原子性合同 |
| `Embeddings` | 可选保留 | 本地 ONNX/sqlite-vec；隐式模型下载关闭 |
| Tree-sitter/TypeScript | 保留 | 不参与医疗跨域链的权威 absence 判定 |

## 3. 新依赖的选择

| 需求 | 采用 | 复用边界 |
|---|---|---|
| C# 语义 | Roslyn Workspaces 5.3.0 | 只在 Indexing 层使用；MCP 不暴露 Roslyn 类型 |
| XAML | 上游 XML/XAML parser + Roslyn semantic resolver | parser 负责 markup，唯一类型/成员解析由 Roslyn 证明 |
| protobuf | Grpc.Tools/protoc 2.82.0 + Google.Protobuf 3.35.1 | 固定编译器生成 descriptor/source info；不手写 grammar |
| C/C++ | ClangSharp 21.1.8.4 + libclang 21.1.8 | 运行在独立 worker；只返回有界归一化事实 |
| PE exports | 内置只读 `BinaryExportVerifier` | 只核验配置 artifact/target，不把源码 `dllexport` 等同于二进制导出 |
| 图与搜索 | Microsoft.Data.Sqlite + Dapper + SQLite/FTS5 | 每 scope 单库；派生事实不另建数据库 |
| 可选向量 | ONNX Runtime + Microsoft.ML.Tokenizers + sqlite-vec | 默认无需下载；不影响结构查询 |
| MCP | ModelContextProtocol 1.2.0 | stdio、结构化输出、只读/idempotent annotation |

Native analyzer 没有采用名称含糊的外部 `CodeGraph`。Clang 已能提供目标感知的 AST、
layout、USR 和 direct call；再引入 tree-sitter C++ 图会带来第二事实来源和可信度冲突，
当前没有收益。

## 4. 关键复用决策

### 4.1 在现有 SQLite 上增加证据，而非重写

上游 `edges(src,dst,kind)` 适合作为逻辑边，却不足以保存重复调用点。实现保留该表的
查询兼容性，新增一对多 `edge_evidence`，再补 producer-owned 原子替换 API。这样既
复用了 FTS5、scope 和现有查询，也支持逐 hop 证据、增量删除和 last-good 投影。

### 4.2 统一 canonical key，而非包装第三方模型

Roslyn symbol、protobuf descriptor 和 Clang cursor/USR 只在 adapter 内出现。跨语言
linker 使用稳定的 `csharp:`、`xaml:`、`proto:`、`c:`、`cpp:` canonical key 与内部
payload codec。这避免 SDK/MCP 被某一 Roslyn、Clang 或 protobuf 版本锁死。

### 4.3 派生 linker，而非在语言分析器里猜跨域边

- XAML analyzer 只在唯一语义上下文下链接 C# property；
- protobuf analyzer 只发布 descriptor 合同；
- Roslyn analyzer 只发布 generated-code 和调用事实；
- gRPC linker 在完整快照上联合验证后发布 client/server/RPC 边；
- managed/native extractor 分别发布事实，Interop matcher 在目标和 export universe
  完整时链接。

这种分工使某个输入不完整时可以保留 last-good，同时禁止从局部失败推导全局
“无调用/无实现/无风险”。

### 4.4 进程外 Native adapter，而非主进程加载 libclang

ClangSharp 被复用为 AST/ABI 权威输入，但通过固定 JSON 协议的 child process 使用。
worker 输入输出、stderr、集合、深度和时间均有上限；coordinator 还做用户信任授权、
物理路径边界、两次解析和 content hash 绑定。此设计复用了官方 LLVM 语义，又缩小了
崩溃和恶意输入对 MCP host 的影响面。

### 4.5 保留 MCP 宿主并增加稳定别名

上游 graph/scope/history/health 工具继续存在。MedInteropLens 增加或固定了：

- 查询：`search_code`、`find_symbol`、`trace_call`、`impact_analysis`；
- Interop：`match_pinvoke`、`compare_struct`、`analyze_native_boundary`；
- gRPC：`trace_rpc`、`check_proto_contract`；
- WPF：`trace_binding`、`trace_command`、`check_resources`。

原有 Phase 1 兼容入口 `find_definition`、`find_reference`、`find_callers`、
`find_callees`、`trace_call_path` 仍保留。所有新增领域工具都返回 bounded structured
content，并标明 read-only/idempotent。

## 5. 没有直接复用的方案

### `roslyn-codelens-mcp`

Phase 0 将其视为算法参考。当前事件/调用/影响功能已经落入唯一 Roslyn/SQLite 图，
因此没有复制该项目的 server、存储或公开 DTO。这样避免两个索引的新鲜度、证据和
工具名发生分叉。

### 外部 CodeGraph

工作计划没有固定 owner/repo/version，且 tree-sitter 无法权威判断 ABI、export、
overload、function pointer 或 target layout。当前不作为运行时依赖；未来只有在基准
证明增益、固定许可证/SHA 且结果明确标为 `Inferred` 时，才可能作为进程外候选源。

### 手写 proto parser

已明确拒绝。descriptor 是 service/method/message/field/streaming/source-info 的唯一
输入；没有 source info 就不制造精确行列。

### 第二套数据库或 MCP

已明确拒绝。所有 analyzer 进入同一 producer-aware store；所有用户入口由同一 MCP
host、scope router、输出预算和隐私策略管理。

## 6. 供应链与维护方式

仓库保留 MIT LICENSE、中央包版本和每个项目的 `packages.lock.json`。Phase 0 发现的
`SQLitePCLRaw.lib.e_sqlite3 2.1.11` 与
`System.Security.Cryptography.Xml 10.0.7` 告警已经通过兼容的
`SQLitePCLRaw.bundle_e_sqlite3 3.0.4` 和
`System.Security.Cryptography.Xml 10.0.10` 等集中升级处理，而不是关闭 NuGetAudit。

正常维护要求：

1. 上游同步和产品改动分开提交；
2. package/runtime 版本集中更新并重生成 lock files；
3. 执行 locked restore、Release build/test 和 transitive vulnerability scan；
4. ClangSharp/libclang 保持同一 LLVM release line；
5. Grpc.Tools 的 protoc 与 descriptor runtime 保持已测试组合；
6. schema、SDK wire contract、MCP tool shape 发生变化时增加迁移/快照/兼容测试。

## 7. 当前复用限制

- `MSBuildWorkspace` 的执行模型无法仅靠 solution 清理变成安全沙箱；只应打开可信工程。
- 插件仍是 host 内代码执行，`AssemblyLoadContext` 只解决依赖冲突。
- libclang worker 是进程边界，不保证容器/低完整性 token/seccomp 等 OS 沙箱。
- ABI、PE export 和 native direct-call 的结论只对配置的 target、编译参数和 artifact
  有效。
- WPF/protobuf/gRPC 语义只对静态可证明形状负责，不替代 runtime tracing。

这些不是重新造轮子的理由，而是部署和风险披露边界；详见 `risk.md`。

## 8. Phase 0 决策历史

2026-07-23 初始工作区为空。Phase 0 对固定上游进行原样测试：旧依赖在启用
warning-as-error 的 NuGet audit 下因 NU1903 阻断；仅诊断性关闭审计后，上游测试主体
通过 806、跳过 2。由此得出的结论是“高价值骨架，但不能原样作为医疗分析底座”。

后续实施已经完成当时列出的主要适配项：供应链升级、隐私强制重建、逐边证据、
XAML 语义/增量、`proto/c/cpp` key、protoc/Clang adapter、完整链和风险规则。因此，
旧文中的“当前工作区没有可复用源码”“Native/proto 后续阶段禁止实现”仅记录了当时
的阶段门禁，不描述当前仓库。
