"""MCP stdio adapter for the dedicated NX 12 experiment instance."""
from lab_paths import lab_path
import json
import subprocess
import sys
import threading
import datetime
import re
import math
from pipe_client import call
from native_step import import_step
from pathlib import Path
from mcp.server.fastmcp import FastMCP

ROOT = lab_path('')
# Load NumPy-backed calculations before the Windows stdio event-loop workers start.
# Lazy native-extension import during a tool callback deadlocked in the recorded probe.
if str(ROOT) not in sys.path:sys.path.insert(0,str(ROOT))
from ai_studio.transport_outcome import unsent
from mechanism_agent import calculations as mechanism_calculations
from mechanism_agent.synthesis import synthesize as synthesize_mechanism
from mechanism_agent.realization import realize as realize_mechanism
from mechanism_agent.native_mobility import gear_rank as native_gear_rank
LOCK = threading.Lock()
mcp = FastMCP('NX12 AI Lab')

def execute(command: str) -> dict:
    with LOCK:
        entered_dispatch = False
        try:
            if any(c in command for c in '\r\n\x00') or len(command)>2048:
                raise ValueError('One command per call, maximum 2048 characters')
            if command.split()[0] in {'nx_surface_spline_fixture','nx_surface_edit_pole'}:
                raise ValueError('Spline experiment disabled after three repair rounds; no NX mutation dispatched. See reports/phase5_advanced/Surface_Parametric_Rebuild_Acceptance.md.')
            if command.split()[0]=='nx_export_step_precise':
                raise ValueError('Experimental precise STEP export is disabled after three failed native repair attempts. Translator ignored precision settings; final attempt exported no solids. See reports/phase5/Surface_Tools_Progress.md. No new export dispatched.')
            if command.startswith('nx_motion_solve ') and '\\phase3_' in call('nx_status').get('part','').lower():
                audit=call('nx_motion_audit')
                if not audit.get('ok'):raise ValueError('Cannot verify Motion limits: '+str(audit))
                solution=next(s for s in audit['solutions'] if s['name']==command.split()[1])
                joints={j['name']:j for j in audit['joints']}
                for driver in audit['drivers']:
                    if driver['type'] not in ('Constant','Polynomial') or driver['integration']!='Displacement':raise ValueError('Unsupported driver form for range preflight')
                    if driver['unit']!='DegreesPerSecond' or float(driver['acceleration'])!=0 or float(driver['jerk'])!=0:raise ValueError('Only verified constant angular velocity is supported by phase3 range preflight')
                    start=float(driver['displacement']);end=start+float(driver['velocity'])*solution['time'];joint=joints[driver['joint']]
                    if not all(math.isfinite(v) for v in [start,end,solution['time']]) or solution['time']<=0:raise ValueError('Finite positive motion duration and finite angles required')
                    if not joint['lower_defined'] or not joint['upper_defined']:raise ValueError('Phase3 requires explicit joint limits')
                    if min(start,end)<joint['lower_degrees'] or max(start,end)>joint['upper_degrees']:raise ValueError('Joint range exceeded before solver: '+driver['joint'])
            entered_dispatch = True
            result = import_step(command,call) if command.split()[0]=='nx_import_step' else call(command)
            if result.get('response_decode_error') or (not result.get('ok') and not unsent(result.get('transport_receipt'))):
                result=dict(result,retry_safe=False,outcome='UNKNOWN_AFTER_WRITE')
        except Exception as exc:
            receipt = getattr(exc, 'transport_receipt', None)
            if not entered_dispatch and receipt is None:
                receipt = {'outcome':'NOT_SENT','counts':{'WriteFile':0,'ReadFile':0},
                           'source':'adapter_pre_dispatch'}
            # A STEP translator may already have created output before its
            # subsequent pipe call fails. That handle cannot certify STEP as unsent.
            safe = unsent(receipt) and not (entered_dispatch and command.startswith('nx_import_step '))
            result = {'ok':False, 'execution_started':False if safe else None,
                      'retry_safe':safe, 'outcome':'NOT_SENT' if safe else 'UNKNOWN_AFTER_WRITE',
                      'transport_receipt':receipt, 'error_type':type(exc).__name__}
            result['validation_error' if safe and isinstance(exc, ValueError) else 'transport_error'] = str(exc)
        try:
            with (ROOT/'logs/commands.jsonl').open('a',encoding='utf-8') as f:
                f.write(json.dumps({'time':datetime.datetime.now().isoformat(),'command':command,'response':result},ensure_ascii=False)+'\n')
        except (OSError,TypeError,ValueError) as exc:
            result=dict(result,ok=False,retry_safe=False,outcome='UNKNOWN_AFTER_WRITE',
                        evidence_write_error=type(exc).__name__)
    return result

