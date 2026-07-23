# MedInteropLens 风险登记（Phase 0）

> 分级：P0 = 阻断进入下一阶段；P1 = Phase 1 发布前必须关闭；P2 = 后续阶段门禁。  
> “已发现”只表示有代码或环境证据，不表示问题已经修复。

## 1. 风险总览

| ID | 级别 | 风险 | 当前证据 | 缓解与关闭条件 |
|---|---|---|---|---|
| R-001 | P0 | 本地没有仓库基线 | 评估开始时为空；当前仅有四份 Phase 0 文档且不是 Git 仓库，没有项目源码 | 建立有效 Git；引入经批准的固定上游提交；记录许可证和 remote |
| R-002 | P0 | 推荐上游当前无法通过依赖审计/restore | 对 `6b32a8b` 执行 `dotnet test -c Release`，restore 因 NU1903 高危告警失败，测试未启动 | 不得通过关闭审计作为验收；升级或替换受影响依赖，确保 restore/build/test 和 vulnerability scan 全部通过 |
| R-003 | P1 | 图边不能满足“证据优先” | 上游 `Edge`/`edges` 无文件、行列、Evidence、Confidence | 添加逐边证据模型；六个 MCP 查询对每个 hop 输出位置和可信度；无证据不输出确定关系 |
| R-004 | P1 | 医疗隐私默认排除不完整 | 上游各扫描入口的硬编码排除集合与工作计划不一致 | 建立单一、默认启用、大小写不敏感的排除策略；冷索引、watcher、漂移修复共享；用诱饵 PatientData/DICOM 测试 |
| R-005 | P1 | XAML 索引可能陈旧 | 上游 `SolutionWatcher` 只监听 `*.cs`；非 C# 主要在冷索引分发 | 监听 `.xaml` 的新增/修改/改名/删除；测试等待 watcher 后图一致 |
| R-006 | P1 | Binding/Command/ViewModel 关系可能误报或漏报 | Binding 目标可成为 placeholder；事件识别含启发式 | 为 unresolved/inferred 明确降级；优先用 `x:DataType`、DataContext、Roslyn 符号做语义解析；建立正反样例 |
| R-007 | P1 | 缺少 `trace_call_path` 稳定工具 | 上游只有 caller/callee/impact，无目标工具 | 实现受限遍历、环检测、最大深度/节点数和逐 hop 证据测试 |
| R-008 | P1 | 工具名称与合同不一致 | `find_references`、`list_callers`、`list_callees`、`impact_of_change` 与计划名称不同 | 增加兼容入口并冻结 MedInteropLens MCP schema；保留上游工具避免破坏 |
| R-009 | P1 | .NET 10-only 与医疗遗留工程兼容性未知 | 上游目标 `net10.0`；`vswhere` 未发现 Visual Studio 实例，`cl/devenv` 不可调用；真实项目可能含 .NET Framework/旧 WPF | 用代表性遗留 solution 做 Spike；记录跳过/失败项目；必要时安装 Build Tools，而不是假定可加载 |
| R-010 | P1 | 派生索引升级会整体重建 | 上游 schema 低版本会 drop/rebuild | 数据仍以源码为真；测量大型仓库重建时间、磁盘和查询不可用窗口；保留失败恢复 |
| R-011 | P1 | 读只 SQL 逃生口可能扩大代码暴露 | `query_graph` 可读取全部可查询视图 | 保持 stdio/local-only；scope 隔离不视作安全边界；增加路径授权、结果大小限制和审计日志 |
| R-012 | P2 | C/C++/proto 工具链缺失 | 本机无 Clang、CMake、Ninja、protoc、MSVC | 在进入对应阶段前锁版本安装；`Grpc.Tools` 可避免全局 protoc，但 Native fixture 仍需编译器 |
| R-013 | P2 | `CodeGraph` 名称有多个不相干项目 | 工作计划未给 owner/URL/commit | 技术选型前写 ADR，固定仓库、提交、许可证、Windows 支持和输出证据能力 |
| R-014 | P2 | 第三方 AST/DTO 泄漏会锁死架构 | 长期需同时接 Roslyn、protoc、Clang | MCP 和规则层只依赖 MedInteropLens 统一模型；第三方对象止于 analyzer adapter |
| R-015 | P2 | 完整跨语言链的错误会累积 | 每个推断 hop 都可能降低可信度 | 路径置信度取最弱 hop；明确 exact/semantic/inferred；允许“未找到可靠路径”而不是补猜测 |
| R-016 | P1 | 分析不可信 solution/plugin 可执行任意代码 | MSBuild、Roslyn analyzer/source generator 和 host 内插件都可能执行代码；上游安全策略明确列出该风险 | 可信仓库/插件白名单、非特权账户、默认禁第三方执行；不可信分析放隔离子进程并设超时/资源上限 |
| R-017 | P2 | SDK 当前拒绝 proto/native canonical key | validator 只允许 `csharp/xaml/js/ts/jsx/tsx` | 进入对应阶段前扩展 `proto/c/cpp` scheme，写跨平台稳定性和冲突测试；不能假定外部插件已足够 |
| R-018 | P1 | 上游仍是 pre-1.0 早期项目 | 评估基线 v0.8.0，API/schema 仍可能快速变化 | 固定 commit 后 fork；上游同步单独评审；MedInteropLens 自有稳定 DTO/测试隔离变化 |

