"""Isolated physical contact/license probe. Never registered as an accepted mechanism."""
import asyncio,json,math,re,sys
import numpy as np
from phase4_common import Run,Client

async def main():
    wide="--wide" in sys.argv;output_face=12 if wide else 10;step=.000001 if wide else .0001
    plan={'schema_version':1,'family':'completion_contact_probe','units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'cube_edge_mm':10,'density_kg_m3':7800,'initial_gap_mm':1,'input_speed_mm_s':1,'gravity':0,'external_load':0,'contact_stiffness_native':1000,'contact_damping_native':.1,'contact_force_exponent':1,'contact_max_depth_mm':.001},'joints':[{'name':'InputSlide','type':'Slider','first':'Input','second':'Link0','origin':[5,0,25],'axis':[1,0,0]},{'name':'OutputSlide','type':'Slider','first':'Output','second':'Link0','origin':[16,0,25],'axis':[1,0,0]}],'drive':{'duration_s':2,'steps':200},'tolerances':{'mass_kg':1e-8,'inertia_kg_mm2':1e-5},'repair_limit':3,'independent_calculation':{'each_moving_mass_kg':.0078,'each_principal_inertia_kg_mm2':.13,'nominal_first_contact_s':1},'construction':'Two 10 mm steel-density cubes on ideal translational joints; one prescribed input, output driven only by native body contact. No gravity or external load. Numerical contact parameters are experimental, not material calibration. This is a solver capability probe, not a finished mechanism or zero-penetration acceptance.'}
    plan["parameters"].update(output_dimensions_mm=[10,output_face,output_face],contact_model="Midplane" if wide else "ProjectedVertex",integration_max_step_s=step)
    output_mass=10*output_face**2*7800/1e9
    plan['independent_calculation']={'input_mass_kg':.0078,'input_principal_inertia_kg_mm2':[.13]*3,'output_mass_kg':output_mass,'output_principal_inertia_kg_mm2':[output_mass*output_face**2/6,output_mass*(100+output_face**2)/12,output_mass*(100+output_face**2)/12],'nominal_first_contact_s':1}
    if wide:plan["construction"]+=" Repair 3 uses a 10x12x12 mm output pad with unchanged centre/axis and 1 mm initial gap, avoiding coincident face boundaries. Original equal-cube failures remain separate."

    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run('completion_contact_probe',plan)
        for name,center,width,height,depth,color in [('ground',[10,0,0],40,30,5,103),('input',[5,0,20],10,10,10,186),('output',[16,0,25-output_face/2],10,output_face,output_face,36)]:
            await c.call('nx_create_part '+str(run.model/(name+'.prt')));await c.call('nx_application modeling');await run.shape(c,name,center,[0,0,1],width,depth,kind='box',height=height);run.save(name+'-density.json',await c.call('nx_body_density 7800'));await run.finish(c,name,color)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -');await c.call('nx_save_part');await run.export(c,'assembly')
        await c.call('nx_motion_new '+str(run.model/'contact_probe.sim'));await c.call('nx_motion_setup');report={}
        def save():run.save('contact-probe-results.json',report)
        report['analysis']=await c.call('nx_motion_analysis dynamics',True);save()
        if not report['analysis']['ok']:return
        for name,component in [('Link0','GROUND'),('Input','INPUT'),('Output','OUTPUT')]:await c.call(f'nx_motion_link {name} {int(name=="Link0")} {component}')
        for j in plan['joints']:await c.call(f'nx_motion_joint {j["name"]} Slider {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
        report['contact']=await c.call('nx_motion_body_contact PushContact INPUT OUTPUT 1000 .1 .001',True);save()
        if not report['contact']['ok']:await c.call('nx_save_part');return
        if wide:report['contact_model_edit']=await c.call('nx_motion_contact_model PushContact midplane');save()
        await c.call('nx_motion_driver InputDrive InputSlide slider 1');await c.call('nx_motion_marker PInput Input 5 0 25');await c.call('nx_motion_marker POutput Output 16 0 25')
        report['solution']=await c.call('nx_motion_solution Contact normal 2 200');report['mass']=await c.tool('nx_motion_mass_state',{});save()
        for link in report['mass']['links']:
            if link['fixed_link']:continue
            mass=.0078 if link['name']=='Input' else 10*output_face**2*7800/1e9;inertia=[.13]*3 if link['name']=='Input' else [mass*output_face**2/6,mass*(100+output_face**2)/12,mass*(100+output_face**2)/12]
            assert link['mass_unit']=='Kilogram' and link['inertia_unit']=='KilogramMilliMeterSquared';assert abs(link['mass']-mass)<1e-8 and max(abs(link[k]-v) for k,v in zip(['ixx','iyy','izz'],inertia))<1e-5
        report['precision']=await c.call(f'nx_motion_integrator_refine Contact 1e-9 {step}');await c.call('nx_save_part');report['solve']=await c.call('nx_motion_solve Contact',True);save()
        if not report['solve']['ok']:return
        await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso .8');await c.call('nx_motion_frame Contact 0');report['markers']=await c.call('nx_motion_marker_results P');save();data={r['marker']:np.array([a['values'] for a in r['coordinates']]).T for r in report['markers']['markers']};a,b=data['PInput'],data['POutput'];assert len(a)==201 and len(b)==201
        report['observations']={'input_displacement_mm':float(a[-1,0]-a[0,0]),'output_displacement_mm':float(b[-1,0]-b[0,0]),'sampled_max_penalty_overlap_mm':float(max(0,np.max(a[:,0]+10-b[:,0]))),'before_contact_output_drift_mm':float(max(abs(b[:95,0]-16))),'mass_and_inertia_basis':'Real NX solid volume at 7800 kg/m^3; each input/output mass and three principal inertias checked against the recorded actual box dimensions','acceptance_scope':'Capability probe only; positive penalty overlap is reported, never relabeled as zero interference.'};save()
        await c.call('nx_motion_frame Contact finish');await c.call('nx_save_part');report['native_contact_motion_observed']=report['observations']['output_displacement_mm']>.5 and report['observations']['before_contact_output_drift_mm']<.001;save();run.meta.update(simulation=str(run.model/'contact_probe.sim'),solution='Contact',probe_only=True);run.pointer()
        if wide:
            from mechanism_agent.repair import RepairBudget
            from phase4_common import ROOT
            speed=float(np.mean(np.diff(b[-21:,0]))/.01);native=(run.model/'contact_probe-contact.minf').read_text(errors='replace');bad=any(x in native.lower() for x in ['not apply force','cannot be closed']);report['native_warnings']=[v.strip() for v in native.splitlines() if any(k in v.lower() for k in ['warning','error','not apply force'])];report['final_output_speed_mm_s']=speed;report['physics_check_passed']=1<=speed<=2.01 and not bad and report['observations']['before_contact_output_drift_mm']<.001;save()
            RepairBudget(ROOT/'logs/phase4_completion/repairs','native-contact-energy').finish(3,{'final_output_speed_mm_s':speed,'warnings':report['native_warnings'],'geometry':'10x12x12 output pad; original 10 mm cube not retroactively accepted'},run.model/'contact_probe.sim',run.out/'contact-probe-results.json',report['physics_check_passed'])
            print(json.dumps({'physics_check_passed':report['physics_check_passed'],'speed':speed}))
if __name__=='__main__':asyncio.run(main())
