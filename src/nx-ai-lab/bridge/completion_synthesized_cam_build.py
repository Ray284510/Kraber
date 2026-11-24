import asyncio,math
from phase4_common import Run,Client

from mechanism_agent.synthesized_cam import plan_from_selection

async def main(selection,family):
    plan,law,profile=plan_from_selection(selection);p=plan['parameters'];base=p['pitch_base_radius'];roller=p['roller_radius'];disk=p['disk_radius'];guide=p['guide_center_y'];slider=p['slider_center_y'];layout=plan['layout']
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run(family,plan);run.save('actual-mcp-selection.json',selection)
        for name,offset in [('inner',roller+.2),('outer',-roller-.2),('pitch',0)]:
            (run.model/(name+'.csv')).write_text('\n'.join(','.join(f'{x:.12f}' for x in profile(i*math.pi/360,offset)) for i in range(720)))
        async def new(name):await c.call('nx_create_part '+str(run.model/(name+'.prt')))
        async def finish(name,color):await run.finish(c,name,color)
        await new('cam');await c.call('nx_application modeling');body=await run.cylinder(c,'disk',[0,0],20,2*disk,8)
        body=(await c.call(f'nx_profile_boolean GrooveOuter {run.model/"outer.csv"} 24 4 subtract {body}'))['body_id'];body=(await c.call(f'nx_profile_boolean GrooveInner {run.model/"inner.csv"} 24 4 unite {body}'))['body_id'];body=await run.cylinder(c,'shaft_bore',[0,0],20,8.4,8,body,'subtract');await c.call(f'nx_profile_curve PitchCurve {run.model/"pitch.csv"} 26');await finish('cam',186)
        await new('ground');body=await run.shape(c,'base',[0,layout['base_y'],0],[0,0,1],layout['base_width'],5,kind='box',height=layout['base_length']);body=await run.cylinder(c,'bearing',[0,0],5,20,14.6,body);body=await run.cylinder(c,'bore',[0,0],0,8.4,19.6,body,'subtract')
        for x in [-12,12]:body=await run.shape(c,'guide',[x,guide,5],[0,0,1],10,40,body,kind='box',height=40)
        await finish('ground',103)
        await new('camshaft');body=await run.cylinder(c,'shaft',[0,0],-.2,8,28.2);body=await run.cylinder(c,'head',[0,0],-2.2,12,2,body);await finish('camshaft',36)
        await new('carriage');body=await run.shape(c,'slider',[0,slider,30],[0,0,1],10,6,kind='box',height=40);body=await run.shape(c,'arm',[0,base,33],[0,1,0],6,slider-base,body);body=await run.cylinder(c,'pivot',[0,base],28.4,2*roller,7.6,body);body=await run.cylinder(c,'pin',[0,base],24.4,4,4,body);await finish('carriage',211)
        await new('roller');body=await run.cylinder(c,'roller',[0,base],24.2,2*roller,3.6);body=await run.cylinder(c,'bore',[0,base],24.2,4.2,3.6,body,'subtract');await finish('roller',14)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -');geos={n:await c.call('nx_component_geometry '+n) for n in ['CAM','GROUND','CARRIAGE','ROLLER']};run.save('assembly-geometry.json',geos)
        def cylinder(n,r):
            f=min((f for f in geos[n]['faces'] if f['kind']=='Cylindrical'),key=lambda f:abs(f['radius_mm']-r));assert abs(f['radius_mm']-r)<1e-4;return f['reference']
        def plane(n,axis,sign,value):
            f=min((f for f in geos[n]['faces'] if f['kind']=='Planar' and f['direction'][axis]*sign>.99),key=lambda f:abs(f['point_mm'][axis]-value));assert abs(f['point_mm'][axis]-value)<1e-4;return f['reference']
        await c.call(f'nx_constraint axis CamAxis CAM {cylinder("CAM",4.2)} GROUND {cylinder("GROUND",4.2)} 0 same');await c.call(f'nx_constraint distance CamSeat CAM {plane("CAM",2,-1,20)} GROUND {plane("GROUND",2,1,19.6)} .4 opposite');await c.call('nx_constraint bond ShaftBond CAMSHAFT component CAM component 0 same')
        await c.call(f'nx_constraint distance GuideSide CARRIAGE {plane("CARRIAGE",0,1,5)} GROUND {plane("GROUND",0,-1,7)} 2 opposite');await c.call(f'nx_constraint distance GuideHeight CARRIAGE {plane("CARRIAGE",2,-1,30)} GROUND {plane("GROUND",2,1,5)} 25 opposite')
        await c.call(f'nx_constraint axis RollerAxis ROLLER {cylinder("ROLLER",2.1)} CARRIAGE {cylinder("CARRIAGE",2)} 0 same');await c.call(f'nx_constraint distance RollerSeat ROLLER {plane("ROLLER",2,1,27.8)} CARRIAGE {plane("CARRIAGE",2,-1,28.4)} .6 opposite')
        state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0;inter=await c.call('nx_interference');run.save('static-interference.json',inter);assert all(x['nx_result']!=1 for x in inter['pairs']);await c.call('nx_save_part');await run.export(c,'assembly');run.meta['build_passed']=True;run.pointer()
