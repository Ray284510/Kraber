"""Physics-based candidate synthesis from cloud-interpreted requirements.

No natural-language keyword routing, fixed per-mechanism scores, or task-specific
answer/dimensions. This computes proposals; it never asserts NX acceptance.
"""
import math
import numpy as np
from .calculations import slider_crank

def validate_spec(s):
    required=['source_text','interpretation','input_motion','output_motion','objectives','assumptions']
    if not isinstance(s,dict):raise ValueError('Structured cloud-interpreted specification required')
    allowed=set(required+['period_s','stroke_mm','max_pressure_angle_deg','stroke_range_mm','dwell_fraction','motion_law','adjustable_stroke','quick_return_ratio','quick_return_tolerance','low_pair_only','maxima','roller_radius_mm','output_step_deg','center_distance_mm','dwell_tolerance','module_mm','signed_speed_ratio','ratio_relative_tolerance','coaxial'])
    if set(s)-allowed:raise ValueError('Unrecognized requirements cannot be silently ignored: '+', '.join(sorted(set(s)-allowed)))
    if any(k not in s for k in required):raise ValueError('Cloud interpretation, original text, explicit assumptions and ordered objectives required')
    if not isinstance(s['source_text'],str) or not s['source_text'].strip() or not isinstance(s['interpretation'],list) or not s['interpretation']:raise ValueError('Explain the semantic mapping; do not silently infer requirements')
    if any(not isinstance(row,dict) or any(not isinstance(row.get(k),str) or not row[k].strip() for k in ['field','reason']) for row in s['interpretation']):raise ValueError('Each interpretation needs a field and explicit reason')
    if not isinstance(s['assumptions'],list) or not s['assumptions'] or not all(isinstance(v,str) and v.strip() for v in s['assumptions']):raise ValueError('Explicit list of assumptions required')
    if s['input_motion']!='continuous_rotation':raise ValueError('This synthesis revision handles continuous rotary input; other inputs need explicit extension')
    if not isinstance(s['objectives'],list) or not s['objectives'] or not all(isinstance(v,str) for v in s['objectives']) or len(set(s['objectives']))!=len(s['objectives']):raise ValueError('Distinct ordered objectives required')
    finite=lambda v:isinstance(v,(int,float)) and not isinstance(v,bool) and math.isfinite(v)
    for k in ['period_s','stroke_mm','max_pressure_angle_deg','module_mm','output_step_deg','center_distance_mm','roller_radius_mm','quick_return_ratio','quick_return_tolerance','dwell_tolerance','ratio_relative_tolerance']:
        if k in s and (not finite(s[k]) or s[k]<=0):raise ValueError('Positive finite '+k+' required')
    if 'stroke_range_mm' in s and (not isinstance(s['stroke_range_mm'],list) or len(s['stroke_range_mm'])!=2 or not all(finite(v) for v in s['stroke_range_mm']) or not 0<s['stroke_range_mm'][0]<=s['stroke_range_mm'][1]):raise ValueError('Increasing finite positive stroke range required')
    for k in ['adjustable_stroke','low_pair_only','coaxial']:
        if k in s and not isinstance(s[k],bool):raise ValueError('Boolean '+k+' required')
    if s.get('adjustable_stroke') and ('stroke_range_mm' not in s or s['stroke_range_mm'][0]==s['stroke_range_mm'][1]):raise ValueError('Choose and record a nonzero adjustment range; a single fixed stroke is not adjustable')
    if s.get('quick_return_ratio',1)<1:raise ValueError('Quick return ratio is defined as longer/shorter stroke time, at least one')
    if 'dwell_fraction' in s and not finite(s['dwell_fraction']):raise ValueError('Finite dwell fraction required')
    if not 0<=s.get('dwell_fraction',0)<.8:raise ValueError('Dwell fraction outside supported calculation range')
    if s.get('motion_law','unspecified') not in ['unspecified','quintic','cycloidal','simple_harmonic','constant_speed_segment','specified_profile']:raise ValueError('Unmodeled motion law')
    if not isinstance(s.get('maxima',{}),dict) or any(not finite(v) or v<0 for v in s.get('maxima',{}).values()):raise ValueError('Finite nonnegative maximum limits required')
    return s

