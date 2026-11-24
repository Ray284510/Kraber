"""Realization of a calculated R-R-R-P proposal; contains no held-out request or dimensions."""
import math
import numpy as np
from .calculations import slider_crank,constraint_rank

def plan_from_selection(selection):
    if selection['selected']!='inline_slider_crank' or selection.get('nx_acceptance'):raise ValueError('Requires an unverified synthesized inline-slider-crank proposal')
    q=selection['selected_parameters'];r=q['crank_radius_mm'];rod=q['rod_length_mm'];lo,hi=np.array(q['stroke_range_mm'])/2
    if not 10<=lo<=r<=hi<=40:raise ValueError('Current 6 mm-pin realization supports radii 10..40; synthesize different hardware outside this range')
    # The guide and crank pin head share an axial layer: ensure their swept X envelopes separate.
    minimum_rod=2*hi+10+10+5+.4
    original_rod=rod;rod=max(rod,math.floor(minimum_rod)+1)
    initial=30.;theta=math.radians(initial);A=[r*math.cos(theta),r*math.sin(theta)];B=[float(slider_crank(r,rod,theta)['x']),0];disk=2*(hi+8)
    bearing_diameter=min(18.,2*(lo-5-.4))
    if bearing_diameter<9.2-1e-9:raise ValueError('Bearing wall below 1.4 mm around 6.4 mm bore')
    xmin=-disk/2-10;xmax=rod+hi+20
    joints=[{'name':'Input','type':'Revolute','first':'Crank','second':'Link0','origin':[0,0,18.2],'axis':[0,0,1]},
            {'name':'CrankPin','type':'Revolute','first':'Coupler','second':'Crank','origin':A+[24.6],'axis':[0,0,1]},
            {'name':'SliderPin','type':'Revolute','first':'Coupler','second':'Slider','origin':B+[24.6],'axis':[0,0,1]},
            {'name':'Guide','type':'Slider','first':'Slider','second':'Link0','origin':B+[17.2],'axis':[1,0,0]}]
    calc=slider_crank(r,rod,np.linspace(0,2*np.pi,1441));calc.pop('x');calc.pop('pressure_angle_deg');calc['rank']=constraint_rank(['Link0','Crank','Coupler','Slider'],joints,'Link0')
    return {'schema_version':1,'family':'synthesized_feeder','selection':selection,'units':{'length':'mm','angle':'degree','time':'s'},'realization_adjustments':{'bearing_diameter_mm':bearing_diameter,'bearing_clearance_reason':'Minimum crank radius minus 5 mm pin-head radius and 0.4 mm radial clearance; bore remains 6.4 mm','original_rod_length_mm':original_rod,'final_rod_length_mm':rod,'reason':'Separate actual crank-pin and guide swept envelopes; recompare candidate metrics before building.'},'parameters':{'crank_radius':r,'rod_length':rod,'offset':0,'initial_angle_deg':initial,'stroke_range_mm':q['stroke_range_mm'],'adjustment_slot_radius_range_mm':[float(lo),float(hi)],'crank_disk_diameter':disk,'pin_diameter':6,'slot_width_mm':6.4,'bearing_diameter':bearing_diameter},'layout':{'base_x':(xmin+xmax)/2,'base_length':xmax-xmin,'base_width':max(60,disk+8),'guide_x':rod,'guide_length':2*hi+40},'initial_points':{'O':[0,0],'A':A,'B':B},'joints':joints,'independent_calculation':calc,'drive':{'joint':'Input','velocity_deg_s':q['input_speed_deg_s'],'duration_s':360/q['input_speed_deg_s'],'steps':240},'tolerances':{'marker_mm':.02,'angle_deg':.02,'stroke_mm':.02,'quick_return_ratio':.005},'repair_limit':3,'construction':'New dimensioned proposal from structured cloud reasoning and independent candidate comparison. True radial pin-adjustment slot, separate parts and low-pair native closed loop; new acceptance required. Pin clamp, friction, payload and force control are not validated.'}