@mcp.tool()
def nx_status() -> dict:
    """Read actual NX version, bridge readiness and active experimental part."""
    result = execute('nx_status')
    # Actual host-side identity distinguishes a stale C-drive MCP from the D bridge.
    import os
    result['mcp_runtime'] = {'root': str(ROOT), 'python': sys.executable,
                             'pid': os.getpid(), 'cwd': os.getcwd()}
    return result

@mcp.tool()
def nx_session_parts() -> dict:
    """Read actual loaded-part paths and unsaved flags without saving or closing anything."""
    return execute("nx_session_parts")

@mcp.tool()
def nx_inspect() -> dict:
    """Read NX bodies, consistency faults, bounds, volume, features/errors, sketch DOF, expressions and components."""
    return execute('nx_inspect')

@mcp.tool()
def nx_execute(command: str) -> dict:
    """Execute one allowlisted operation in the NX GUI, returning actual NX output.
    Paths must be under the checkout models directory, without spaces. New files cannot overwrite.
    Commands (all lengths mm):
    nx_create_part PATH; nx_open_part PATH; nx_save_part; nx_close_part 1
    nx_create_sketch XY|XZ|YZ; nx_sketch_rectangle SKETCH_ID CX CY WIDTH HEIGHT
    nx_dimension_rectangle SKETCH_ID NAME_PREFIX WIDTH HEIGHT (while sketch is active)
    nx_sketch_circle SKETCH_ID CX CY DIAMETER; nx_sketch_line SKETCH_ID X1 Y1 X2 Y2
    nx_finish_sketch SKETCH_ID
    nx_extrude SKETCH_ID DISTANCE REVERSE_0_OR_1 START_OFFSET create|subtract [BODY_ID]
    create with BODY_ID unites to that body. Do not assume IDs; use returned values.
    nx_hole BODY_ID CX CY DIAMETER DEPTH [START_OFFSET] (XY circular cut)
    nx_list_edges BODY_ID; nx_list_faces BODY_ID; nx_list_features; nx_list_bodies; nx_list_sketches
    nx_blend_strict BODY_ID RADIUS EDGE_INDEX_CSV (atomic rollback on NX failure)
    nx_fit_view; nx_export_step PATH (verify file completion separately)
    nx_surface_loft NAME CSV_PATH solid|sheet (phase4_* sandbox; 2..32 YZ ellipse sections, X,Y,Z,RY,RZ)
    nx_surface_shell BODY_ID THICKNESS xmin|both (native shell removing planar X end face(s))
    nx_surface_sew BODY_ID_CSV (2..16 distinct sheets, native sew at fixed 0.001 mm tolerance)
    nx_surface_thicken SHEET_BODY_ID THICKNESS (native thicken; preserves construction sheet)
    nx_surface_state (sheet/solid topology, visibility, feature errors and body consistency)
    Use nx_surface_state for parts containing construction sheets; nx_inspect is currently solid-oriented.
    Experimental nx_export_step_precise is DISABLED after failed validation; ordinary export remains available.
    nx_add_component PRT_PATH NAME X Y Z (identity orientation, fixed placement)
    nx_move_component NAME DX DY DZ (relative translation, re-read component position)
    nx_research_profile_driver NAME JOINT CSV [linear|akima] (research-only prescribed ideal angle profile; not unilateral contact)
    nx_research_analytic_state (read actual analytical contact definitions, no force/convergence claim)
    nx_research_face_normal COMPONENT FACE_REF X Y Z (research-only read actual face normal and solid containment on both sides)
    nx_import_step STEP_PATH NEW_PRT_PATH (NX native STEP translator, no active part, opens result in GUI)
    nx_interference (actual assembly occurrence pairs, 1 interference / 2 clear / 3 touching)
    Every response must be checked for ok; do not automatically retry timeouts.
    Constraint/Motion commands only modify models/phase2_* or phase3_* parts. Query current face/edge
    references again after reopen or topology change; indices are not persistent.
    nx_assembly_state [RELATIVE_COMPONENT]; nx_component_geometry COMPONENT
    nx_constraint fix|axis|touch|concentric|align|distance|angle|bond NAME C1 REF1 C2 REF2 VALUE same|opposite
    nx_constraint_value NAME NUMBER; nx_constraint_suppress NAME 1|0; nx_constraint_alignment NAME same|opposite
    nx_motion_available; nx_motion_new NEW_PATH.sim; nx_motion_setup; nx_motion_state
    nx_motion_link NAME FIXED_1_OR_0 COMPONENT [COMPONENT...]
    nx_motion_joint NAME Revolute|Slider LINK1 LINK2 X Y Z AX AY AZ [LOWER UPPER]
    nx_motion_driver NAME JOINT revolute|slider VELOCITY (deg/s or mm/s)
    nx_motion_solution NAME normal|articulation TIME_SECONDS STEPS
    nx_motion_solve SOLUTION; nx_motion_frame SOLUTION FRAME_INDEX|finish
    nx_motion_collision_setup; nx_motion_collision_check FRAME_INDEX
    nx_motion_angle COMPONENT1 EDGE_REF COMPONENT2 EDGE_REF FRAME_INDEX
    nx_motion_joint_axis JOINT AX AY AZ; nx_color NX_PALETTE_INDEX
    Motion uses actual Simcenter results. Collision checks are discrete, not continuous.
    Per-component assembly DOF must never be summed. Use Motion Gruebler count.
    """
    return execute(command)

