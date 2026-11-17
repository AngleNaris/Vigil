# CLI 使用与覆盖说明

CLI 入口为发行包内的 `cli\vigil.exe`，不加载 WPF，不自动提权。下面的示例供审阅后使用，不会由帮助命令执行。当前语法以[路由表](<../src/SystemAutorun.Cli/CliArguments.cs>)和命令级帮助为准；工程与真实旅程的验收结果只记录在[验证账本](<../verification.md>)。

## 先查帮助与内置 Skill

```powershell
$cli = 'C:\path\to\package\cli\vigil.exe'
& $cli --help
& $cli help skill
& $cli help task
& $cli task add --help
& $cli help startup self set --json
```

`help skill` / `skill --help`（及 `vigil --help` 末尾摘要）内置了面向 AI Agent 与自动化脚本的操作规范、完整 `autorun.yaml` 结构说明以及 `task add` / `settings set|patch` / `host add|update|upsert` 的完整 JSON DTO 契约。

`help COMMAND` 与 `COMMAND --help` 返回相同内容。命令组列出已注册的子命令；完整命令给出参数、确认要求、输入格式和适用示例。帮助可以省略实际执行时必需的名称、文件、修订和 `--yes`，也不会读取传入的文件/stdin、解析配置根、连接 Agent 或启动后台。未知主题仍返回参数错误，不把尚未实现的入口伪装成可用命令。

`--json` 将帮助放在原有单行信封的 `data.text` 中，版本位于 `data.version`。名称带空格时加引号；需要把以 `--` 开头的值作为名称时，先用 `--` 结束选项解析。

## 连接与只读查询

```powershell
$root = 'C:\path\to\config'
# 只有此显式命令启动后台；既有配置与自动策略仍须事先审阅。
& $cli --root $root daemon start --json
& $cli --root $root task list --json
& $cli --root $root monitor status --task 'Media Server' --json
& $cli --root $root monitor history 'Media Server' --window 24h --buckets 120 --json
& $cli --root $root logs sources --json
& $cli --root $root logs --tail 50 --follow --count 2 --json
& $cli --root $root logs exceptions --level error --search 'timeout' --json
```

实际业务命令要求 root 中已有有效配置；查询不自动启动 Agent。`monitor --task` 是大小写不敏感的精确名称筛选，不是通配符。空结果、不可用来源和缺失观测不代表健康；历史可用率也不是 SLA。

`logs` 默认读取 Agent 控制日志。`--source` 指向已配置业务来源；`--from-start` 和 `--cursor` 用于有界分页，`--export` 只创建新文件并导出当前一页，不能与 follow 混用。`logs exceptions` 则是旧 GUI 的异常聚合快照：先截取有界尾部再筛选，不是全文搜索；来源尝试数与截断标记不能证明所有文件读取成功。

高级历史 JSON 查询使用 `taskName`、`taskIdentity`、带时区的 `from`/`to` 和 `bucketCount`。身份不能用任务名称代替；通常优先使用上面的命名查询，由客户端根据当前配置构造身份。日志的 `--since` 同样必须包含 `Z` 或明确时区偏移。

## JSON 输入示例

`--file PATH` 读取 UTF-8 文件，`--file -` 读取 stdin。JSON 字段按 Core DTO 定义，不能把完整响应信封当作输入。帮助打印的普通模板使用占位路径或文档地址；它们不是经过实际目标验证的配置。

### 添加任务

```json
{"name":"example-app","type":"windows","executable":"C:\\Apps\\Example\\Example.exe","arguments":[],"expectedRunning":false}
```

保存审阅后的内容为自己的输入文件，再取当前修订并提交：

```powershell
$snapshot = & $cli --root $root config show --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $snapshot.ok) { throw '读取配置失败，不提交' }
& $cli --root $root task add --file task-add.json --expected-revision $snapshot.revision --json
```

