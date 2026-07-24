# MedInteropLens 风险登记

> 状态日期：2026-07-24
> 本登记区分“已由代码控制”“仍需部署控制”和“产品固有限制”。`Mitigated` 不表示医疗认证或零风险，只表示仓库内已有可验证控制。

## 1. 当前风险总览

| ID | 等级 | 状态 | 风险 | 当前控制 / 仍需动作 |
|---|---:|---|---|---|
| R-001 | P0 | Closed | 初始工作区无 Git/源码/测试基线 | 已建立本地 Git，固定并保留上游历史，按小步提交演进 |
| R-002 | P0 | Mitigated | 旧依赖 NU1903 / 供应链漏洞 | 中央版本、transitive pin、lock files 和 CI 漏洞门禁；每次发布仍需重新扫描 |
| R-003 | P0 | Mitigated | 逻辑边去重导致证据或重复调用点丢失 | schema v13 `edge_evidence` 一对多，producer/file 精确清理和原子替换 |
| R-004 | P0 | Mitigated | 患者数据、DICOM、图片或越界路径进入索引 | 不可覆盖的医疗排除、物理路径/reparse 解析、所有入口共享 policy、schema v12 强制重建 |
| R-005 | P1 | Mitigated | 修改/删除后残留幽灵 symbol/edge/finding | 文件事务删除、producer ownership、结构 reload、漂移修复和删除/重命名测试 |
| R-006 | P0 | Open | 恶意 `.sln`/MSBuild target/analyzer/source generator 在 host 权限下执行 | 仅处理可信工程；隐私 sanitizer 不是 sandbox。未来需把 MSBuild/Roslyn evaluation 移入受限 worker |
| R-007 | P0 | Open | 插件可在 MCP host 内执行任意代码 | `AssemblyLoadContext` 仅隔离依赖；生产部署应禁用未审核插件并使用外部白名单/最小权限 |
| R-008 | P0 | Mitigated | libclang 崩溃、卡死或恶意 native 输入影响 host | 用户信任门禁、独立 worker、固定协议/大小/深度/时间上限；仍建议 OS sandbox 和低权限账户 |
| R-009 | P1 | Open | 错误 target/compile flags/pack 产生错误 ABI 结论 | 所有事实带 `InteropTarget`；缺少或冲突输入时 partial/unknown。部署必须维护真实 compile commands 和 target matrix |
| R-010 | P1 | Mitigated | 把源码 `dllexport` 当成最终 binary export | 可选 PE export verifier 核对 architecture/module/entry；没有完整 artifact universe 就不做权威 absence |
| R-011 | P1 | Mitigated | gRPC 首次观察被误报为“变更”，或失败刷新破坏历史 | v14 insert-only 首次成功 baseline；partial/malformed 输入不更新 baseline、不生成推测 finding |
| R-012 | P1 | Mitigated | WPF Binding/event/thread 规则误报 | 只报告唯一可证形状，unsupported/ambiguous 记录 unknown；仍可能漏掉动态 DataContext、alias 和间接 callback |
| R-013 | P1 | Mitigated | 路径查询混合两个索引代际 | 查询前后比较 SQLite read version 与 runtime-state identity；变化时标记 `query-snapshot` partial，absence 非权威 |
| R-014 | P1 | Mitigated | 失败刷新后 last-good 被误当当前事实 | gRPC/native 显式 `Partial`、`RetainedLastGood`、failure/stale counts；查询完整性门禁禁止权威否定 |
| R-015 | P1 | Mitigated | 大图/恶意查询造成 CPU、内存或 MCP 输出耗尽 | query/scope/depth/node/row/evidence/时间限制及约 50K 输出预算；仍需大仓库性能基线 |
| R-016 | P0 | Open | 用户把分析结果当作医疗合规/安全认证 | 文档和工具必须持续声明：静态证据用于开发辅助，不是临床、法规或医疗设备认证 |
| R-017 | P2 | Open | 单一 E2E fixture 不能代表真实 WPF/Native 工程 | 完整链使用 buildable generated-shape stubs，并为可重复性注入 native extractor/export verifier；真实 WPF/Clang/worker/PE 由独立测试覆盖 |
| R-018 | P1 | Open | 多 RID 原生 runtime/工具打包漂移 | NuGet runtime graph、安装后 `--help`/stdio smoke 和平台 CI；发布前仍需逐 RID 验证 |
| R-019 | P1 | Open | 本地日志/MCP evidence 暴露敏感路径或代码片段 | 本地 stdio、最小 evidence、隐私路径排除和日志测试；使用者仍应保护 `.sourcegraph` DB 与客户端会话 |
| R-020 | P1 | Open | 静态图漏掉反射、动态代理、function pointer 或运行时 dispatch | 结果必须表述为“已找到证据”，完整性状态只覆盖配置的静态分析 universe |

