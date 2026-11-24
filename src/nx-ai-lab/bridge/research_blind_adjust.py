"""Reconfigure the SAME manufactured part geometry at another crank-pin clamp radius.
New assemblies are isolated; every copied part must remain byte-identical.
"""
import asyncio,json,sys,copy,shutil,hashlib,math
from pathlib import Path
from phase4_common import Run,Client,LOG

async def main(source_family,family,radius):
    base=json.loads((LOG/f'current-{source_family}.json').read_text());assert base['motion_passed'] and base['plan']['selection']['selected']=='scotch_yoke';plan=copy.deepcopy(base['plan']);p=plan['parameters'];old=p['crank_radius'];radius=float(radius);assert p['minimum_crank_radius']<=radius<=p['maximum_crank_radius'] and radius!=old;dx=radius-old;p['crank_radius']=radius;plan['independent_calculation']['stroke_mm']=2*radius
    for j in plan['joints']:
        if j['name']=='Output':j['origin'][0]=radius
    plan['adjustment_configuration']={'nominal_source':base['models'],'source_radius_mm':old,'new_radius_mm':radius,'mode':'Stop then reposition/reclamp original pin and shift free output guide coordinate. No geometry resizing or new mechanism selection.','selection_record_scope':'Original blind selection remains unchanged; this is its operating range verification.'};plan['family']=family

    async with Client() as c:
        status=await c.call('nx_status');assert status.get('part') in [None,'',base['simulation']]
        if status.get('part'):await c.call('nx_save_part');await c.call('nx_close_part 0')
        run=Run(family,plan);hashes={}
        for name in base['part_names']:
            source=Path(base['models'])/(name+'.prt');dest=run.model/(name+'.prt');hashes[name]=hashlib.sha256(source.read_bytes()).hexdigest();shutil.copy2(source,dest);await c.call('nx_open_part '+str(dest));first=await c.call('nx_inspect');assert first['failed_feature_count']==0 and all(b['consistency_fault_count']==0 and b['solid'] for b in first['bodies']);await run.export(c,name);await c.call('nx_close_part 0');await c.call('nx_open_part '+str(dest));second=await c.call('nx_inspect');assert first['bodies'][0]['volume_mm3']==second['bodies'][0]['volume_mm3'];await c.call('nx_close_part 0');run.parts[name]={'created':first,'reopened':second}
        run.meta['part_names']=list(run.parts);run.save('parts-verified.json',run.parts);await run.add_all(c)
        for name in ['PIN','YOKE']:await c.call(f'nx_move_component {name} {dx} 0 0')
        await c.call('nx_constraint fix GroundFixed GROUND component -')
        for kind,key in [('touch','point:BASE_P'),('align','curve:BASE_A')]:await c.call(f'nx_constraint {kind} Input_{kind} CRANK {key} GROUND {key} 0 same')
        await c.call('nx_constraint bond PinClamped PIN component CRANK component 0 same');await c.call('nx_constraint bond ShaftFixed SHAFT component GROUND component 0 same')
        geo={n:await c.call('nx_component_geometry '+n) for n in ['YOKE','GROUND']};run.save('guide-geometry.json',geo)
        def plane(n,axis,sign,value):
            f=min((f for f in geo[n]['faces'] if f['kind']=='Planar' and f['direction'][axis]*sign>.99),key=lambda f:abs(f['point_mm'][axis]-value));assert abs(f['point_mm'][axis]-value)<1e-4;return f['reference']
        await c.call(f'nx_constraint distance GuideSide YOKE {plane("YOKE",1,1,4.8)} GROUND {plane("GROUND",1,-1,5)} .2 opposite');await c.call(f'nx_constraint distance GuideHeight YOKE {plane("YOKE",2,1,30)} GROUND {plane("GROUND",2,-1,30.4)} .4 opposite')
        state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0
        for row in state['components']:
            expected=dx if row['name'] in ['PIN','YOKE'] else 0
            assert max(abs(x-y) for x,y in zip(row['position_mm'],[expected,0,0]))<1e-6
        inter=await c.call('nx_interference');run.save('static-interference.json',inter);assert all(v['nx_result']!=1 for v in inter['pairs']);await c.call('nx_save_part');await run.export(c,'assembly')
        same={n:hashlib.sha256((run.model/(n+'.prt')).read_bytes()).hexdigest()==h for n,h in hashes.items()};run.save('identical-part-geometry.json',{'source_sha256':hashes,'byte_identical_after_actual_assembly':same,'adjustment':plan['adjustment_configuration']});assert all(same.values());run.meta.update(build_passed=True,verified_scope=plan['construction']+' Same-part-hash operating adjustment verification.');run.pointer()

if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
