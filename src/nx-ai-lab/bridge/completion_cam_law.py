"""Write calculated displacement, velocity and acceleration without claiming NX measurements."""
import csv,json,sys
from pathlib import Path
import numpy as np
from phase4_common import LOG
from mechanism_agent.completion_cam import pose
def main(kind):
    r=json.loads((LOG/f'current-completion_{kind}.json').read_text());out=Path(r['logs']);h=1e-4;rows=[]
    for t in np.linspace(0,4,1441):
        p=pose(kind,t);before=pose(kind,t-h);after=pose(kind,t+h)
        if 'oscillating' in kind:
            value=p['angle_rad'];v=(after['angle_rad']-before['angle_rad'])/(2*h);a=(after['angle_rad']+before['angle_rad']-2*value)/h**2
        else:value=p['point'][1];v=p['velocity_mm_s'];a=p['acceleration_mm_s2']
        rows.append([t,value,v,a,p['pressure_deg']])
    with (out/'independent-cam-law.csv').open('w',newline='') as f:
        w=csv.writer(f);w.writerow(['time_s','displacement_rad_or_mm','velocity_per_s','acceleration_per_s2','pressure_deg']);w.writerows(rows)
    (out/'independent-cam-law-method.json').write_text(json.dumps({'source':'Analytic circle closure; not native solver results','units':'rad, rad/s, rad/s2' if 'oscillating' in kind else 'mm, mm/s, mm/s2','derivatives':'central differences h=0.0001 s of analytic angular closure' if 'oscillating' in kind else 'closed-form first and second derivatives','original_pivot_rejected':{'xy_mm':[-55,45],'reason':'oscillating sharp tip maximum pressure angle 53.61 degrees'},'selected_pivot_mm':[-50,40]},indent=2))
if __name__=='__main__':main(sys.argv[1])
