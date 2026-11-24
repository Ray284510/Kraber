"""Export actual NX contact input in new diagnostic SIMs; never grant acceptance."""
import asyncio,json,sys,hashlib,shutil,re
from pathlib import Path
from phase4_common import Client,LOG


async def main(family,tag='diagnostic'):
    assert family.startswith('finalization_geneva_')
    assert re.fullmatch('[a-z0-9_]+',tag)
    run=json.loads((LOG/f'current-{family}.json').read_text());model=Path(run['models']);out=Path(run['logs'])
    assert run['build_passed'];report={'scope':'Native export diagnostics only; no solver run or mechanism acceptance','modes':{}}
    target=out/('contact-export-audit-'+tag+'.json');assert not target.exists()
    def save():target.write_text(json.dumps(report,indent=2))
    async with Client() as client:
        for mode in ['body','face']:
            status=await client.call('nx_status')
            if not status.get('part'):await client.call('nx_open_part '+run['assembly'])
            else:assert status['part']==run['assembly']
            stem=mode+'_'+tag
            await client.call('nx_motion_new '+str(model/(stem+'.sim')));await client.call('nx_motion_setup');await client.call('nx_motion_analysis dynamics')
            for name,component in [('Link0','GROUND'),('InputLink','INPUT'),('OutputLink','OUTPUT')]:await client.call(f'nx_motion_link {name} {int(name=="Link0")} {component}')
            for joint in run['plan']['joints']:await client.call(f'nx_motion_joint {joint["name"]} Revolute {joint["first"]} {joint["second"]} '+' '.join(map(str,joint['origin']+joint['axis'])))
            item={};report['modes'][mode]=item
            if mode=='body':item['contact']=await client.call('nx_motion_body_contact Contact INPUT OUTPUT 1000 .1 .001')
            else:
                refs={};item['actual_geometry']={}
                for name in ['INPUT','OUTPUT']:
                    geometry=await client.call('nx_component_geometry '+name);item['actual_geometry'][name]=geometry
                    faces=[f for f in geometry['faces'] if f['bbox_mm'][5]-f['bbox_mm'][2]>1e-6 and f['bbox_mm'][5]>20.4+1e-8 and f['bbox_mm'][2]<27.6-1e-8]
                    assert faces;refs[name]=','.join(f['reference'] for f in faces)
                item['contact']=await client.call(f'nx_motion_body_face_contact Contact INPUT {refs["INPUT"]} OUTPUT {refs["OUTPUT"]} 1000 .1 .001')
            item['model']=await client.call('nx_motion_contact_model Contact midplane')
            await client.call('nx_motion_driver InputDrive Input revolute 90');item['solution']=await client.call('nx_motion_solution Cycle normal 4 240');await client.call('nx_motion_integrator_refine Cycle 1e-9 .0001');await client.call('nx_save_part');save()
            item['export']=await client.call('nx_motion_export_input Cycle',True);save()
            capture=out/('native-export-'+stem);capture.mkdir(exist_ok=False)
            item['files']=[]
            for p in model.glob(stem+'-*'):
                if p.is_file():
                    dest=capture/p.name;shutil.copy2(p,dest)
                    item['files'].append({'name':p.name,'size':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'captured':str(dest)})
            save()
            await client.call('nx_save_part');await client.call('nx_close_part 0')
            assert item['export']['ok']
    print(target)


if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
