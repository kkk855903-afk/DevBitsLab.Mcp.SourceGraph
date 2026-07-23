# MedInteropLens 实施计划（Phase 0 输出）

> 本计划从空工作区出发。它是下一阶段的可执行顺序，不表示这些功能已完成。  
> Phase 0 本身只交付评估文档，不引入 ClangSharp、Interop 规则，不重写数据库。

## 1. Phase 0 完成标准

- [x] 检查当前项目结构：评估开始时为空；除本次四份文档外无项目源码。
- [x] 检查 Git：不是有效仓库。
- [x] 检查本地编译/测试：无工程，无法执行。
- [x] 检查 MCP/数据库：本地均不存在。
- [x] 定位推荐上游的真实公开仓库并固定提交。
- [x] 审阅上游 C#、XAML、MCP、SQLite、增量索引和测试结构。
- [x] 实际执行上游 `dotnet test`，记录 restore 阻断。
- [x] 诊断性关闭 NuGetAudit 后验证测试主体：通过 806、跳过 2、失败 0；
  该结果不作为安全验收。
- [x] 输出 `architecture.md`、`reuse-analysis.md`、`risk.md`、
  `implementation-plan.md`。

## 2. 下一阶段入口门禁

以下门禁按顺序执行，任何一项失败都不进入功能开发：

### Gate A：建立可追踪仓库

1. 初始化 MedInteropLens Git 仓库。
2. 将
   `Jak3b0/DevBitsLab.Mcp.SourceGraph@6b32a8b9b353c30322889d5ab644c2d19bca779a`
   作为上游 fork 基线。
3. 保留 MIT LICENSE 和第三方 notices。
4. 配置 `origin`（MedInteropLens）与 `upstream`（候选上游）。
5. 提交纯上游基线，不混入产品修改。

验收：工作树干净；提交 SHA、remote 和许可证可查询。

### Gate B：供应链和原样基线

1. 更新/替换受 NU1903 影响的直接与传递依赖。
2. 生成并提交 NuGet lock files。
3. 升级本机受影响的 .NET 10 runtime/SDK patch。
4. 在 Windows 执行 restore、Release build、全部 tests 和 vulnerability scan。
5. 不以关闭 NuGetAudit 作为解决方案。
6. 明确受信任 solution/plugin 白名单；默认不执行未审查的 analyzer、source
   generator 或插件。

验收：restore/build/test 退出 0；解析 vulnerability scan 的结构化输出并确认没有
High/Critical 漏洞（不能只依赖该命令的退出码）。

### Gate C：范围冻结

只批准 Phase 1：

- C# 类/接口/方法/属性/字段；
- 调用、引用、继承；
- WPF `x:Class`、Binding、Command、Click、Resource、ViewModel 关联；
- 六个 MCP 查询；
- 增量索引和文件删除；
- 医疗隐私默认排除；
- 每个结果的代码证据。

Native、ABI、Interop、proto/gRPC 和完整跨层链继续保持 out of scope。

## 3. Phase 1 增量

每个增量都必须完成“设计 → 实现 → 测试 → Release 编译 → 结果记录”，不合并
未验证的多个增量。

### Increment 1：TestAssets 与基线契约

新增最小 `TestAssets/ManagedApp`：

- WPF View：Button、`Command`、`Click`、Binding、Resource；
- ViewModel：命令和属性；
- Service：接口 + 实现；
- 明确的多跳方法调用、继承和引用；
- 一个无效 Binding/Command 反例；
- 文件改名、修改、删除用的可复制 fixture。

先写黑盒验收测试，固定每个查询的 JSON schema、文件路径和 1-based 行号语义。

完成条件：

- 上游原有测试保持全绿，不得回归；
- TestAssets 能在 Windows Release 编译；
- 测试中不包含真实患者或医学数据。

### Increment 2：统一领域模型与证据

新增 MedInteropLens 自有模型：

- `Symbol`
- `Edge`
- `SourceLocation`
- `Evidence`
- `Confidence`

对现有 SQLite 做附加式演进：

- 保留 `symbols`、`refs`、`edges`；
- 为一条逻辑边保存一到多条 source evidence；
- 对 language/project 做最小持久化或确定性映射；
- schema 升级有重建/兼容测试。

完成条件：

- C# call/reference/inheritance 每条结果有证据；
- XAML exact/inferred 能区分；
- 无证据边不会出现在“确定结果”中；
- DTO 不含 Roslyn/XAML parser 类型。

### Increment 3：C# 图可靠性

复用 RoslynIndexer，针对 TestAssets 补齐：

- 类、接口、方法、属性、字段；
- resolved calls；
- read/write/reference；
- inherits/implements；
- 重载和接口派发；
- 新增、修改、改名、删除后的图清理。