## 2. 证据正确性原则

MedInteropLens 的核心安全约束不是“尽量给答案”，而是区分已证明、推断和未知：

| 可信度 | 允许来源 | 示例 |
|---|---|---|
| `Exact` | 语法/descriptor/binary 能唯一确定且有精确 source span | proto field number、XAML resource declaration、已核验 export |
| `Semantic` | 编译器或类型系统唯一解析 | Roslyn invocation、Binding property、gRPC generated signature |
| `Inferred` | 受控启发式或信息不全 | 明确标注的兼容性未知，不得升级为 absence 证明 |

强制规则：

1. 逻辑边至少有一条 occurrence evidence 才能进入 evidence-backed 路径。
2. 每条 evidence 带 file、1-based range、confidence 和 producer。
3. 跨域 linker 只消费完整上游事实；payload 解码失败使投影 partial。
4. exact canonical key 不允许 fuzzy fallback；名称歧义返回候选/错误。
5. 路径整体可信度不高于最弱 hop。
6. query 被截断、图发生变化或任一 projection 不完整时，空结果不是权威 absence。

这些控制降低 false certainty，但不会消除 false negative；R-012、R-020 因此保持开放
的产品限制。

## 3. 隐私和本地数据风险

硬性排除：

```text
Directories:
  bin obj .vs Debug Release Images PatientData Database Logs
  .git .sourcegraph node_modules
Files:
  *.dcm *.jpg *.jpeg *.png
```

scope 自定义 exclude 只能进一步缩小范围。冷索引、Roslyn sanitizer、XAML/proto
secondary discovery、language dispatcher、watcher、native include closure、删除和
history 都使用同一边界。路径先做 lexical pruning，再解析现有 symlink/junction/
reparse components；无法建立物理身份的非排除路径会失败，而不是被乐观接受。

仍需运维控制：

- `.sourcegraph/scopes/*.db`、usage log 和 MCP 客户端 transcript 可能包含允许索引的
  源文件路径、符号和短 evidence，应按源代码同等级保护；
- 本地运行不等于匿名化；不要把客户端会话或数据库同步到未批准的云端；
- 当前排除是面向已知医疗目录/扩展名的默认防线，不是内容级 PHI/DLP 扫描；
- embedding model 本地运行，但模型下载仍是网络行为，默认必须显式 opt in。

## 4. 执行与隔离风险

### 4.1 MSBuild / Roslyn

`MSBuildWorkspace.OpenSolutionAsync` 在 sanitizer 得到 `Solution` 之前已经评估工程。
恶意 `.targets`、SDK resolver、analyzer 或 source generator 可能执行代码。当前
`ExecutionCapability` 合同虽然包含 `MsBuildEvaluation` 和
`ProjectSourceGenerators`，主 cold-index 路径尚未将整个 evaluation 放入独立 worker。

因此 R-006 是当前最重要的残余安全风险：

- 只对可信内部仓库运行；
- 使用非管理员、无生产凭据、最小网络/文件权限账户；
- CI 对外部 PR 使用隔离 runner；
- 后续将 workspace open、generator 和 analyzer 迁移到受限进程，返回有界事实而非
  Roslyn 对象。

### 4.2 插件

NuGet/path plugin 会在 host 中加载。`AssemblyLoadContext` 防止依赖版本互相污染，
不能阻止文件、网络、进程或环境变量访问。信任文档和 bundle fingerprint 能用于
授权设计，但未审核插件仍不得在医疗代码环境启用。

### 4.3 Native