def token(value: str) -> str:
    if not value or re.search(r'\s|[\x00-\x1f]',value):raise ValueError('Expected one nonempty command token')
    return value

@mcp.tool()
def nx_assembly_state(relative_to: str | None = None) -> dict:
    """Read actual NX constraint statuses, load state and per-component DOF. Unknown DOF is not valid; never sum correlated DOFs."""
    return execute('nx_assembly_state'+(' '+token(relative_to) if relative_to else ''))

@mcp.tool()
def nx_component_geometry(component: str) -> dict:
    """Read real occurrence faces, cylinder axes/radii, edges and current ephemeral references. Requery after reopen/topology changes."""
    return execute('nx_component_geometry '+token(component))

@mcp.tool()
def nx_assembly_constraint(kind: str, name: str, component: str, reference: str = 'component', other_component: str = '-', other_reference: str = 'component', value: float = 0, alignment: str = 'same') -> dict:
    """Create and solve an NX12 assembly constraint in a phase2_, phase3_ or phase4_ model. Kinds: fix, axis, concentric, touch, align, distance, angle, bond. Check returned solver_status and reread DOF."""
    if kind not in ('fix','axis','concentric','touch','align','distance','angle','bond'):raise ValueError('Unsupported kind')
    if alignment not in ('same','opposite'):raise ValueError('Invalid alignment')
    return execute('nx_constraint '+' '.join([kind,token(name),token(component),token(reference),token(other_component),token(other_reference),str(value),alignment]))

@mcp.tool()
def nx_constraint_edit(name: str, action: str, value: str) -> dict:
    """Edit value, suppression (1/0), or alignment (same/opposite), then solve. Retains conflicting constraints for diagnosis; bounded repairs belong to the agent."""
    if action not in ('value','suppress','alignment'):raise ValueError('Unsupported edit')
    return execute(f'nx_constraint_{action} {token(name)} {token(value)}')

@mcp.tool()
def nx_motion_state() -> dict:
    """Read actual NX Motion links, joints, drivers, solver and per-solution Gruebler DOF. Requires a loaded initialized .sim part."""
    return execute('nx_motion_state')

@mcp.tool()
def nx_motion_audit() -> dict:
    """Read NX12 native solver selector, joint origins/axes/limits, actual driver expressions and per-solution DOFs. Dynamic enum includes kinematics; solver=1 means kinematic in installed NX12 UF headers."""
    return execute('nx_motion_audit')

