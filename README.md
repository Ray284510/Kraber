# Kraber · UG / Siemens NX 12 + MCP 建模基准

在 Windows 11 上，用 MCP 将自然语言任务接到 NXOpen，通过可编辑的原生特征、尺寸测量、装配检查和关闭重开验证建模结果。仓库收录通用建模工具、机构案例，以及收藏模型项目用于检验网格导入和数字装配的记录。

**整体完成度 70%（截至 2026-11-24）**。按本归档定义的 10 项等权验收目标，7 项已有对应证据。P13 原生 Smoke、整机分件冻结和实物验证分别保留独立状态。计数口径和证据见[进度报告](docs/progress.md)。

| 查找内容 | 入口 |
|---|---|
| 阶段进展、最近失败与当前阻塞 | [进度报告](docs/progress.md) |
| 比较不同模型的结果 | [31 个基线用例](docs/benchmark-cases.csv) · [能力索引](docs/capability-index.md) |
| 原 MCP Server、NXOpen Loader、Worker、TaskStore 和通用机构源码 | [运行目录](src/nx-ai-lab/) · [构建和连接说明](src/nx-ai-lab/RUNTIME.md) |
| 在 NX 中运行网格导入或参数化桌面展台案例 | [三个 NXOpen Journal](src/nxopen-journals/README.md) |
| 查阅实物以外的历史验收证据 | [STEP、真实 NX 截图和运行记录](outputs/README.md) |
| 复核本次打包、测试及收录范围 | [发布核验](docs/verification.md) · [208 个历史交付包索引](docs/delivery-index.csv) · [来源与归档范围](docs/archive-scope.md) · [SHA-256](SHA256SUMS.txt) |

## 快速启动

依赖为 Windows x64、Python 3.12 和锁定的 Python 包。实际执行 NX 建模还需要用户合法安装的 Siemens NX 12.0.2.9、对应 NXOpen .NET SDK、.NET Framework 编译器和专用会话中的 Loader。

在仓库根目录打开 PowerShell，先验证 MCP stdio 服务。该命令执行协议初始化和工具列举，适合用作模型对比前的环境检查。

```powershell
Set-Location src/nx-ai-lab
py -3.12 -m venv .venv
& ./.venv/Scripts/python.exe -m pip install -r requirements-lock.txt
& ./.venv/Scripts/python.exe -B bench_list_tools.py
```

本次发布副本实测返回 **41 个工具**。如需挂到 MCP 客户端，按[配置样例](src/nx-ai-lab/config/mcp.stdio.example.json)将两个占位符换为本机解释器和 Server 脚本的绝对路径。原生 Loader 的校验、编译与专用 NX 会话连接步骤见[运行说明](src/nx-ai-lab/RUNTIME.md)。历史共享任务的 STOP-C 仍然有效。

## 使用同一基准比较模型

从 CSV 选一个 `case_id`，在各次运行中固定 NX 版本、输入哈希、工具版本和验收条件。分别记录实际模型 ID、工具回执、独立几何检查，以及保存重开后解析到的子件。历史资料无法绑定到具体模型时保留 `UNKNOWN`。基线表保留失败和未执行用例。
