# MedInteropLens 复用分析（Phase 0）

> 本文区分“本地已有”和“上游候选”。当前工作区没有任何可复用源码。  
> 候选评估固定在 2026-07-23 可取得的公开提交，避免把移动的 `main` 当作可重复基线。

## 1. 结论

推荐以
[`Jak3b0/DevBitsLab.Mcp.SourceGraph@6b32a8b`](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/tree/6b32a8b9b353c30322889d5ab644c2d19bca779a)
作为首选基础，但必须先解决供应链告警并建立本地 Git 基线。
它是 2026 年创建的 pre-1.0 项目（评估基线 v0.8.0），应视作“高价值骨架”，
不应仅凭 README 或早期 CI 信号视作生产级医疗底座。

可重复性验证结果：

- 正常 `dotnet test -c Release` 在 restore 阶段被 High 级 NU1903 阻断；
- 仅为诊断而设置 `-p:NuGetAudit=false` 后，IntegrationTests 通过 22/22，
  单元测试通过 784、跳过 2、失败 0；
- 因此实现本身具有较高复用价值，但安全基线当前不合格。

复用策略：

- **直接复用**：Roslyn solution 加载、C# 符号/引用、SQLite/FTS5、MCP hosting、
  scope 路由、现有测试基础。
- **适配后复用**：XAML、增量 watcher、图模型、查询返回 DTO、隐私排除。
- **仅作算法参考**：`roslyn-codelens-mcp` 的深层 C# 分析。
- **后续阶段再引入**：ClangSharp、protoc/Protobuf、选定的 C/C++ CodeGraph。
- **不复用**：通用 AI 聊天、完整 IDE、另一套向量数据库。

## 2. 主候选逐模块评估

| 模块 | 证据 | 复用判断 | 必须修改/验证 |
|---|---|---|---|
| `Core` | `Symbol`、`Edge`、`Reference` 等记录 | 适配后复用 | 增加 MedInteropLens 稳定领域模型；补 Language、Project、Evidence、Confidence、Location |
| `Indexing` | Roslyn + `MSBuildWorkspace`，C# symbol/ref/call/inherit/implement | 直接复用为基础 | 用医疗 TestAssets 验证重载、接口调用、属性读写和旧式 WPF 工程 |
| `Indexing.Xaml` | `x:Class`、Binding、资源、事件及跨语言边 | 适配后复用 | Binding 目标目前常为 placeholder；Command/ViewModel 解析不足；需证据和增量删除测试 |
| `Storage` | SQLite schema v11、FTS5 trigram、可选 sqlite-vec、scope DB | 直接复用并附加扩展 | 不重写；增加最小证据存储，验证 schema 重建和查询兼容 |
| `Watcher` | `.cs` + Git HEAD 的 200 ms debounce | 适配后复用 | 当前监听器明确只看 `.cs`；补 `.xaml`、新增/改名/删除和隐私排除一致性 |
| `Server` | MCP stdio、结构化输出、scope fan-out、读只 SQL view | 直接复用为宿主 | 增加精确工具别名和 `trace_call_path`；所有结果接入证据门禁 |
| `Sdk` | `ILanguageIndexer`、`IndexEvent`、插件工具 | 适配后复用 | canonical scheme 白名单当前不接受 `proto/c/cpp`；需先扩展合同并避免暴露第三方 AST |
| `Tests` | 大量单元/集成测试、SampleWpf、漂移恢复 | 直接复用并扩展 | 新建 MedInteropLens TestAssets；覆盖完整证据、增量 XAML、文件删除 |
| `Embeddings` | ONNX + sqlite-vec，可关闭 | Phase 1 默认关闭 | 结构查询不依赖向量；模型下载约 640 MB，医疗离线环境需显式启用 |
| TypeScript/JS | tree-sitter 索引 | 保留但不扩展 | 不属于当前医疗链路的 Phase 1 验收范围 |

核心代码证据：