## 2. 供应链阻断详情

在临时浅克隆的固定上游提交上执行：

```powershell
dotnet test DevBitsLab.Mcp.SourceGraph.slnx --configuration Release --nologo
```

restore 因仓库启用 `TreatWarningsAsErrors` 而被 NU1903 阻断，至少包括：

- `SQLitePCLRaw.lib.e_sqlite3 2.1.11`：
  [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)
- `System.Security.Cryptography.Xml 10.0.7`：
  [GHSA-23rf-6693-g89p](https://github.com/advisories/GHSA-23rf-6693-g89p)、
  [GHSA-8q5v-6pqq-x66h](https://github.com/advisories/GHSA-8q5v-6pqq-x66h)、
  [GHSA-cvvh-rhrc-wg4q](https://github.com/advisories/GHSA-cvvh-rhrc-wg4q)、
  [GHSA-g8r8-53c2-pm3f](https://github.com/advisories/GHSA-g8r8-53c2-pm3f)、
  [GHSA-mmjf-rqrv-855v](https://github.com/advisories/GHSA-mmjf-rqrv-855v)

这不是 MedInteropLens 本地项目的构建失败，因为本地还没有项目；它是“推荐上游
在当前日期和 NuGet 审计数据下不能作为未修改绿色基线”的证据。

为分离“代码/测试失败”与“供应链门禁失败”，另做了一次明确标注的诊断运行：

```powershell
dotnet test DevBitsLab.Mcp.SourceGraph.slnx --configuration Release `
  --nologo -p:NuGetAudit=false
```

该运行编译成功，IntegrationTests 通过 22/22，单元测试通过 784、跳过 2、失败 0。
这说明固定提交的测试主体在本机可运行，但因为命令关闭了安全审计，不能算作绿色
基线，也不能关闭 R-002。

以下做法不能关闭 R-002：

- 永久关闭 NuGetAudit；
- 将 NU1903 从 warning-as-error 中豁免；
- 只记录“上游 CI 曾通过”；
- 未扫描 transitive dependencies 就发布。

关闭 R-002 的验收命令至少包括：

```powershell
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet list package --vulnerable --include-transitive
```

验收结果必须记录 SDK、操作系统、提交 SHA、包锁文件和命令退出码。
漏洞查询命令即使列出漏洞也可能退出 0，因此 CI 还必须解析其 JSON/结构化输出并
显式断言没有 High/Critical 项，不能只检查退出码。

## 3. 正确性与可信度风险

MedInteropLens 面向医疗设备软件，静态分析的“不确定”必须成为数据，而不是被文字
掩盖：

| 可信度 | 允许来源 | 示例 |
|---|---|---|
| `Exact` | 语法/描述符能唯一确定，且有精确 source span | XAML `x:Class`；proto field number；显式 `DllImport.EntryPoint` |
| `Semantic` | 编译器/类型系统解析到唯一符号 | Roslyn resolved invocation；接口实现 |
| `Inferred` | 规则或命名启发式 | 未知 DataContext 的 Binding；疑似 routed event |

规则：

1. 每条返回边都带 producer、证据位置和可信度。
2. 传递路径的整体可信度不得高于最弱 hop。
3. placeholder 不得伪装成真实 ViewModel/C++ 符号。
4. ambiguous symbol 必须返回候选集或错误，不能任选一个。
5. 删除/改名后旧边必须被清理，避免“幽灵路径”。

## 4. 隐私与本地运行风险

scope 的 `isolated` 只是查询 fan-out 提示，不是安全边界。Phase 1 必须把以下条件作为
测试事实：

- 默认不遍历患者数据、DICOM、医学图片、日志和数据库目录；
- 不把文件内容用于遥测；
- embeddings 默认关闭或离线，下载模型不等于允许上传代码；
- MCP 输出仅包含回答查询所需的最小证据片段；
- 日志不记录完整源码、患者标识或未脱敏绝对路径；
- 用户显式 include 也不能绕过硬性医疗数据扩展名策略，除非有单独高风险开关和审计。

此外，上游的 `AssemblyLoadContext` 只隔离依赖，不是安全沙箱。加载工程前必须区分
“受信任内部仓库”和“不受信任输入”；后一类不能在主 MCP 进程中执行 MSBuild、
analyzer、source generator 或插件。Native/protoc 子进程同样需要固定二进制、
hash 校验、超时、stdout/stderr 上限和最小权限。

## 5. Phase 0 风险结论

当前没有可安全进入功能开发的绿色仓库基线。下一阶段第一项工作不是 Clang、Interop
或更多 MCP 工具，而是依次关闭 R-001 和 R-002；随后用测试驱动关闭 R-003 至
R-008。R-012 及之后风险在对应阶段开始前重新评估。
