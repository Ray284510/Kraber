"""Check native measured pawl motion against tooth geometry, not profile generator."""
import json,sys,math
from pathlib import Path
import numpy as np
from phase4_client import LOG

def verify(run,markers):
    data={v['marker']:np.array([a['values'] for a in v['coordinates']]).T for v in markers['markers']};p=run['plan']['initial_points'];d=np.array(p['D']);c=data['RRocker'][:,:2];tip=data['RDriveTip'][:,:2];hold=data['RHoldTip'][:,:2];wheel=data['RWheel'][:,:2]-d
    q=np.unwrap(np.arctan2(wheel[:,1],wheel[:,0]));phi=np.unwrap(np.arctan2(c[:,1]-d[1],c[:,0]-d[0]));advancing=np.r_[np.diff(phi)>1e-6,False]
    v=tip-d;local=np.column_stack([np.cos(q)*v[:,0]+np.sin(q)*v[:,1],-np.sin(q)*v[:,0]+np.cos(q)*v[:,1]])
    # After each index the drive pawl seats in the next physical tooth, not the
    # same tooth index. Fold only by integer tooth pitches, not arbitrary angles.
    pitch=2*math.pi/run['plan']['parameters']['teeth'];az=np.arctan2(local[:,1],local[:,0]);tooth=np.round((az-az[0])/pitch);fold=tooth*pitch
    local=np.column_stack([np.cos(fold)*local[:,0]+np.sin(fold)*local[:,1],-np.sin(fold)*local[:,0]+np.cos(fold)*local[:,1]])
    relative=local-local[0];seat_error=float(max(np.linalg.norm(relative[advancing],axis=1)))
    psi=np.unwrap(np.arctan2(tip[:,1]-c[:,1],tip[:,0]-c[:,0]));hv=hold-np.array(p['hold_pivot']);hpsi=np.unwrap(np.arctan2(hv[:,1],hv[:,0]));lift=np.degrees(psi-psi[0]-(phi-phi[0]));hlift=np.degrees(hpsi-hpsi[0])
    end_indices=[0,240,480,720];repeat=max(float(np.linalg.norm(tip[i]-tip[0])) for i in end_indices);hrepeat=max(float(np.linalg.norm(hold[i]-hold[0])) for i in end_indices)
    drive_seat_angle=math.radians(run['plan']['parameters'].get('drive_face_deg',86));normal=np.array([math.sin(drive_seat_angle),-math.cos(drive_seat_angle)]);gap=local@normal-1.5
    result=dict(scope='Independent verification from actual native markers and actual tooth face orientation; discrete nearest-tooth index is allowed to change each cycle. Does not read generated profile CSVs or call design state function',forward_tip_to_indexed_tooth_error_mm=seat_error,forward_nominal_face_gap_min_mm=float(min(gap[advancing])),forward_nominal_face_gap_max_mm=float(max(gap[advancing])),drive_lift_range_deg=[float(min(lift)),float(max(lift))],holding_lift_range_deg=[float(min(hlift)),float(max(hlift))],drive_tip_max_radius_mm=float(max(np.linalg.norm(v,axis=1))),holding_tip_max_radius_mm=float(max(np.linalg.norm(hold-d,axis=1))),cycle_seat_repeat_error_mm=repeat,holding_seat_repeat_error_mm=hrepeat)
    print(json.dumps(result,indent=2),flush=True)
    assert seat_error<.001 and abs(result['forward_nominal_face_gap_min_mm']-.05)<.001 and abs(result['forward_nominal_face_gap_max_mm']-.05)<.001
    assert repeat<.001 and hrepeat<.001 and max(lift)>34.99 and max(hlift)>34.99
    assert result['drive_tip_max_radius_mm']>26.5 and result['holding_tip_max_radius_mm']>26.5
    result['passed']=True;return result

if __name__=='__main__':
    r=json.loads((LOG/f'current-{sys.argv[1]}.json').read_text());o=Path(r['logs']);v=verify(r,json.loads((o/'motion-results.json').read_text())['markers']);(o/'native-pawl-crosscheck.json').write_text(json.dumps(v,indent=2));print(json.dumps(v,indent=2))