def _candidate(kind,parameters,metrics,violations,reason,route):
    return {'mechanism':kind,'parameters':parameters,'metrics':metrics,'feasible':not violations,'violations':violations,'reason':reason,'nx_route':route,'nx_verified_for_these_parameters':False}

def normalized_cam_law(u,law):
    if law in ['unspecified','quintic']:return 10*u**3-15*u**4+6*u**5,30*u*u-60*u**3+30*u**4,60*u-180*u*u+120*u**3,'quintic'
    if law=='cycloidal':return u-np.sin(2*np.pi*u)/(2*np.pi),1-np.cos(2*np.pi*u),2*np.pi*np.sin(2*np.pi*u),law
    if law=='simple_harmonic':return (1-np.cos(np.pi*u))/2,np.pi*np.sin(np.pi*u)/2,np.pi**2*np.cos(np.pi*u)/2,law
    if law=='constant_speed_segment':
        a=.2;v=1/(1-a);q=np.minimum(u,1-u)/a;cap_s=v*a*(q**3-.5*q**4);cap_v=v*(3*q*q-2*q**3);cap_a=v/a*(6*q-6*q*q)
        s=np.where(u<a,cap_s,np.where(u>1-a,1-cap_s,v*(u-a)+v*a/2));ds=np.where((u<a)|(u>1-a),cap_v,v);dds=np.where(u<a,cap_a,np.where(u>1-a,-cap_a,0.))
        return s,ds,dds,'constant_speed_segment'
    raise ValueError('No synthesized cam law for '+law)

