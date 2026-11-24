# Siemens NX 与数字外观项目能力目录

本目录依据本机历史验收报告与档案证据编制。所有用例在本次整理均为 `NOT_RUN`。记录给出软件能力、任务证据和适用状态。缺少逐用例可独立绑定的 AI 模型与操作者记录，基线统一标为 `UNKNOWN`。用户设计决策保留其角色。比较模型时，应为每轮新运行单独记录模型标识、操作者、输入版本和真实运行环境。

[机器可读对比基线](benchmark-cases.csv)包含用例、历史状态、可观察结果、比较核验条件与证据索引。带链接的条目提供本仓库证据副本；其他条目以历史报告标题标识。

- `PASS` 指本条目所列范围内的历史通过，`PASS_OFFLINE` 指离线验证。
- `FAIL` 保留原验收失败。可与同材料中其他通过条目并存。
- `OBSERVED` 记录运行观察，`REVIEWED` 记录索引、静态审计或设计决策。
- `UNKNOWN` 为历史证据不足，`NOT_RUN` 为明确未运行。
- 新运行需分别记录真实 NX 回执、独立计算或离线模拟。离散姿态通过、连续扫掠、实物分件和制造分别判定。

## NX 核心、机构及桥接

| 用例 | 能力 | 历史状态 | 历史观察与证据 |
| --- | --- | --- | --- |
| NX-01 · NX Phase 1 | 原生建模与 STEP | PASS | 九项历史验收通过；关节装配四个组件，六对干涉检查，五个 PRT 重开和五个 STEP 实际导入。 [NX 初始验收状态](../outputs/records/phase1-acceptance.json) |
| NX-02 · NX Phase 2 | 4R 装配与运动学 | PASS | 记录 81 帧，66 个跨刚体对，5,346 次离散查询零干涉。 [NX12 第二阶段验收报告](../outputs/images/nx12/four-axis-robot.png) |
| NX-03 · NX Phase 3 | 空间六轴与轨迹 | PASS | 12 项核心运动学条目通过，求解结果记录 61 帧。 [NX12 第三阶段验收报告](../outputs/records/phase3-acceptance.json) |
| NX-04 · NX Phase 4 | 机构求解配置 | PASS | 原七个限定配置记录通过，三种送料行程为同一设计的设置。 [NX12 多类型机构阶段验收记录](../outputs/images/nx12/fourbar.png) |
| NX-05 · NX Phase 4 | 阶段完成边界 | FAIL | 收尾报告保留 18 项限定配置，第四阶段最终验收未通过；新盲测未启动。 NX12 第四阶段最终验收报告 |
| NX-06 · NX Phase 5 | 双孔独立体积积分 | PASS | 八个边界面，独立体积与 NX 差约 5.46e-12 mm³，冷重开复算差为零。 双孔八面独立体积验收 |
| NX-07 · NX Phase 5 | 复杂 NURBS 积分域 | NOT_RUN | 缺少完整原生裁剪边界和周期覆盖，复杂体积积分未执行。 Complex_NURBS_Integration_Acceptance |
| NX-08 · NX Phase 5 | UF B-rep 边提取对照 | OBSERVED | EDGE3D 四组矩阵记录两个交线边与两个普通边的观测差；四组中两组复用历史结果。 UF_BREP EDGE3D 四组补测 |
| NX-09 · NX Phase 5 | STEP 导入漂移 | FAIL | 四次 AP214 导入均超过 0.001 mm 门槛；导入内部首个致差步骤未知。 NX 12 AP214 受控导入与几何闭合 |
| NX-10 · NX P10 | 架构审计 | REVIEWED | 留存架构、门禁、几何能力矩阵、来源和风险审计。 P10 Architecture Audit |
| NX-11 · NX P11 | 结算与几何门禁 | PASS_OFFLINE | 普通模式 221/221 通过，新增测试优化模式 164/164 通过。 P11 两项 P0 离线修复验收 |
| NX-12 · NX P12 | 编排时序门禁 | PASS_OFFLINE | 几何修改前使旧检查失效，白名单显示操作可在身份不变时保留检查。 P12 nx_batch 时序 Gate 离线验收 |
| NX-13 · NX P13 | 只读会话上下文 | OBSERVED | 一次状态快照支持隔离 Probe 的 Work、Display 和 UF 身份一致。 P13 一次 Context 响应记录 |
| NX-14 · NX P13 | 旧写入回执与 Smoke | UNKNOWN | 两条历史写入缺可关联终态；原生几何 Smoke 为 NOT_RUN。 P13 历史未决记录与项目主计划 |
| NX-15 · NX P15 | 执行安全 Batch A | PASS_OFFLINE | 普通模式与 Python 优化模式分别 399/399；本批增加 33 项。 P15 Batch A 验收 |