@mcp.tool()
def nx_motion_transforms(frame: int) -> dict:
    """Read native solver link 4x4 row-major transforms at an existing result frame. Matrices transform design-position global coordinates; they are not host-generated FK."""
    if frame<0:raise ValueError('Nonnegative frame required')
    return execute(f'nx_motion_transforms {frame}')

@mcp.tool()
def nx_motion_marker(name: str, link: str, point_mm: list[float]) -> dict:
    """Add a real Motion measurement marker on a link. Create before solving. A four-marker frame recovers end-effector position and orientation without animation matrix quantization."""
    if len(point_mm)!=3:raise ValueError('Three coordinates required')
    return execute('nx_motion_marker '+' '.join(map(str,[token(name),token(link),*point_mm])))

@mcp.tool()
def nx_motion_marker_results(prefix: str) -> dict:
    """Read native absolute XYZ series for matching markers from loaded solver results; not generated trajectory data."""
    return execute('nx_motion_marker_results '+token(prefix))

@mcp.tool()
def nx_motion_joint_results() -> dict:
    """Read native joint rotational series relative to the second link, 3-1-3 Euler angles in radians. Revolute angle uses the appropriate combined axial Euler rotation; verify initial offset."""
    return execute('nx_motion_joint_results')

@mcp.tool()
def nx_spatial_feature(name: str, profile: str, origin_mm: list[float], axis: list[float], width: float, depth: float, height: float=1, operation: str='create', target_body: str='-') -> dict:
    """Create a fixed fully constrained circle/rectangle sketch and native extrusion at a 3D origin/axis in a phase3_ or phase4_ part. Circle width is diameter. create+target unites; subtract requires target. Failure rolls back feature. Inspect actual body/feature status afterwards."""
    if profile not in ('box','cylinder') or operation not in ('create','subtract') or len(origin_mm)!=3 or len(axis)!=3 or min(width,height,depth)<=0:raise ValueError('Invalid spatial feature')
    if operation=='subtract' and target_body=='-':raise ValueError('Subtract requires target')
    return execute('nx_shape '+' '.join(map(str,[token(name),profile,*origin_mm,*axis,width,height,depth,operation,token(target_body)])))

@mcp.tool()
def nx_motion_create(path: str) -> dict:
    """Create and save a real NX12 Motion simulation from the current phase2_, phase3_ or phase4_ assembly. Path must be new and end .sim."""
    return execute('nx_motion_new '+token(path))

@mcp.tool()
def nx_motion_link(name: str, components: list[str], fixed: bool = False) -> dict:
    """Group actual assembly components into a rigid Motion link. Fixed links are grounded. Avoid reserved name Ground."""
    if not components:raise ValueError('At least one component required')
    return execute('nx_motion_link '+' '.join([token(name),str(int(fixed)),*[token(x) for x in components]]))

@mcp.tool()
def nx_motion_joint(name: str, kind: str, first_link: str, second_link: str, origin_mm: list[float], axis: list[float], limits: list[float] | None = None) -> dict:
    """Create a native Revolute, Slider or Spherical joint at an explicit global origin/axis. Optional bounds are degrees or mm for Revolute/Slider. Spherical has three rotations; validate actual mobility."""
    if kind not in ('Revolute','Slider','Spherical') or len(origin_mm)!=3 or len(axis)!=3:raise ValueError('Invalid joint')
    if kind=='Spherical' and limits is not None:raise ValueError('Spherical bounds are not implemented')
    if limits is not None and (len(limits)!=2 or limits[0]>limits[1]):raise ValueError('Invalid limits')
    return execute('nx_motion_joint '+' '.join([token(name),kind,token(first_link),token(second_link),*[str(v) for v in origin_mm+axis+(limits or [])]]))

@mcp.tool()
def nx_motion_driver(name: str, joint: str, kind: str, velocity: float) -> dict:
    """Add a real polynomial joint driver, constant velocity in deg/s (revolute) or mm/s (slider). Response includes NX expression units."""
    if kind not in ('revolute','slider'):raise ValueError('Invalid driver kind')
    return execute(f'nx_motion_driver {token(name)} {token(joint)} {kind} {velocity}')

