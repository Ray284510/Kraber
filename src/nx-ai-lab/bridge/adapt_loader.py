if not globals().get('VERIFIED_BUILD'):
    raise RuntimeError('Use bridge/build.ps1 or verified_loader.py for checked generation')
from lab_paths import lab_path
from pathlib import Path
p=lab_path('src\\NX_MCP-Enhanced\\loader\\NX_MCP_Loader.cs')
s=p.read_text(encoding='utf-8-sig')
# Legacy hand-built JSON did not escape control characters in NX exception messages.
import re
s=re.sub(r'private static string Esc\(string s\)\s*\{.*?\n    \}', '''private static string Esc(string s)
    {
        if(s==null)return "";
        string encoded=Json(s);return encoded.Substring(1,encoded.Length-2);
    }''',s,count=1,flags=re.S)
s=s.replace('mb.DeleteSourceBody = false;', '// NX12 has no DeleteSourceBody; mirror is disabled by allowlist.')
s=s.replace('coll.AddRules(new SelectionIntentRule[] { faceRule });','coll.ReplaceRules(new SelectionIntentRule[] { faceRule }, false);')
s=s.replace('nx_mcp_loader', 'nx12_ai_lab')
s=s.replace('\\\"2506\\\"', '\\\"12.0.2.9\\\"')
s=s.replace('if (File.Exists(path)) { try { File.Delete(path); } catch { } }','SafePath(path); if (File.Exists(path)) throw new IOException("Refusing to overwrite existing part");')
s=s.replace('if (File.Exists(stepPath)) { try { File.Delete(stepPath); } catch { } }','SafePath(stepPath); if (File.Exists(stepPath)) throw new IOException("Refusing to overwrite existing STEP");')
s=s.replace('private static string Execute(string cmd)\n    {','''private static string Execute(string cmd)
    {
        string commandName = cmd.Split(' ')[0];
        string allowed = "|ping|nx_status|nx_create_part|nx_open_part|nx_save_part|nx_close_part|nx_create_sketch|nx_sketch_line|nx_sketch_rectangle|nx_sketch_circle|nx_sketch_arc|nx_finish_sketch|nx_extrude|nx_hole|nx_edge_blend|nx_chamfer|nx_unite|nx_fit_view|nx_export_step|nx_list_sketches|nx_list_bodies|nx_list_features|nx_list_edges|nx_list_faces|nx_undo|nx_release|";
        if (!allowed.Contains("|" + commandName + "|")) throw new Exception("Command not enabled in NX12 lab");
        if (commandName == "nx_open_part" || commandName == "nx_create_part" || commandName == "nx_export_step") SafePath(cmd.Split(' ')[1]);
        if (commandName != "ping" && commandName != "nx_status" && commandName != "nx_create_part" && commandName != "nx_open_part")
        {
            Part active = Session.GetSession().Parts.Work;
            if (active != null) SafePath(active.FullPath);
        }
''')
s=s.replace('public static int Startup()', '''public static void Main(string[] args) { Startup(); }
    private static void SafePath(string path)
    {
        string root = @"@NX_LAB_ROOT@\\models\\";
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Only lab model paths are allowed");
    }
    public static int Startup()''')
