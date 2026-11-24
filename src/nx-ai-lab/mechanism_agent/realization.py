"""Recompare physical packaging before any NX files are created."""
import copy,math
import numpy as np
from .synthesis import synthesize
from .calculations import slider_crank

def realize(requirements):
    result=synthesize(requirements);result['theoretical_selection']=result['selected'];objectives=requirements['objectives']
    if requirements['output_motion']!='reciprocating_translation':raise ValueError('Automatic realization comparison currently covers reciprocating translation only; retain other proposals without claiming a build route')
    for row in result['candidates']:
        row['theoretical_metrics']=copy.deepcopy(row['metrics'])
        row['realization']={'status':'not_attempted_infeasible'}
        if not row['feasible']:continue
        candidate=copy.deepcopy(result);candidate['selected']=row['mechanism'];candidate['selected_parameters']=row['parameters']
        try:
            if row['mechanism']=='inline_slider_crank':
                from .synthesized_feeder import plan_from_selection
                p=plan_from_selection(candidate);q=p['parameters'];r=q['crank_radius'];rod=q['rod_length'];layout=p['layout'];length=layout['base_length'];width=layout['base_width']
                theta=np.linspace(0,2*math.pi,1441);x=np.array(slider_crank(r,rod,theta)['x']);omega=2*math.pi/requirements['period_s'];v=np.gradient(x,theta)*omega;a=np.gradient(v,theta)*omega
                row['metrics'].update(peak_velocity_mm_s=float(max(abs(v))),peak_accel_mm_s2=float(max(abs(a))),max_pressure_angle_deg=p['independent_calculation']['maximum_pressure_angle_deg'])
            elif row['mechanism']=='disk_cam':
                from .synthesized_cam import plan_from_selection
                p,_,_=plan_from_selection(candidate);length=p['layout']['base_length'];width=p['layout']['base_width'];row['metrics']['max_pressure_angle_deg']=p['independent_calculation']['maximum_pressure_angle_deg']
            elif row['mechanism']=='scotch_yoke':
                from .synthesized_yoke import plan_from_selection
                p=plan_from_selection(candidate);length=p['parameters']['base_length'];width=p['parameters']['base_width']
            else:raise ValueError('No matching automatic realization adapter; no substitution')
            row['metrics'].update(footprint_area_mm2=length*width,footprint_length_mm=length,footprint_width_mm=width)
            for key,limit in requirements.get('maxima',{}).items():
                if row['metrics'].get(key,float('inf'))>limit:raise ValueError('Physical realization exceeds required maximum '+key)
            row['realization']={'status':'dimensioned_requires_NX','adjustments':p.get('realization_adjustments',{}),'physical_base_length_mm':length,'physical_base_width_mm':width,'plan_family':p['family']}
        except ValueError as exc:
            row['feasible']=False;row['violations'].append(str(exc));row['realization']={'status':'not_buildable_by_current_adapter','reason':str(exc)}
    feasible=[r for r in result['candidates'] if r['feasible']]
    def dominates(a,b):return all(a['metrics'][k]<=b['metrics'][k]+1e-10 for k in objectives) and any(a['metrics'][k]<b['metrics'][k]-1e-10 for k in objectives)
    pareto=[r for r in feasible if not any(dominates(a,r) for a in feasible)];chosen=min(pareto,key=lambda r:tuple(r['metrics'][k] for k in objectives)) if pareto else None
    result.update(selected=chosen['mechanism'] if chosen else None,selected_parameters=chosen['parameters'] if chosen else None,pareto_candidates=[r['mechanism'] for r in pareto],status='dimensioned_requires_native_NX_validation' if chosen else 'no_realizable_candidate',realization_compared=True)
    result['selection_policy']+=' Recomputed physical packaging and kinematics for available adapters before the final comparison; rejected dimensions are retained. No NX acceptance is inherited.'
    return result