@mcp.tool()
def nx_motion_solution(name: str, kind: str, duration: float, steps: int) -> dict:
    """Create a native Motion solution, normal or articulation; returns actual Gruebler DOF. This creates a solution definition and does not solve it."""
    if kind not in ('normal','articulation') or duration<=0 or not 1<=steps<=10000:raise ValueError('Invalid solution settings')
    return execute(f'nx_motion_solution {token(name)} {kind} {duration} {steps}')

@mcp.tool()
def nx_motion_solve(solution: str) -> dict:
    """Execute Siemens NX12 Simcenter solver. A returned call alone is not acceptance: load native results, inspect steps and collisions, then save/reopen."""
    return execute('nx_motion_solve '+token(solution))

@mcp.tool()
def nx_motion_frame(solution: str, frame: int | None = None) -> dict:
    """Show an actual solver output frame in NX. None restores design position and finishes animation. No synthetic transforms are generated."""
    return execute(f'nx_motion_frame {token(solution)} '+('finish' if frame is None else str(frame)))

@mcp.tool()
def nx_motion_collision_check(frame: int | None = None) -> dict:
    """With None, define body-pair checks across Motion links. With a frame, query UF_MOTION_interference against native solver results. Discrete sampling only."""
    return execute('nx_motion_collision_setup' if frame is None else f'nx_motion_collision_check {frame}')

@mcp.tool()
def nx_mechanism_select(requirement: str, output_motion: str | None = None, adjustable_stroke: bool = False, minimize_quick_return: bool = False, dwell: bool = False, coaxial: bool = False) -> dict:
    """Compare mechanism candidates from functional requirements. Host reasoning only; creates no NX geometry. Codex must produce a dimensioned plan and actual NX validation. Structured output_motion resolves the limited text parser; unsupported mechanisms remain unverified."""
    if str(ROOT) not in sys.path:sys.path.insert(0,str(ROOT))
    from mechanism_agent.selection import Requirement,interpret,select
    if output_motion is not None:
        if output_motion not in ('reciprocating_translation','oscillation','intermittent_rotation','continuous_rotation','spatial_translation'):raise ValueError('Unsupported motion goal')
        r=Requirement(output_motion=output_motion,source_text=requirement,input_motion='three_rotary_inputs' if output_motion=='spatial_translation' else 'continuous_rotation')
    else:r=interpret(requirement)
    for field,value in [('adjustable_stroke',adjustable_stroke),('minimize_quick_return',minimize_quick_return),('dwell',dwell),('coaxial',coaxial)]:setattr(r,field,getattr(r,field) or value)
    return {'ok':True,'source':'host requirement comparison; not NX runtime evidence',**select(r)}

@mcp.tool()
def nx_mechanism_capabilities() -> dict:
    """Read mechanism catalog and recorded acceptance paths. This is an evidence index, not a live rerun. Planned, failed and verified configurations remain distinct."""
    catalog=json.loads((ROOT/'mechanism_agent/catalog.json').read_text(encoding='utf-8'))
    ledger_path=ROOT/'logs/phase4/acceptance.json'
    ledger=json.loads(ledger_path.read_text(encoding='utf-8')) if ledger_path.exists() else {}
    return {'ok':True,'catalog':catalog,'recorded_acceptance':ledger,'live_nx_validation_performed':False}

@mcp.tool()
def nx_mechanism_calculate(kind: str, parameters: dict) -> dict:
    """Independent mechanical calculation, not an NX result. Supports fourbar full-cycle metrics, slider_crank, planetary mesh rank and spatial constraint Jacobian rank. Cross-check actual NX before acceptance."""
    if str(ROOT) not in sys.path:sys.path.insert(0,str(ROOT))
    calc=mechanism_calculations
    p=parameters
    if kind=='fourbar':result=calc.fourbar_metrics(p['ground'],p['crank'],p['coupler'],p['rocker'])
    elif kind=='slider_crank':result=calc.slider_crank(p['crank'],p['rod'],[math.radians(x) for x in p['angles_deg']],p.get('offset',0))
    elif kind=='planetary':result={**calc.planetary_ratio(p['sun'],p['planet'],p['ring']),**calc.planetary_mesh_rank(p['sun'],p['planet'],p['ring'],p['number'])}
    elif kind=='constraint_rank':result=calc.constraint_rank(p['bodies'],p['joints'],p.get('ground','Ground'),p.get('local_dof',0))
    else:raise ValueError('Unsupported calculation; do not infer implementation from catalog membership')
    return {'ok':True,'kind':kind,'result':result,'source':'independent host calculation','nx_cross_validation_performed':False}

