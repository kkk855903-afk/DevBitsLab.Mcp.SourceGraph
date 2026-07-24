# MedInteropLens 实施状态与后续门禁

> 状态日期：2026-07-24
> 本文把 Phase 0 的“未来实施计划”更新为当前交付台账。工作计划 Phase 1–4、初始风险规则和 MCP 领域工具已经落地；尚未完成的事项是发布/部署级持续门禁与明确的残余强化，不是“工作区为空”的前置工作。

## 1. 总体状态

| 阶段 | 状态 | 已交付结果 |
|---|---|---|
| Phase 0：评估与基线 | 完成 | 本地 Git、固定 MIT 上游历史、依赖/隐私/架构差距记录 |
| Gate A/B：仓库与供应链 | 完成并持续执行 | 中央版本、lock files、漏洞依赖升级、Release/CI 门禁 |
| Phase 1：C#/WPF 证据图 | 完成 | Roslyn/XAML 语义、增量/删除、逐次证据、六个兼容查询、医疗隐私 |
| Phase 2：Managed/Native | 完成 | managed/native 归一化事实、P/Invoke 匹配、PE export、`Interop001/003–006` |
| Phase 3：ABI | 完成 | managed/native record layout 与 `Interop002`、`compare_struct` |
| Phase 4：gRPC 与完整链 | 完成 | protoc descriptor、gRPC linker/baseline、八跳 execution profile |
| WPF 初始风险集 | 完成 | Binding、Command、Resource、event lifetime、UI thread |
| MCP 领域工具 | 完成 | query、Interop、gRPC、WPF 稳定入口及 read-only/idempotent metadata |
| 0.9 打包流水线 | 已实现 | tool 0.9.0、SDK 2.5.0、runtime graph、安装后 help/tools-list CI smoke |
| 最终发布资格 | 持续门禁 | 每个目标 RID、真实工程规模、OS sandbox 和医疗组织流程仍需发布方验证 |

## 2. 已完成的实施增量

### 2.1 基线与供应链

- [x] 初始化本地 Git，并按可审计小步骤提交。
- [x] 引入固定上游历史并保留 MIT LICENSE。
- [x] 集中管理 NuGet 版本并生成项目 lock files。
- [x] 升级受 NU1903 影响的 SQLite 与 cryptography 依赖，不关闭审计。
- [x] 保留 warning-as-error、CI、CodeQL/依赖治理和发布工作流。
- [x] 将工具版本更新为 0.9.0、SDK 更新为 2.5.0。

### 2.2 统一事实和证据存储

- [x] 扩展 `csharp:`、`xaml:`、`proto:`、`c:`、`cpp:` canonical key。
- [x] 统一开放的 symbol/edge kinds，第三方 AST 不越过 adapter。
- [x] schema v13 将逻辑边和一对多 occurrence evidence 分离。
- [x] 保存 file/range/confidence/producer/payload。
- [x] 实现 file、producer/file、producer/scope、multi-file derived projection 原子替换。
- [x] 实现事务文件删除、orphan native symbol 安全清理和 producer 证据隔离。
- [x] schema v14 增加 insert-only gRPC first-success baseline。
- [x] 增加 graph read version，路径查询能拒绝混合代际的权威结论。

### 2.3 C# 与增量索引

- [x] 类、接口、方法、属性、字段和 generated document。
- [x] 调用、引用、读写、继承、实现、override、实例化等关系。
- [x] 重载/接口解析与 exact canonical-key 查询。
- [x] `ICommand` property → handler 的 `command-executes`。
- [x] 新增、修改、结构 reload、重命名、删除和 drift recovery。
- [x] diagnostics 在完整项目上重新协调，并支持跨文件 WPF 风险清理。

### 2.4 WPF/XAML

- [x] `x:Class`、element、code-behind、event、Binding、Command、resource、merged dictionary。
- [x] 使用 Roslyn compilation 解析 DataContext 和唯一 property/command target。
- [x] missing/ambiguous/unsupported/incomplete 使用结构化 outcome，不制造假目标。
- [x] 项目级 resource snapshot 及增量删除。
- [x] `XAMLBINDING001`、`XAMLCOMMAND001`、`XAMLRESOURCE001`。
- [x] `WPFEVENT001`：可证明的 static-event named-handler 未解绑。
- [x] `WPFTHREAD001`：可证明的后台 inline callback 访问 `DispatcherObject`。
- [x] `trace_binding`、`trace_command`、`check_resources` 及只读/idempotent 标注。