- [`Models.cs`](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/blob/6b32a8b9b353c30322889d5ab644c2d19bca779a/src/DevBitsLab.Mcp.SourceGraph.Core/Models.cs)
- [`RoslynIndexer.cs`](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/blob/6b32a8b9b353c30322889d5ab644c2d19bca779a/src/DevBitsLab.Mcp.SourceGraph.Indexing/RoslynIndexer.cs)
- [`XamlLanguageIndexer.cs`](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/blob/6b32a8b9b353c30322889d5ab644c2d19bca779a/src/DevBitsLab.Mcp.SourceGraph.Indexing.Xaml/XamlLanguageIndexer.cs)
- [`SolutionWatcher.cs`](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/blob/6b32a8b9b353c30322889d5ab644c2d19bca779a/src/DevBitsLab.Mcp.SourceGraph.Watcher/SolutionWatcher.cs)
- [`GraphTools.cs`](https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/blob/6b32a8b9b353c30322889d5ab644c2d19bca779a/src/DevBitsLab.Mcp.SourceGraph.Server/Tools/GraphTools.cs)

## 3. Phase 1 MCP 能力映射

对固定提交源码中的 `[McpServerTool]` 注册点进行计数，共有 33 个内置工具，分布在
graph、scope、history、embeddings 和 health/operations 类别；另有
symbol/file/namespace/help resource templates。它已经是可复用的 MCP 宿主和查询
层，但工具数量不等于满足 MedInteropLens 的证据合同。

| 工作计划要求 | 上游现有工具 | 状态 | 决策 |
|---|---|---|---|
| `find_definition` | `find_definition` | 已有 | 保留名称，返回值补统一证据 |
| `find_reference` | `find_references` | 部分已有 | 提供兼容别名或统一为工作计划名称；保留逐位置结果 |
| `find_callers` | `list_callers` | 部分已有 | 提供要求名称；为每条 caller 边附调用点 |
| `find_callees` | `list_callees` | 部分已有 | 提供要求名称；为每条 callee 边附调用点 |
| `trace_call_path` | 无对应 curated tool | 缺失 | 在图存储上实现有深度/节点上限和环检测的路径查询；逐 hop 返回证据 |
| `impact_analysis` | `impact_of_change` | 部分已有 | 提供要求名称；传递结果必须说明路径、深度、边类型和最低可信度 |

上游额外的 `find_data_bindings`、`find_event_handlers`、`graph_stats`、
`verify_scope`、`reconcile_drift` 等可以保留，不应替代工作计划的六个稳定入口。

## 4. WPF 能力差距

候选 XAML 索引器已经发出：

- 符号：`xaml-view`、`xaml-element`、`xaml-resource`、`xaml-style`、
  `xaml-template`
- 边：`code-behind`、`binds-path`、`binds-element`、`handles-event`、
  `uses-resource`、`instantiates-type`、`merges`、`applies-style`

但不能直接判定为满足 Phase 1：

1. 普通 `{Binding SaveCommand}` 的目标依赖 DataContext；当前实现会生成
   placeholder，而非可靠解析到 ViewModel 属性。
2. routed event 识别带有启发式：PascalCase 属性名 + bare identifier；
   需要 `Inferred` 标识，不能冒充语义解析。
3. ViewModel/DataContext 关联不是完整的静态语义关系。
4. 实时 watcher 只监听 `.cs`，XAML 冷索引存在不等于 XAML 增量索引可靠。
5. `EdgeEmitted` 没有统一 source span，无法满足逐边证据要求。

因此 XAML 代码应复用解析和 canonical key 设计，但查询层必须区分
`Exact`、`Semantic`、`Inferred`。

## 5. C# 增强参考

[`MarcelRoozekrans/roslyn-codelens-mcp`](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp)
同样采用 MIT 许可证。Phase 0 固定参考提交为
[`f635aec45d04117eaf8c7325518dea3a8b4dc810`](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/tree/f635aec45d04117eaf8c7325518dea3a8b4dc810)。

可参考但本阶段不复制整套服务器的能力：

- 调用点级 `find_callers` / `find_references`
- 深度受限、带环检测的 call graph
- 事件订阅与取消订阅
- DI 注册图
- 异常传播
- 变更影响分析
- solution trust model

