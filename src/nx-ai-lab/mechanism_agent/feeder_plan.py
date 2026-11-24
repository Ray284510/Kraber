"""Functional feeder request -> selected inline adjustable slider crank."""
import math
import numpy as np
from .selection import interpret, select
from .calculations import slider_crank, constraint_rank

REQUEST='设计一个自动往复送料机构，要求输入连续旋转运动，输出具有可调行程的往复直线运动，并尽量减小急回特性。'


def design(radius=20.):
    if not 15<=radius<=25:raise ValueError('Chosen adjustment slot supports radius 15..25 mm')
    choice=select(interpret(REQUEST));assert choice['selected']=='inline_slider_crank'
    theta=math.radians(30);rod=100.;A=[radius*math.cos(theta),radius*math.sin(theta)];B=[float(slider_crank(radius,rod,theta)['x']),0]
    joints=[{'name':'Input','type':'Revolute','first':'Crank','second':'Link0','origin':[0,0,18.2],'axis':[0,0,1]},
            {'name':'CrankPin','type':'Revolute','first':'Coupler','second':'Crank','origin':A+[24.6],'axis':[0,0,1]},
            {'name':'SliderPin','type':'Revolute','first':'Coupler','second':'Slider','origin':B+[24.6],'axis':[0,0,1]},
            {'name':'Guide','type':'Slider','first':'Slider','second':'Link0','origin':B+[17.2],'axis':[1,0,0]}]
    rank=constraint_rank(['Link0','Crank','Coupler','Slider'],joints,'Link0');assert rank['functional_dof']==1
    metrics=slider_crank(radius,rod,np.linspace(0,2*math.pi,1201));metrics.pop('x');metrics.pop('pressure_angle_deg')
    return {'schema_version':1,'family':'feeder','selection':choice,'units':{'length':'mm','angle':'degree','time':'s'},
      'parameters':{'crank_radius':radius,'rod_length':rod,'initial_angle_deg':30,'offset':0,'stroke_range_mm':[30,50],'validation_radii_mm':[15,20,25],'adjustment_slot_radius_range_mm':[15,25],'slot_width_mm':6.4,'pin_diameter':6,'crank_disk_diameter':66,'crank_thickness':6,'coupler_thickness':6,'coupler_end_diameter':14,'slider_size_mm':[20,18,14.4],'guide_side_clearance_mm':1,'axial_link_clearance_mm':.4},
      'initial_points':{'O':[0,0],'A':A,'B':B},'joints':joints,'independent_calculation':{**metrics,'rank':rank,'functional_mobility':1,'dimension_reason':'Rod length is four times the largest crank radius, bounding pressure angle below 14.48 degrees. Inline geometry gives equal half-turn forward and return strokes. 66 mm disk leaves 4.8 mm material beyond slot end; strength is not certified.'},
      'drive':{'joint':'Input','velocity_deg_s':90,'duration_s':4,'steps':120},'tolerances':{'marker_mm':.02,'angle_deg':.02,'stroke_mm':.02,'quick_return_ratio':.005},'repair_limit':3,
      'construction':'Actual radial capsule slot permits continuous crank pin adjustment from 15 to 25 mm. Pin and collar are ideal rigid clamps at each selected setting; separate saved assemblies validate settings 15,20,25 without overwriting any prior setting. This is intended stroke adjustment, not a collision repair by translating parts. Slider mounting holes provide a feed-interface surface; material gripping, payload, friction and clamp preload are not validated.'}