Native extraction 已移到 worker，且 trust file 必须位于 repository 外并拒绝
reparse-point 绕过。协议限制为 1 MiB request、16 MiB response、64 KiB stderr，
并限制函数、调用、类型深度和集合数量；进程超时不超过十分钟。

剩余风险是 worker 仍以启动它的 OS 用户身份运行。更强部署应增加 job object/
low-integrity token（Windows）、seccomp/container（Linux）、无网络和只读 source
mount。worker 崩溃只会产生 partial/last-good，不应被解释为 native 没有风险。

## 5. ABI 与跨语言语义风险

ABI 结果始终绑定：

- OS / architecture / runtime identifier；
- toolchain ABI；
- pointer width；
- calling convention；
- pack/alignment；
- translation-unit compiler arguments；
- 可用时的实际 binary architecture/export table。

同一源码在 Win-x86、Win-x64、Linux-x64 和 ARM64 上可能得到不同结论。没有真实
编译参数、include closure 或 artifact 时，系统应返回 incomplete/unknown，而不是
用 host 默认值代替目标。

gRPC linker 同样要求 generated container、descriptor field、request/response type、
streaming shape 和 exact proto contract 一致。代码生成器模式超出已识别形状时会漏链，
不会按方法名猜测。

## 6. WPF 规则的已知盲区

当前五类规则刻意偏向高精度：

- 未知/运行时 DataContext、`RelativeSource`、显式 `Source`、复杂 converter 不会被
  猜成唯一 Binding target；
- event lifetime 只处理 source-defined static event + 当前实例 named method；
  lambda、delegate alias、外部 framework event 和间接 removal 是 unknown；
- UI thread 只处理 `Task.Run`、ThreadPool queue 和立即 `new Thread(lambda).Start()`；
  method group、任意 scheduler、Rx 和第三方 dispatcher 不在证明范围；
- 资源结论依赖完整的 project resource snapshot；动态加载字典可能不可见。

因此“没有 WPF finding”只代表未发现符合当前证明模式的风险。

## 7. gRPC 历史基线风险

`grpc_contract_baselines` 保存同一 canonical key 的本地首次完整成功观察，解决了
“首次索引即变更”的错误。但它不是 Git tag、发布版本或组织 API registry：

- 删除再创建同一 key 仍会与首次观察比较，这是有意的历史语义；
- 删除 scope DB 会删除该本地历史；
- 多分支共享 DB 可能把另一分支的首次观察作为 baseline；
- field rename 若 canonical key 改变，不等同于同 key number change。

需要发布级兼容治理时，应另行导入/导出签名化 baseline，而不是修改当前事实表去模拟
版本管理。

## 8. 验证和发布门禁

每次交付至少执行：

```powershell
dotnet restore DevBitsLab.Mcp.SourceGraph.slnx --locked-mode
dotnet build DevBitsLab.Mcp.SourceGraph.slnx -c Release --no-restore
dotnet test DevBitsLab.Mcp.SourceGraph.slnx -c Release --no-build
dotnet list DevBitsLab.Mcp.SourceGraph.slnx package --vulnerable --include-transitive
```

发布还必须 pack 0.9.0 工具和 2.5.0 SDK，从本地 feed 安装工具，执行 `--help` 和真实
stdio `tools/list`。Native/ABI 变更需运行 Clang tests 和至少一个真实目标 artifact；
WPF 变更需保留 Windows WPF fixture；storage 变更需包含旧版本 rebuild、事务失败和
删除测试。

漏洞命令可能在列出漏洞时仍退出 0，CI 必须解析结构化结果，不能只看退出码。测试
通过也不关闭 R-006、R-007、R-016、R-020 这类架构/产品风险。

## 9. Phase 0 历史风险

Phase 0 曾记录“没有可安全进入开发的绿色仓库基线”。当时 R-001 是空工作区，R-002
是固定上游的 NU1903。它们推动了 Git 基线、依赖升级、lock files 和验证门禁，现已
不再阻断功能实现。

历史记录不应被删除，但也不能继续表述成当前状态。当前最高优先级已经从“建立仓库”
转为：隔离不可信 MSBuild/plugin、持续多 RID/供应链验证，以及避免把静态分析结果
误用为医疗认证。