## Kraber 历史路线与 R11 外观复核

| 用例 | 能力 | 历史状态 | 历史观察与证据 |
| --- | --- | --- | --- |
| KA-03 · Kraber R3 与 R3.1 | 参考网格导入与 GUI 调查 | REVIEWED | 索引登记派生 3MF 对象导入、Qt 稳定性和原因调查。 Kraber 项目索引 |
| KA-04 · Kraber R4 | STL 清点与导入 | REVIEWED | 索引登记历史 STL 清点、导入与对应原生文件。 Kraber 项目索引 |
| KA-05 · Kraber R5 | 参考网格原生导入 | REVIEWED | 索引登记 R5 原生导入成果。 Kraber 项目索引 |
| KA-06 · Kraber R6 | 分件与位姿归属 | UNKNOWN | 历史映射涉及 121 个 STL、98 个普通网格原型、16 个打印盘；没有经核验的成品装配位姿。 R10 主体外形交付记录 |
| KA-07 · Kraber R7 | 独立桌面展台 | PASS | 三个离散位置、恢复中位及重开有原生记录；扫掠和实物试装 NOT_RUN。 [R7 展台验证](../outputs/records/tabletop-stand-verification.json) |
| KA-08 · Kraber R8 | 静态外观浮雕装配 | PASS | 五个 PRT；记录参数更新、10 条装配约束求解与重开。 Kraber 项目索引 |
| KA-09 · Kraber R9 | 参数化静态主体 | PASS | 第一轮中央实体与顶层总装在 GUI 打开、卸载和重开。 Kraber 项目索引 |
| KA-10 · Kraber R10 | 多段静态外形 | PASS | 四个实体；四约束求解，六对中三对接触、三对分离。 R10 主体外形交付记录 |
| KA-11A · Kraber R11 M1 V2F | 对象拆分与三态几何 | FAIL | 三态重开复核 12 个实体，固定对象位移为零，相交 4/3/3。 Kraber 项目变更记录 |
| KA-11B · Kraber R11 M1 V4 | A/B 静态候选 | FAIL | 两套 HOME 各 12 个独立实体，四对相交，均未通过几何规则 A。 Kraber 项目变更记录 |
| KA-11C · Kraber R11 M1 V6–V7 | 参考图与网格溯源 | UNKNOWN | 候选和盘内实例可核对，照片到网格的唯一身份未知。 Kraber 项目变更记录 |
| KA-11D · Kraber R11 M1 V8–V9 | 独立数字外观三态 | FAIL | 重开与身份通过，三个姿态均出现涉及活动组的正体积相交。 Kraber 项目变更记录 |
| KA-11E · Kraber R11 M1 V10 | 数字路线变更 | OBSERVED | 新方案的活动外观件在三态无正体积相交，固定件仍有三处搭接。 Kraber 项目变更记录 |
| KA-11F · Kraber R11 M1 R8-B | 用户决策与版本身份 | REVIEWED | 记录用户选定的外观分组和删除件，12 个 PRT 只读核验。 Kraber 项目变更记录 |
| KA-11G · Kraber R11 M1 R9 | 整机复核与重开 | PASS | 11 个独立实体，三个姿态每态均检查 55 对，44 分离、9 接触、2 固定件正体积相交。重开前后均一致。 [R9 整机复核](../outputs/records/r11-r9-pair-summary.json) |
| KA-11H · Kraber R11 M1 R9 | 实物分件与打印 | UNKNOWN | 两个固定件搭接的实物分件接受依据未知；本轮打印、采购、实物验证 NOT_RUN。 [R9 整机复核](../outputs/records/r11-r9-pair-summary.json) |

