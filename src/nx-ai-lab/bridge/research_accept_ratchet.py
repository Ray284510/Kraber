"""Evidence gate for ideal D+E research; physical contact route stays unaccepted."""
import json,datetime,sys
from pathlib import Path
from phase4_client import ROOT,LOG
sys.path.insert(0,str(ROOT))
from mechanism_agent.repair import RepairBudget

ctx=json.loads((ROOT/'logs/contact_research/current.json').read_text());research=Path(ctx['logs']);reports=Path(ctx['reports']);ptr=LOG/'current-research_ratchet_ideal2.json';run=json.loads(ptr.read_text());out=Path(run['logs']);model=Path(run['models'])
e={name:json.loads((out/(name+'.json')).read_text()) for name in ['motion-results','cold-reopen-verification','reopen-verification','cold-marker-crosscheck','native-backstop-probe','native-contact-normal-verification','native-pawl-crosscheck']}
assert run['build_passed'] and run['motion_passed'] and run['io_passed']
for k in e:
    if k=='motion-results':assert e[k]['simulation_passed'] and len(e[k]['frames'])==721 and all(f['passed'] for f in e[k]['frames'])
    else:assert e[k]['passed']
assert (out/'normal-diagnostic-correction.json').exists()
for v in json.loads((out/'step-roundtrip-accurate.json').read_text()).values():assert v['passed']
review=dict(reviewed=datetime.datetime.now().astimezone().isoformat(),reviewer='Codex actual PNG inspection',frames=['frame_00050.png','frame_00200.png','frame_00400.png'],observed='Actual NX12 Motion GUI, blue ratchet wheel, two distinct orange hooked pawls, fourbar input, different actual native playback steps46/187/379. No NX error dialog; desktop task notification overlay present.',synthetic_images=False)
for n in review['frames']:assert (Path(run['recording'])/n).is_file()
(out/'visual-review.json').write_text(json.dumps(review,indent=2))
cv=e['motion-results']['cross_validation'];normal=e['native-contact-normal-verification'];back=e['native-backstop-probe'];pawl=e['native-pawl-crosscheck'];cold=e['cold-marker-crosscheck']
accept=dict(status='ideal_kinematics_passed',contact_dynamics_passed=False,trial=2,trial_limit=2,models=str(model),logs=str(out),cross_validation=cv,pawl_geometry=pawl,native_backstop_obstruction_volume_mm3=back['positive_blocking_volume_mm3'],native_target_face_moment_signs=[v['moment_per_unit_normal_force_mm'] for v in normal['faces']],cold_restart=True,recording=run['recording'],artifact_paths=[str(p) for p in model.iterdir() if p.suffix.lower() in ['.prt','.step','.sim']],limitations=['Four prescribed ideal drivers, no native unilateral reaction-force or spring/loaded backstop validation','Geometric reverse obstruction test deliberately clamps holding pawl at dwell; combined with target-face normal direction only establishes ideal geometric backstop','Nominal0.05mm seat clearance; reverse obstruction measured at0.2degree, not zero backlash','721 discrete collision frames, not continuous swept-volume proof','Native nominal Gruebler1/-3 differs from redundancy-corrected physical4/fully-driven0; original solver warnings retained'])
(research/'ratchet/acceptance.json').write_text(json.dumps(accept,indent=2));run.update(accepted=True,accepted_scope='ideal_kinematics_only',functional_backstop_passed=True,backstop_scope='ideal geometry only; no loaded passive contact-force validation',gui_review_pending=False);ptr.write_text(json.dumps(run,indent=2))
RepairBudget(research/'full_trials','ratchet',limit=2).finish(2,'Native threecycle indexing,721frames/all21pairs,independent link/pawl geometry,actual target-face normals+solid containment,virtual reverse geometric obstruction,PRT/STEP/SIM reopen,cold restart and GUI reviewed. IdealD+E ONLY; loaded unilateral contact still unaccepted.',model/'mechanism.sim',research/'ratchet/acceptance.json',True)
text=f'''# 棘轮专项验收

结论：**理想运动学模型通过（路线D+E）**。**真实单边接触动力学、弹簧和负载止回未通过验收**。完整修复预算已用2/2次，不再追加完整修复试验。

第一轮模型虽完成规定运动并无穿透，但反向齿面法向会抬起自由棘爪，不能把固定棘爪的碰撞冒充止回，因此整体失败。第二轮保留四杆输入，曲柄5.477310858917672mm、连杆40mm、摇杆35mm、机架40mm，实现20°摇角；18齿、外径50mm、根径44mm；修改齿相位和钩爪连接结构，使反转力趋向压紧、正向齿坡力趋向抬起。独立七零件、真实装配，两个棘爪各有转动副和理想抬起/落座驱动。

| 检查 | 实际证据 |
|---|---|
| 连续三周期分度 | {cv['cycle_indexes_deg']}°；原生完整12秒721点 |
| 运动独立校验 | 实测刚性杆长、闭环误差最大{cv['loop_closure_mm']:.10f}mm；推进段轮/摇杆增量差{cv['robust_forward_increment_error_deg']:.10f}° |
| 棘爪进入/退出 | 实测推进段相对对应齿位置误差{pawl['forward_tip_to_indexed_tooth_error_mm']:.10f}mm；两棘爪实际抬起约35°，离开齿顶后复位，多周期落座误差{pawl['cycle_seat_repeat_error_mm']:.10f}mm |
| 停歇 | 回程轮角相邻采样差最大{cv['dwell_increment_error_deg']:.10f}°；轨迹无负向增量 |
| 全实体干涉 | 每帧全部21对实体，721帧无正体积穿透；离散采样范围 |
| 理想反向止回几何 | 独立诊断副本，固定落座棘爪并虚拟反转实测{back['measured_probe_angles_deg']['12']:.10f}°，产生{back['positive_blocking_volume_mm3']:.10f}mm³阻挡；正向0.2°和零位无相交。该故意碰撞副本不是通过的运动轨迹，也不是反力求解 |
| 真实目标齿面法向 | 根据实际面边界选择，UF面法向±0.001mm点的实体内外分类验证；单位反向法向力矩{normal['faces'][0]['moment_per_unit_normal_force_mm']:.10f}mm，正向齿坡{normal['faces'][1]['moment_per_unit_normal_force_mm']:.10f}mm |
| 自由度 | 六活动刚体36坐标，下副矩阵独立秩32，物理自由度4；NX标称Gruebler未剔除3个平面虚约束，显示未驱动1、四驱动后-3；原生求解器剔除3个冗余约束后剩余DOF0。不能称单边棘轮固定1自由度 |
| 文件操作 | 独立PRT及装配保存/重开、STEP导出/回读并比较体积、SIM重开通过 |
| 冷启动 | 正常关闭并重新启动NX后重新求解、3帧重新干涉查询、全部标记坐标复核；最大重复坐标差{max(cold['max_repeated_coordinate_error_mm'].values()):.10f}mm |
| 界面证据 | {run['recording']}\\NX_actual.mp4；实际截图已检查 |

模型：`{model}`。完整证据：`{out}`。MCP逐次调用：`{LOG/'actual-calls.jsonl'}`。

完整保留原生警告：平面冗余约束自动剔除、初始条件QR补足、全驱动约束数警告、末端曲线表浮点边界警告。第一轮在1/5/9秒还有矩阵分解警告25，第二轮首次和冷启动求解未记录该警告。原生结果实际覆盖12秒，所有独立位置/几何指标通过；这不构成动力学反力的验证。原生瞬时单边接触雅可比/接触激活秩未取得，不伪造。

诊断修正也保留：最初按方向选面误选对侧平行齿面，`native-backstop-probe.json/native_geometry_moment_signs`的旧派生方向字段不作验收依据；以`native-contact-normal-verification.json`实际目标面+实体内外测试替代。原始求解、碰撞体积和文件未改。R1错误、R2两次失败预检查、第三次12001点预检查和所有历史接触失败均保留。

本结论仅解锁后续盲测；第四阶段总验收、D盘迁移尚须各自新证据。
'''
(reports/'NX12_Ratchet_Acceptance.md').write_text(text,encoding='utf-8');print(json.dumps(accept,indent=2))
