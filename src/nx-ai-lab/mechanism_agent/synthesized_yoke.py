"""Dimension a real slotted yoke, adjustable input disk and captured output guide."""
import math

def plan_from_selection(selection):
    if selection['selected']!='scotch_yoke' or selection.get('nx_acceptance'):raise ValueError('Unverified scotch-yoke selection required')
    q=selection['selected_parameters'];r=q['crank_radius_mm'];lo,hi=[v/2 for v in q['stroke_range_mm']]
    if not 8<=lo<=r<=hi<=40:raise ValueError('Current six-mm crank pin and main shaft require radius range 8..40 mm')
    guide=2*hi+35;end=guide+25+2*hi;left=-hi-15;right=end+10;duration=360/q['input_speed_deg_s']
    p={'crank_radius':r,'minimum_crank_radius':lo,'maximum_crank_radius':hi,'disk_radius':hi+8,'slot_half_length':hi,'pin_radius':3.,'slot_radius':3.2,'guide_x':guide,'arm_end_x':end,'base_x':(left+right)/2,'base_length':right-left,'base_width':2*hi+32}
    for key,limit in selection['requirements'].get('maxima',{}).items():
        actual={'footprint_area_mm2':p['base_length']*p['base_width'],'footprint_length_mm':p['base_length'],'footprint_width_mm':p['base_width']}.get(key)
        if actual is not None and actual>limit:raise ValueError('Realized yoke packaging exceeds '+key+'; recompare alternatives')
    return {'schema_version':1,'family':'synthesized_scotch_yoke','selection':selection,'units':{'length':'mm','angle':'degree','time':'s'},'parameters':p,'joints':[{'name':'Input','type':'Revolute','first':'Crank','second':'Link0','origin':[0,0,14],'axis':[0,0,1]},{'name':'Output','type':'Slider','first':'Yoke','second':'Link0','origin':[r,0,27],'axis':[1,0,0]}],'drive':{'duration_s':duration,'steps':240,'velocity_deg_s':q['input_speed_deg_s']},'independent_calculation':{'moving_links':2,'lower_pairs':2,'higher_pairs':1,'functional_dof':3*2-2*2-1,'stroke_mm':2*r,'quick_return_ratio':1.,'pressure_angle_deg':0.,'position_equation':'x = r cos(theta); slot coordinate = r sin(theta)','dead_centers_deg':[0,180]},'tolerances':{'position_mm':.02,'angle_deg':.02},'repair_limit':3,'construction':'Physical slotted yoke and adjustable crank pin with 0.2 mm radial clearance. Native PointOnCurve constrains pin centre to slot centreline; only input rotation is driven. Ideal bilateral kinematics, no friction, preload, contact-force or wear claim. Ground guide captures the output arm. Adjustability requires relocating/clamping the crank pin between runs; no in-motion adjustment claim.'}