def reciprocating(s):
    if not all(k in s for k in ['stroke_mm','period_s','max_pressure_angle_deg']):raise ValueError('Choose and record stroke, cycle time and pressure-angle design assumptions before synthesis')
    h=s['stroke_mm'];hlo,hhi=s.get('stroke_range_mm',[h,h]);assert hlo<=h<=hhi
    alpha=s['max_pressure_angle_deg'];assert 0<alpha<75
    omega=2*math.pi/s['period_s'];r=h/2;rmax=hhi/2;rod=math.ceil(rmax/math.sin(math.radians(alpha)));theta=np.linspace(0,2*math.pi,1441)
    dwell=s.get('dwell_fraction',0);law=s.get('motion_law','unspecified');adjust=s.get('adjustable_stroke',False);target=s.get('quick_return_ratio',1.0);rows=[]
    common=[]
    if dwell>0:common.append('Finite dwell cannot be produced by an ordinary constant-speed crank linkage')
    if law in ['constant_speed_segment','specified_profile','quintic','cycloidal']:common.append('Requested displacement law requires shaped geometry or an independently synthesized input drive')
    for kind,e in [('inline_slider_crank',0.)]+([('offset_slider_crank',x*rod) for x in [.1,.2,.3,.4]] if target>1.02 else []):
        rr=r
        if e:
            # Rescale r to produce the specified stroke at the selected offset.
            lo,hi=1e-6,min(rod-abs(e)-1e-5,h)
            for _ in range(55):
                rr=(lo+hi)/2;stroke=slider_crank(rr,rod,np.array([0]),e)['stroke_mm']
                if stroke<h:lo=rr
                else:hi=rr
        calc=slider_crank(rr,rod,theta,e);x=np.array(calc['x']);velocity=np.gradient(x,theta)*omega;acceleration=np.gradient(velocity,theta)*omega;pressure=calc['maximum_pressure_angle_deg'];violations=list(common)
        if abs(calc['quick_return_ratio']-target)>s.get('quick_return_tolerance',.03):violations.append('Quick-return ratio outside required band')
        if pressure>alpha+1e-10:violations.append('Pressure angle exceeds specified limit')
        if law=='simple_harmonic':violations.append('Finite connecting rod produces a second harmonic')
        if adjust and e:violations.append('Offset variant adjustable range not synthesized in this revision')
        width=rod+2*rmax+max(12,hhi*.4);height=2*rmax+max(12,hhi*.4)+abs(e)
        rows.append(_candidate(kind,{'crank_radius_mm':rr,'rod_length_mm':rod,'offset_mm':e,'stroke_range_mm':[hlo,hhi],'input_speed_deg_s':360/s['period_s']},dict(footprint_area_mm2=width*height,footprint_length_mm=width,footprint_width_mm=height,moving_links=3,higher_pairs=0,quick_return_deviation=abs(calc['quick_return_ratio']-target),quick_return_ratio=calc['quick_return_ratio'],max_pressure_angle_deg=pressure,peak_accel_mm_s2=float(max(abs(acceleration))),peak_velocity_mm_s=float(max(abs(velocity))),stroke_mm=calc['stroke_mm']),violations,'Low-pair closed loop; rod length calculated from the pressure-angle limit. Adjustable crank radius changes stroke; speeds follow loop closure.','parameterized R-R-R-P builder; each new parameter set requires actual NX validation'))
    violations=list(common)
    if law not in ['unspecified','simple_harmonic']:violations.append('Yoke gives sinusoidal displacement at constant input speed')
    if abs(target-1)>s.get('quick_return_tolerance',.03):violations.append('Symmetric yoke has equal half-cycle durations')
    if s.get('low_pair_only',False):violations.append('Pin-slot higher pair is prohibited by requirement')
    width=2*hhi+max(12,.4*hhi);height=hhi+max(12,.4*hhi)
    rows.append(_candidate('scotch_yoke',{'crank_radius_mm':r,'slot_travel_mm':hhi,'input_speed_deg_s':360/s['period_s'],'stroke_range_mm':[hlo,hhi]},dict(footprint_area_mm2=width*height,footprint_length_mm=width,footprint_width_mm=height,moving_links=2,higher_pairs=1,quick_return_deviation=abs(1-target),quick_return_ratio=1.,max_pressure_angle_deg=0.,peak_accel_mm_s2=r*omega**2,peak_velocity_mm_s=r*omega,stroke_mm=h),violations,'Exact sinusoidal motion with fewer links; pin-slot sliding wear and local contact need separate engineering.','not yet a verified NX builder'))
    # Symmetric 3-4-5 cam: calculate base radius from pressure angle, not a fixed score.
    beta=math.pi*(1-dwell);u=np.linspace(0,1,721);sn,dn,ddn,cam_law=normalized_cam_law(u,law if law!='specified_profile' else 'quintic');disp=h*sn;ds=h*dn/beta;dds=h*ddn/(beta*beta)
    roller=s.get('roller_radius_mm',max(2.,h/5));base=max(roller*2,math.ceil(float(max(ds/math.tan(math.radians(alpha))-disp))))
    for _ in range(1000):
        radial=base+disp;denominator=radial*radial+2*ds*ds-radial*dds
        if np.all(denominator>0):
            rho=(radial*radial+ds*ds)**1.5/denominator
            if min(rho)>roller*1.2:break
        base+=max(1,h/10)
    else:raise ValueError('No convex non-undercut cam within bounded search')
    pressure=float(np.degrees(np.arctan(ds/(base+disp))).max());violations=[]
    if adjust:violations.append('Changing a fixed cam lift requires a reconfigurable follower or interchangeable cam; continuous adjustment not provided by this candidate')
    if law=='specified_profile':violations.append('An arbitrary specified profile must be supplied numerically and independently checked')
    if law=='simple_harmonic' and dwell:violations.append('Simple harmonic acceleration jumps at a dwell boundary; C2 transition not synthesized')
    if abs(target-1)>s.get('quick_return_tolerance',.03):violations.append('This symmetric law has equal rise/return times')
    if s.get('low_pair_only',False):violations.append('Cam higher pair is prohibited by requirement')
    width=2*(base+h+roller);height=width+h
    rows.append(_candidate('disk_cam',{'law':cam_law,'pitch_base_radius_mm':base,'lift_mm':h,'roller_radius_mm':roller,'rise_angle_deg':math.degrees(beta),'dwell_fraction':dwell,'input_speed_deg_s':360/s['period_s'],'minimum_pitch_curvature_mm':float(min(rho))},dict(footprint_area_mm2=width*height,footprint_length_mm=width,footprint_width_mm=height,moving_links=3,higher_pairs=1,quick_return_deviation=abs(1-target),quick_return_ratio=1.,max_pressure_angle_deg=pressure,peak_accel_mm_s2=float(max(abs(dds)))*omega**2,peak_velocity_mm_s=float(max(ds))*omega,stroke_mm=h),violations,'Specified law and dwell synthesized; pressure angle and pitch curvature checked independently.','grooved cam builder can be extended; dimensions do not inherit old acceptance'))
    return rows

