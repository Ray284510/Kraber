import asyncio,sys
from phase4_common import Run,Client
from completion_helpers import rectangle,datum,hinges_and_slider,finish_assembly
from mechanism_agent.completion_inversions import design

async def main(kind):
    p=design(kind);refs={};B,A,C,u=[p['initial'][k] for k in ['B','A','C','u']]
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run('completion_'+kind,p)
        async def finish(name,color):await run.finish(c,name,color);run.save('joint-references.json',refs)
        await c.call('nx_create_part '+str(run.model/'ground.prt'));await c.call('nx_application modeling')
        if kind!='fixed_block':
            body=await run.shape(c,'base',[40,0,0],[0,0,1],120,4,kind='box',height=45)
            top=28.0 if kind=='guide_bar' else 28.4
            for xy,height in [(A,18.),(B,top)]:body=await run.cylinder(c,'bearing',xy,4,18,height-4,body);body=await run.cylinder(c,'bore',xy,0,6.4,height,body,'subtract')
            pivot='GuidePivot' if kind=='guide_bar' else 'BlockPivot';await datum(c,refs,'ground','InputPivot',A+[18.2]);await datum(c,refs,'ground',pivot,B+[top+.2]);await finish('ground',103)
            await run.bar(c,'input',A,C,18.4,width=18);await datum(c,refs,'input','InputPivot',A+[18.2]);await datum(c,refs,'input','MovingPin',C+[27.6]);await finish('input',186)
            if kind=='guide_bar':
                await c.call('nx_create_part '+str(run.model/'guide.prt'));body=await run.cylinder(c,'pivot',B,28.4,18,10.8);body=await rectangle(run,c,'root',B,u,0,35,18,28.4,10.8,body)
                body=await rectangle(run,c,'guide_blank',B,u,25,135,20.4,28.4,10.8,body)
                for name,width,z,depth in [('pin_channel',6.4,28.4,2.2),('block_channel',12.4,30.6,6.4),('collar_channel',9.4,37.0,2.2)]:body=await rectangle(run,c,name,B,u,35,125,width,z,depth,body,'subtract')
                body=await run.cylinder(c,'pivot_bore',B,28.4,6.4,10.8,body,'subtract');await datum(c,refs,'guide',pivot,B+[28.2]);await finish('guide',36)
                await c.call('nx_create_part '+str(run.model/'slider.prt'));body=await rectangle(run,c,'slider_blank',C,u,-6,6,12,30.8,6);body=await run.cylinder(c,'slider_bore',C,30.8,6.4,6,body,'subtract');await datum(c,refs,'slider','MovingPin',C+[27.6]);await finish('slider',211)
                hinges=[('InputPivot','input','ground'),('MovingPin','slider','input'),(pivot,'guide','ground')];slider=('slider','guide',u,6,6.2,30.8,30.6);groups={'Link0':['GROUND','PIN_A','PIN_B'],'Input':['INPUT','PIN_C','COLLAR_C'],'Guide':['GUIDE'],'Slider':['SLIDER']};pin_b_end=39.1
            else:
                await c.call('nx_create_part '+str(run.model/'block.prt'));body=await rectangle(run,c,'sleeve_blank',B,u,-12,12,20.4,28.8,10);body=await rectangle(run,c,'rod_passage',B,u,-12,12,8.4,30.6,6.4,body,'subtract');body=await run.cylinder(c,'pivot_bore',B,28.8,6.4,1.8,body,'subtract');await datum(c,refs,'block',pivot,B+[28.6]);await finish('block',36)
                await c.call('nx_create_part '+str(run.model/'rod.prt'));body=await rectangle(run,c,'sliding_rod',C,u,-150,0,8,30.8,6);body=await run.cylinder(c,'rod_eye',C,30.8,18,6,body);body=await run.cylinder(c,'rod_eye_bore',C,30.8,6.4,6,body,'subtract');await datum(c,refs,'rod','MovingPin',C+[27.6]);await finish('rod',211)
                hinges=[('InputPivot','input','ground'),('MovingPin','rod','input'),(pivot,'block','ground')];slider=('rod','block',u,4,4.2,30.8,30.6);groups={'Link0':['GROUND','PIN_A','PIN_B'],'Input':['INPUT','PIN_C','COLLAR_C'],'Block':['BLOCK'],'Rod':['ROD']};pin_b_end=30.5
            pins=[('pin_a',A,0,24.3,-2),('pin_b',B,0,pin_b_end,-2),('pin_c',C,18.4,39.1,16.4)];collars=[('collar_c',C,37.)]
        else:
            body=await run.shape(c,'base',[0,0,0],[0,0,1],50,4,kind='box',height=55);body=await run.cylinder(c,'lever_bearing',B,4,18,14,body);body=await run.cylinder(c,'lever_bore',B,0,6.4,18,body,'subtract')
            for y in [-18,18]:body=await run.shape(c,'guide_support',[0,y,4],[0,0,1],24,35,body,kind='box',height=6)
            body=await run.shape(c,'fixed_sleeve',[0,0,39],[0,0,1],24,14.4,body,kind='box',height=42);body=await run.shape(c,'rod_passage',[0,0,43],[0,0,1],24,6.4,body,'subtract',kind='box',height=8.4)
            await datum(c,refs,'ground','LeverPivot',B+[18.2]);await finish('ground',103)
            await run.bar(c,'lever',B,A,18.4,width=18);await datum(c,refs,'lever','LeverPivot',B+[18.2]);await datum(c,refs,'lever','LeverPin',A+[27.6]);await finish('lever',186)
            await run.bar(c,'connecting',A,C,30.8,width=18);await datum(c,refs,'connecting','LeverPin',A+[27.6]);await datum(c,refs,'connecting','RodPin',C+[40]);await finish('connecting',36)
            await c.call('nx_create_part '+str(run.model/'rod.prt'));body=await rectangle(run,c,'sliding_rod',C,u,-175,0,8,43.2,6);body=await run.cylinder(c,'rod_eye',C,43.2,18,6,body);body=await run.cylinder(c,'rod_bore',C,43.2,6.4,6,body,'subtract');await datum(c,refs,'rod','RodPin',C+[40]);await finish('rod',211)
            hinges=[('LeverPivot','lever','ground'),('LeverPin','connecting','lever'),('RodPin','connecting','rod')];slider=('rod','ground',u,4,4.2,43.2,43.0);groups={'Link0':['GROUND','PIN_B'],'Lever':['LEVER','PIN_A','COLLAR_A'],'Connecting':['CONNECTING'],'Rod':['ROD','PIN_C','COLLAR_C']}
            pins=[('pin_b',B,0,24.3,-2),('pin_a',A,18.4,39.1,16.4),('pin_c',C,30.6,51.5,28.6)];collars=[('collar_a',A,37),('collar_c',C,49.4)]
        for name,xy,z,end,head_z in pins:
            await c.call('nx_create_part '+str(run.model/(name+'.prt')));body=await run.cylinder(c,'shaft',xy,z,6,end-z);body=await run.cylinder(c,'head',xy,head_z,9,2,body);await finish(name,192)
        for name,xy,z in collars:
            await c.call('nx_create_part '+str(run.model/(name+'.prt')));body=await run.cylinder(c,'collar',xy,z,9,2);body=await run.cylinder(c,'collar_bore',xy,z,6.1,2,body,'subtract');await finish(name,192)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -');await hinges_and_slider(run,c,refs,hinges,slider)
        primary={'Link0':'GROUND','Input':'INPUT','Guide':'GUIDE','Slider':'SLIDER','Block':'BLOCK','Rod':'ROD','Lever':'LEVER','Connecting':'CONNECTING'}
        for link,names in groups.items():
            for name in names:
                if name!=primary[link]:await c.call(f'nx_constraint bond {name}_rigid {name} component {primary[link]} component 0 same')
        await finish_assembly(run,c,{name:link for link,names in groups.items() for name in names})
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
