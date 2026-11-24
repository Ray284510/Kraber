# 来源、隐私与归档范围

本仓库从干净的目标仓库工作副本整理，保留已有历史。NX-AI-Lab 来源为 feature/nx12-router-integration 工作树（起点 bbd91c0a2c2d8fcd1a331bd9c8a7e07deeaa8f22，包含后续工作树修改）。历史裁决核验器另取自 p13/legacy-adjudication-validator 工作树。R11 数字外观最新证据取自 M1 R9 集成复核。

| 项目 | 收录方式 |
|---|---|
| 通用桥接、编辑和检查 | 实际 MCP 入口、Loader 快照、生成器、附加 NXOpen 源及构建锁；以 checkout 相对路径和本机环境变量处理安装位置 |
| 阶段源码 | [Bridge 文件清单](history/BRIDGE_SOURCE_INDEX.md)，含原逻辑文件名与收录原因。生成器锁定的源使用经过整合编译的版本；阶段示例保留相应依赖结构 |
| 任务与几何 | 机制选择/计算/实现、Worker 和 TaskStore、阶段协议、通用几何模块、诊断核心与隔离历史裁决测试。与单台机器的真实派发票据和生产状态库分开 |
| 原生建模示例 | 三个可读 NXOpen Journal，移植历史输入位置为运行目录变量。脱敏版编译结果与历史原生运行分别记录 |
| 二进制和图像 | 6 个经元数据检查的通用文本 STEP、真实 NX 图像和结构化记录。部分截图裁去非建模窗口区域并移除元数据。处理方式和来源摘要见 outputs |
| 原始历史包 | 在本地保持完整。原生 PRT 可能嵌入私有路径/用户标识，R7 四件均实际命中；原始总包还含本机日志。GitHub 使用可审阅的交换模型、原生 Journal、证据摘录及阶段索引 |
| 配置与授权 | 提供 MCP 客户端模板、依赖锁、构建配置与可公开的 Schema。正式账本、机器私有配置、用户输入正文、审批票据、身份令牌和账户信息留在本机。商业 NX SDK/DLL 由使用者的安装提供 |
| 外部作品 | 收藏模型专题保留本项目数字外观截图用于私有工程复盘。社区 3MF/STL/CAD、第三方参考照片、制造用打印工程不随库分发 |

## 第三方源码来源

Loader 生成器依赖的 MIT 源和上游声明随库保留。另两份历史研究 checkout 以精确版本索引，便于对比来源，无需复制无关的第三方历史与测试资产。

- [NX_MCP-Enhanced · 构建输入](https://github.com/kongbaic/NX_MCP-Enhanced/tree/4e9b8fe163a3ea794b004d06e1d7d653d2c4c6e5)
- [NX_MCP · 研究参照](https://github.com/DreamEnding/NX_MCP/tree/4a7c8811fbc4ade3f8efb571263e4d6adf8878b4)
- [nx-auto-mcp · 研究参照](https://github.com/cyw2527/nx-auto-mcp/tree/82b8bd033b7c3bd377f057fe70bdbe94050f5d70)

本项目记录的状态分别属于源码编译、离线测试、原生运行、数字装配或实物试验。未能逐案确认实际模型 ID、签发依据或实物性能时保留 UNKNOWN / NOT_RUN。

## 历史交付总清单

两个项目交付目录共找到 208 个 ZIP，按文件内容有 207 个不同 SHA-256。逐包计算本机原件的字节数及哈希，保留相同字节在两个位置出现的记录。索引覆盖 NX-AI-Lab 129 项、Kraber-AI 79 项。[项目、文件名和原包摘要](delivery-index.csv)