### 2.5 Managed/Native 与 ABI

- [x] 提取 `DllImport`、`LibraryImport`、`StructLayout`、`MarshalAs`、参数方向和 target。
- [x] 提取 managed callback rooting、native-return release usage 和 managed record layout。
- [x] 使用 ClangSharp/libclang 提取 C/C++ function/export/record/enum/type/layout/direct call。
- [x] 使用真实 compiler arguments、include closure、USR 和 target ABI。
- [x] 用户信任门禁、独立 native worker、严格有界协议和进程超时。
- [x] 两次解析与 included-file SHA-256 content binding，防止 TOCTOU/mixed snapshot。
- [x] PE export table 的 architecture/module/entry 核验。
- [x] native snapshot → managed boundary → stale cleanup 的 last-good 原子协调。
- [x] `pinvoke-maps-to` 与 `struct-maps-to`。
- [x] `Interop001`–`Interop006` 及逐 finding 多侧 evidence。
- [x] `match_pinvoke`、`compare_struct`、`analyze_native_boundary`。

### 2.6 protobuf/gRPC

- [x] 使用固定 Grpc.Tools `protoc` 编译 descriptor set，不手写 parser。
- [x] 投影 message、field、service、RPC 和 source info。
- [x] 合同 payload 严格、版本化、大小/数量受限。
- [x] generated client/server 与 proto RPC 的唯一语义链接。
- [x] 原子发布 `grpc-calls`、`implements-rpc` 和 `rpc-dispatches-to`。
- [x] partial refresh 标记、真实 retained-evidence 探测和 last-good 语义。
- [x] RPC 无实现、generated signature mismatch、field number、streaming change 检查。
- [x] first-success baseline，首次观察不报变更。
- [x] `trace_rpc`、`check_proto_contract`。

### 2.7 完整执行链

- [x] `trace_call_path(profile="execution")` 固定跨域关系白名单。
- [x] exact canonical-key selection，不做模糊降级。
- [x] max depth/path/node/scope/evidence/总输出预算和取消支持。
- [x] 查询前后 graph read version 与 runtime-state identity 验证。
- [x] scope/managed/gRPC/native/export/failure/stale 的完整性矩阵。
- [x] 八跳 golden contract：

```text
Button
  → Command property
  → ViewModel handler
  → managed service
  → proto RPC
  → server handler
  → P/Invoke
  → C export
  → C++ algorithm
```

- [x] 单独验证 server handler → RPC 的 `implements-rpc` 审计关系。
- [x] 每个 execution hop 均要求 occurrence evidence；gRPC 派生关系含 managed+proto 证据。

## 3. MCP 稳定入口

| 领域 | 已实现入口 |
|---|---|
| 通用查询 | `search_code`、`find_symbol`、`trace_call`、`impact_analysis` |
| Phase 1 兼容 | `find_definition`、`find_reference`、`find_callers`、`find_callees`、`trace_call_path` |
| Interop | `match_pinvoke`、`compare_struct`、`analyze_native_boundary` |
| gRPC | `trace_rpc`、`check_proto_contract` |
| WPF | `trace_binding`、`trace_command`、`check_resources` |

所有领域查询都以本地持久化事实为输入，不临时把未知代码交给模型分析。工具输出包含
scope status、partial/truncated/omitted、evidence 和 failure，避免把输出预算裁剪伪装成
完整答案。

## 4. 验收资产

### 4.1 `MedInteropChain`

`tests/fixtures/MedInteropChain` 包含：

- ManagedApp 的 XAML Button、Command 和 ViewModel；
- managed service 与 generated-shape gRPC client；
- `.proto`、server handler、P/Invoke 和 managed structs；
- C header、C export、C++ algorithm；
- `Interop001`–`Interop006` 独立负例；
- 八跳 `graph-contract.json` 和 finding golden file。

合同测试在同一 SQLite store 上运行生产 Roslyn、XAML/protobuf dispatcher、gRPC
linker、native snapshot publication、Interop analysis 和 execution query。为了跨平台
可重复，完整链注入 native extraction/export verification；生产 Clang/worker/PE
组件由各自测试验证。

### 4.2 专项 fixture

- `SampleWpfWindows`：真实 Windows WPF 工程合同；
- `SampleWpf` / `SampleAvalonia`：XAML dialect、Binding/resource 增量；
- `ProtoContracts`：descriptor 与合同失败；
- Clang test project：真实 parser、layout、direct call 和 native 风险；
- storage tests：原子替换、producer cleanup、baseline、read version、损坏/漂移；
- integration tests：cold/live scope、MCP handshake 和 installed-tool smoke。