def intermittent(s):
    if 'output_step_deg' not in s:raise ValueError('Choose index angle before synthesis')
    step=s['output_step_deg'];n=round(360/step);vi=[]
    if n<3 or abs(n*step-360)>1e-8:vi.append('Geneva requires an integer slot count >=3')
    center=s.get('center_distance_mm');assert center and center>0,'Explicit centre distance assumption required'
    ratio=math.sin(math.pi/max(n,3));dwell=.5+1/max(n,3)
    if abs(dwell-s.get('dwell_fraction',dwell))>s.get('dwell_tolerance',.02):vi.append('Specified dwell does not match the external Geneva slot geometry')
    rows=[_candidate('geneva',{'slots':n,'center_distance_mm':center,'crank_radius_mm':center*ratio},dict(moving_links=2,higher_pairs=2,footprint_area_mm2=4*center**2,dwell_fraction=dwell),vi,'Pin engagement and locking disk give geometric indexing and dwell; contact transitions require actual solver validation.','contact-capable NX simulation required; not accepted by a prescribed output driver')]
    rows.append(_candidate('ratchet',{'teeth':n,'step_deg':step},dict(moving_links=5,higher_pairs=2,footprint_area_mm2=3*center**2,dwell_fraction=.5),([] if n>=6 and abs(n*step-360)<1e-8 else ['Tooth count/step incompatible with this ratchet candidate']), 'Continuous input needs an oscillating drive linkage, driving pawl and holding pawl; unilateral contact and retention must be modeled.','not yet a verified NX builder'))
    return rows