添加写入配置，不显式启动目标，但已有补启动/自动恢复等策略仍可能影响新任务。`expectedRunning` 仅控制常驻追踪，不是禁用开关。更复杂的任务可提供 `dependsOn`、`startupDelay`、`healthType`、`healthPattern`、`healthHost`、`healthPort`、`healthTimeoutSeconds` 和 `launch`；字段与类型见[添加契约](<../src/SystemAutorun.Core/Configuration/ConfigurationFileEditor.cs#L19-L37>)。

### 只修改策略开关

```powershell
$snapshot = & $cli --root $root config show --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $snapshot.ok) { throw '读取配置失败，不提交' }
'{"autoRecoverFailedTasks":false,"autoStartMissingOnLaunch":false}' |
    & $cli --root $root settings patch --file - --expected-revision $snapshot.revision --json
```

`settings patch` 使用 camelCase，保留未提供的开关及主机、日志来源。`settings set` 使用 snake_case，是完整偏好替换：先从 `settings get --json` 的 `data.preferences` 取完整对象，保留 `remote_hosts`、`log_sources` 和未知设置，再修改所需字段。不要拿空对象或只有一个开关的对象执行 set。

### 主机定义

```json
{"id":"example-host","name":"Example","enabled":false,"platform":"linux","host":"192.0.2.25","port":22,"user_name":"example","authentication":"ssh_agent","known_hosts_file":"C:\\Keys\\known_hosts"}
```

示例默认禁用，地址为文档用途。换成自己的配置并准备可信的本地 known_hosts 文件后再启用。`host add/update/upsert --file JSON --expected-revision SHA256` 都接收完整定义：add 拒绝已存在 ID，update 拒绝不存在的 ID，upsert 允许创建或替换。update 不是局部 patch；不要遗漏已有字段。

## YAML 文档交换

`config validate/apply` 接收完整 UTF-8 YAML，而不是 JSON 包装。先用 `config show --raw --json` 取得字节快照和修订，按该快照编辑，再执行 validate，审阅差异后以原修订执行 apply：

```powershell
& $cli --root $root config validate --file reviewed.yaml --json
# 仅在验证成功、差异已审阅且修订仍对应原快照时执行：
& $cli --root $root config apply --file reviewed.yaml --expected-revision SHA256 --yes --json
```

`SHA256` 是需替换的占位符，不是有效修订。原文与候选各限 40 KiB；apply 保留提交内容的字节、BOM、注释及未知字段，但不会自动补回候选遗漏的旧任务。独立 overrides/preferences 仍保留。使用参与协议的客户端可获得修订冲突保护；外部编辑器不受同一锁保护，不能据此承诺任意外部写入下的原子 CAS。

## 自启准备、提交与不确定结果

登录自启不是 `startup self enable/disable` 两条快捷命令。先执行 `startup self prepare enable|disable --json`，审阅其 `data` 中的目标、上下文与注册指纹，再把**完整且新鲜的 data 对象**交给 `startup self set --file JSON --yes`。不提供可伪造的固定指纹模板；不要把上一次准备或整个响应信封重复提交。

现有未决或不可读提示会封锁 prepare/set。set 在交换前保留操作 ID，并向 stderr 输出，即使使用 `--json` 也是如此。超时、取消、断线或 unknown 后保存原 ID，查询 `operation show ID`；不要换 ID、重新准备、清档或盲重放。历史完成、当前状态匹配和查询本身都不解除未决封锁。资格仅针对当前服务端包的固定 GUI、当前主体与 root；包清单完整性不是发布者认证。

`startup inspect` 和 `startup adopt` 同样需要真实清单选中项，而不是编造来源/路径/摘要。inspect 只读；adopt 只写纳管任务配置，不禁用原生或其他 root 的启动入口，明确确认也不代表获得独占启动权。

## GUI 与 CLI 的当前覆盖

以下区分“已有入口”和“已完成总体旅程验收”，不将其等同。

| 能力 | GUI / 桌面入口 | 当前 CLI 与边界 |
| --- | --- | --- |
| 工作负载管理 | 添加、删除、启停、启用与常驻追踪 | `task` 命令族；配置修改需要修订，删除需确认。 |
| 配置原文 | 打开本地编辑器；外部编辑不参与共享锁 | `config show/path/validate/apply` 提供显式文档交换。 |
| 策略偏好 | 共享补启动、自动恢复开关 | `settings get/set/patch`；set 是完整替换，patch 只修改策略字段。 |
| 本机与远程主机 | 主机 tab 与远程定义工具 | `host list/show/add/update/upsert/remove/status`；远程只读观测，不接受任意命令。 |
| 监测与历史 | 服务/主机视图及历史信息 | `monitor status/watch/history`；保留未知与覆盖率语义。 |
| 日志 | 共享日志工具与异常日志 tab | `logs` 与 `logs exceptions` 是两个不同读取契约；系统日志 provider 仍有缺口。 |
| 启动项元数据与纳管 | 清单、复核、原生槽只读观察及纳管表单 | `startup list/inspect/adopt`、`startup operation inspect`；后者只诊断既有操作。 |
| 启动项禁用、恢复、重建 | 当前受保护入口未开放；旧禁用/恢复命令被阻断 | 无 `startup disable/restore/rebuild` CLI 路由，不因存在底层实现而宣称可用。 |
| 本应用登录自启 | 共享准备/确认/未决结果工具 | `startup self status/prepare/set` 已接通；没有 enable/disable 别名。 |
| 系统 Boot 任务 | 保留既有管理员 Helper 菜单，本轮未执行或迁移 | 只有 `startup boot status`；无 CLI install/uninstall 写入口。既有桌面入口不构成新增 CLI 写入口授权。 |
| 自动恢复暂停 | 当前状态、准备、明确确认与原回执查询 | `recovery state/resume`；未决状态保留原 ID，离线/孤立提示清单仍有缺口。 |
| 后台与诊断 | 托盘、系统诊断与后台生命周期 | `daemon status/start/stop`、`doctor`、`operation show`；stop 不停止工作负载。 |
| 外观与窗口 | 深浅色、窗口大小、tab、动效、托盘显示 | 纯展示行为不模拟为 CLI 命令。 |

[原始架构目标](<architecture.md#cli-与共享数据契约g1-r1-已批准>)中的目标表不是已注册命令表。当前原生写保护和验收缺口以[验证账本](<../verification.md>)为准；本轮不开放新的 startup/Boot 写入口。

## 超时、取消与退出码

`--timeout` 默认 30 秒，可选大于 0 且不超过 600 秒；输入等待和后续 RPC 各有自己的期限，不是全流程总墙钟保证。stdin 超时停止等待且不会提交未读完的输入，不保证底层非协作读取已取消。Ctrl+C 正常结束 watch/follow 时返回 0，单次请求取消返回 6；取消客户端不证明服务端已停止或回滚。

| 退出码 | 含义 |
| --- | --- |
| 0 | 当前命令成功；诊断成功不等于原操作成功。 |
| 1 | 业务失败、部分失败或特定来源不可用。 |
| 2 | 参数、输入或验证错误。 |
| 3 | 连接、协议或未决存储不可用。 |
| 4 | 权限或缺少明确确认。 |
| 5 | 配置或所选记录冲突，需要重新读取和审阅。 |
| 6 | 超时、单次取消、忙碌或结果未知；保留已接纳的原 ID。 |

`--json` 的 stdout 保持单行信封或逐行流。stderr 可能包含自启预占 ID、恢复提示等必要诊断；不要只读取 stdout 后丢弃它们。