## 5. 当前验证矩阵

| 层 | 必须验证 |
|---|---|
| Build | locked restore；Release build；warning-as-error |
| Unit | canonical key、codec、rule、query budget、privacy/trust、WPF/proto/ABI |
| Storage | success/rollback、并发代际检测、删除、last-good、schema rebuild |
| Component | Roslyn、XAML、protoc、Clang、native worker、PE、gRPC linker |
| End-to-end | 八跳 chain、逐 hop evidence、完整性为 complete、无截断 |
| Package | pack tool/SDK、本地 feed 安装、`--help`、stdio initialize/`tools/list` |
| Security | transitive vulnerability JSON、CodeQL、依赖/runtime hash/版本检查 |
| Platform | Windows/Linux/macOS 支持 RID；Native/WPF 在适用 OS 上专项验证 |

标准命令：

```powershell
dotnet restore DevBitsLab.Mcp.SourceGraph.slnx --locked-mode
dotnet build DevBitsLab.Mcp.SourceGraph.slnx -c Release --no-restore
dotnet test DevBitsLab.Mcp.SourceGraph.slnx -c Release --no-build
dotnet package list --project DevBitsLab.Mcp.SourceGraph.slnx --vulnerable --include-transitive --format json --output-version 1 --no-restore
dotnet pack src/DevBitsLab.Mcp.SourceGraph.Server/DevBitsLab.Mcp.SourceGraph.Server.csproj -c Release --no-restore /p:ContinuousIntegrationBuild=true
dotnet pack src/DevBitsLab.Mcp.SourceGraph.Sdk/DevBitsLab.Mcp.SourceGraph.Sdk.csproj -c Release --no-build
```

仓库级最终回归由交付流程统一运行，避免并行写入 build output 的重复任务互相干扰。

## 6. 发布门禁

功能“已实现”不自动等于“可发布”。0.9 发布候选必须同时满足：

1. 工作树只含预期变更且所有小步提交可追溯；
2. locked restore、Release build、全量 test 全绿；
3. 结构化 transitive vulnerability scan 不报告任何已知漏洞；
4. tool 0.9.0 和 SDK 2.5.0 package metadata、license、symbols/source link 正确；
5. 从本地 package feed 安装的实际 executable 能执行 `--help`；
6. 安装后的进程完成 MCP stdio initialize 和 `tools/list`；
7. 每个宣称支持的 RID 有 runtime asset 检查；Windows 路径额外跑 WPF/native smoke；
8. 清理临时 package、DB、日志和 test artifacts。

## 7. 后续强化 backlog

这些事项不否定工作计划的已完成范围，但应作为下一版本的明确候选：

### 安全隔离

- 将 MSBuild evaluation、project analyzer 和 source generator 放入受限 worker；
- 为 plugin 增加实际执行时的 capability gate，或迁移进程外；
- 为 native/MSBuild worker 增加 Windows job/low-integrity、Linux sandbox/container
  profile、无网络和只读 mount。

### 真实世界兼容

- 增加大型、旧式 csproj、C++/CLI、复杂 WPF DataContext、不同 gRPC generator 的 corpus；
- 加入 Win-x86/Win-x64/ARM64、Linux ABI、不同 pack/toolchain 的真实 artifact matrix；
- 对 function pointer、虚调用、delegate alias 和动态资源只增加明确标注的
  candidate/unknown，不降低当前权威边标准。

### 发布治理

- gRPC baseline 导入/导出与版本签名；
- 可重复的 performance budgets 和大仓库 soak test；
- SBOM/provenance、签名 package 和 release artifact hash；
- PHI/DLP 内容扫描作为可选更强防线，但不得取代硬路径排除。

## 8. Phase 0 历史计划

初始 Phase 0 计划从空工作区出发，要求先建立 Git、修复 NU1903，再只进入 C#/WPF
Phase 1；Native、ABI、proto/gRPC 当时明确 out of scope。该顺序已经按阶段执行，随后
Phase 1 验收通过后继续完成 Phase 2–4。

因此旧的“下一次任务应为 Gate A-B”不再适用。当前下一步应是：

> **完成仓库级最终回归与 0.9 发布门禁；随后按风险优先级强化 MSBuild/plugin 的进程隔离。**
