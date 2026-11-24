# Bridge 源码清单

范围包括原桥接目录及原生子目录中的文本源码和 JSON 案例。二进制、字节码缓存及阶段 DLL 文件不在源码清单内。

原始源码文件经 SHA-256 比较，无字节相同的版本。各阶段实现保留原逻辑名。

| 原逻辑名 | 发布范围 | 用途或依据 |
| --- | --- | --- |
| `acceptance-hole.json` | 纳入 | 通用 CAD 测试、接触回放或命令案例。 |
| `acceptance-repair.json` | 纳入 | 历史 NX 命令模板，运行前需替换根目录并确认目标。 |
| `acceptance-sketch.json` | 纳入 | 通用 CAD 测试、接触回放或命令案例。 |
| `adapt_loader.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `assess_final_validation.py` | 排除 | 残存历史绝对路径，需要迁移到独立参数化入口。 |
| `build.ps1` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `build_acceptance_clean.py` | 纳入 | 通用 CAD 测试、接触回放或命令案例。 |
| `build_joint.py` | 纳入 | 通用 CAD 测试、接触回放或命令案例。 |
| `capture_gui.py` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `check_codex_discovery.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `close_lab_window.py` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `collect_final_evidence.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_accept_reviewed.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_ack_save_failure.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_agent_cases.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_audit_retained_mechanics.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_cam_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_cam_law.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_cam_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_catalog_sync.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_contact_check.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_contact_diagnose.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_contact_probe.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_contact_probe_retry.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_datum_references.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_evidence_inventory.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_fourbar_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_fourbar_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_freeze_planner.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_gears_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_gears_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_geneva_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_geneva_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_groove_run.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_helpers.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_inversions_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_inversions_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_live_mobility.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_native_solve_gate.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_park_cursor.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_partial_contact_diagnose.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_planetary_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_planetary_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_preserve_fault.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_progress.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_ratchet_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_ratchet_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_refine_cam_sampling.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_repair_inventory.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_runtime_probes.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_session_safety.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `completion_synthesize_run.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_synthesized_cam_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_synthesized_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `completion_yoke_build.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 移除旧 GUI 光标定位与对应回执。 |
| `completion_yoke_motion.py` | 纳入 | 通用工业机构或接触、运动与约束能力评估。 |
| `Deploy-P13-Context.ps1` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `Deploy-Phase2.ps1` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `Deploy-Phase3.ps1` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `Deploy-Phase4.ps1` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `dsh_api_models_probe.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `dsh_bounded_probe.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `dsh_continue.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `dsh_nx_readonly.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `dsh_probe_report.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `dsh_store_openai_key.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `extend_adapter.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `extra_methods.cs` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `final_validation.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `finalization_contact_audit.py` | 纳入 | 通用 CAD 测试、接触回放或命令案例。 |
| `finalization_failure_replay.py` | 纳入 | 通用 CAD 测试、接触回放或命令案例。 |
| `finalization_report.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `finalize_adapter.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `inspect_pending_windows.py` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `joint-reopen.json` | 纳入 | 历史 NX 命令模板，运行前需替换根目录并确认目标。 |
| `lab_paths.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `LICENSE-upstream.txt` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `mcp_call.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `mcp_server.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `migration_agent_check.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_change_manifest.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_codex_config.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_copy.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_delivery_index.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_final_connection.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_history.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_launcher.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_native_audit.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_native_chat_verify.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_new_part.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_path_audit.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_preservation.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_progress.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_rebase.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_record_review.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_regression.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_replay.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_report.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_rollback_config.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_sample_replay.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_step_regression.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `migration_visual_gate.py` | 排除 | 旧迁移环境、历史项目根目录或验收归档专用。 |
| `native/ask2d-native-planar-trial.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/ask2d-native-sheet-trial.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/complex-offset-brep-probe.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/geometry-core-minimal.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/geometry-core-p01.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/geometry-core-p03.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/geometry-core-p04.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/geometry-core-p07.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/nurbs-native-brep-probe.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/nurbs-native-safety-source.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/nurbs-spline-recovery.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/periodic-hole-probe.cpp` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native/periodic-surface-net.h` | 纳入 | 通用 C++ UF 建模及诊断源，需本地 NX SDK 与相应 ABI。 |
| `native_step.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `NX12_Loader.cs` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `p13_context_capture.cs` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `p13_context_protocol.cs` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `p13_deployment_replace.psm1` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `p13_directory_preflight.psm1` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `p13_environment_contract.psm1` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `p13_fixed_smoke.cs` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `p13_launch_journal.psm1` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `p13_strict_launcher_core.psm1` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `package_delivery.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase13_context_methods.cs` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `phase2_acceptance_snapshot.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase2_build_robot.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase2_client.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase2_constraint_test.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase2_methods.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase2_motion.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase2_motion_test.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase2_play_robot.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase2_remaining_tests.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase2_robot_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase2_roundtrip.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3-design.json` | 纳入 | 前批已验证的阶段示例。 |
| `phase3_audit.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_build.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase3_client.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase3_cold_verify.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_guard_and_regression.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_joint_probe.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_methods.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase3_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_phase2_cold_solve.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_play_robot.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase3_precision.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_results.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_roundtrip.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase3_validate_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_accept.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase4_cam_build.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_cam_groove_build.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_cam_groove_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_cam_knife_build.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_cam_knife_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_cam_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_client.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase4_cold_gui.py` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `phase4_common.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase4_delta_build.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_delta_datum_repair.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_delta_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_evidence_audit.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase4_feeder_build.py` | 纳入 | 保留供料机构建模及前置验收条件，移除旧 GUI 光标定位。 |
| `phase4_feeder_motion.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_feeder_settings_audit.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_fourbar_build.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase4_fourbar_motion.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase4_methods.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase4_package.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase4_planetary_build.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase4_planetary_direction_repair.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_planetary_motion.py` | 纳入 | 前批已验证的阶段示例。 |
| `phase4_planetary_repair.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_planetary_willis_repair.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase4_play.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase4_preservation_audit.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase4_report.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `phase5_ask2d_native_trial.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_complex_step.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_fin_geometry.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_fin_representation_audit.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_finsp_edge_coverage.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_finsp_formation.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_finsp_trim_offset.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_general_sweep.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase5_independent_analytic.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_independent_annular.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_independent_box.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_independent_multihole.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_independent_nurbs_topology.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase5_independent_periodic.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_independent_surface_probe.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_nurbs_native_brep_trial.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_nurbs_pcurve_edge_mapping.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_nurbs_spline_probe.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_parametric.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase5_parametric_spline.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase5_periodic_hole.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_sheet_extend_offline_attempt0.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_sheet_trim_offline_attempt0.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_step214_cli_help.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_step214_offline_diagnostic.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_step214_planar_diagnostic.py` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_step_import_drift.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_step_readonly_probe.cs` | 纳入 | 前批已验证的阶段示例。 |
| `phase5_step_template_fin.cs` | 纳入 | 阶段建模、换算、评估、Motion 或曲面试验。 |
| `phase5_surface.cs` | 纳入 | 前批已验证的阶段示例。 |
| `pipe_client.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `probe.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `record_native_chat_result.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `record_nx.py` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `record_phase2.py` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `regenerate_acceptance.py` | 纳入 | 测试或分步命令模板，路径由本地配置提供。 |
| `RegenerateAcceptance.cs` | 纳入 | 通用 CAD 测试、接触回放或命令案例。 |
| `report_codex_discovery.py` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `research_accept_blind.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `research_accept_geneva.py` | 排除 | 属于历史验收签认及证据记录流程。 |
| `research_accept_ratchet.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_blind_adjust.py` | 纳入 | 通用运动、接触或轮廓技术试验。 移除旧 GUI 光标定位与对应回执。 |
| `research_blind_fault.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_blind_trajectory.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_cold_marker_compare.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `research_contact_diagnostics.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_geneva_motion.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_phase4_final.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `research_preservation.py` | 排除 | 阶段归档、验收签认、目录清点或仓库状态清查。 |
| `research_profile_probe.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_ratchet_backstop.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_ratchet_motion.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_ratchet_normals.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `research_ratchet_pawl_verify.py` | 纳入 | 通用运动、接触或轮廓技术试验。 |
| `run_commands.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `Start-NX-AI-Lab-P13-Strict.ps1` | 排除 | 专属投运、应用接入、供应商身份或回执链。 |
| `test_pipe_response.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
| `test_step_native.py` | 纳入 | 保留离线 STEP 翻译测试，移除旧 GUI 对话框自动点击。 |
| `ui_inspect.py` | 排除 | 历史部署、进程窗口、GUI 采集或屏幕控制。 |
| `verified_loader.py` | 由主加载器组处理 | 主桥接、协议、上游许可或基础运行依赖。 |