@mcp.tool()
def nx_motion_joint_coupler(name: str, first_joint: str, second_joint: str, first_scale: float, second_scale: float) -> dict:
    """Create an NX12 native rotational two-joint coupler in a phase4 SIM. Verify sign, direction, mobility and real trajectories. Ideal kinematic relation, not force-resolved gear contact."""
    if not all(math.isfinite(x) and x!=0 for x in (first_scale,second_scale)):raise ValueError('Finite nonzero scales required')
    return execute('nx_motion_joint_coupler '+' '.join(map(str,[token(name),token(first_joint),token(second_joint),first_scale,second_scale])))

@mcp.tool()
def nx_motion_suppress(solution: str, object_name: str, suppressed: bool = True) -> dict:
    """Set phase4 solution-specific suppression of a named gear coupler or joint driver; returns actual native Gruebler count. Suppression is not proof of a valid repair."""
    return execute(f'nx_motion_suppress {token(solution)} {token(object_name)} {int(suppressed)}')

@mcp.tool()
def nx_motion_higher_pairs() -> dict:
    """Read actual native curve/curve and point/curve motion pairs in the loaded NX part. Does not enumerate gear or general couplers."""
    return execute('nx_motion_higher_pairs')

@mcp.tool()
def nx_motion_universal(name: str, first_link: str, second_link: str, origin_mm: list[float], first_cross_axis: list[float], second_cross_axis: list[float]) -> dict:
    """Create an NX12 native Universal joint with two orthogonal cross-pin axes in a phase4 SIM. Actual creation and closed Delta simulation verified; check real joint centres and DOF for new geometry."""
    if any(len(v)!=3 for v in (origin_mm,first_cross_axis,second_cross_axis)):raise ValueError('Three coordinates per vector required')
    return execute('nx_motion_universal '+' '.join(map(str,[token(name),token(first_link),token(second_link),*origin_mm,*first_cross_axis,*second_cross_axis])))

@mcp.tool()
def nx_motion_coupler_state() -> dict:
    """Read active solution, actual gear/general couplers, suppression and LMS tolerances in a phase4 SIM. This is a native query, not catalog metadata."""
    return execute('nx_motion_coupler_state')

@mcp.tool()
def nx_motion_driver_harmonic(name: str, joint: str, kind: str, amplitude: float, frequency_hz: float, phase_deg: float = 0, offset: float = 0) -> dict:
    """Create a native NX12 harmonic input. Amplitude/offset are degrees or mm. Convert Hz to NX12's actual DegreesPerSecond frequency field. Verify the native trajectory; creation is not acceptance."""
    if kind not in ('revolute','slider') or not all(math.isfinite(x) for x in (amplitude,frequency_hz,phase_deg,offset)) or amplitude<=0 or frequency_hz<=0:raise ValueError('Finite positive amplitude and frequency required')
    result=execute('nx_motion_driver_harmonic '+' '.join(map(str,[token(name),token(joint),kind,amplitude,360*frequency_hz,phase_deg,offset])))
    if result.get('ok'):
        result['frequency_conversion']={'requested_hz':frequency_hz,'native_degrees_per_second':360*frequency_hz}
        if result['operation']['frequency_unit']!='DegreesPerSecond':result.update(ok=False,error='Unexpected native frequency unit; driver was created, inspect before retry')
    return result

@mcp.tool()
def nx_motion_driver_details() -> dict:
    """Read actual native rotational and translational driver laws and expression units."""
    return execute('nx_motion_driver_details')

@mcp.tool()
def nx_motion_refine_point_curve_sampling(name: str, spacing_mm: float) -> dict:
    """Refine an existing native PointOnCurve's geometric sampling in an experimental phase4 SIM. This does not loosen contact acceptance or replace the physical curve. Re-solve and check all frames."""
    if not math.isfinite(spacing_mm) or not .001<=spacing_mm<=1:raise ValueError('Spacing must be 0.001..1 mm')
    return execute(f'nx_motion_point_curve_sampling {token(name)} {spacing_mm}')

