"""Independent section integration for this end-disc / cylindrical-web coupler.

All lengths are mm. This follows the actual round web geometry; it does not
approximate the web as a rectangular plate or consume NX mass results.
"""
import math
import numpy as np
from numpy.polynomial.legendre import leggauss


def coupler_mass(radius=7., length=40., thickness=6., bore=3.2,
                 density_kg_m3=7800., order=512):
    r=thickness/2
    if not radius>r or not length>2*radius or not 0<bore<radius:
        raise ValueError('Separated end discs with contained bores required')
    a=math.sqrt(radius*radius-r*r)
    u,w=leggauss(order);x=a+(u+1)*(radius-a)/2
    s=np.sqrt(np.maximum(0,radius*radius-x*x));theta=np.arcsin(np.minimum(1,s/r))
    area=2*s*np.sqrt(np.maximum(0,r*r-s*s))+2*r*r*theta
    y2=r**4/2*(theta-np.sin(4*theta)/4)
    z2=r**4*(theta/2+np.sin(2*theta)/3+np.sin(4*theta)/24)
    def integrate(values):return float(w@values)*(radius-a)/2
    overlap=np.array([math.pi*r*r*a+integrate(area),
                      math.pi*r*r*((a-length/2)**3-(-length/2)**3)/3+integrate((x-length/2)**2*area),
                      math.pi*r**4/4*a+integrate(y2),
                      math.pi*r**4/4*a+integrate(z2)])
    def vertical_cyl(R):
        volume=math.pi*R*R*thickness
        return volume*np.array([1,R*R/4+(length/2)**2,R*R/4,thickness*thickness/12])
    web=math.pi*r*r*length*np.array([1,length*length/12,r*r/4,r*r/4])
    volume,x2total,y2total,z2total=2*vertical_cyl(radius)+web-2*overlap-2*vertical_cyl(bore)
    rho=density_kg_m3/1e9
    return {'volume_mm3':float(volume),'mass_kg':float(volume*rho),
            'principal_inertia_kg_mm2':[float(rho*(y2total+z2total)),float(rho*(x2total+z2total)),float(rho*(x2total+y2total))],
            'axis_order':'along web, perpendicular in XY, global Z','quadrature_order':order,
            'basis':'Analytic disc/cylinder moments minus independently integrated overlap and bores; original body density assumption'}