采用“参考算法/测试形状，落到唯一统一图”比并行运行第二个 MCP
服务器更容易保持证据、索引新鲜度和工具契约一致。

## 6. 后续依赖边界

| 技术 | 预期职责 | 复用方式 | 当前决定 |
|---|---|---|---|
| Roslyn | C# 语义、调用、继承、引用 | 使用主候选现有实现 | Phase 1 |
| XML/XAML parser | WPF markup | 使用主候选现有实现并增强 | Phase 1 |
| SQLite/FTS5 | 图事实和文本检索 | 使用主候选现有实现 | Phase 1 |
| MCP .NET SDK | 对外工具协议 | 使用主候选现有 hosting | Phase 1 |
| ClangSharp | C/C++ AST、导出、struct/enum | 作为后续 language analyzer | Phase 2+，本阶段禁止实现 |
| protoc + Protobuf Reflection | `.proto` 解析与描述符 | 锁版本生成 descriptor set，禁止手写 parser | Phase 4，当前不引入 |
| CodeGraph | C/C++ 基础调用图/文件关系 | 必须先明确具体仓库、提交、许可证和 Windows 能力 | 当前名称有歧义，不得盲目依赖 |

### 6.1 CodeGraph 边界

工作计划的 `CodeGraph` 没有 owner/repo，无法作为可重复依赖。当前最匹配的候选是
MIT 项目
[`colbymchenry/codegraph`](https://github.com/colbymchenry/codegraph)：
tree-sitter 建图、SQLite/FTS5、本地 CLI/MCP，并提供 Windows 构建。

若后续基准证明它有增益，只允许：

- 作为进程外 adapter 提供 C/C++ 候选 symbol/call edge；
- 把 tree-sitter 关系标记为 `Inferred`；
- 默认通过 `CODEGRAPH_TELEMETRY=0` 或 `DO_NOT_TRACK=1` 关闭可选遥测；
- 固定 release、SHA 和构建来源。

不允许：

- 与主候选的 SQLite schema 合库；
- 运行第二套面向用户的 MCP；
- 用 tree-sitter 调用边判定函数指针、虚调用或 ABI；
- 运行时通过 `curl | shell` 安装。

### 6.2 Native 与 proto 的权威输入

后续 Native analyzer 首选官方 MIT 项目
[`dotnet/ClangSharp`](https://github.com/dotnet/ClangSharp)，并配套
Apache-2.0 WITH LLVM-exception 的 libclang 原生运行时。它必须读取真实
`compile_commands.json` 或等价的 MSVC include/define/std/target/arch/pack
参数；没有编译参数时只能返回 `Unknown/Inferred`。源码中的
`extern "C"`/`__declspec(dllexport)` 也不等于最终 DLL 导出，`Exact` 还需核对
指定架构与配置的 PE export table。

proto 使用固定版本 `protoc` 生成 descriptor set：

```text
protoc -I <root> --descriptor_set_out=<tmp.pb> \
  --include_imports --include_source_info <files...>
```

再由 `Google.Protobuf.Reflection`/descriptor proto 遍历 service、method、
message、field、enum。没有 `SourceCodeInfo` 时不得制造行号。protobuf 采用
BSD-3-Clause；protoc 和 C# runtime 必须按官方兼容矩阵锁定同一版本族。

## 7. 引入建议

由于 MedInteropLens 需要修改统一模型、证据和隐私入口，建议使用可追踪的
上游 fork，而不是只消费已发布的全局工具：

1. 在有效 Git 仓库中引入固定提交并保留 MIT LICENSE/NOTICE。
2. 记录 `upstream` remote 和基线 SHA。
3. 先修复依赖审计、原样 build/test，再开始产品差异修改。
4. 每个上游同步单独提交，并在同步后运行全部基线与 MedInteropLens 测试。
5. 不复制 `roslyn-codelens-mcp` 的完整宿主；只在许可证归属清晰时移植必要算法。
6. 不可信 `.sln`、MSBuild、analyzer/source generator、plugin 和 native parser
   不在主 MCP 进程以当前用户权限直接执行。
