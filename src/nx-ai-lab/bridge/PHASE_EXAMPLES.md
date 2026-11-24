# NX-AI-Lab 源级案例

| 阶段 | 源文件与依赖 |
| --- | --- |
| Phase 2 | phase2_methods.cs 和 phase2_motion.cs 是加载器内部的建模、约束及运动命令片段。phase2_build_robot.py 使用本阶段的 MCP 客户端。 |
| Phase 3 | phase3_methods.cs 是空间建模片段。phase3_build.py 与 phase3-design.json 共同描述通用空间机械臂案例。 |
| Phase 4 | phase4_methods.cs 是通用机构加载器片段。四连杆与行星齿轮的构建、运动案例使用 phase4_common.py、phase4_client.py 和 mechanism_agent 的方案模块。 |
| Phase 5 | phase5_surface.cs、phase5_general_sweep.cs、phase5_parametric.cs 和 phase5_parametric_spline.cs 属于加载器类内部的曲面、扫描和参数化试验片段。phase5_independent_nurbs_topology.cs 和 phase5_step_readonly_probe.cs 可以作为加载器附加源文件编译。 |

源级案例以 NX 12.0.2.9 为目标。C# 片段保留对原加载器类型、命令分派和路径校验的依赖。配置运行环境时设置 NX_AI_LAB_ROOT 为同一套 NX-AI-Lab 环境的绝对根目录，历史样件目录按该根目录解析。两份 Phase 5 附加源的历史测试入口仍有原样件名称和哈希校验。完整复跑需要原测试环境或适配新的自有样件。

Python 案例依赖同一 checkout 下的 lab_paths、bridge/mcp_server.py 和对应 NX 加载器命令。运行环境还需要 mcp，部分运动案例需要 numpy。Phase 4 需要 mechanism_agent（contract、fourbar_plan、gear_plan、calculations），以及可读取版本信息的 Git checkout。示例会调用活跃 NX 会话并生成 PRT 和运行日志，在专用空会话使用。现场命令或环境差异可能需要调整。

两份 Phase 5 附加源已对 NX 12.0.2.9 SDK 离线编译通过，旧 STEP214 API 产生弃用警告。Python 源通过语法和代码对象编译检查，Phase 3 设计 JSON 解析成功。加载器内部片段的集成编译与本批公开版的 NX 运行均为 NOT_RUN。

## 扩充的技术案例

[逐文件源码清单](../../../docs/history/BRIDGE_SOURCE_INDEX.md) 覆盖桥接目录和原生子目录中的 242 个原有文本源码与命令案例。其中 144 个位于本源码组，16 个交由主加载器源码组，82 个属于历史投运、归档、签认、GUI 或其他专用环境流程。原始源文件按 SHA-256 比较，无字节完全相同的组。不同阶段的方法和原型以各自原逻辑名保留。

- Phase 2–3 补充关节、机器人、装配重开、运动设置、解算及评估脚本。部分历史测试需要先建立相应样件。
- Phase 4、completion 和 research 增补通用凸轮、四杆、齿轮、行星、Delta、Geneva、供料、曲柄滑块与轭等案例，以及约束、接触、运动和轮廓评估。部分案例需要前序阶段的验收记录与通用方案模块。移除了旧光标自动定位，原生操作依赖相应的 NX 会话和当前部件。运行前清除选择状态。
- Phase 5 增补曲面、曲线、UF BREP、STEP 及 C++ 原生测量探针。原生探针需要 NX SDK、对应位数的 Windows C++ 工具链及匹配的导出 API。托管试验的 P/Invoke 保留原探针 DLL 的逻辑文件名，使用前需配套构建并在受控的 NX 实验环境解析这些依赖。部分历史探针需要对应测试件目录和原样件身份检查。
- 设置 NX_AI_LAB_ROOT 与 NX_INSTALL_DIR 为本地实验根目录和 NX 安装根目录。包含花括号占位符的 JSON 为人工核对和替换路径的命令模板。源码不会携带任何本地安装文件。
- 扩充后 96 个 Python 文件通过语法及代码对象编译，5 个 JSON 的格式已解析。RegenerateAcceptance.cs 离线编译通过。其余新增 C# 作为加载器片段保留，C++ 原生探针、整链运行和 NX 图形验收均为 NOT_RUN。