完成条件：

- 定义、引用、caller、callee 的 golden tests 通过；
- 相同输入重复索引结果稳定；
- 文件删除后无残留 symbol/ref/edge；
- ambiguous query 不静默选错。

### Increment 4：WPF 关系与增量

在现有 XAML indexer 上做小步增强：

1. 保留 `x:Class`、资源和事件基础。
2. 优先解析 `x:DataType`、显式 DataContext 和可唯一确定的 ViewModel。
3. Command/Binding 无法唯一确定时保留 placeholder，标为 `Inferred` 或
   `unresolved`。
4. watcher 使用 language-indexer 扩展集合，而不是只写死 `*.cs`。
5. `.xaml` 新增、修改、改名、删除均触发一致更新。

完成条件：

- `trace_command` 所需基础边可从 Button 到真实 Command 属性，或明确返回 unresolved；
- 不存在的 Binding/Command 测试不产生伪目标；
- XAML 删除后资源、binding、event 边全部清理；
- 默认隐私排除对冷索引和 watcher 结果一致。

### Increment 5：六个 MCP 查询

在保留上游工具的同时增加 MedInteropLens 稳定入口：

| 工具 | 最小合同 |
|---|---|
| `find_definition` | 唯一定义或明确 ambiguity；包含 symbol + location + confidence |
| `find_reference` | 每个 occurrence 的文件、行列、reference kind |
| `find_callers` | caller symbol + 精确调用点；分页/上限 |
| `find_callees` | callee symbol + 精确调用点；分页/上限 |
| `trace_call_path` | from/to、最大深度、环检测；每 hop 的 edge/evidence/confidence |
| `impact_analysis` | 反向传递路径、深度、关系过滤、整体最低可信度 |

完成条件：

- MCP schema snapshot tests 通过；
- 所有路径查询有深度和节点上限；
- 取消令牌贯穿查询；
- 空结果说明“未发现证据”，不生成推测路径。

### Increment 6：隐私、可靠性与 Phase 1 验收

建立一个所有入口共享的默认排除策略：

```text
Directories:
  bin obj .vs Debug Release Images PatientData Database Logs
Files:
  *.dcm *.jpg *.png
```

增加：

- 大小写与 Windows 路径测试；
- symlink/junction 越界策略；
- source/log 输出最小化；
- 可信工程门禁和不可信输入的隔离进程测试；
- 索引中断恢复；
- SQLite integrity/rebuild；
- 大仓库查询和索引时间基线。

Phase 1 最终验收矩阵：

| 能力 | 正例 | 反例/恢复 |
|---|---|---|
| C# 调用链 | 多跳 caller/callee/path | 重载歧义、循环 |
| Binding | 可解析 ViewModel 属性 | 不存在或未知 DataContext |
| Command/Click | Command 与事件 handler | 不存在的成员 |
| 增量索引 | `.cs`/`.xaml` 修改与新增 | burst/debounce |
| 文件删除 | symbol/ref/edge 全清 | 改名视为删+增 |
| 隐私 | 普通源码被索引 | PatientData/DICOM/图片永不进入图 |
| MCP | 六工具结构化输出 | 取消、limit、空结果 |

## 4. 每个增量的验证命令

仓库建立后，把实际 solution 路径固化到 CI；最低命令集合：

```powershell
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet list package --vulnerable --include-transitive
```

后续 Native 阶段再增加 CMake/CTest/ABI fixture 命令；Phase 1 不提前安装或编写
ClangSharp 代码。

每次结果记录至少包含：

```text
Commit:
Environment:
Scope:
Commands and exit codes:
Tests passed/failed/skipped:
Known limitations:
Evidence links:
```

## 5. 后续阶段顺序

只有 Phase 1 全部通过后才继续：

1. Phase 2：Managed/Native Interop 映射与边界分析。
2. Phase 3：ABI struct/enum/layout 兼容检查。
3. Phase 4：proto/gRPC 与完整 WPF → C# → gRPC → Native → C++ 调用链。
4. 最后扩展风险规则和更广泛 MCP 工具。

任何阶段都沿用统一证据模型、隐私默认值和固定依赖版本，不另建第二套事实数据库。
进入 Phase 2/4 前还必须扩展并测试 `c:`、`cpp:`、`proto:` canonical scheme；
ClangSharp 需要真实编译参数和目标架构，protoc 需要 descriptor source info。两者均
以受限子进程运行，第三方 AST 不越过 analyzer adapter。

## 6. 下一步建议

下一次开发任务应严格命名为：

> **Phase 1 / Gate A-B：引入固定上游基线并关闭供应链阻断**

它只处理仓库建立、许可证、依赖安全、原样 build/test；不要同时加入业务功能。
