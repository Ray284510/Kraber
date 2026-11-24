# Siemens NX 12 + MCP 验收档案

本目录收录历史现场证据的公开副本及阶段索引。运行环境记录为 Windows 11、Siemens NX 12.0.2.9。NX 截图保留模型画布，桌面叠层以可见实色遮盖，PNG 均重新编码。STEP 为原始导出副本。这次归档未重新打开模型或重跑用例。

| 阶段 | 历史结果与适用范围 | 本目录证据 |
| --- | --- | --- |
| 初始验收 | 原生 MCP/NX Open 完成验收板及四组件关节装配。记录九项验收通过，包含原生 PRT 重开和实际 STEP 导入。 | [九项记录](records/phase1-acceptance.json) · [验收板](images/nx12/acceptance-plate.png) · [关节装配](images/nx12/joint-assembly.png) · [STEP](models/acceptance/) |
| Phase 2 · 平面四轴 | NX 实际求解 81 帧，66 跨刚体实体对，5,346 次采样干涉查询记录为零干涉。 | [四轴结果](images/nx12/four-axis-robot.png) |
| Phase 3 · 空间六轴 | 12 项核心运动学演示验收在规定范围内通过。 | [交付验证摘录](records/phase3-acceptance.json) · [六轴结果](images/nx12/six-axis-robot.png) |
| Phase 4 · 多类型机构 | 初期七项限定配置通过，含三种送料行程设置。收尾报告保留 18 项限定配置的既有结果，第四阶段最终验收未通过。 | [四杆](images/nx12/fourbar.png) · [槽凸轮](images/nx12/cam_groove.png) · [行星齿轮](images/nx12/planetary.png) · [Delta](images/nx12/delta.png) · [送料](images/nx12/feeder.png) |
| Phase 5 · Bridge 与几何能力 | 后续现场研究覆盖多孔、NURBS、B-rep、STEP 交换和会话恢复。各试验的诊断、离线检查和运行结果仍按原记录的独立边界解释。 | 阶段索引 |
| P10–P12 · 架构及门禁 | 包含架构审计、修正，以及运行与检查的时间顺序门禁。 | 阶段索引 |
| P13 · 原生桥接续验 | 历史只读调用记录与完整写入链验收分别跟踪。几何 Smoke 为 NOT_RUN，两条历史写入操作仍为 UNKNOWN。 | 阶段索引 |
| P15 · Batch A | 记录普通模式与 Python 优化模式各 399/399 通过。该批结论适用于离线用例。 | 阶段索引 |
| R7 · 独立桌面展台 | 三个离散位置、恢复中位和关闭后重开有 NX 记录。连续扫掠和实物试装为 NOT_RUN。 | [起点](images/tabletop-stand/start.png) · [中位](images/tabletop-stand/middle.png) · [终点](images/tabletop-stand/end.png) · [重开](images/tabletop-stand/reopened.png) · [原生日志摘录](records/tabletop-stand-native-excerpt.txt) · [核验摘录](records/tabletop-stand-verification.json) |
| R11 · 收藏道具数字外观研究 | R9 由本轮 NX 会话记录三张同机位整机外观。每姿态检查 55 对，44 对分离、9 对接触、2 对固定件正体积相交。两组固定搭接的实物分件接受依据仍为 UNKNOWN。 | [HOME](images/r11-r9/home.png) · [MID](images/r11-r9/mid.png) · [PULLED](images/r11-r9/pulled.png) · [检查计数](records/r11-r9-pair-summary.json) |

[来源和图像处理记录](records/asset-provenance.json)包含收录文件对应的原始 SHA-256 及图像处理区域，区域使用源图像像素坐标。公开副本的 SHA-256 见 [清单](SHA256SUMS.txt)。STEP 内容包含验收板、关节总装和四个组成零件。原生 PRT 保持在本地，独立展台 PRT 中检出本机路径和用户标识。

运动和碰撞结果适用于报告中的离散姿态与指定配置。连续扫掠、强度、动力学、制造和实物验证须分别提供证据。R9 图像仅供本项目私有复盘，视觉参考的权属和使用权不随此档案转授。第三方参考资产与照片保存在本地。