def rotary(s):
    target=s.get('signed_speed_ratio');module=s.get('module_mm');limit=s.get('ratio_relative_tolerance',.02)
    if not isinstance(target,(int,float)) or not math.isfinite(target) or target==0 or not module or module<=0:raise ValueError('Signed output/input speed ratio and explicit module assumption required')
    rows=[]
    def add(kind,parameters,ratio,length,width,moving,higher,reason,extra=None):
        violations=list(extra or []);error=abs(ratio-target)/abs(target)
        if error>limit:violations.append('Integer tooth ratio outside specified relative tolerance')
        if s.get('coaxial',False) and kind!='planetary_gears':violations.append('This layout does not provide coaxial input/output')
        rows.append(_candidate(kind,dict(module_mm=module,pressure_angle_deg=20,**parameters),dict(ratio_relative_error=error,signed_speed_ratio=ratio,footprint_area_mm2=length*width,footprint_length_mm=length,footprint_width_mm=width,moving_links=moving,higher_pairs=higher),violations,reason,'Native ideal gear/coupler route; new teeth and layout require actual NX modeling, direction and interference validation'))
    pair=min(((a,b) for a in range(18,81) for b in range(18,101)),key=lambda x:(abs(-x[0]/x[1]-target),sum(x)))
    a,b=pair;distance=module*(a+b)/2;add('external_gear_pair',{'input_teeth':a,'output_teeth':b,'center_distance_mm':distance},-a/b,module*(a+b+4),module*(max(a,b)+2),2,1,'One external mesh reverses rotation; tooth count >=18 avoids the standard 20-degree full-depth undercut threshold, but root stress is not validated.')
    internal=min(((a,b) for a in range(18,61) for b in range(a+20,121)),key=lambda x:(abs(x[0]/x[1]-target),sum(x)))
    a,b=internal;add('internal_gear_pair',{'pinion_teeth':a,'ring_teeth':b,'center_distance_mm':module*(b-a)/2},a/b,module*(b+8),module*(b+8),2,1,'Internal pair has equal rotation signs; this is a geometry and ideal-relation proposal, not force contact.')
    pairs=[(a,b,a/b) for a in range(18,61) for b in range(18,81)]
    first=sorted(pairs,key=lambda x:abs(x[2]-math.sqrt(abs(target))))[:30]
    a,b,c,d=min(((a,b,c,d) for a,b,_ in first for c,d,_ in pairs),key=lambda x:(abs(x[0]*x[2]/(x[1]*x[3])-target),sum(x)))
    add('multistage_gears',{'first_pair':[a,b],'second_pair':[c,d]},a*c/(b*d),module*(a+b+c+d+8)/2,module*(max(a,b,c,d)+2),3,2,'Two external meshes give same-sign output, with a rigid compound shaft and separated gear planes.')
    options=[]
    for sun in range(18,61):
        for planet in range(18,51):
            ring=sun+2*planet
            if (sun+ring)%3 or module*(sun+planet)*math.sin(math.pi/3)<=module*(planet+2):continue
            for fixed,input_member,output_member,ratio in [('ring','sun','carrier',sun/(sun+ring)),('ring','carrier','sun',(sun+ring)/sun),('sun','ring','carrier',ring/(sun+ring)),('carrier','sun','ring',-sun/ring)]:options.append((abs(ratio-target),ring,sun,planet,fixed,input_member,output_member,ratio))
    _,ring,sun,planet,fixed,inp,out,ratio=min(options)
    add('planetary_gears',{'sun_teeth':sun,'planet_teeth':planet,'ring_teeth':ring,'planets':3,'fixed_member':fixed,'input_member':inp,'output_member':out},ratio,module*(ring+8),module*(ring+8),5,6,'Willis equation and three-planet tooth phasing/clearance determine the dimensions; internal geometry, ideal coupling and force contact are separate capabilities.')
    return rows

def synthesize(spec):
    s=validate_spec(spec)
    if s['output_motion']=='reciprocating_translation':rows=reciprocating(s)
    elif s['output_motion']=='intermittent_rotation':rows=intermittent(s)
    elif s['output_motion']=='continuous_rotation':rows=rotary(s)
    else:raise ValueError('This synthesis revision does not silently map an unsupported output to a feeder template')
    for row in rows:
        for key,limit in s.get('maxima',{}).items():
            if key not in row['metrics']:row['violations'].append('Cannot verify required maximum '+key)
            elif row['metrics'][key]>limit:row['violations'].append(key+' exceeds required maximum')
        row['feasible']=not row['violations']
    feasible=[r for r in rows if r['feasible']];objectives=s['objectives']
    if any(any(k not in r['metrics'] for k in objectives) for r in rows):raise ValueError('Objective is not comparable across all candidates')
    def dominates(a,b):return all(a['metrics'][k]<=b['metrics'][k]+1e-10 for k in objectives) and any(a['metrics'][k]<b['metrics'][k]-1e-10 for k in objectives)
    pareto=[r for r in feasible if not any(dominates(a,r) for a in feasible)]
    chosen=min(pareto,key=lambda r:tuple(r['metrics'][k] for k in objectives)) if pareto else None
    return {'requirements':s,'candidates':rows,'pareto_candidates':[r['mechanism'] for r in pareto],'selection_policy':'Hard requirements, Pareto nondominance, then user/cloud-declared ordered numerical objectives. No fixed mechanism scores.','selected':chosen['mechanism'] if chosen else None,'selected_parameters':chosen['parameters'] if chosen else None,'status':'proposal_requires_native_NX_validation' if chosen else 'no_feasible_candidate','nx_acceptance':False,'scope':'Geometric/kinematic synthesis; materials, strength, loads, wear and force contact remain separate checks.'}
