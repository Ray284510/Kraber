import asyncio,json,math,re,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG

async def main(kind):
    pointer=LOG/f'current-completion_{kind}.json';run=json.loads(pointer.read_text());m=Path(run['models']);out=Path(run['logs']);p=run['plan'];report={};fixed=p['fixed_member']
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    async with Client() as c:
        assert run['build_passed'] and (await c.call('nx_status'))['part']==run['assembly'];await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
        groups={'Link0':['GROUND',fixed],**{name.title():[name.upper()] for name in ['carrier','sun','ring','planet1','planet2','planet3'] if name.upper()!=fixed}}
        for name,parts in groups.items():await c.call(f'nx_motion_link {name} {int(name=="Link0")} '+' '.join(parts))
        for j in p['joints']:await c.call(f'nx_motion_joint {j["name"]} Revolute {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
        report['couplers']=[];sun_axis='SunAxis' if fixed=='CARRIER' else 'CarrierAxis'
        for i in range(1,4):
            phi=(i-1)*2*math.pi/3;report['couplers'].append(await c.call(f'nx_motion_gear_pair SunMesh{i} {sun_axis} PlanetAxis{i} 30 15 {30*math.cos(phi)} {30*math.sin(phi)} 24'));save()
        report['couplers'].append(await c.call('nx_motion_gear_pair InternalMesh RingAxis PlanetAxis1 60 -15 60 0 24'));save()
        report['unactuated']=await c.call('nx_motion_solution Free articulation 4 240');save();assert report['unactuated']['gruebler_dof']==1
        await c.call(f'nx_motion_driver Input {p["drive"]["joint"]} revolute {p["drive"]["velocity_deg_s"]}')
        for name,point in [('sun',[20,0,24]),('carrier',[0,35,13]),('ring',[64,0,24])]:await c.call('nx_motion_marker G'+name+' '+('Link0' if name.upper()==fixed else name.title())+' '+' '.join(map(str,point)))
        for i,xy in enumerate(p['planet_centers'],1):
            await c.call(f'nx_motion_marker GC{i} Planet{i} {xy[0]} {xy[1]} 24');await c.call(f'nx_motion_marker GT{i} Planet{i} {xy[0]+8} {xy[1]} 24')
        report['driven']=await c.call('nx_motion_solution Cycle normal 4 240');save();assert report['driven']['gruebler_dof']==0
        report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['integrator']=await c.tool('nx_motion_refine_integrator',{'solution':'Cycle','tolerance':1e-9,'maximum_step_s':.001});report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        text=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[l.strip() for l in text.splitlines() if any(k in l.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save();counts=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',text,re.M);assert counts and int(counts[-1])==0 and re.search(r'Number of Drivers\s*=\s*1',text)
        await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.8');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results G');save();data={r['marker']:np.array([a['values'] for a in r['coordinates']]).T for r in report['markers']['markers']};t=np.linspace(0,4,241);speeds=p['expected_speeds'];angles={};checks={}
        def angle(v):return np.degrees(np.unwrap(np.arctan2(v[:,1],v[:,0])))
        for name,offset in [('sun',0),('carrier',90),('ring',0)]:angles[name]=angle(data['G'+name][:,:2])-offset;checks[name+'_angle_error_deg']=float(max(abs(angles[name]-speeds[name]*t)))
        for i in range(1,4):
            center=data[f'GC{i}'][:,:2];tip=data[f'GT{i}'][:,:2];angles[f'planet{i}']=angle(tip-center);checks[f'planet{i}_angle_error_deg']=float(max(abs(angles[f'planet{i}']-speeds['planet']*t)));phi=np.radians(speeds['carrier']*t+120*(i-1));expected=45*np.array([np.cos(phi),np.sin(phi)]).T;checks[f'planet{i}_center_error_mm']=float(max(np.linalg.norm(center-expected,axis=1)))
            checks[f'planet{i}_internal_mesh_angle_residual_deg']=float(max(abs((angles[f'planet{i}']-angles['carrier'])-4*(angles['ring']-angles['carrier']))))
        report['cross_validation']={'checks':checks,'angles_deg':{k:v.tolist() for k,v in angles.items()},'independent_speeds_deg_s':speeds};save();assert all(v<.02 for v in checks.values()),checks
        report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        for frame in range(241):
            await c.call(f'nx_motion_frame Cycle {frame}');check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
            if check['interference_count']:
                report['intersection']=await c.call(f'nx_motion_collision_volumes {frame}');save();await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual planetary variant interference')
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True);pointer.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