@mcp.tool()
def nx_motion_refine_integrator(solution: str, tolerance: float = 1e-9, maximum_step_s: float = .001) -> dict:
    """Tighten native LMS integration/solution tolerance and maximum integration step in an experimental phase4 solution. Separate from assembly/kinematic equation tolerance. Re-solve and inspect actual MINP/MINF and contact results."""
    if not math.isfinite(tolerance) or not math.isfinite(maximum_step_s) or not 1e-12<=tolerance<=1e-6 or not .000001<=maximum_step_s<=.01:raise ValueError('Refinement outside bounded range')
    return execute(f'nx_motion_integrator_refine {token(solution)} {tolerance} {maximum_step_s}')

@mcp.tool()
def nx_motion_rack_pinion(name: str, revolute_joint: str, slider_joint: str, radius_mm: float, contact_mm: list[float]) -> dict:
    """Create actual NX12 CouplerRckpn. Verify units, direction, native trajectory and teeth before claiming success; this is an ideal transmission relation, not force contact dynamics."""
    if len(contact_mm)!=3 or not all(math.isfinite(x) for x in [radius_mm,*contact_mm]) or radius_mm<=0:raise ValueError('Positive finite radius and XYZ contact required')
    return execute('nx_motion_rack_pinion '+' '.join(map(str,[token(name),token(revolute_joint),token(slider_joint),radius_mm,*contact_mm])))

@mcp.tool()
def nx_mechanism_synthesize(specification: dict) -> dict:
    """Compare dimensioned candidates from CLOUD Codex semantic interpretation. Supply original source_text, field/reason interpretation, assumptions, input_motion, output_motion and ordered numerical objectives. Reciprocating translation needs stroke_mm, period_s, max_pressure_angle_deg; optional dwell_fraction, motion_law, stroke_range_mm, adjustable_stroke, quick_return_ratio and maxima. Intermittent rotation needs output_step_deg and center_distance_mm. No keyword routing or fixed mechanism scores. Result is a proposal; never inherits NX acceptance."""
    result=synthesize_mechanism(specification)
    return {'ok':True,**result,'calculation_source':'Independent host mechanics; actual NX cross-validation required for every generated model'}

@mcp.tool()
def nx_mechanism_realize(specification: dict) -> dict:
    """Recompare reciprocating candidates after real shaft, pin, bore and guide packaging. Retains theoretical metrics and rejected routes. Uses the same declared objectives; no fixed type priority. No NX model is created and no acceptance is inherited."""
    return {'ok':True,**realize_mechanism(specification)}

@mcp.tool()
def nx_mechanism_native_mobility() -> dict:
    """Read actual joint/higher-pair/contact/coupler state, then independently compute current spatial mobility for lower pairs and verified Gear/RackPinion equations. Rejects unmodeled constraints; reports raw native Gruebler counts separately. Not a collision or force check."""
    audit=execute('nx_motion_audit');higher=execute('nx_motion_higher_pairs');couplers=execute('nx_motion_coupler_state');inputs={'audit':audit,'higher_pairs':higher,'couplers':couplers}
    if not all(v.get('ok') for v in inputs.values()):return {'ok':False,'native_inputs':inputs,'reason':'Native state incomplete'}
    if audit.get('body_contact_count')!=0 or higher.get('pairs'):return {'ok':False,'native_inputs':inputs,'reason':'Contact/higher-pair equations or contact count not covered; no fabricated mobility value'}
    try:return {'ok':True,'native_inputs':inputs,'independent_result':native_gear_rank(audit,couplers)}
    except ValueError as exc:return {'ok':False,'native_inputs':inputs,'reason':str(exc)}

@mcp.tool()
def nx_motion_contact_capabilities() -> dict:
    """Query actual NX12 native BodyContact builder and selection types without committing a contact. Availability is not simulation acceptance or proof of a Dynamics license."""
    return execute('nx_motion_contact_api')

@mcp.tool()
def nx_motion_mass_state() -> dict:
    """Read actual link mass/inertia values, units and source mode. Defaults must not be presented as a justified material/load model."""
    return execute('nx_motion_mass_state')

if __name__ == '__main__':
    mcp.run(transport='stdio')
