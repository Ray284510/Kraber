"""Independent derivatives of native positions; preserve the measured data and units."""
import json,csv,math,sys
from pathlib import Path
import numpy as np
from phase4_client import LOG

def main(family):
    run=json.loads((LOG/f'current-{family}.json').read_text());o=Path(run['logs']);d=json.loads((o/'motion-results.json').read_text());assert d['simulation_passed'];row=next(v for v in d['markers']['markers'] if v['marker']=='YOutput');full=np.array(row['coordinates'][0]['values']);x=full[:-1];period=run['plan']['drive']['duration_s'];dt=period/len(x);t=np.arange(len(x))*dt;r=run['plan']['parameters']['crank_radius'];omega=2*math.pi/period
    velocity=(np.roll(x,2)-8*np.roll(x,1)+8*np.roll(x,-1)-np.roll(x,-2))/(12*dt);accel=(-np.roll(x,-2)+16*np.roll(x,-1)-30*x+16*np.roll(x,1)-np.roll(x,2))/(12*dt*dt)
    v_error=float(max(abs(velocity+r*omega*np.sin(omega*t))));a_error=float(max(abs(accel+r*omega**2*np.cos(omega*t))))
    report={'source':'Actual NX native YOutput position samples; velocity/acceleration are periodic five-point finite differences, not native reaction/force output.','sampling_interval_s':dt,'endpoint_closure_mm':float(abs(full[-1]-full[0])),'peak_velocity_mm_s':float(max(abs(velocity))),'peak_acceleration_mm_s2':float(max(abs(accel))),'velocity_check_error_mm_s':v_error,'acceleration_check_error_mm_s2':a_error,'velocity_limit_mm_s':r*omega*.01,'acceleration_limit_mm_s2':r*omega**2*.01,'criteria':'1 percent of independently calculated harmonic peak for numerical derivatives; preserves all original position and zero-positive-intersection gates. No continuous-time force or wear claim.'}
    report['passed']=report['endpoint_closure_mm']<.001 and v_error<report['velocity_limit_mm_s'] and a_error<report['acceleration_limit_mm_s2'];(o/'native-speed-crosscheck.json').write_text(json.dumps(report,indent=2))
    with (o/'native-displacement-speed-acceleration.csv').open('w',newline='') as f:
        w=csv.writer(f);w.writerow(['time_s','native_x_mm','derived_velocity_mm_s','derived_acceleration_mm_s2']);w.writerows(zip(t,x,velocity,accel))
    svg=['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="770" viewBox="0 0 1000 770"><rect width="1000" height="770" fill="white"/><g font-family="Arial" fill="#111"><text x="60" y="28" font-size="19">Actual NX trajectory: '+family+'</text><text x="60" y="52" font-size="13">Native positions; velocity and acceleration from five-point finite differences. Not a force simulation.</text>']
    for k,(values,label,color) in enumerate([(x,'Displacement (mm)','#2563eb'),(velocity,'Velocity (mm/s)','#15803d'),(accel,'Acceleration (mm/s^2)','#b91c1c')]):
        top=85+k*210;limit=max(abs(values))*1.15;svg.append(f'<text x="60" y="{top}" font-size="15">{label}</text>')
        for f in [-1,0,1]:
            y=top+95-f*75;svg.append(f'<path d="M100 {y}H950" stroke="#ddd"/><text x="35" y="{y+4}" font-size="12">{f*limit:.2f}</text>')
        points=' '.join(f'{100+850*tt/period:.3f},{top+95-vv/limit*75:.3f}' for tt,vv in zip(t,values));svg.append(f'<polyline points="{points}" fill="none" stroke="{color}" stroke-width="2"/>')
        for second in np.linspace(0,period,5):svg.append(f'<text x="{100+850*second/period:.1f}" y="{top+190}" font-size="12">{second:.1f}s</text>')
    svg.append('</g></svg>');(o/'native-trajectory.svg').write_text(''.join(svg));print(json.dumps(report,indent=2));assert report['passed']
if __name__=='__main__':main(sys.argv[1])
