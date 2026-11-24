import asyncio
from phase4_common import Run,Client

from mechanism_agent.synthesized_yoke import plan_from_selection

async def main(selection,family):
    plan=plan_from_selection(selection);p=plan['parameters'];r=p['crank_radius'];hi=p['maximum_crank_radius'];lo=p['minimum_crank_radius'];g=p['guide_x'];end=p['arm_end_x']
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run(family,plan);run.save('actual-mcp-selection.json',selection)
        async def new(n):await c.call('nx_create_part '+str(run.model/(n+'.prt')));await c.call('nx_application modeling')
        async def done(n,color):await run.finish(c,n,color)
        async def slot(body,name,x0,y0,x1,y1,z,depth):
            for x,y in sorted(set([(x0,y0),(x1,y1)])):body=await run.cylinder(c,name,[x,y],z,6.4,depth,body,'subtract')
            if x0!=x1:body=await run.shape(c,name+'web',[(x0+x1)/2,y0,z],[0,0,1],abs(x1-x0),depth,body,'subtract',kind='box',height=6.4)
            elif y0!=y1:body=await run.shape(c,name+'web',[x0,(y0+y1)/2,z],[0,0,1],6.4,depth,body,'subtract',kind='box',height=abs(y1-y0))
            return body
        await new('ground');body=await run.shape(c,'base',[p['base_x'],0,0],[0,0,1],p['base_length'],5,kind='box',height=p['base_width']);body=await run.cylinder(c,'bearing',[0,0],5,18,8.6,body);body=await run.cylinder(c,'bore',[0,0],0,6.4,13.6,body,'subtract')
        for y in [-9,9]:body=await run.shape(c,'rail',[g,y,5],[0,0,1],30,29,body,kind='box',height=8)
        body=await run.shape(c,'roof',[g,0,30.4],[0,0,1],30,3.6,body,kind='box',height=26);await c.call('nx_joint_reference BASE 0 0 14 0 0 1');await done('ground',103)
        await new('crank');body=await run.cylinder(c,'disk',[0,0],14,2*p['disk_radius'],6);body=await run.cylinder(c,'bore',[0,0],14,6.4,6,body,'subtract');body=await slot(body,'adjustment',lo,0,hi,0,14,6);await c.call('nx_joint_reference BASE 0 0 14 0 0 1');await done('crank',186)
        await new('yoke');body=await run.shape(c,'frame',[r,0,24],[0,0,1],20,6,kind='box',height=2*hi+18);body=await run.shape(c,'output_arm',[(r+8+end)/2,0,24],[0,0,1],end-r-8,6,body,kind='box',height=9.6);body=await slot(body,'vertical_slot',r,-hi,r,hi,24,6);await c.call(f'nx_curve_line SLOT_CENTER {r} {-hi-1} 27 {r} {hi+1} 27');await done('yoke',211)
        await new('pin');body=await run.cylinder(c,'pin',[r,0],13.8,6,20.6);body=await run.cylinder(c,'head',[r,0],34.2,10,2,body);await done('pin',36)
        await new('shaft');body=await run.cylinder(c,'shaft',[0,0],-.2,6,20.4);body=await run.cylinder(c,'head',[0,0],-2.2,10,2,body);await done('shaft',192)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -')
        for kind,key in [('touch','point:BASE_P'),('align','curve:BASE_A')]:await c.call(f'nx_constraint {kind} Input_{kind} CRANK {key} GROUND {key} 0 same')
        await c.call('nx_constraint bond PinClamped PIN component CRANK component 0 same');await c.call('nx_constraint bond ShaftFixed SHAFT component GROUND component 0 same')
        geo={name:await c.call('nx_component_geometry '+name) for name in ['YOKE','GROUND']};run.save('guide-geometry.json',geo)
        def plane(n,axis,sign,value):
            f=min((f for f in geo[n]['faces'] if f['kind']=='Planar' and f['direction'][axis]*sign>.99),key=lambda f:abs(f['point_mm'][axis]-value));assert abs(f['point_mm'][axis]-value)<1e-4;return f['reference']
        await c.call(f'nx_constraint distance GuideSide YOKE {plane("YOKE",1,1,4.8)} GROUND {plane("GROUND",1,-1,5)} .2 opposite');await c.call(f'nx_constraint distance GuideHeight YOKE {plane("YOKE",2,1,30)} GROUND {plane("GROUND",2,-1,30.4)} .4 opposite')
        state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0 and all(v['dof_result']=='Success' for v in state['components'])
        inter=await c.call('nx_interference');run.save('static-interference.json',inter);assert all(v['nx_result']!=1 for v in inter['pairs']);await c.call('nx_save_part');await run.export(c,'assembly');run.meta['build_passed']=True;run.pointer()
