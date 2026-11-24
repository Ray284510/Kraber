# 历史阶段记录

通用 NXOpen 实现见 [Journal 示例](../../src/nxopen-journals/README.md)。以下为历史记录摘录。

| 阶段 | 记录 |
| --- | --- |
| R1–R3.1 | 早期网格引入、导入试验及 Qt 故障复盘。 |
| R4 | 独立 STL 导入、原生三角面统计、跨度测量和保存重开。 |
| R5–R6 | 第二份 STL 原生导入与来源映射。 |
| R7 | 独立桌面指示器构建、表达式与三种离散状态验证、装配重开及截面检查。 |
| R8 | 静态外观、CAD 分件和默认 GUI 加载测试。 |
| R9–R10 | 静态审阅与展陈。R10 归档留有首次缩放收集器失败。 |
| R11-01–02 | 来源和知识产权登记、静态外观及装配审阅。首次重开有部件引用不匹配记录。 |
| R11-M1-03–13 | 静态外形、颜色、对象身份及数字位置验证。出现圆角失败、显示重叠和装配引用解析失败。 |
| M1 V2A–V2C05 | 外形修订、数字定位和表面方案迭代。 |
| M1 V2D–V2L | 相依性、重叠和对象分类诊断。V2K 的两方案均未通过 HOME 验收。V2L 三态仍有显示相交。 |
| M1 V3–V7 | V3 重开记录含 12 个独立实体，仍有显示相交。V4 两方案 HOME 未通过。V5 基线复核。V6 统计 194 个 3MF 对象及 16 个盘，候选网格归属为 UNKNOWN。V7 留有视觉对象重组记录。 |
| M1 V8–V9 | 独立显示和空间复核，V8 留有图像输出、窗口可见性失败记录。 |
| M1 V10 R1–R7 | 多轮外观复核。R7 留有选择为空、对象断言和面匹配失败记录。 |
| M1 V10 R8 / R8-B | 静态外观修订与方案登记。R8 留有一次目标校验失败。 |
| M1 R9 | 默认重开完整加载 12 个 PRT，包含 11 个独立有效实体。每个离散姿态检查 55 对，重开前后分类一致，44 对分离、9 对接触、2 对正体积相交。两对固定对象相交的设计接受依据为 UNKNOWN。历史数字复核通过，最终视觉接受待确认，实物验证为 NOT_RUN。 |

## NX-AI-Lab 原生能力记录

阶段实现与依赖见 [NX-AI-Lab 源级案例](../../src/nx-ai-lab/bridge/PHASE_EXAMPLES.md)。

| 阶段 | 记录 |
| --- | --- |
| Phase 2–3 | 原生零件及装配建模。验收覆盖原生实体、特征、求解及保存重开。 |
| Phase 4 | 迁移检查点记载 32 个配置的原生离散碰撞检查、10 组代表性 STEP 原生回读。碰撞圈定在采样姿态。 |
| Phase 5 | 修剪 NURBS、有序环、周期曲面、UF BREP 和复杂 STEP 检查。 |
| P13 | 两条历史操作保持 UNKNOWN，禁止自动重试。STOP-C 有效，Smoke 为 NOT_RUN。 |
| P15 | 存在全项目审计归档，运行能力按各阶段的原生验收记录判断。 |

## 原始失败消息摘录

| 历史阶段 | 消息 |
| --- | --- |
| M1 V10 R8 · 初次目标核对 | `System.IO.InvalidDataException: wrong removal target` |
| M1 V10 R7 · 重组 | `System.NullReferenceException: 未将对象引用设置到对象的实例。` |
| M1 V8 · GUI/图像 | `System.IO.InvalidDataException: invisible window`；`NXOpen.NXException: 不能创建图像文件。` |
| M1 V2E · 重名导出 | `NXOpen.NXException: 文件已存在` |
| M1 V2D · 相依性 | `NXOpen.NXException: 发现迂回相依性。` |
| M1 V2B · 数字布置 | `NXOpen.NXException: 必须在此操作中使用独立布置` |
| M1-08 · 圆角 | `NXOpen.NXException: 圆角跨锐边滚动且无法修剪。` |
| R11 静态装配 · 重开 | `System.IO.InvalidDataException: Component path mismatch PANEL_09` |