s=s.replace('|ping|nx_status|', '|nx_inspect|nx_blend_strict|nx_dimension_rectangle|nx_add_component|nx_interference|ping|nx_status|')
s=s.replace('case "ping":', 'case "nx_inspect": return InspectPart();\n            case "nx_blend_strict": return StrictBlend(parts);\n            case "nx_dimension_rectangle": return DimensionRectangle(parts);\n            case "nx_add_component": return AddComponentLab(parts);\n            case "nx_interference": return InterferenceLab();\n            case "ping":')
s=s[:s.rfind('}')]+lab_path('bridge\\extra_methods.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('try { sk.Activate(Sketch.ViewReorient.True); } catch { }','sk.Activate(Sketch.ViewReorient.True);')
s=s.replace('try { sk.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }','sk.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model);')
s=s.replace('try { _session.Parts.Work.Views.WorkView.Fit(); } catch { }','_session.Parts.Work.Views.WorkView.Orient(NXOpen.View.Canned.Isometric, NXOpen.View.ScaleAdjustment.Fit);')
s=s.replace('|nx_inspect|', '|nx_move_component|nx_import_step|nx_inspect|')
s=s.replace('case "nx_inspect":', 'case "nx_move_component": return MoveComponentLab(parts);\n            case "nx_import_step": return ImportStepLab(parts);\n            case "nx_inspect":')
s=s.replace(r'\"nx\":\"12.0.2.9\"', r'\"nx\":\"" + Esc(NxVersion()) + "\"')
s=s.replace('NXOpen.BasePart.CloseWholeTree.False', 'NXOpen.BasePart.CloseWholeTree.True')
s=s[:s.rfind('}')]+lab_path('bridge\\phase2_methods.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('var ell = _part.Curves.CreateEllipse(', 'var ell = LabCircle(').replace('r, r, 0.0, 2.0 * Math.PI);','r);')
s=s.replace('var sk = GetSketch(parts[1]);','var sk = LabEditingSketch(parts[1]);').replace('Sketch sk=GetSketch(args[1]);','Sketch sk=LabEditingSketch(args[1]);')
s=s.replace('try { lw.Open(); } catch { }','if(path.StartsWith(@"@NX_LAB_ROOT@\\models\\phase2_",StringComparison.OrdinalIgnoreCase))lw.Close();else lw.Open();')
s=s.replace('sk.AddGeometry(ell, Sketch.InferConstraintsOption.InferNoConstraints);','sk.AddGeometry(ell, Sketch.InferConstraintsOption.InferNoConstraints);\n        LockLabCircle(sk,ell);')
s=s.replace('|nx_inspect|','|nx_constraint_alignment|nx_motion_new|nx_inspect|').replace('case "nx_inspect":','case "nx_constraint_alignment": return EditAssemblyConstraint(parts);\n            case "nx_motion_new": return MotionNew(parts);\n            case "nx_inspect":')
s=s.replace('|nx_inspect|','|nx_assembly_state|nx_component_geometry|nx_constraint|nx_constraint_value|nx_constraint_suppress|nx_motion_available|nx_inspect|')
s=s.replace('case "nx_inspect":','case "nx_assembly_state": return AssemblyState(parts);\n            case "nx_component_geometry": return ComponentGeometry(parts);\n            case "nx_constraint": return CreateAssemblyConstraint(parts);\n            case "nx_constraint_value": case "nx_constraint_suppress": return EditAssemblyConstraint(parts);\n            case "nx_motion_available": return MotionAvailability();\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge\\phase2_motion.cs').read_text(encoding='utf-8-sig')+'\n}\n'
motion={'nx_motion_setup':'MotionSetup()','nx_motion_state':'MotionState()','nx_motion_link':'MotionLinkCreate(parts)','nx_motion_joint':'MotionJointCreate(parts)','nx_motion_driver':'MotionDriverCreate(parts)','nx_motion_solution':'MotionSolutionCreate(parts)','nx_motion_solve':'MotionSolve(parts)','nx_motion_frame':'MotionFrame(parts)'}
motion['nx_motion_legacy_new']='MotionLegacyCreate()'
motion['nx_motion_collision_setup']='MotionCollisionSetup()'
motion['nx_motion_joint_axis']='MotionJointAxis(parts)'
motion['nx_motion_angle']='MotionAngle(parts)'
motion['nx_color']='Phase2Color(parts)'
motion['nx_view']='Phase2View(parts)'
motion['nx_application']='Phase2Application(parts)'
motion['nx_motion_collision_check']='MotionCollisionCheck(parts)'
for command,method in motion.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')
s=s.replace('_part.Save(NXOpen.BasePart.SaveComponents.False, NXOpen.BasePart.CloseAfterSave.False);','''var savedStatus=_part.Save(NXOpen.BasePart.SaveComponents.False, NXOpen.BasePart.CloseAfterSave.False);
        try {if(savedStatus.NumberUnsavedParts>0 || savedStatus.NumberUnsavedObjects>0)throw new IOException("NX save failed: unsaved parts="+savedStatus.NumberUnsavedParts+" objects="+savedStatus.NumberUnsavedObjects);}
        finally {savedStatus.Dispose();}''')
s=s.replace('if (part == null) part = _session.Parts.Work;','''if(part==null) {var errors=new List<string>();if(status!=null)for(int i=0;i<status.NumberUnloadedParts;i++)errors.Add(status.GetPartName(i)+": "+status.GetStatusDescription(i));throw new IOException("NX did not load requested part: "+String.Join("; ",errors));}''')
s=s[:s.rfind('}')]+lab_path('bridge\\phase3_methods.cs').read_text(encoding='utf-8-sig')+'\n}\n'
for command,method in {'nx_motion_joint_results':'MotionJointResults()','nx_motion_marker':'MotionMarkerCreate(parts)','nx_motion_marker_results':'MotionMarkerResults(parts)','nx_shape':'SpatialShape(parts)','nx_motion_audit':'MotionAudit()','nx_motion_transforms':'MotionTransforms(parts)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge\\phase4_methods.cs').read_text(encoding='utf-8-sig')+'\n}\n'
for command,method in {'nx_accurate_volume':'AccurateVolume(parts)','nx_profile_solid':'ProfileSolid(parts)','nx_motion_curve_pair':'CurvePairCreate(parts)','nx_motion_higher_pairs':'HigherPairState()','nx_motion_collision_volumes':'CollisionVolumes(parts)','nx_motion_tighten':'TightenSolution(parts)','nx_motion_point_curve':'PointCurveCreate(parts)','nx_profile_boolean':'ProfileSolid(parts)','nx_profile_curve':'ProfileCurve(parts)','nx_motion_driver_velocity':'DriverVelocity(parts)','nx_motion_gear_pair':'GearPairCreate(parts)','nx_motion_suppress':'MotionSuppression(parts)','nx_motion_joint_coupler':'GeneralCouplerCreate(parts)','nx_motion_coupler_state':'MechanismCouplerState()','nx_sphere':'SphereSolid(parts)','nx_joint_reference':'JointReference(parts)','nx_component_datums':'ComponentDatums(parts)','nx_motion_universal':'UniversalJointCreate(parts)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')
for command,method in {'nx_research_profile_driver':'ResearchProfileDriver(parts)','nx_research_face_normal':'ResearchFaceNormal(parts)','nx_research_analytic_state':'ResearchAnalyticalState()','nx_motion_driver_harmonic':'HarmonicDriverCreate(parts)','nx_motion_driver_details':'MotionDriverDetails()','nx_motion_point_curve_sampling':'PointCurveSampling(parts)','nx_motion_integrator_refine':'IntegratorRefine(parts)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')
for command,method in {'nx_motion_analytic_sphere_extrusion':'AnalyticalSphereExtrusion(parts)','nx_motion_export_input':'MotionExportInput(parts)','nx_motion_precise_mass':'PreciseMotionMass(parts)','nx_motion_body_face_contact':'BodyFaceContactCreate(parts)','nx_session_parts':'SessionPartSafetyState()','nx_motion_contact_state':'BodyContactState(parts)','nx_motion_contact_model':'BodyContactModelEdit(parts)','nx_motion_joint_torque':'JointTorqueCreate(parts)','nx_curve_line':'MechanismLine(parts)','nx_motion_rack_pinion':'RackPinionCreate(parts)','nx_motion_analysis':'MotionAnalysisSelect(parts)','nx_motion_contact_api':'MotionContactApi()','nx_motion_mass_state':'MotionMassState()','nx_body_density':'Phase4Density(parts)','nx_motion_body_contact':'BodyContactCreate(parts)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')
s=s.replace('commandName != "nx_status" &&', 'commandName != "nx_status" && commandName != "nx_session_parts" &&')

s=s[:s.rfind('}')]+lab_path('bridge\\phase5_surface.cs').read_text(encoding='utf-8-sig')+'\n}\n'
for command,method in {'nx_surface_refs':'SurfaceRefs(parts)','nx_surface_trim':'SurfaceTrim(parts)','nx_surface_extend':'SurfaceExtend(parts)','nx_surface_fixture':'SurfaceFixture(parts)','nx_surface_sweep':'SurfaceSweep(parts)','nx_surface_sweep_fixture':'SurfaceSweepFixture(parts)','nx_surface_loft':'SurfaceLoft(parts)','nx_surface_shell':'SurfaceShell(parts)','nx_surface_sew':'SurfaceSew(parts)','nx_surface_thicken':'SurfaceThicken(parts)','nx_surface_state':'SurfaceState()','nx_export_step_precise':'SurfaceStep(parts)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')

s=s.replace('|nx_inspect|','|nx_mass_convergence|nx_inspect|').replace('case "nx_inspect":','case "nx_mass_convergence": if(parts.Length!=2)throw new Exception("One relative tolerance required"); return Phase5StepReadonlyProbe.RelativeMass(parts[1]);\n            case "nx_inspect":')

s=s.replace('|nx_inspect|','|nx_surface_deviation|nx_inspect|').replace('case "nx_inspect":','case "nx_surface_deviation": if(parts.Length!=4)throw new Exception("Source target and new log path required"); return Phase5StepReadonlyProbe.RunDeviation(parts[1],parts[2],parts[3]);\n            case "nx_inspect":')

# Bind model guards to the actual deployed lab assembly, never the official NX installation.
s=s.replace('|nx_inspect|','|nx_step_exact_bounds|nx_inspect|').replace('case "nx_inspect":','case "nx_step_exact_bounds": return Phase5StepReadonlyProbe.ExactBounds();\n            case "nx_inspect":')
s=s.replace('|nx_inspect|','|nx_step_readonly_probe|nx_inspect|').replace('case "nx_inspect":','case "nx_step_readonly_probe": if(parts.Length!=2)throw new Exception("One new diagnostic JSON path required"); Phase5StepReadonlyProbe.Main(new string[]{parts[1]}); return File.ReadAllText(parts[1]).Insert(1,"\\\"ok\\\":true,");\n            case "nx_inspect":')
s=re.sub(r'@"@NX_LAB_ROOT@\\([^"\r\n]*)"',lambda m:'LabPath(@"'+m.group(1)+'")',s)
s=s[:s.rfind('}')]+r'''
    private static string LabPath(string relative)
    {
        string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(NX_MCP_Loader).Assembly.Location), @"..\.."));
        string configured=Environment.GetEnvironmentVariable("NX_AI_LAB_ROOT");
        if(!String.IsNullOrEmpty(configured) && !String.Equals(Path.GetFullPath(configured),root,StringComparison.OrdinalIgnoreCase))throw new IOException("Mixed project roots are forbidden");
        return Path.Combine(root,relative);
    }
'''+"\n}\n"

s=s.replace('var opened = _session.Parts.OpenBaseDisplay(path, out status);', 'var load = _session.Parts.LoadOptions; load.ComponentLoadMethod=NXOpen.LoadOptions.LoadMethod.SearchDirectories; load.SetSearchDirectories(new string[]{Path.GetDirectoryName(path)},new bool[]{false}); var opened = _session.Parts.OpenBaseDisplay(path, out status);')

# Ordinary AP214 export must include actual sheet bodies as well as solids.
# Apply selectors after loading the official template; never edit that template.
_export_start=s.index('    private static string ExportStep(string[] parts)')
_export_end=s.index('    // ---- composite acceptance part', _export_start)
_export_block=s[_export_start:_export_end]
assert _export_block.count('            creator.Commit();')==1
_export_fixed=_export_block.replace('            creator.Commit();', '            creator.ObjectTypes.Solids = true;\n            creator.ObjectTypes.Surfaces = true;\n            creator.Commit();')
s=s[:_export_start]+_export_fixed+s[_export_end:]

s=s.replace('|nx_inspect|','|nx_sheet_surface_deviation|nx_inspect|').replace('case \"nx_inspect\":','case \"nx_sheet_surface_deviation\": if(parts.Length!=6)throw new Exception(\"Source path/tag target path/tag and new log path required\"); return Phase5StepReadonlyProbe.RunSheetDeviation(parts[1],parts[2],parts[3],parts[4],parts[5]);\n            case \"nx_inspect\":')

# Publish the command pipe only after its main-thread dispatch handle exists.
# An early readiness probe previously could SendMessage to a zero handle.
_startup_old = '''            _pipeThread = new System.Threading.Thread(PipeLoop) { IsBackground = true };
            _pipeThread.Start();
            _timerWindow = new TimerWindow();
            _timerWindow.Start();'''
_startup_new = '''            _timerWindow = new TimerWindow();
            _timerWindow.Start();
            if (_timerWindow.Handle == IntPtr.Zero) throw new Exception("NX scheduler handle unavailable");
            _pipeThread = new System.Threading.Thread(PipeLoop) { IsBackground = true };
            _pipeThread.Start();'''
assert s.count(_startup_old) == 1
s = s.replace(_startup_old, _startup_new)

# Native parameter edit candidate; protected independent experiment directory.
s=s.replace("using System;", "using System;\nusing System.Linq;",1)
s=s[:s.rfind('}')]+lab_path('bridge\\phase5_parametric.cs').read_text(encoding='utf-8-sig')+'\n}\n'
for command,method in {'nx_surface_parametric_state':'SurfaceParametricState(parts)','nx_surface_edit_ellipse':'SurfaceEditBasic(parts,true)','nx_surface_edit_arc':'SurfaceEditBasic(parts,false)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')

# Authored official subscription fallback; same exact parametric fixture guard.
s=s[:s.rfind('}')]+lab_path('bridge/phase5_parametric_spline.cs').read_text(encoding='utf-8-sig')+'\n}\n'
for command,method in {'nx_surface_spline_state':'SurfaceSplineState(parts)','nx_surface_spline_fixture':'SurfaceSplineFixture(parts)','nx_surface_edit_pole':'SurfaceEditPole(parts)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')

s=s.replace('|nx_inspect|','|nx_mass_convergence_target|nx_inspect|').replace('case "nx_inspect":','case "nx_mass_convergence_target": if(parts.Length!=3)throw new Exception("Target mode and tolerance required"); return Phase5StepReadonlyProbe.RelativeMassTargeted(parts[1],parts[2]);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_general_sweep.cs').read_text(encoding='utf-8-sig')+'\n}\n'
for command,method in {'nx_surface_sweep_arc_circle_trial':'SurfaceSweepArcCircleTrial(parts)','nx_surface_sweep_general_fixture':'SurfaceSweepGeneralFixture(parts)','nx_surface_sweep_general':'SurfaceSweepGeneral(parts)','nx_surface_sweep_general_edit':'SurfaceSweepGeneralEdit(parts)','nx_surface_sweep_general_state':'SurfaceSweepGeneralState(parts)'}.items():
 s=s.replace('|nx_inspect|',f'|{command}|nx_inspect|').replace('case "nx_inspect":',f'case "{command}": return {method};\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_independent_box.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_independent_box_boundary|nx_inspect|').replace('case "nx_inspect":','case "nx_independent_box_boundary": return IvBox2609Read(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_independent_analytic.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_independent_analytic_boundary|nx_inspect|').replace('case "nx_inspect":','case "nx_independent_analytic_boundary": return IvAn2609Read(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_independent_annular.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_independent_annular_caps|nx_inspect|').replace('case "nx_inspect":','case "nx_independent_annular_caps": return IvTrim2610Read(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_independent_periodic.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_independent_annular_periodic|nx_inspect|').replace('case "nx_inspect":','case "nx_independent_annular_periodic": return IvPer2610Read(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_independent_surface_probe.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_independent_surface_probe|nx_inspect|').replace('case "nx_inspect":','case "nx_independent_surface_probe": return IndependentSurfaceProbe(parts);\n            case "nx_inspect":')
s=s.replace('|nx_inspect|','|nx_independent_nurbs_topology|nx_inspect|').replace('case "nx_inspect":','case "nx_independent_nurbs_topology": return Json(IvNurbs2610.Read(parts, _part));\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_independent_multihole.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_independent_multihole|nx_inspect|').replace('case "nx_inspect":','case "nx_independent_multihole": return IvMulti2610Read(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_nurbs_native_brep_trial.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_nurbs_brep_trial_once|nx_inspect|').replace('case "nx_inspect":','case "nx_nurbs_brep_trial_once": return IvBrep2610Trial(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_fin_geometry.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_fin_geometry_probe|nx_inspect|').replace('case "nx_inspect":','case "nx_fin_geometry_probe": return FinGeometryProbe(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_step_import_drift.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_step_import_drift_probe|nx_inspect|').replace('case "nx_inspect":','case "nx_step_import_drift_probe": return StepDriftCapture(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_step_template_fin.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_step_template_experiment|nx_inspect|').replace('case "nx_inspect":','case "nx_step_template_experiment": return StepTemplateExperiment(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_fin_representation_audit.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_fin_representation_audit|nx_inspect|').replace('case "nx_inspect":','case "nx_fin_representation_audit": return FinRepresentationAudit(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_finsp_formation.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_finsp_formation_probe|nx_inspect|').replace('case "nx_inspect":','case "nx_finsp_formation_probe": return FinspFormationProbe(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_finsp_trim_offset.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_finsp_trim_offset_audit|nx_inspect|').replace('case "nx_inspect":','case "nx_finsp_trim_offset_audit": return FinspTrimOffsetAudit(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_finsp_edge_coverage.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_finsp_edge_coverage|nx_inspect|').replace('case "nx_inspect":','case "nx_finsp_edge_coverage": return FinspEdgeCoverage(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_complex_step.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_complex_step_capture|nx_inspect|').replace('case "nx_inspect":','case "nx_complex_step_capture": return ComplexStepCapture(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_periodic_hole.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_periodic_hole_prepare|nx_periodic_hole_surface|nx_inspect|').replace('case "nx_inspect":','case "nx_periodic_hole_prepare":\n            case "nx_periodic_hole_surface": return PeriodicHoleBuild(parts);\n            case "nx_inspect":')
s=s.replace('|nx_inspect|','|nx_periodic_hole_cold|nx_inspect|').replace('case "nx_inspect":','case "nx_periodic_hole_cold": return PeriodicHoleCold(parts);\n            case "nx_inspect":')
s=s.replace('|nx_inspect|','|nx_periodic_complex_probe|nx_inspect|').replace('case "nx_inspect":','case "nx_periodic_complex_probe": return PeriodicComplexRead(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_nurbs_spline_probe.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_nurbs_spline_probe_once|nx_inspect|').replace('case "nx_inspect":','case "nx_nurbs_spline_probe_once": return NurbsSplineProbe(parts);\n            case "nx_inspect":')
s=s.replace('|nx_inspect|','|nx_nurbs_trim_recovery_prepare|nx_nurbs_trim_recovery_surface|nx_inspect|').replace('case "nx_inspect":','case "nx_nurbs_trim_recovery_prepare":\n            case "nx_nurbs_trim_recovery_surface": return TrimRecovery(parts);\n            case "nx_inspect":')
s=s.replace('|nx_inspect|','|nx_nurbs_trim_recovery_read|nx_inspect|').replace('case "nx_inspect":','case "nx_nurbs_trim_recovery_read": return TrimRecoveryRead(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_nurbs_pcurve_edge_mapping.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_nurbs_pcurve_edge_mapping|nx_inspect|').replace('case "nx_inspect":','case "nx_nurbs_pcurve_edge_mapping": return NurbsPcurveEdgeMappingEvidence(parts);\n            case "nx_inspect":')
s=s[:s.rfind('}')]+lab_path('bridge/phase5_ask2d_native_trial.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s=s.replace('|nx_inspect|','|nx_ask2d_planar_trial_once|nx_ask2d_sheet_trial_once|nx_inspect|').replace('case "nx_inspect":','case "nx_ask2d_planar_trial_once":\n            case "nx_ask2d_sheet_trial_once": return IvAsk2610Trial(parts);\n            case "nx_inspect":')
# P13 minimal context: a separate typed v2 request path, never the v1 RespQueue.
# Verify each injection anchor before making this authorized generator change.
_context_anchors = [
    '    private static volatile bool _started;',
    '        if (_started) return 0;\n        _started = true;',
    '            _pipeThread.Start();\n            Log("pipe thread + scheduler window started");',
    '            Log("Startup ERR: " + e);',
    '        return 0;\n    }\n\n    // ---- main-thread scheduler',
    '            base.WndProc(ref m);',
    '                byte[] lineBytes = ReadLineBytes(inst);',
    '                if (line.Length == 0) continue;\n                CmdQueue.Enqueue(line);',
]
for _anchor in _context_anchors:
    if s.count(_anchor) != 1:
        raise RuntimeError('P13 context injection anchor must occur exactly once: ' + _anchor)
s=s.replace('    private static volatile bool _started;', '    // P13 initialization uses ContextStartup atomic state, not volatile check/set.')
s=s.replace('        if (_started) return 0;\n        _started = true;', '        if (!ContextStartup.TryEnter()) return ContextStartup.State == "DISPATCH_CONTEXT_PUBLISHED" ? 0 : 1;')
s=s.replace('            _pipeThread.Start();\n            Log("pipe thread + scheduler window started");', '            _pipeThread.Start();\n            ContextStartup.Publish();\n            Log("pipe thread + scheduler window started");')
s=s.replace('            Log("Startup ERR: " + e);', '            ContextStartup.Fail();\n            Log("Startup ERR: " + e);')
s=s.replace('        return 0;\n    }\n\n    // ---- main-thread scheduler', '        return ContextStartup.State == "DISPATCH_CONTEXT_PUBLISHED" ? 0 : 1;\n    }\n\n    // ---- main-thread scheduler')
s=s.replace('            base.WndProc(ref m);', '            else if (m.Msg == WmReadonlyContext)\n            {\n                DrainReadonlyContextQueue();\n            }\n            base.WndProc(ref m);')
s=s.replace('                byte[] lineBytes = ReadLineBytes(inst);', '                byte[] lineBytes = ReadContextAwareLineBytes(inst);')
s=s.replace('                if (line.Length == 0) continue;\n                CmdQueue.Enqueue(line);', '                if (line.Length == 0) continue;\n                if (line.StartsWith("{", StringComparison.Ordinal))\n                {\n                    HandleReadonlyContextFrame(inst, lineBytes);\n                    continue;\n                }\n                CmdQueue.Enqueue(line);')
s=s[:s.rfind('}')]+lab_path('bridge/phase13_context_methods.cs').read_text(encoding='utf-8-sig')+'\n}\n'
s += '\n' + lab_path('bridge/p13_context_capture.cs').read_text(encoding='utf-8-sig')
s += '\n' + lab_path('bridge/p13_context_protocol.cs').read_text(encoding='utf-8-sig')
s=s.replace('CmdQueue.Enqueue(line);','CmdQueue.Enqueue(BindSmokePeer(inst, line));')
s=s.replace('string resp = Execute(cmd);','string resp = DispatchSmokeAware(cmd);')
s=s.replace('            _part = work;', '            if (_part != null && _part.Tag != work.Tag) ResetTaskState();\n            _part = work;')
s=s.replace('    private static string Status()', lab_path('bridge/p13_fixed_smoke.cs').read_text(encoding='utf-8-sig')+'\n    private static string Status()')
lab_path('bridge\\NX12_Loader.cs').write_text(s,encoding='utf-8-sig')


