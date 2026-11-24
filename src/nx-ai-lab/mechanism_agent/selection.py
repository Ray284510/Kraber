"""Transparent candidate comparison. Codex resolves requirements; no local language model."""
from dataclasses import dataclass,asdict
@dataclass
class Requirement:
    input_motion:str='continuous_rotation'
    output_motion:str='reciprocating_translation'
    adjustable_stroke:bool=False
    minimize_quick_return:bool=False
    dwell:bool=False
    coaxial:bool=False
    source_text:str=''

def interpret(text):
    r=Requirement(source_text=text)
    if any(w in text for w in ['间歇旋转','intermittent']):r.output_motion='intermittent_rotation'
    elif any(w in text for w in ['并联','Delta','delta']):r.input_motion='three_rotary_inputs';r.output_motion='spatial_translation'
    elif any(w in text for w in ['减速','齿轮','gear']):r.output_motion='continuous_rotation';r.coaxial='同轴' in text or '行星' in text
    elif any(w in text for w in ['摆动','摇杆','oscillat']):r.output_motion='oscillation'
    elif not any(w in text for w in ['往复','升降','直线','凸轮','reciprocat']):raise ValueError('Ambiguous motion goal; Codex must clarify or supply a structured Requirement')
    r.adjustable_stroke=any(w in text for w in ['可调','adjustable']);r.minimize_quick_return=any(w in text for w in ['减小急回','无急回','尽量减小急回','low quick']);r.dwell=any(w in text for w in ['停歇','dwell','凸轮']);return r

def select(r):
    candidates={
      'reciprocating_translation':[('inline_slider_crank',90,'低副闭环、偏心半径可调；对心安装可使往返输入角均为180°'),('disk_cam',80 if r.dwell else 65,'能定制停歇和运动规律；轮廓制造及调整行程更复杂'),('scotch_yoke',60,'理论正弦往复，销槽滑动与接触磨损需要额外设计')],
      'oscillation':[('crank_rocker',95,'单一连续回转输入，以四个低副形成周期摆动'),('oscillating_cam',70,'可定制摆动规律，但需轮廓和接触验证')],
      'intermittent_rotation':[('geneva',90,'连续输入形成索引与几何停歇，需检查进出槽冲击'),('ratchet',75,'适合单向间歇；需要止逆和棘爪接触设计'),('ordinary_gear_pair',0,'普通恒比齿轮不能自行产生停歇')],
      'continuous_rotation':[('planetary_gear',95 if r.coaxial else 80,'适合紧凑同轴变速；需要齿数相容及载体相对运动验证'),('external_gear_train',85 if not r.coaxial else 70,'结构直观，轴线通常错开')],
      'spatial_translation':[('delta_parallel',95,'三支链约束动平台姿态，适合空间平移；必须检查奇异性'),('serial_robot',60,'工作空间灵活，但构件与驱动数量通常较多')]}[r.output_motion]
    if r.dwell and r.output_motion=='reciprocating_translation':candidates=[(k,(100 if k=='disk_cam' else score-30),reason) for k,score,reason in candidates]
    if r.adjustable_stroke:candidates=[(k,score+(10 if k=='inline_slider_crank' else -10),reason) for k,score,reason in candidates]
    if r.minimize_quick_return:candidates=[(k,score+(10 if k=='inline_slider_crank' else 0),reason) for k,score,reason in candidates]
    ranked=sorted(candidates,key=lambda x:-x[1]);return {'requirements':asdict(r),'candidates':[{'mechanism':k,'score':score,'reason':reason} for k,score,reason in ranked],'selected':ranked[0][0],'evidence_status':'Selection reasoning only; NX acceptance must be supplied separately','unknown_requirements':'Load, life, material and manufacturing constraints are not inferred as verified.'}
