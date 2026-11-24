// NX_MCP_Loader — NXOpen .NET resident plugin (V2)
// Loaded automatically by NX via a "startup" folder (ufsta/Startup user exit).
// NamedPipe command channel -> thread-safe queue -> SendMessage scheduler on
// the NX main thread executes NXOpen operations. No journal, no Alt+F8, no
// keyboard simulation.
//
// Scheduler: NX's MFC main thread does not pump WinForms messages, so a
// System.Windows.Forms.Timer never fires here. A plain Win32 hidden window
// (NativeWindow) receives our custom message via SendMessage (verified: the NX
// main thread dispatches it synchronously) and runs the NXOpen ops on the NX
// main thread. The pipe/background thread NEVER calls NXOpen directly.
//
// Command protocol (UTF-8 line over named pipe "nx12_ai_lab"):
//   ping
//   nx_status
//   nx_create_part <prt-path>
//   nx_save_part
//   nx_release
//   nx_create_sketch <XY|XZ|YZ>
//   nx_sketch_line <sketch_id> <x1> <y1> <x2> <y2>
//   nx_sketch_rectangle <sketch_id> <cx> <cy> <w> <h>
//   nx_sketch_circle <sketch_id> <cx> <cy> <diameter>
//   nx_sketch_arc <sketch_id> <cx> <cy> <radius> <start_deg> <end_deg>
//   nx_finish_sketch <sketch_id>
//   nx_extrude <sketch_id> <distance> [reverse=0|1] [start_offset] [create|subtract] [body_id]
//   nx_hole <body_id> <cx> <cy> <diameter> <depth> [start_offset]
//   nx_edge_blend <body_id> <radius> [edge_indices csv]
//   nx_chamfer <body_id> <offset> [edge_indices csv]
//   nx_unite <target_body_id> <tool_body_ids csv>
//   nx_revolve <sketch_id> <ax> <ay> <bx> <by> <angle> [reverse=0|1]
//   nx_mirror <body_id> <XY|XZ|YZ> [offset]
//   nx_linear_pattern <body_id> <X|Y|Z> <count> <spacing> [reverse=0|1]
//   nx_fit_view
//   nx_export_step <step-path>
//   nx_build_plate_test           (single-command acceptance part)
//   create_block <w> <h> <d>      (kept for backward compatibility)
//
// Responses are one line of JSON: {"ok":true,...} or {"ok":false,"error":"..."}.
//
// The loader stays resident for the whole NX session. nx_release only clears
// the current task state and restores normal NX interaction; it never stops
// the pipe server or unloads the plugin.
using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;

public static class NX_MCP_Loader
{
    private const string PipeName = "nx12_ai_lab";
    private const int WmSelfTest = 0x8000 + 1; // WM_APP+1

    private static readonly ConcurrentQueue<string> CmdQueue = new ConcurrentQueue<string>();
    private static readonly ConcurrentQueue<string> RespQueue = new ConcurrentQueue<string>();
    private static NamedPipeServerStream _pipe;
    private static System.Threading.Thread _pipeThread;
    private static TimerWindow _timerWindow;
    // P13 initialization uses ContextStartup atomic state, not volatile check/set.

    // ---- task state ----------------------------------------------------
    private static Session _session;
    private static NXOpen.Part _part;
    private static readonly Dictionary<string, Sketch> _sketches = new Dictionary<string, Sketch>();
    private static readonly Dictionary<string, string> _sketchPlanes = new Dictionary<string, string>();
    private static readonly Dictionary<string, Body> _bodies = new Dictionary<string, Body>();
    private static int _sketchCounter;
    private static int _bodyCounter;
    private static string _partPath;

    private static string LogPath
    {
        get
        {
            string ws = null;
            try { ws = Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE"); } catch { }
            if (string.IsNullOrEmpty(ws))
            {
                ws = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "NX_MCP_WORKSPACE");
            }
            try { Directory.CreateDirectory(ws); } catch { }
            return Path.Combine(ws, "nx12_ai_lab.log");
        }
    }

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    // ---- JSON helpers --------------------------------------------------
    private static string Esc(string s)
    {
        if(s==null)return "";
        string encoded=Json(s);return encoded.Substring(1,encoded.Length-2);
    }

    private static string OkJson(string result)
    {
        return "{\"ok\":true,\"result\":\"" + Esc(result) + "\"}";
    }

    private static string OkJson(string key, string value, string result)
    {
        return "{\"ok\":true,\"" + Esc(key) + "\":\"" + Esc(value) + "\",\"result\":\"" + Esc(result) + "\"}";
    }

    private static string OkJsonList(string key, List<string> values, string result)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"").Append(Esc(key)).Append("\":[");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(Esc(values[i])).Append("\"");
        }
        sb.Append("],\"result\":\"").Append(Esc(result)).Append("\"}");
        return sb.ToString();
    }

    private static string ErrJson(string msg)
    {
        return "{\"ok\":false,\"error\":\"" + Esc(msg) + "\"}";
    }

    // ---- NX user exits -------------------------------------------------
    public static int GetUnloadOption(string dummy)
    {
        // AtTermination (UF_UNLOAD_UG_TERMINATE = 3): keep resident for the whole NX session
        return 3;
    }

    public static void Main(string[] args) { Startup(); }
    private static void SafePath(string path)
    {
        string root = LabPath(@"models\");
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Only lab model paths are allowed");
    }
    public static int Startup()
    {
        if (!ContextStartup.TryEnter()) return ContextStartup.State == "DISPATCH_CONTEXT_PUBLISHED" ? 0 : 1;
        try
        {
            Log("Startup() called; thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                " messageLoop=" + Application.MessageLoop);
            _timerWindow = new TimerWindow();
            _timerWindow.Start();
            if (_timerWindow.Handle == IntPtr.Zero) throw new Exception("NX scheduler handle unavailable");
            _pipeThread = new System.Threading.Thread(PipeLoop) { IsBackground = true };
            _pipeThread.Start();
            ContextStartup.Publish();
            Log("pipe thread + scheduler window started");
        }
        catch (Exception e)
        {
            ContextStartup.Fail();
            Log("Startup ERR: " + e);
        }
        return ContextStartup.State == "DISPATCH_CONTEXT_PUBLISHED" ? 0 : 1;
    }

    // ---- main-thread scheduler ----------------------------------------
    private sealed class TimerWindow : NativeWindow
    {
        public void Start()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmSelfTest)
            {
                DrainQueue();
            }
            else if (m.Msg == WmReadonlyContext)
            {
                DrainReadonlyContextQueue();
            }
            base.WndProc(ref m);
        }
    }

    private static void DrainQueue()
    {
        string cmd;
        while (CmdQueue.TryDequeue(out cmd))
        {
            try
            {
                string resp = DispatchSmokeAware(cmd);
                RespQueue.Enqueue(resp);
                Log("exec ok: " + cmd + " -> " + resp);
            }
            catch (Exception ex)
            {
                RespQueue.Enqueue(ErrJson(ex.Message));
                Log("exec ERR: " + cmd + " -> " + ex);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---- pipe server loop (background, never calls NXOpen) -------------
    private static void PipeLoop()
    {
        while (true)
        {
            NamedPipeServerStream inst = null;
            try
            {
                inst = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _pipe = inst;
                inst.WaitForConnection();
                Log("pipe client connected");
                byte[] lineBytes = ReadContextAwareLineBytes(inst);
                if (lineBytes == null || lineBytes.Length == 0) continue;
                string line = Encoding.UTF8.GetString(lineBytes).Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("{", StringComparison.Ordinal))
                {
                    HandleReadonlyContextFrame(inst, lineBytes);
                    continue;
                }
                CmdQueue.Enqueue(BindSmokePeer(inst, line));
                Log("cmd queued: " + line);
                SendMessage(_timerWindow.Handle, WmSelfTest, IntPtr.Zero, IntPtr.Zero);
                string resp = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(300);
                while (DateTime.UtcNow < deadline)
                {
                    if (RespQueue.TryDequeue(out resp)) break;
                    System.Threading.Thread.Sleep(50);
                }
                if (resp == null) resp = "{\"ok\":false,\"error\":\"timeout\"}";
                byte[] outBytes = Encoding.UTF8.GetBytes(resp + "\n");
                inst.Write(outBytes, 0, outBytes.Length);
                inst.Flush();
                Log("resp sent: " + resp);
            }
            catch (Exception e)
            {
                Log("pipe ERR: " + e.Message);
                System.Threading.Thread.Sleep(300);
            }
            finally
            {
                try { if (inst != null) inst.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }

    private static byte[] ReadLineBytes(System.IO.Stream s)
    {
        using (var ms = new MemoryStream())
        {
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                if (b == (byte)'\n') break;
                ms.WriteByte((byte)b);
            }
            return ms.ToArray();
        }
    }

    // ---- command dispatch ----------------------------------------------
    private static string Execute(string cmd)
    {
        string commandName = cmd.Split(' ')[0];
        string allowed = "|nx_move_component|nx_import_step|nx_constraint_alignment|nx_motion_new|nx_assembly_state|nx_component_geometry|nx_constraint|nx_constraint_value|nx_constraint_suppress|nx_motion_available|nx_motion_setup|nx_motion_state|nx_motion_link|nx_motion_joint|nx_motion_driver|nx_motion_solution|nx_motion_solve|nx_motion_frame|nx_motion_legacy_new|nx_motion_collision_setup|nx_motion_joint_axis|nx_motion_angle|nx_color|nx_view|nx_application|nx_motion_collision_check|nx_motion_joint_results|nx_motion_marker|nx_motion_marker_results|nx_shape|nx_motion_audit|nx_motion_transforms|nx_accurate_volume|nx_profile_solid|nx_motion_curve_pair|nx_motion_higher_pairs|nx_motion_collision_volumes|nx_motion_tighten|nx_motion_point_curve|nx_profile_boolean|nx_profile_curve|nx_motion_driver_velocity|nx_motion_gear_pair|nx_motion_suppress|nx_motion_joint_coupler|nx_motion_coupler_state|nx_sphere|nx_joint_reference|nx_component_datums|nx_motion_universal|nx_research_profile_driver|nx_research_face_normal|nx_research_analytic_state|nx_motion_driver_harmonic|nx_motion_driver_details|nx_motion_point_curve_sampling|nx_motion_integrator_refine|nx_motion_analytic_sphere_extrusion|nx_motion_export_input|nx_motion_precise_mass|nx_motion_body_face_contact|nx_session_parts|nx_motion_contact_state|nx_motion_contact_model|nx_motion_joint_torque|nx_curve_line|nx_motion_rack_pinion|nx_motion_analysis|nx_motion_contact_api|nx_motion_mass_state|nx_body_density|nx_motion_body_contact|nx_surface_refs|nx_surface_trim|nx_surface_extend|nx_surface_fixture|nx_surface_sweep|nx_surface_sweep_fixture|nx_surface_loft|nx_surface_shell|nx_surface_sew|nx_surface_thicken|nx_surface_state|nx_export_step_precise|nx_mass_convergence|nx_surface_deviation|nx_step_exact_bounds|nx_step_readonly_probe|nx_sheet_surface_deviation|nx_surface_parametric_state|nx_surface_edit_ellipse|nx_surface_edit_arc|nx_surface_spline_state|nx_surface_spline_fixture|nx_surface_edit_pole|nx_mass_convergence_target|nx_surface_sweep_arc_circle_trial|nx_surface_sweep_general_fixture|nx_surface_sweep_general|nx_surface_sweep_general_edit|nx_surface_sweep_general_state|nx_independent_box_boundary|nx_independent_analytic_boundary|nx_independent_annular_caps|nx_independent_annular_periodic|nx_independent_surface_probe|nx_independent_nurbs_topology|nx_independent_multihole|nx_nurbs_brep_trial_once|nx_fin_geometry_probe|nx_step_import_drift_probe|nx_step_template_experiment|nx_fin_representation_audit|nx_finsp_formation_probe|nx_finsp_trim_offset_audit|nx_finsp_edge_coverage|nx_complex_step_capture|nx_periodic_hole_prepare|nx_periodic_hole_surface|nx_periodic_hole_cold|nx_periodic_complex_probe|nx_nurbs_spline_probe_once|nx_nurbs_trim_recovery_prepare|nx_nurbs_trim_recovery_surface|nx_nurbs_trim_recovery_read|nx_nurbs_pcurve_edge_mapping|nx_ask2d_planar_trial_once|nx_ask2d_sheet_trial_once|nx_inspect|nx_blend_strict|nx_dimension_rectangle|nx_add_component|nx_interference|ping|nx_status|nx_create_part|nx_open_part|nx_save_part|nx_close_part|nx_create_sketch|nx_sketch_line|nx_sketch_rectangle|nx_sketch_circle|nx_sketch_arc|nx_finish_sketch|nx_extrude|nx_hole|nx_edge_blend|nx_chamfer|nx_unite|nx_fit_view|nx_export_step|nx_list_sketches|nx_list_bodies|nx_list_features|nx_list_edges|nx_list_faces|nx_undo|nx_release|";
        if (!allowed.Contains("|" + commandName + "|")) throw new Exception("Command not enabled in NX12 lab");
        if (commandName == "nx_open_part" || commandName == "nx_create_part" || commandName == "nx_export_step") SafePath(cmd.Split(' ')[1]);
        if (commandName != "ping" && commandName != "nx_status" && commandName != "nx_session_parts" && commandName != "nx_create_part" && commandName != "nx_open_part")
        {
            Part active = Session.GetSession().Parts.Work;
            if (active != null) SafePath(active.FullPath);
        }

        string[] parts = cmd.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ErrJson("empty command");
        switch (parts[0])
        {
            case "nx_move_component": return MoveComponentLab(parts);
            case "nx_import_step": return ImportStepLab(parts);
            case "nx_constraint_alignment": return EditAssemblyConstraint(parts);
            case "nx_motion_new": return MotionNew(parts);
            case "nx_assembly_state": return AssemblyState(parts);
            case "nx_component_geometry": return ComponentGeometry(parts);
            case "nx_constraint": return CreateAssemblyConstraint(parts);
            case "nx_constraint_value": case "nx_constraint_suppress": return EditAssemblyConstraint(parts);
            case "nx_motion_available": return MotionAvailability();
            case "nx_motion_setup": return MotionSetup();
            case "nx_motion_state": return MotionState();
            case "nx_motion_link": return MotionLinkCreate(parts);
            case "nx_motion_joint": return MotionJointCreate(parts);
            case "nx_motion_driver": return MotionDriverCreate(parts);
            case "nx_motion_solution": return MotionSolutionCreate(parts);
            case "nx_motion_solve": return MotionSolve(parts);
            case "nx_motion_frame": return MotionFrame(parts);
            case "nx_motion_legacy_new": return MotionLegacyCreate();
            case "nx_motion_collision_setup": return MotionCollisionSetup();
            case "nx_motion_joint_axis": return MotionJointAxis(parts);
            case "nx_motion_angle": return MotionAngle(parts);
            case "nx_color": return Phase2Color(parts);
            case "nx_view": return Phase2View(parts);
            case "nx_application": return Phase2Application(parts);
            case "nx_motion_collision_check": return MotionCollisionCheck(parts);
            case "nx_motion_joint_results": return MotionJointResults();
            case "nx_motion_marker": return MotionMarkerCreate(parts);
            case "nx_motion_marker_results": return MotionMarkerResults(parts);
            case "nx_shape": return SpatialShape(parts);
            case "nx_motion_audit": return MotionAudit();
            case "nx_motion_transforms": return MotionTransforms(parts);
            case "nx_accurate_volume": return AccurateVolume(parts);
            case "nx_profile_solid": return ProfileSolid(parts);
            case "nx_motion_curve_pair": return CurvePairCreate(parts);
            case "nx_motion_higher_pairs": return HigherPairState();
            case "nx_motion_collision_volumes": return CollisionVolumes(parts);
            case "nx_motion_tighten": return TightenSolution(parts);
            case "nx_motion_point_curve": return PointCurveCreate(parts);
            case "nx_profile_boolean": return ProfileSolid(parts);
            case "nx_profile_curve": return ProfileCurve(parts);
            case "nx_motion_driver_velocity": return DriverVelocity(parts);
            case "nx_motion_gear_pair": return GearPairCreate(parts);
            case "nx_motion_suppress": return MotionSuppression(parts);
            case "nx_motion_joint_coupler": return GeneralCouplerCreate(parts);
            case "nx_motion_coupler_state": return MechanismCouplerState();
            case "nx_sphere": return SphereSolid(parts);
            case "nx_joint_reference": return JointReference(parts);
            case "nx_component_datums": return ComponentDatums(parts);
            case "nx_motion_universal": return UniversalJointCreate(parts);
            case "nx_research_profile_driver": return ResearchProfileDriver(parts);
            case "nx_research_face_normal": return ResearchFaceNormal(parts);
            case "nx_research_analytic_state": return ResearchAnalyticalState();
            case "nx_motion_driver_harmonic": return HarmonicDriverCreate(parts);
            case "nx_motion_driver_details": return MotionDriverDetails();
            case "nx_motion_point_curve_sampling": return PointCurveSampling(parts);
            case "nx_motion_integrator_refine": return IntegratorRefine(parts);
            case "nx_motion_analytic_sphere_extrusion": return AnalyticalSphereExtrusion(parts);
            case "nx_motion_export_input": return MotionExportInput(parts);
            case "nx_motion_precise_mass": return PreciseMotionMass(parts);
            case "nx_motion_body_face_contact": return BodyFaceContactCreate(parts);
            case "nx_session_parts": return SessionPartSafetyState();
            case "nx_motion_contact_state": return BodyContactState(parts);
            case "nx_motion_contact_model": return BodyContactModelEdit(parts);
            case "nx_motion_joint_torque": return JointTorqueCreate(parts);
            case "nx_curve_line": return MechanismLine(parts);
            case "nx_motion_rack_pinion": return RackPinionCreate(parts);
            case "nx_motion_analysis": return MotionAnalysisSelect(parts);
            case "nx_motion_contact_api": return MotionContactApi();
            case "nx_motion_mass_state": return MotionMassState();
            case "nx_body_density": return Phase4Density(parts);
            case "nx_motion_body_contact": return BodyContactCreate(parts);
            case "nx_surface_refs": return SurfaceRefs(parts);
            case "nx_surface_trim": return SurfaceTrim(parts);
            case "nx_surface_extend": return SurfaceExtend(parts);
            case "nx_surface_fixture": return SurfaceFixture(parts);
            case "nx_surface_sweep": return SurfaceSweep(parts);
            case "nx_surface_sweep_fixture": return SurfaceSweepFixture(parts);
            case "nx_surface_loft": return SurfaceLoft(parts);
            case "nx_surface_shell": return SurfaceShell(parts);
            case "nx_surface_sew": return SurfaceSew(parts);
            case "nx_surface_thicken": return SurfaceThicken(parts);
            case "nx_surface_state": return SurfaceState();
            case "nx_export_step_precise": return SurfaceStep(parts);
            case "nx_mass_convergence": if(parts.Length!=2)throw new Exception("One relative tolerance required"); return Phase5StepReadonlyProbe.RelativeMass(parts[1]);
            case "nx_surface_deviation": if(parts.Length!=4)throw new Exception("Source target and new log path required"); return Phase5StepReadonlyProbe.RunDeviation(parts[1],parts[2],parts[3]);
            case "nx_step_exact_bounds": return Phase5StepReadonlyProbe.ExactBounds();
            case "nx_step_readonly_probe": if(parts.Length!=2)throw new Exception("One new diagnostic JSON path required"); Phase5StepReadonlyProbe.Main(new string[]{parts[1]}); return File.ReadAllText(parts[1]).Insert(1,"\"ok\":true,");
            case "nx_sheet_surface_deviation": if(parts.Length!=6)throw new Exception("Source path/tag target path/tag and new log path required"); return Phase5StepReadonlyProbe.RunSheetDeviation(parts[1],parts[2],parts[3],parts[4],parts[5]);
            case "nx_surface_parametric_state": return SurfaceParametricState(parts);
            case "nx_surface_edit_ellipse": return SurfaceEditBasic(parts,true);
            case "nx_surface_edit_arc": return SurfaceEditBasic(parts,false);
            case "nx_surface_spline_state": return SurfaceSplineState(parts);
            case "nx_surface_spline_fixture": return SurfaceSplineFixture(parts);
            case "nx_surface_edit_pole": return SurfaceEditPole(parts);
            case "nx_mass_convergence_target": if(parts.Length!=3)throw new Exception("Target mode and tolerance required"); return Phase5StepReadonlyProbe.RelativeMassTargeted(parts[1],parts[2]);
            case "nx_surface_sweep_arc_circle_trial": return SurfaceSweepArcCircleTrial(parts);
            case "nx_surface_sweep_general_fixture": return SurfaceSweepGeneralFixture(parts);
            case "nx_surface_sweep_general": return SurfaceSweepGeneral(parts);
            case "nx_surface_sweep_general_edit": return SurfaceSweepGeneralEdit(parts);
            case "nx_surface_sweep_general_state": return SurfaceSweepGeneralState(parts);
            case "nx_independent_box_boundary": return IvBox2609Read(parts);
            case "nx_independent_analytic_boundary": return IvAn2609Read(parts);
            case "nx_independent_annular_caps": return IvTrim2610Read(parts);
            case "nx_independent_annular_periodic": return IvPer2610Read(parts);
            case "nx_independent_surface_probe": return IndependentSurfaceProbe(parts);
            case "nx_independent_nurbs_topology": return Json(IvNurbs2610.Read(parts, _part));
            case "nx_independent_multihole": return IvMulti2610Read(parts);
            case "nx_nurbs_brep_trial_once": return IvBrep2610Trial(parts);
            case "nx_fin_geometry_probe": return FinGeometryProbe(parts);
            case "nx_step_import_drift_probe": return StepDriftCapture(parts);
            case "nx_step_template_experiment": return StepTemplateExperiment(parts);
            case "nx_fin_representation_audit": return FinRepresentationAudit(parts);
            case "nx_finsp_formation_probe": return FinspFormationProbe(parts);
            case "nx_finsp_trim_offset_audit": return FinspTrimOffsetAudit(parts);
            case "nx_finsp_edge_coverage": return FinspEdgeCoverage(parts);
            case "nx_complex_step_capture": return ComplexStepCapture(parts);
            case "nx_periodic_hole_prepare":
            case "nx_periodic_hole_surface": return PeriodicHoleBuild(parts);
            case "nx_periodic_hole_cold": return PeriodicHoleCold(parts);
            case "nx_periodic_complex_probe": return PeriodicComplexRead(parts);
            case "nx_nurbs_spline_probe_once": return NurbsSplineProbe(parts);
            case "nx_nurbs_trim_recovery_prepare":
            case "nx_nurbs_trim_recovery_surface": return TrimRecovery(parts);
            case "nx_nurbs_trim_recovery_read": return TrimRecoveryRead(parts);
            case "nx_nurbs_pcurve_edge_mapping": return NurbsPcurveEdgeMappingEvidence(parts);
            case "nx_ask2d_planar_trial_once":
            case "nx_ask2d_sheet_trial_once": return IvAsk2610Trial(parts);
            case "nx_inspect": return InspectPart();
            case "nx_blend_strict": return StrictBlend(parts);
            case "nx_dimension_rectangle": return DimensionRectangle(parts);
            case "nx_add_component": return AddComponentLab(parts);
            case "nx_interference": return InterferenceLab();
            case "ping":
                return OkJson("pong");
            case "nx_status":
                return Status();
            case "nx_create_part":
                return CreatePart(parts);
            case "nx_save_part":
                return SavePart();
            case "nx_release":
                return ReleaseTask();
            case "nx_create_sketch":
                return CreateSketch(parts);
            case "nx_sketch_line":
                return SketchLine(parts);
            case "nx_sketch_rectangle":
                return SketchRectangle(parts);
            case "nx_sketch_circle":
                return SketchCircle(parts);
            case "nx_sketch_arc":
                return SketchArc(parts);
            case "nx_finish_sketch":
                return FinishSketch(parts);
            case "nx_extrude":
                return Extrude(parts);
            case "nx_hole":
                return Hole(parts);
            case "nx_counterbore_hole":
                return CounterboreHole(parts);
            case "nx_countersink_hole":
                return CountersinkHole(parts);
            case "nx_shell":
                return Shell(parts);
            case "nx_edge_blend":
                return EdgeBlend(parts);
            case "nx_chamfer":
                return Chamfer(parts);
            case "nx_unite":
                return Unite(parts);
            case "nx_revolve":
                return Revolve(parts);
            case "nx_mirror":
                return Mirror(parts);
            case "nx_linear_pattern":
                return LinearPattern(parts);
            case "nx_circular_pattern":
                return CircularPattern(parts);
            case "nx_fit_view":
                return FitView();
            case "nx_export_step":
                return ExportStep(parts);
            case "nx_open_part":
                return OpenPart(parts);
            case "nx_close_part":
                return ClosePart(parts);
            case "nx_list_sketches":
                return ListObjects("Sketches", "sketch");
            case "nx_list_bodies":
                return ListObjects("Bodies", "body");
            case "nx_list_features":
                return ListObjects("Features", "feature");
            case "nx_list_edges":
                return ListEdges(parts);
            case "nx_list_faces":
                return ListFaces(parts);
            case "nx_undo":
                return Undo();
            case "nx_build_plate_test":
                return BuildPlateTest();
            case "create_block":
                return CreateBlock(parts);
            default:
                return ErrJson("unknown command: " + cmd);
        }
    }

    // ---- commands ------------------------------------------------------
    // This guard applies only to the disposable P13 fixture. All geometry is
    // still performed by the existing Execute/Shape/Hole handlers.
    private const string SmokeDirectory = @"@NX_LAB_ROOT@\models\phase4_p13_smoke_e80a1f33";
    private const string SmokeLock = @"@NX_LAB_ROOT@\logs\phase5\nx-execution.lock";
    private static bool smokeBetweenParts;
    private static string smokeCreateRequest;
    private static string smokeCreatedTag;
    private static bool smokeBlankInspected;

    private static void RecordSmokeRoles(Dictionary<string,object> data, PartCollection parts)
    {
        Part work = parts.Work, display = parts.Display;
        data["p13_work_path"] = work == null ? "" : work.FullPath;
        data["p13_display_path"] = display == null ? "" : display.FullPath;
        data["p13_work_tag"] = work == null ? null : work.Tag.ToString();
        data["p13_display_tag"] = display == null ? null : display.Tag.ToString();
    }
    private static readonly HashSet<string> smokeDispatches = new HashSet<string>();

    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    private static string BindSmokePeer(NamedPipeServerStream pipe, string line)
    {
        uint peer = 0;
        if (line.StartsWith("nx_p13_once ", StringComparison.Ordinal) &&
            !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out peer))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return peer.ToString(CultureInfo.InvariantCulture) + "\n" + line;
    }

    private static bool SmokePath(string path)
    {
        return !String.IsNullOrEmpty(path) && Path.IsPathRooted(path) &&
            Path.GetFullPath(path).StartsWith(SmokeDirectory + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameSmokePath(string left, string right)
    {
        return !String.IsNullOrEmpty(left) && !String.IsNullOrEmpty(right) &&
            String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SmokeRead(string cmd)
    {
        return cmd == "nx_status" || cmd == "nx_session_parts" || cmd == "nx_inspect";
    }

    private static string SmokeStartUtc()
    {
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
            return process.StartTime.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture);
    }

    private static string SmokeMvid()
    {
        return typeof(NX_MCP_Loader).Module.ModuleVersionId.ToString("D");
    }

    private static Dictionary<string,object> SmokeIntent(string lockPath)
    {
        // The Worker owns the mandatory byte-range lock on byte zero, and
        // fsyncs one transient dispatch permit starting at byte one.
        using (var file = new FileStream(lockPath, FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite, 1, FileOptions.None))
        {
            bool locked = false;
            try { file.ReadByte(); }
            catch (IOException e) { if ((e.HResult & 65535) != 33) throw; locked = true; }
            if (!locked || file.Length < 3 || file.Length > 4096)
                throw new InvalidOperationException("No current Worker dispatch permit");
            long end = file.Length;
            file.Position = 1;
            using (var reader = new BinaryReader(file, Encoding.ASCII))
            {
                byte[] bytes = reader.ReadBytes((int)end - 1);
                if (bytes.Length != end - 1 || file.Length != end)
                    throw new IOException("Incomplete Worker dispatch permit");
                return new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string,object>>(Encoding.ASCII.GetString(bytes));
            }
        }
    }

    private static string SmokeOperationTarget(string operation, string command)
    {
        string final = Path.Combine(SmokeDirectory,"batch_validation.prt");
        switch (operation)
        {
            case "batch-validation.create.0":
                if (command == "nx_create_part " + final) return ""; break;
            case "batch-validation.solid.0":
                if (command == "nx_shape batch_outline box 0 0 0 0 0 1 20 16 4 create -") return final; break;
            case "batch-validation.hole.0":
                if (System.Text.RegularExpressions.Regex.IsMatch(command, @"\Anx_hole BODY_[1-9][0-9]* 0 0 4 4 0\z")) return final; break;
            case "batch-validation.view.0": if (command == "nx_fit_view") return final; break;
            case "batch-validation.save.0": if (command == "nx_save_part") return final; break;
            case "batch-validation.close.0":
                if (command == "nx_close_part 0") return final; break;
            case "batch-validation.reopen.0": if (command == "nx_open_part " + final) return ""; break;
        }
        throw new InvalidOperationException("Command outside fixed P13 operation");
    }

    private static string SmokeMutation(string request, string cmd, int peer, string lockPath)
    {
        var permit = SmokeIntent(lockPath);
        if ((string)permit["request"] != request || (string)permit["command"] != cmd ||
            (string)permit["start_utc"] != SmokeStartUtc() || (string)permit["loader_mvid"] != SmokeMvid() ||
            (int)permit["peer_pid"] != peer || (int)permit["nx_pid"] != System.Diagnostics.Process.GetCurrentProcess().Id)
            throw new InvalidOperationException("Stale permit, adapter, or NX process");
        string target = SmokeOperationTarget((string)permit["operation"],cmd);
        var parts = Session.GetSession().Parts;
        var work = parts.Work;
        var display = parts.Display;
        var loaded = parts.ToArray();
        if (target.Length == 0)
        {
            if (work != null || display != null || loaded.Length != 0 ||
                (string)permit["expected_path"] != "" || (string)permit["expected_tag"] != "")
                throw new InvalidOperationException("An empty NX session is required");
        }
        else
        {
            if (work == null || display == null || loaded.Length != 1 ||
                display.Tag != work.Tag || loaded[0].Tag != work.Tag ||
                !SameSmokePath(target, work.FullPath) || !SameSmokePath(target,(string)permit["expected_path"]) ||
                work.Tag.ToString() != (string)permit["expected_tag"] || !work.IsFullyLoaded || work.IsReadOnly ||
                _part == null || _part.Tag != work.Tag)
                throw new InvalidOperationException("Work/Display/Loaded Part identity changed");
            if (cmd.StartsWith("nx_hole ",StringComparison.Ordinal))
            {
                var solid = GetBody(cmd.Split(' ')[1]);
                if (solid.IsOccurrence || !solid.IsSolidBody || solid.OwningPart == null ||
                    solid.OwningPart.Tag != work.Tag || !work.Bodies.ToArray().Any(b => b.Tag == solid.Tag))
                    throw new InvalidOperationException("Body does not belong to the active solid part");
            }
        }
        if (!smokeDispatches.Add(request)) throw new InvalidOperationException("Dispatch already entered");
        if (cmd == "nx_close_part 0")
        {
            if (NXOpen.UF.UFSession.GetUFSession().Part.IsModified(work.Tag))
                throw new InvalidOperationException("Modified part cannot be closed");
            work.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            _part = null;
            _partPath = null;
            ResetTaskState();
            smokeBetweenParts = true;
            return OkJson("closed part");
        }
        bool creating = cmd.StartsWith("nx_create_part ",StringComparison.Ordinal);
        bool opening = cmd.StartsWith("nx_open_part ",StringComparison.Ordinal);
        if (creating || opening)
        {
            smokeCreateRequest = null;
            smokeCreatedTag = null;
            smokeBlankInspected = false;
        }
        if (cmd.StartsWith("nx_shape ",StringComparison.Ordinal))
        {
            if (!smokeBlankInspected || smokeCreatedTag != work.Tag.ToString() ||
                work.Bodies.ToArray().Length != 0)
                throw new InvalidOperationException("Freshly created blank part and I0 required before block");
            smokeBlankInspected = false;
            smokeCreateRequest = null;
        }
        string result = Execute(cmd);
        if (creating || opening)
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer();
            var data = js.Deserialize<Dictionary<string,object>>(result);
            if ((bool)data["ok"])
            {
                var current = Session.GetSession().Parts;
                var active = current.Work;
                string expected = cmd.Substring(cmd.IndexOf(' ') + 1);
                if (active == null || current.Display == null || current.Display.Tag != active.Tag ||
                    !SameSmokePath(active.FullPath, expected) || current.ToArray().Length != 1 ||
                    _part == null || _part.Tag != active.Tag)
                    throw new InvalidOperationException("Created or reopened part identity mismatch");
                RecordSmokeRoles(data, current);
                data["receipt_kind"] = "fresh_dispatch";
                if (creating)
                {
                    smokeCreatedTag = active.Tag.ToString();
                    smokeCreateRequest = request;
                    data["p13_create_request"] = request;
                }
                result = js.Serialize(data);
            }
        }
        if (target.Length == 0) smokeBetweenParts = true;
        if (cmd.StartsWith("nx_hole ",StringComparison.Ordinal) || cmd.StartsWith("nx_shape ",StringComparison.Ordinal))
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer();
            var data = js.Deserialize<Dictionary<string,object>>(result);
            if ((bool)data["ok"])
            {
                string id = cmd.StartsWith("nx_shape ",StringComparison.Ordinal) ? (string)data["body_id"] : cmd.Split(' ')[1];
                Body owner = GetBody(id);
                if (owner.OwningPart.Tag != work.Tag || !work.Bodies.ToArray().Any(b => b.Tag == owner.Tag))
                    throw new InvalidOperationException("After modeling, body identity is unknown");
                data["body_id"] = id;
                data["body_tag"] = owner.Tag.ToString();
                data["owning_part_tag"] = work.Tag.ToString();
                result = js.Serialize(data);
            }
        }
        return result;
    }

    private static string DispatchSmokeAware(string queued)
    {
        int cut = queued.IndexOf('\n');
        int peer = Int32.Parse(queued.Substring(0,cut), CultureInfo.InvariantCulture);
        string line = queued.Substring(cut+1);
        if (!line.StartsWith("nx_p13_once ",StringComparison.Ordinal))
        {
            if (!SmokeRead(line) && line != "ping")
            {
                var parts = Session.GetSession().Parts;
                bool touches = line.Split(' ').Any(SmokePath) ||
                    parts.ToArray().Any(p => SmokePath(p.FullPath)) ||
                    (_part != null && SmokePath(_partPath));
                if (touches || (smokeBetweenParts && parts.Work == null))
                    throw new InvalidOperationException("Fixed Smoke model requires the formal Worker");
            }
            return Execute(line);
        }
        var frame = line.Split(new[]{' '}, 3);
        Guid parsed;
        if (frame.Length != 3 || peer <= 0 || !Guid.TryParseExact(frame[1],"D",out parsed) ||
            parsed.ToString("D") != frame[1]) throw new InvalidOperationException("Invalid scoped request");
        string cmd = frame[2];
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        serializer.MaxJsonLength = 4000000;
        Dictionary<string,object> response;
        try
        {
            string raw;
            if (SmokeRead(cmd))
            {
                var current = Session.GetSession().Parts;
                var work = current.Work;
                if (cmd == "nx_inspect" && (work == null || current.Display == null ||
                    current.Display.Tag != work.Tag || current.ToArray().Length != 1 ||
                    !SmokePath(work.FullPath) || _part == null || _part.Tag != work.Tag))
                    throw new InvalidOperationException("Inspection part ownership changed");
                if (work != null && SmokePath(work.FullPath)) smokeBetweenParts = true;
                raw = Execute(cmd);
            }
            else raw = SmokeMutation(frame[1],cmd,peer,SmokeLock);
            response = serializer.Deserialize<Dictionary<string,object>>(raw);
            if ((cmd == "nx_status" || cmd == "nx_inspect") && (bool)response["ok"])
            {
                var parts = Session.GetSession().Parts;
                RecordSmokeRoles(response, parts);
                if (cmd == "nx_inspect" && smokeCreateRequest != null)
                {
                    var work = parts.Work;
                    smokeBlankInspected = work != null && parts.Display != null &&
                        parts.Display.Tag == work.Tag && work.Tag.ToString() == smokeCreatedTag &&
                        work.Bodies.ToArray().Length == 0;
                    response["p13_create_request"] = smokeCreateRequest;
                    response["p13_blank_observed"] = smokeBlankInspected;
                }
            }
        }
        catch (Exception e)
        {
            response = new Dictionary<string,object>{{"ok",false},{"error",e.Message},{"error_type",e.GetType().FullName}};
        }
        response["p13_request"] = frame[1];
        response["p13_command"] = cmd;
        response["p13_start_utc"] = SmokeStartUtc();
        response["p13_loader_mvid"] = SmokeMvid();
        response["pipe_client_pid"] = peer;
        response["p13_server_pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
        return serializer.Serialize(response);
    }

    private static string Status()
    {
        if (_session == null) _session = Session.GetSession();
        try
        {
            // The NX work part is authoritative. _part may become an inactive
            // wrapper when the user closes/switches parts outside the Loader.
            NXOpen.Part work = _session.Parts.Work;
            if (work == null)
            {
                if (_part != null || _partPath != null) ResetTaskState();
                _part = null;
                _partPath = null;
                return "{\"ok\":true,\"ready\":true,\"loader\":\"nx12_ai_lab\",\"nx\":\"" + Esc(NxVersion()) + "\"," +
                       "\"part\":\"\",\"sketches\":0,\"bodies\":0}";
            }

            string fullPath = "";
            try { fullPath = work.FullPath; } catch { }
            if (_part != null && _part.Tag != work.Tag) ResetTaskState();
            _part = work;
            _partPath = fullPath;

            // Keep an unsaved active part visible to Runner preflight so it is
            // never mistaken for "no active part".
            string partName = string.IsNullOrEmpty(fullPath)
                ? "<unsaved-active-part>"
                : fullPath;

            return "{\"ok\":true,\"ready\":true,\"loader\":\"nx12_ai_lab\",\"nx\":\"" + Esc(NxVersion()) + "\"," +
                   "\"part\":\"" + Esc(partName) + "\",\"sketches\":" + _sketches.Count +
                   ",\"bodies\":" + _bodies.Count + "}";
        }
        catch (Exception e)
        {
            Log("status ERR: " + e.Message);
            return ErrJson("status failed: " + e.Message);
        }
    }

    private static string CreatePart(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_create_part <path>");
        string path = parts[1];
        _session = Session.GetSession();
        var lw = _session.ListingWindow;
        if(path.StartsWith(LabPath(@"models\phase2_"),StringComparison.OrdinalIgnoreCase))lw.Close();else lw.Open();

        SafePath(path); if (File.Exists(path)) throw new IOException("Refusing to overwrite existing part");

        // Use NewDisplay: FileNewBuilder crashes with access violation in this
        // resident-loader context (unmanaged exception cannot be caught).
        NXOpen.Part part = _session.Parts.NewDisplay(path, Part.Units.Millimeters);
        try { _session.ApplicationSwitchImmediate("UG_APP_MODELING"); } catch { }
        _part = part;
        _partPath = path;
        ResetTaskState();
        return OkJson("part", path, "created");
    }

    private static void ResetTaskState()
    {
        _sketches.Clear();
        _sketchPlanes.Clear();
        _bodies.Clear();
        _sketchCounter = 0;
        _bodyCounter = 0;
    }

    private static string SavePart()
    {
        if (_part == null) return ErrJson("no active part");
        var savedStatus=_part.Save(NXOpen.BasePart.SaveComponents.False, NXOpen.BasePart.CloseAfterSave.False);
        try {if(savedStatus.NumberUnsavedParts>0 || savedStatus.NumberUnsavedObjects>0)throw new IOException("NX save failed: unsaved parts="+savedStatus.NumberUnsavedParts+" objects="+savedStatus.NumberUnsavedObjects);}
        finally {savedStatus.Dispose();}
        return OkJson("saved", _part.FullPath, "saved");
    }

    private static string ReleaseTask()
    {
        // Loader stays resident. Only clear task state and restore normal
        // manual editing (NX is already interactive after each command).
        ResetTaskState();
        _part = null;
        _partPath = null;
        try { FitView(); } catch { }
        return OkJson("task state cleared; loader stays ready");
    }

    private static Plane PlaneZ(double z)
    {
        return _part.Planes.CreatePlane(
            new Point3d(0.0, 0.0, z),
            new Vector3d(0.0, 0.0, 1.0),
            SmartObject.UpdateOption.WithinModeling);
    }

    private static string CreateSketch(string[] parts)
    {
        if (_part == null) return ErrJson("no active part");
        string plane = (parts.Length > 1 ? parts[1] : "XY").ToUpperInvariant();
        if (plane != "XY" && plane != "XZ" && plane != "YZ")
            return ErrJson("plane must be XY, XZ or YZ");

        _sketchCounter++;
        string id = "SKETCH_" + _sketchCounter;

        // NX 2506 does not reliably honor a bare CreatePlane(origin, normal)
        // when CreateSketchInPlaceBuilder2 commits a non-XY sketch: the sketch
        // can silently fall back to Z=0. Use an explicitly oriented fixed plane
        // and fully specify origin + horizontal axis + ExistingPlane mode.
        Point3d origin = new Point3d(0.0, 0.0, 0.0);
        Plane planeRef = _part.Planes.CreateFixedTypePlane(
            origin,
            SketchPlaneMatrix(plane),
            SmartObject.UpdateOption.WithinModeling);
        Direction axisRef = _part.Directions.CreateDirection(
            origin,
            SketchLocalXAxis(plane),
            SmartObject.UpdateOption.WithinModeling);
        NXOpen.Point sketchOrigin = _part.Points.CreatePoint(origin);

        var b = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk;
        try
        {
            b.PlaneReference = planeRef;
            b.AxisReference = axisRef;
            b.SketchOrigin = sketchOrigin;
            b.PlaneOption = Sketch.PlaneOption.ExistingPlane;
            b.OriginOption = OriginMethod.SpecifyPoint;
            sk = (Sketch)b.Commit();
        }
        finally
        {
            b.Destroy();
        }
        try { sk.SetName(id); } catch { }
        sk.Activate(Sketch.ViewReorient.True);
        _sketches[id] = sk;
        _sketchPlanes[id] = plane;
        return OkJson("sketch_id", id, "created plane=" + plane);
    }

    private static string SketchLine(string[] parts)
    {
        if (parts.Length < 6) return ErrJson("usage: nx_sketch_line <sketch_id> x1 y1 x2 y2");
        var sk = LabEditingSketch(parts[1]);
        double x1 = D(parts[2]), y1 = D(parts[3]), x2 = D(parts[4]), y2 = D(parts[5]);
        var curve = _part.Curves.CreateLine(
            SketchPoint(parts[1], x1, y1),
            SketchPoint(parts[1], x2, y2));
        sk.AddGeometry(curve, Sketch.InferConstraintsOption.InferNoConstraints);
        return OkJson("added line " + parts[2] + "," + parts[3] + " -> " + parts[4] + "," + parts[5]);
    }

    private static string SketchRectangle(string[] parts)
    {
        if (parts.Length < 6) return ErrJson("usage: nx_sketch_rectangle <sketch_id> cx cy w h");
        var sk = LabEditingSketch(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), w = D(parts[4]), h = D(parts[5]);
        double x1 = cx - w / 2.0, y1 = cy - h / 2.0;
        double x2 = cx + w / 2.0, y2 = cy + h / 2.0;
        AddLine(sk, parts[1], x1, y1, x2, y1);
        AddLine(sk, parts[1], x2, y1, x2, y2);
        AddLine(sk, parts[1], x2, y2, x1, y2);
        AddLine(sk, parts[1], x1, y2, x1, y1);
        return OkJson("added rectangle " + w + "x" + h + " at " + cx + "," + cy);
    }

    private static string SketchCircle(string[] parts)
    {
        if (parts.Length < 5) return ErrJson("usage: nx_sketch_circle <sketch_id> cx cy diameter");
        var sk = LabEditingSketch(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), dia = D(parts[4]);
        double r = dia / 2.0;
        string plane = GetSketchPlaneName(parts[1]);
        var ell = LabCircle(
            SketchPoint(parts[1], cx, cy),
            SketchVectorForPlane(plane, 1.0, 0.0),
            SketchVectorForPlane(plane, 0.0, 1.0),
            r);
        sk.AddGeometry(ell, Sketch.InferConstraintsOption.InferNoConstraints);
        LockLabCircle(sk,ell);
        return OkJson("added circle d=" + dia + " at " + cx + "," + cy);
    }

    private static string SketchArc(string[] parts)
    {
        if (parts.Length < 7) return ErrJson("usage: nx_sketch_arc <sketch_id> cx cy radius start_deg end_deg");
        var sk = LabEditingSketch(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), r = D(parts[4]);
        double a1 = D(parts[5]), a2 = D(parts[6]);
        double rad1 = a1 * Math.PI / 180.0;
        double rad2 = a2 * Math.PI / 180.0;
        string plane = GetSketchPlaneName(parts[1]);
        var arc = _part.Curves.CreateArc(
            SketchPoint(parts[1], cx, cy),
            SketchVectorForPlane(plane, 1.0, 0.0),
            SketchVectorForPlane(plane, 0.0, 1.0),
            r, rad1, rad2);
        sk.AddGeometry(arc, Sketch.InferConstraintsOption.InferNoConstraints);
        return OkJson("added arc r=" + r + " " + a1 + ".." + a2 + " deg at " + cx + "," + cy);
    }

    private static string FinishSketch(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_finish_sketch <sketch_id>");
        var sk = LabEditingSketch(parts[1]);
        sk.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model);
        return OkJson("finished " + parts[1]);
    }

    private static string Extrude(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_extrude <sketch_id> <distance> [reverse] [start_offset] [create|subtract] [body_id]");
        var sk = LabEditingSketch(parts[1]);
        double dist = D(parts[2]);
        bool reverse = parts.Length > 3 && parts[3] == "1";
        double startOff = parts.Length > 4 ? D(parts[4]) : 0.0;
        string op = parts.Length > 5 ? parts[5] : "create";
        Body target = null;
        if (parts.Length > 6 && _bodies.ContainsKey(parts[6])) target = _bodies[parts[6]];

        var bt = op == "subtract"
            ? BooleanOperation.BooleanType.Subtract
            : (target != null
                ? BooleanOperation.BooleanType.Unite
                : BooleanOperation.BooleanType.Create);

        var section = _part.Sections.CreateSection();
        var rule = _part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sk.Feature });
        section.AddToSection(
            new SelectionIntentRule[] { rule }, null, null, null,
            new Point3d(0.0, 0.0, 0.0), Section.Mode.Create, false);

        Vector3d axis = SketchExtrudeAxis(GetSketchPlaneName(parts[1]));
        if (reverse) axis = new Vector3d(-axis.X, -axis.Y, -axis.Z);
        var dir = _part.Directions.CreateDirection(
            new Point3d(0.0, 0.0, 0.0), axis,
            SmartObject.UpdateOption.WithinModeling);

        var b = _part.Features.CreateExtrudeBuilder(null);
        Feature feature;
        try
        {
            b.Section = section;
            b.Direction = dir;
            b.Limits.StartExtend.Value.RightHandSide = startOff.ToString("0.###", CultureInfo.InvariantCulture);
            b.Limits.EndExtend.Value.RightHandSide = (startOff + dist).ToString("0.###", CultureInfo.InvariantCulture);
            b.BooleanOperation.Type = bt;
            if (target != null)
            {
                b.BooleanOperation.SetTargetBodies(new Body[] { target });
            }
            b.AllowSelfIntersectingSection(true);
            feature = b.CommitFeature();
        }
        finally
        {
            b.Destroy();
        }

        // identify body: last body of the part
        Body[] bodies = _part.Bodies.ToArray();
        Body newBody = null;
        if (bodies.Length > 0) newBody = bodies[bodies.Length - 1];
        if (bt != BooleanOperation.BooleanType.Subtract && newBody != null)
        {
            _bodyCounter++;
            string bid = "BODY_" + _bodyCounter;
            _bodies[bid] = newBody;
            return OkJson("body_id", bid, "extruded " + dist + (reverse ? " (rev)" : ""));
        }
        return OkJson("extruded subtract ok");
    }

    private static string Revolve(string[] parts)
    {
        if (parts.Length < 7) return ErrJson("usage: nx_revolve <sketch_id> <ax> <ay> <bx> <by> <angle> [reverse]");
        var sk = LabEditingSketch(parts[1]);
        double ax = D(parts[2]), ay = D(parts[3]);
        double bx = D(parts[4]), by = D(parts[5]);
        double angle = D(parts[6]);
        bool reverse = parts.Length > 7 && parts[7] == "1";

        double vx = bx - ax, vy = by - ay;
        double len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1e-9) return ErrJson("revolve axis has zero length");
        vx /= len; vy /= len;
        if (reverse) { vx = -vx; vy = -vy; }

        var section = _part.Sections.CreateSection();
        var rule = _part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sk.Feature });
        section.AddToSection(
            new SelectionIntentRule[] { rule }, null, null, null,
            new Point3d(0.0, 0.0, 0.0), Section.Mode.Create, false);

        NXOpen.Axis axis;
        try
        {
            NXOpen.Point axisPoint = _part.Points.CreatePoint(SketchPoint(parts[1], ax, ay));
            NXOpen.Direction axisDir = _part.Directions.CreateDirection(
                axisPoint, SketchVectorForPlane(GetSketchPlaneName(parts[1]), vx, vy));
            axis = _part.Axes.CreateAxis(axisPoint, axisDir, SmartObject.UpdateOption.WithinModeling);
        }
        catch (Exception e) { return ErrJson("revolve[axis] failed: " + e.Message); }

        var b = _part.Features.CreateRevolveBuilder(null);
        Feature feature;
        try
        {
            b.Section = section;
            b.Axis = axis;
            b.Limits.StartExtend.Value.RightHandSide = "0";
            b.Limits.EndExtend.Value.RightHandSide = angle.ToString("0.###", CultureInfo.InvariantCulture);
            b.BooleanOperation.Type = BooleanOperation.BooleanType.Create;
            feature = b.CommitFeature();
        }
        catch (Exception e)
        {
            try { b.Destroy(); } catch { }
            return ErrJson("revolve[commit] failed: " + e.Message);
        }
        b.Destroy();

        Body[] bodies = _part.Bodies.ToArray();
        Body newBody = null;
        if (bodies.Length > 0) newBody = bodies[bodies.Length - 1];
        if (newBody != null)
        {
            _bodyCounter++;
            string bid = "BODY_" + _bodyCounter;
            _bodies[bid] = newBody;
            return OkJson("body_id", bid, "revolved " + angle + " deg");
        }
        return ErrJson("revolve produced no body");
    }

    private static string Mirror(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_mirror <body_id> <XY|XZ|YZ> [offset]");
        Body body = GetBody(parts[1]);
        string planeName = parts[2].ToUpperInvariant();
        double offset = parts.Length > 3 ? D(parts[3]) : 0.0;

        Point3d origin;
        Vector3d mx, my, mz;
        switch (planeName)
        {
            case "XY":
                origin = new Point3d(0.0, 0.0, offset);
                mx = new Vector3d(1, 0, 0); my = new Vector3d(0, 1, 0); mz = new Vector3d(0, 0, 1);
                break;
            case "XZ":
                origin = new Point3d(0.0, offset, 0.0);
                mx = new Vector3d(0, 0, 1); my = new Vector3d(1, 0, 0); mz = new Vector3d(0, 1, 0);
                break;
            case "YZ":
                origin = new Point3d(offset, 0.0, 0.0);
                mx = new Vector3d(0, 1, 0); my = new Vector3d(0, 0, 1); mz = new Vector3d(1, 0, 0);
                break;
            default:
                return ErrJson("plane must be XY, XZ or YZ");
        }
        var m = new Matrix3x3();
        m.Xx = mx.X; m.Xy = mx.Y; m.Xz = mx.Z;
        m.Yx = my.X; m.Yy = my.Y; m.Yz = my.Z;
        m.Zx = mz.X; m.Zy = mz.Y; m.Zz = mz.Z;

        var before = new HashSet<Tag>();
        foreach (Body b in _part.Bodies) before.Add(b.Tag);

        DatumPlane datumPlane;
        var mb = _part.Features.CreateMirrorBodyBuilder(null);
        try
        {
            datumPlane = _part.Datums.CreateFixedDatumPlane(origin, m);
            mb.MirrorBodyList.SetArray(new Body[] { body });
            mb.Plane.SetValue(datumPlane, null, new Point3d(0.0, 0.0, 0.0));
            // NX12 has no DeleteSourceBody; mirror is disabled by allowlist.
            mb.CommitFeature();
        }
        catch (Exception e)
        {
            try { mb.Destroy(); } catch { }
            return ErrJson("mirror failed: " + e.Message);
        }
        mb.Destroy();

        Body newBody = null;
        foreach (Body b in _part.Bodies)
        {
            if (!before.Contains(b.Tag)) { newBody = b; break; }
        }
        if (newBody != null)
        {
            _bodyCounter++;
            string bid = "BODY_" + _bodyCounter;
            _bodies[bid] = newBody;
            return OkJson("body_id", bid, "mirrored about " + planeName);
        }
        return ErrJson("mirror produced no new body");
    }

    private static string LinearPattern(string[] parts)
    {
        if (parts.Length < 5)
            return ErrJson("usage: nx_linear_pattern <body_id> <X|Y|Z> <count> <spacing> [reverse]");
        Body body = GetBody(parts[1]);
        string dirName = parts[2].ToUpperInvariant();
        int count;
        double spacing;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            return ErrJson("count must be an integer >= 2");
        if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out spacing))
            return ErrJson("bad spacing");
        bool reverse = parts.Length > 5 && parts[5] == "1";
        if (count < 2) return ErrJson("count must be >= 2");
        if (double.IsNaN(spacing) || double.IsInfinity(spacing) || spacing <= 0.0)
            return ErrJson("spacing must be > 0");

        Vector3d vec;
        switch (dirName)
        {
            case "X": vec = new Vector3d(1, 0, 0); break;
            case "Y": vec = new Vector3d(0, 1, 0); break;
            case "Z": vec = new Vector3d(0, 0, 1); break;
            default: return ErrJson("direction must be X, Y or Z");
        }

        var before = new HashSet<Tag>();
        foreach (Body b in _part.Bodies) before.Add(b.Tag);

        // Linear pattern via Move-Object copy: NumberOfCopies is the number of
        // NEW instances (excluding the seed), each translated by `spacing` along
        // the direction vector, so count/spacing/reverse semantics are exact and
        // every copy is an independent non-associative body.
        var mb = _part.BaseFeatures.CreateMoveObjectBuilder(null);
        try
        {
            try { mb.ObjectToMoveObject.SetArray(new NXObject[] { body }); }
            catch (Exception e) { throw new Exception("step Objects: " + e.Message, e); }
            try
            {
                mb.Associative = false;
                mb.MoveObjectResult = MoveObjectBuilder.MoveObjectResultOptions.CopyOriginal;
                mb.NumberOfCopies = count - 1;
            }
            catch (Exception e) { throw new Exception("step Options: " + e.Message, e); }

            try
            {
                Vector3d dirVec = reverse
                    ? new Vector3d(-vec.X, -vec.Y, -vec.Z)
                    : vec;
                Direction dirObj = _part.Directions.CreateDirection(
                    new Point3d(0.0, 0.0, 0.0), dirVec,
                    SmartObject.UpdateOption.WithinModeling);
                ModlMotion motion = mb.TransformMotion;
                motion.Option = ModlMotion.Options.Distance;
                motion.DistanceVector = dirObj;
                motion.DistanceValue.RightHandSide =
                    spacing.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch (Exception e) { throw new Exception("step Motion: " + e.Message, e); }

            try { mb.Commit(); }
            catch (Exception e) { throw new Exception("step Commit: " + e.Message, e); }
        }
        catch (Exception e)
        {
            try { mb.Destroy(); } catch { }
            return ErrJson("linear pattern failed: " + e.Message);
        }
        mb.Destroy();

        var newIds = new List<string>();
        foreach (Body b in _part.Bodies)
        {
            if (!before.Contains(b.Tag))
            {
                _bodyCounter++;
                string bid = "BODY_" + _bodyCounter;
                _bodies[bid] = b;
                newIds.Add(bid);
            }
        }
        if (newIds.Count == 0) return ErrJson("linear pattern produced no new bodies");
        return OkJsonList("body_ids", newIds,
            "linear pattern " + count + " along " + dirName + (reverse ? " (reverse)" : ""));
    }

    private static string CircularPattern(string[] parts)
    {
        // nx_circular_pattern <body_id> <X|Y|Z> <cx> <cy> <cz> <count> <angle> [reverse]
        if (parts.Length < 8)
            return ErrJson("usage: nx_circular_pattern <body_id> <X|Y|Z> <cx> <cy> <cz> <count> <angle> [reverse]");
        Body body = GetBody(parts[1]);
        string axisName = parts[2].ToUpperInvariant();
        double cx = D(parts[3]), cy = D(parts[4]), cz = D(parts[5]);
        int count;
        double totalAngle;
        if (!int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            return ErrJson("count must be an integer >= 2");
        if (!double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out totalAngle))
            return ErrJson("bad angle");
        bool reverse = parts.Length > 8 && parts[8] == "1";
        if (count < 2) return ErrJson("count must be >= 2");
        if (double.IsNaN(totalAngle) || double.IsInfinity(totalAngle)
            || totalAngle <= 0.0 || totalAngle > 360.0)
            return ErrJson("angle must be in (0, 360]");

        Vector3d axisVec;
        switch (axisName)
        {
            case "X": axisVec = new Vector3d(1, 0, 0); break;
            case "Y": axisVec = new Vector3d(0, 1, 0); break;
            case "Z": axisVec = new Vector3d(0, 0, 1); break;
            default: return ErrJson("axis must be X, Y or Z");
        }
        // Angle step semantics:
        //  - angle == 360: step = 360/count (full circle, instances at 0..360-step)
        //  - angle <  360: step = angle/(count-1) (seed at 0, last instance at `angle`)
        double stepAngle = (Math.Abs(totalAngle - 360.0) < 1e-9)
            ? totalAngle / count
            : totalAngle / (count - 1);

        var before = new HashSet<Tag>();
        foreach (Body b in _part.Bodies) before.Add(b.Tag);

        // Circular pattern by rigid rotation copy. Each copy is an independent
        // rigid rotation of the seed about the axis through `center`, by the
        // absolute angle theta_i = sign * i * stepAngle. We set the 3x3 rotation
        // matrix on the Move-Object manipulator so the solid's position AND
        // own orientation both rotate (point-to-point translation kept position
        // but never rotated the body). Matrix convention: local axes rotate by
        // theta, i.e. point P -> R * P about the pivot (WCS origin == center).
        double sign = reverse ? -1.0 : 1.0;

        for (int i = 1; i < count; i++)
        {
            double th = sign * i * stepAngle * Math.PI / 180.0;
            double c = Math.Cos(th), s = Math.Sin(th);
            Matrix3x3 mat = new Matrix3x3();
            mat.Xx = 1.0; mat.Xy = 0.0; mat.Xz = 0.0;
            mat.Yx = 0.0; mat.Yy = 1.0; mat.Yz = 0.0;
            mat.Zx = 0.0; mat.Zy = 0.0; mat.Zz = 1.0;
            switch (axisName)
            {
                case "Z": mat.Xx = c; mat.Xy = -s; mat.Yx = s; mat.Yy = c; mat.Zz = 1.0; break;
                case "X": mat.Xx = 1.0; mat.Yy = c; mat.Yz = -s; mat.Zy = s; mat.Zz = c; break;
                case "Y": mat.Xx = c; mat.Xz = s; mat.Yy = 1.0; mat.Zx = -s; mat.Zz = c; break;
            }

            var mb = _part.BaseFeatures.CreateMoveObjectBuilder(null);
            try
            {
                mb.ObjectToMoveObject.SetArray(new NXObject[] { body });
                mb.Associative = false;
                mb.MoveObjectResult = MoveObjectBuilder.MoveObjectResultOptions.CopyOriginal;
                mb.NumberOfCopies = 1;
                ModlMotion motion = mb.TransformMotion;
                motion.Option = ModlMotion.Options.Dynamic;
                motion.ManipulatorMatrix = mat;
                mb.Commit();
            }
            catch (Exception e)
            {
                try { mb.Destroy(); } catch { }
                return ErrJson("circular pattern failed at instance " + i + ": " + e.Message);
            }
            mb.Destroy();
        }

        var newIds = new List<string>();
        foreach (Body b in _part.Bodies)
        {
            if (!before.Contains(b.Tag))
            {
                _bodyCounter++;
                string bid = "BODY_" + _bodyCounter;
                _bodies[bid] = b;
                newIds.Add(bid);
            }
        }
        if (newIds.Count == 0) return ErrJson("circular pattern produced no new bodies");
        return OkJsonList("body_ids", newIds,
            "circular pattern " + count + " about " + axisName
            + " sweep " + totalAngle + (reverse ? " (reverse)" : ""));
    }

    private static string Hole(string[] parts)
    {
        if (parts.Length < 6) return ErrJson("usage: nx_hole <body_id> cx cy diameter depth [start_offset]");
        Body body = GetBody(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), dia = D(parts[4]), depth = D(parts[5]);
        double startOff = parts.Length > 6 ? D(parts[6]) : 0.0;

        _sketchCounter++;
        string id = "SKETCH_HOLE_" + _sketchCounter;
        var b = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk;
        try
        {
            sk = (Sketch)b.Commit();
        }
        finally
        {
            b.Destroy();
        }
        try { sk.SetName(id); } catch { }
        sk.Activate(Sketch.ViewReorient.True);
        _sketches[id] = sk;

        double r = dia / 2.0;
        var ell = LabCircle(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            r);
        sk.AddGeometry(ell, Sketch.InferConstraintsOption.InferNoConstraints);
        LockLabCircle(sk,ell);
        sk.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model);

        ExtrudeSketchCore(sk, startOff, startOff + depth,
            BooleanOperation.BooleanType.Subtract, body);
        return OkJson("hole d=" + dia + " depth=" + depth + " at " + cx + "," + cy);
    }

    private static string CounterboreHole(string[] parts)
    {
        if (parts.Length < 8)
            return ErrJson("usage: nx_counterbore_hole <body_id> cx cy hole_dia hole_depth cb_dia cb_depth [start_offset]");
        Body body = GetBody(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]);
        double holeDia = D(parts[4]), holeDepth = D(parts[5]);
        double cbDia = D(parts[6]), cbDepth = D(parts[7]);
        double startOff = parts.Length > 8 ? D(parts[8]) : 0.0;

        if (double.IsNaN(holeDia) || double.IsInfinity(holeDia) || holeDia <= 0)
            return ErrJson("hole_diameter must be a positive finite number");
        if (double.IsNaN(holeDepth) || double.IsInfinity(holeDepth) || holeDepth <= 0)
            return ErrJson("hole_depth must be a positive finite number");
        if (double.IsNaN(cbDia) || double.IsInfinity(cbDia) || cbDia <= 0)
            return ErrJson("counterbore_diameter must be a positive finite number");
        if (double.IsNaN(cbDepth) || double.IsInfinity(cbDepth) || cbDepth <= 0)
            return ErrJson("counterbore_depth must be > 0");
        if (cbDia <= holeDia)
            return ErrJson("counterbore_diameter must be > hole_diameter");
        if (cbDepth >= holeDepth)
            return ErrJson("counterbore_depth must be < hole_depth");

        // 1) main through hole (smaller dia, deeper)
        double rHole = holeDia / 2.0;
        _sketchCounter++;
        var b1 = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk1;
        try { sk1 = (Sketch)b1.Commit(); }
        finally { b1.Destroy(); }
        try { sk1.SetName("SKETCH_CB_HOLE_" + _sketchCounter); } catch { }
        try { sk1.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches["SKETCH_CB_HOLE_" + _sketchCounter] = sk1;
        var ellHole = _part.Curves.CreateEllipse(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            rHole, rHole, 0.0, 2.0 * Math.PI);
        sk1.AddGeometry(ellHole, Sketch.InferConstraintsOption.InferNoConstraints);
        try { sk1.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }
        ExtrudeSketchCore(sk1, startOff, startOff + holeDepth,
            BooleanOperation.BooleanType.Subtract, body);

        // 2) counterbore (larger dia, shallower, coaxial, from top surface down cbDepth)
        double rCb = cbDia / 2.0;
        _sketchCounter++;
        var b2 = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk2;
        try { sk2 = (Sketch)b2.Commit(); }
        finally { b2.Destroy(); }
        try { sk2.SetName("SKETCH_CB_" + _sketchCounter); } catch { }
        try { sk2.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches["SKETCH_CB_" + _sketchCounter] = sk2;
        var ellCb = _part.Curves.CreateEllipse(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            rCb, rCb, 0.0, 2.0 * Math.PI);
        sk2.AddGeometry(ellCb, Sketch.InferConstraintsOption.InferNoConstraints);
        try { sk2.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }
        ExtrudeSketchCore(sk2, startOff, startOff + cbDepth,
            BooleanOperation.BooleanType.Subtract, body);

        return OkJson("counterbore hole d=" + holeDia + " depth=" + holeDepth
            + " cb_d=" + cbDia + " cb_depth=" + cbDepth + " at " + cx + "," + cy);
    }

    private static string CountersinkHole(string[] parts)
    {
        if (parts.Length < 8)
            return ErrJson("usage: nx_countersink_hole <body_id> cx cy hole_dia hole_depth cs_dia cs_angle [start_offset]");
        Body body = GetBody(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]);
        double holeDia = D(parts[4]), holeDepth = D(parts[5]);
        double csDia = D(parts[6]), csAngle = D(parts[7]);
        double startOff = parts.Length > 8 ? D(parts[8]) : 0.0;

        if (double.IsNaN(holeDia) || double.IsInfinity(holeDia) || holeDia <= 0)
            return ErrJson("hole_diameter must be a positive finite number");
        if (double.IsNaN(holeDepth) || double.IsInfinity(holeDepth) || holeDepth <= 0)
            return ErrJson("hole_depth must be a positive finite number");
        if (double.IsNaN(csDia) || double.IsInfinity(csDia) || csDia <= 0)
            return ErrJson("countersink_diameter must be a positive finite number");
        if (double.IsNaN(csAngle) || double.IsInfinity(csAngle) || csAngle <= 0.0 || csAngle >= 180.0)
            return ErrJson("countersink_angle must be in (0,180)");
        if (csDia <= holeDia)
            return ErrJson("countersink_diameter must be > hole_diameter");

        // countersink depth from included angle:
        // depth = ((cs_dia - hole_dia)/2) / tan(cs_angle/2)
        double csDepth = ((csDia - holeDia) / 2.0) / Math.Tan(csAngle / 2.0 * Math.PI / 180.0);
        if (csDepth >= holeDepth)
            return ErrJson("computed countersink depth must be < hole_depth");

        // 1) main through hole (smaller dia, deeper cylinder subtract)
        double rHole = holeDia / 2.0;
        _sketchCounter++;
        var b1 = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk1;
        try { sk1 = (Sketch)b1.Commit(); }
        finally { b1.Destroy(); }
        try { sk1.SetName("SKETCH_CS_HOLE_" + _sketchCounter); } catch { }
        try { sk1.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches["SKETCH_CS_HOLE_" + _sketchCounter] = sk1;
        var ellHole = _part.Curves.CreateEllipse(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            rHole, rHole, 0.0, 2.0 * Math.PI);
        sk1.AddGeometry(ellHole, Sketch.InferConstraintsOption.InferNoConstraints);
        try { sk1.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }
        ExtrudeSketchCore(sk1, startOff, startOff + holeDepth,
            BooleanOperation.BooleanType.Subtract, body);

        // 2) conical countersink (cone, large top dia -> small bottom dia, subtract)
        NXOpen.Point axisPt = _part.Points.CreatePoint(new Point3d(cx, cy, startOff));
        NXOpen.Direction axisDir = _part.Directions.CreateDirection(
            new Point3d(cx, cy, startOff), new Vector3d(0.0, 0.0, 1.0),
            SmartObject.UpdateOption.WithinModeling);
        Axis coneAxis = _part.Axes.CreateAxis(axisPt, axisDir, SmartObject.UpdateOption.WithinModeling);

        var cb = _part.Features.CreateConeBuilder(null);
        try
        {
            cb.Type = NXOpen.Features.ConeBuilder.Types.DiametersAndHeight;
            cb.Axis = coneAxis;
            cb.BaseDiameter.RightHandSide = csDia.ToString("0.###", CultureInfo.InvariantCulture);
            cb.TopDiameter.RightHandSide = holeDia.ToString("0.###", CultureInfo.InvariantCulture);
            cb.Height.RightHandSide = csDepth.ToString("0.###", CultureInfo.InvariantCulture);
            cb.BooleanOption.Type = BooleanOperation.BooleanType.Subtract;
            cb.BooleanOption.SetTargetBodies(new Body[] { body });
            cb.CommitFeature();
        }
        catch (Exception e)
        {
            try { cb.Destroy(); } catch { }
            return ErrJson("countersink cone failed: " + e.Message);
        }
        cb.Destroy();

        return OkJson("countersink hole d=" + holeDia + " depth=" + holeDepth
            + " cs_d=" + csDia + " angle=" + csAngle + " depth=" + csDepth.ToString("0.###")
            + " at " + cx + "," + cy);
    }

    private static string Shell(string[] parts)
    {
        if (parts.Length < 4)
            return ErrJson("usage: nx_shell <body_id> <thickness> <remove_face_index> [inward=1]");
        Body body = GetBody(parts[1]);
        double thickness = D(parts[2]);
        int removeIdx;
        if (!int.TryParse(parts[3], out removeIdx))
            return ErrJson("remove_face_index must be an integer");
        bool inward = parts.Length < 5 || parts[4] != "0";

        if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= 0)
            return ErrJson("thickness must be > 0");
        Face[] faces = body.GetFaces();
        if (removeIdx < 0 || removeIdx >= faces.Length)
            return ErrJson("remove_face_index out of range (0.." + (faces.Length - 1) + ")");

        var coll = _part.ScCollectors.CreateCollector();
        var faceRule = _part.ScRuleFactory.CreateRuleFaceDumb(new Face[] { faces[removeIdx] });
        coll.ReplaceRules(new SelectionIntentRule[] { faceRule }, false);

        var sb = _part.Features.CreateShellBuilder(null);
        try
        {
            sb.Body = body;
            sb.Tolerance = 0.0254;
            sb.DefaultThickness.RightHandSide = thickness.ToString("0.###", CultureInfo.InvariantCulture);
            sb.DefaultThicknessFlip = inward;
            sb.RemovedFacesCollector = coll;
            sb.CommitFeature();
        }
        catch (Exception e)
        {
            try { sb.Destroy(); } catch { }
            try { coll.Destroy(); } catch { }
            return ErrJson("shell failed: " + e.Message);
        }
        sb.Destroy();
        try { coll.Destroy(); } catch { }

        return OkJson("body_id", parts[1],
            "shell t=" + thickness + " face=" + removeIdx + " inward=" + inward);
    }

    private static string ListEdges(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_list_edges <body_id>");
        Body body;
        try { body = GetBody(parts[1]); }
        catch
        {
            // open-part sessions do not repopulate _bodies; fall back to the
            // first solid body in the work part.
            Body[] all = _part.Bodies.ToArray();
            body = null;
            foreach (Body b in all) if (b.IsSolidBody) { body = b; break; }
            if (body == null && all.Length > 0) body = all[0];
            if (body == null) return ErrJson("no body in work part");
        }
        Edge[] edges = body.GetEdges();
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"body_id\":\"").Append(Esc(parts[1])).Append("\",\"edge_count\":").Append(edges.Length).Append(",\"edges\":[");
        for (int i = 0; i < edges.Length; i++)
        {
            Edge e = edges[i];
            if (i > 0) sb.Append(",");
            string ctype = "Other";
            try { ctype = e.SolidEdgeType.ToString(); } catch { }
            Point3d s = new Point3d(), en = new Point3d();
            try { e.GetVertices(out s, out en); } catch { }
            double mx = (s.X + en.X) / 2.0, my = (s.Y + en.Y) / 2.0, mz = (s.Z + en.Z) / 2.0;
            bool linear = (ctype == "Linear");
            // bbox: exact for linear edges (start/end min/max); for curves the
            // endpoint min/max is NOT the true bounding box, so return null
            // rather than misleading geometry.
            string bbMinStr = "null", bbMaxStr = "null";
            if (linear)
            {
                Point3d bbmin = new Point3d(
                    Math.Min(s.X, en.X), Math.Min(s.Y, en.Y), Math.Min(s.Z, en.Z));
                Point3d bbmax = new Point3d(
                    Math.Max(s.X, en.X), Math.Max(s.Y, en.Y), Math.Max(s.Z, en.Z));
                bbMinStr = "[" + F(bbmin.X) + "," + F(bbmin.Y) + "," + F(bbmin.Z) + "]";
                bbMaxStr = "[" + F(bbmax.X) + "," + F(bbmax.Y) + "," + F(bbmax.Z) + "]";
            }
            double length = 0.0;
            try { length = e.GetLength(); } catch { }
            // direction: only for linear edges
            string dir = "OTHER";
            try
            {
                if (ctype == "Linear")
                {
                    double dx = Math.Abs(en.X - s.X), dy = Math.Abs(en.Y - s.Y), dz = Math.Abs(en.Z - s.Z);
                    if (dx > 1e-6 && dy < 1e-6 && dz < 1e-6) dir = "X";
                    else if (dy > 1e-6 && dx < 1e-6 && dz < 1e-6) dir = "Y";
                    else if (dz > 1e-6 && dx < 1e-6 && dy < 1e-6) dir = "Z";
                }
            }
            catch { }
            int nAdj = 0;
            try { Face[] af = e.GetFaces(); nAdj = (af != null) ? af.Length : 0; } catch { }
            string tagStr = "0";
            try { tagStr = e.Tag.ToString(); } catch { }
            sb.Append("{\"index\":").Append(i)
              .Append(",\"tag\":").Append(tagStr)
              .Append(",\"curve_type\":\"").Append(ctype).Append("\"")
              .Append(",\"start\":[").Append(F(s.X)).Append(",").Append(F(s.Y)).Append(",").Append(F(s.Z)).Append("]")
              .Append(",\"end\":[").Append(F(en.X)).Append(",").Append(F(en.Y)).Append(",").Append(F(en.Z)).Append("]")
              .Append(",\"midpoint\":[").Append(F(mx)).Append(",").Append(F(my)).Append(",").Append(F(mz)).Append("]")
              .Append(",\"length\":").Append(F(length))
              .Append(",\"bbox_min\":").Append(bbMinStr)
              .Append(",\"bbox_max\":").Append(bbMaxStr)
              .Append(",\"direction\":\"").Append(dir).Append("\"")
              .Append(",\"adjacent_faces\":").Append(nAdj)
              .Append("}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string ListFaces(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_list_faces <body_id>");
        Body body;
        try { body = GetBody(parts[1]); }
        catch
        {
            Body[] all = _part.Bodies.ToArray();
            body = null;
            foreach (Body b in all) if (b.IsSolidBody) { body = b; break; }
            if (body == null && all.Length > 0) body = all[0];
            if (body == null) return ErrJson("no body in work part");
        }
        Face[] faces = body.GetFaces();
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"body_id\":\"").Append(Esc(parts[1])).Append("\",\"face_count\":").Append(faces.Length).Append(",\"faces\":[");
        for (int i = 0; i < faces.Length; i++)
        {
            Face fc = faces[i];
            if (i > 0) sb.Append(",");
            string ftype = "Other";
            try { ftype = fc.SolidFaceType.ToString(); } catch { }
            string tagStr = "0";
            try { tagStr = fc.Tag.ToString(); } catch { }
            double area = 0.0;
            Point3d cent = new Point3d();
            Point3d normalP = new Point3d();
            bool planar = (ftype == "Planar");
            string faceErr = "";
            bool measured = false;
            try
            {
                var measProp = _session.GetType().GetProperty("Measurement",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                object meas = (measProp != null) ? measProp.GetValue(_session, null) : null;
                if (meas != null)
                {
                    var measType = meas.GetType();
                    var mGetFP = measType.GetMethod("GetFaceProperties");
                    if (mGetFP != null)
                    {
                        var par = mGetFP.GetParameters();
                    object alt = 0;
                    foreach (var pp in par)
                    {
                        if (pp.IsOut) continue;
                        if (pp.ParameterType.IsEnum) alt = System.Enum.Parse(pp.ParameterType, "Radius");
                    }
                    object[] margs = new object[] {
                        new ISurface[] { fc },
                        0.0,
                        alt,
                        false,
                        0.0, 0.0, null, new Point3d(), 0.0, 0.0, new Point3d(), false
                    };
                    mGetFP.Invoke(meas, margs);
                    area = (double)margs[4];
                    cent = (Point3d)margs[7];
                    measured = true;
                    }
                }
            }
            catch (System.Exception ex) { faceErr = ex.Message; }
            if (!measured)
            {
                area = 0.0; cent = new Point3d();
            }
            // normal for planar
            if (planar)
            {
                var t = fc.GetType();
                var surfProp = t.GetProperty("Surface");
                object surf = (surfProp != null) ? surfProp.GetValue(fc, null) : null;
                if (surf == null)
                {
                    var mGetSurf = t.GetMethod("GetSurface", new System.Type[0]);
                    if (mGetSurf != null) surf = mGetSurf.Invoke(fc, null);
                }
                if (surf is NXOpen.Plane)
                {
                    NXOpen.Vector3d nv = ((NXOpen.Plane)surf).Normal;
                    normalP = new Point3d(nv.X, nv.Y, nv.Z);
                }
            }
            string normalStr = "null";
            if (planar && (normalP.X != 0 || normalP.Y != 0 || normalP.Z != 0))
            {
                normalStr = "[" + F(normalP.X) + "," + F(normalP.Y) + "," + F(normalP.Z) + "]";
            }
            int nAdj = 0;
            try { Edge[] ed = fc.GetEdges(); nAdj = (ed != null) ? ed.Length : 0; } catch { }
            sb.Append("{\"index\":").Append(i)
              .Append(",\"tag\":").Append(tagStr)
              .Append(",\"face_type\":\"").Append(ftype).Append("\"")
              .Append(",\"centroid\":[").Append(F(cent.X)).Append(",").Append(F(cent.Y)).Append(",").Append(F(cent.Z)).Append("]")
              .Append(",\"area\":").Append(F(area))
              .Append(",\"normal\":").Append(normalStr)
              .Append(",\"adjacent_edges\":").Append(nAdj)
              .Append(",\"err\":\"").Append(Esc(faceErr)).Append("\"")
              .Append("}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string F(double v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EdgeBlend(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_edge_blend <body_id> <radius> [edge_indices csv]");
        Body body = GetBody(parts[1]);
        double r = D(parts[2]);
        int[] idx = parts.Length > 3 ? ParseIndices(parts[3]) : null;
        int done = BlendChamfer(body, r, idx, true);
        return OkJson("edge_blend done=" + done);
    }

    private static string Chamfer(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_chamfer <body_id> <offset> [edge_indices csv]");
        Body body = GetBody(parts[1]);
        double off = D(parts[2]);
        int[] idx = parts.Length > 3 ? ParseIndices(parts[3]) : null;
        int done = BlendChamfer(body, off, idx, false);
        return OkJson("chamfer done=" + done);
    }

    private static string Unite(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_unite <target_body_id> <tool_body_ids csv>");
        Body target;
        try { target = GetBody(parts[1]); }
        catch (Exception e) { return ErrJson(e.Message); }
        List<Body> tools = new List<Body>();
        string[] ids = parts[2].Split(',');
        foreach (string id in ids)
        {
            string t = id.Trim();
            if (t.Length == 0) continue;
            try { tools.Add(GetBody(t)); }
            catch (Exception e) { return ErrJson(e.Message); }
        }
        if (tools.Count == 0) return ErrJson("no tool bodies given");
        try
        {
            if (_session == null) _session = Session.GetSession();
            NXOpen.Features.BooleanBuilder bld = null;
            try { bld = _part.Features.CreateBooleanBuilder(null); }
            catch (Exception e) { return ErrJson("unite[create] failed: " + e.Message); }
            try { bld.Operation = NXOpen.Features.Feature.BooleanType.Unite; }
            catch (Exception e) { return ErrJson("unite[op] failed: " + e.Message); }
            try { bld.Target = target; }
            catch (Exception e) { return ErrJson("unite[target] failed: " + e.Message); }
            NXOpen.DisplayableObject[] objs = new NXOpen.DisplayableObject[tools.Count];
            for (int i = 0; i < tools.Count; i++) objs[i] = tools[i];
            try { bld.Tools.Add(objs); }
            catch (Exception e) { return ErrJson("unite[tools] failed: " + e.Message); }
            NXOpen.NXObject obj = null;
            try { obj = bld.Commit(); }
            catch (Exception e) { return ErrJson("unite[commit] failed: " + e.Message); }
            try { bld.Destroy(); } catch { }
            // tool bodies are consumed by the boolean; drop their stale ids
            foreach (string id in ids)
            {
                string t = id.Trim();
                if (t.Length > 0) _bodies.Remove(t);
            }
            return OkJson("body_id", parts[1], "united " + tools.Count + " tool body(ies)");
        }
        catch (Exception e)
        {
            return ErrJson("unite failed: " + e.Message);
        }
    }

    private static string FitView()
    {
        if (_session == null) _session = Session.GetSession();
        _session.Parts.Work.Views.WorkView.Orient(NXOpen.View.Canned.Isometric, NXOpen.View.ScaleAdjustment.Fit);
        return OkJson("view fitted");
    }

    private static string ExportStep(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_export_step <path>");
        if (_part == null) return ErrJson("no active part");
        string stepPath = parts[1];
        SafePath(stepPath); if (File.Exists(stepPath)) throw new IOException("Refusing to overwrite existing STEP");
        if (_session == null) _session = Session.GetSession();
        var creator = _session.DexManager.CreateStepCreator();
        try
        {
            creator.OutputFile = stepPath;
            creator.InputFile = _partPath;
            creator.ObjectTypes.Solids = true;
            creator.ExportAs = StepCreator.ExportAsOption.Ap214;
            string baseDir = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
            if (!string.IsNullOrEmpty(baseDir))
            {
                string settings = Path.Combine(baseDir, "STEP214UG", "ugstep214.def");
                if (File.Exists(settings)) creator.SettingsFile = settings;
            }
            creator.ObjectTypes.Solids = true;
            creator.ObjectTypes.Surfaces = true;
            creator.Commit();
        }
        finally
        {
            try { creator.Destroy(); } catch { }
        }
        return OkJson("step", stepPath, "exported AP214");
    }

    // ---- composite acceptance part ------------------------------------
    private static string BuildPlateTest()
    {
        string ws = Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE") ?? "";
        if (string.IsNullOrEmpty(ws))
            ws = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NX_MCP_WORKSPACE");
        string prtPath = Path.Combine(ws, "loader_plate_demo.prt");
        string stepPath = Path.Combine(ws, "loader_plate_demo.step");

        // 1. part
        string cr = CreatePart(new string[] { "nx_create_part", prtPath });
        if (cr.Contains("\"ok\":false")) return cr;

        // 2. base plate 120x80x12
        string skB = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skBId = ExtractId(skB, "sketch_id");
        SketchRectangle(new string[] { "nx_sketch_rectangle", skBId, "0", "0", "120", "80" });
        FinishSketch(new string[] { "nx_finish_sketch", skBId });
        string exB = Extrude(new string[] { "nx_extrude", skBId, "12" });
        if (exB.Contains("\"ok\":false")) return exB;
        string bodyId = ExtractId(exB, "body_id");

        // 3. boss 30x20 (circle dia 30, extrude 20 starting at z=12, unite)
        string skT = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skTId = ExtractId(skT, "sketch_id");
        SketchCircle(new string[] { "nx_sketch_circle", skTId, "0", "0", "30" });
        FinishSketch(new string[] { "nx_finish_sketch", skTId });
        string exT = Extrude(new string[] { "nx_extrude", skTId, "20", "0", "12", "create", bodyId });
        if (exT.Contains("\"ok\":false")) return exT;
        string bodyId2 = ExtractId(exT, "body_id");

        // 4. center hole 12 through (32)
        string skH1 = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skH1Id = ExtractId(skH1, "sketch_id");
        SketchCircle(new string[] { "nx_sketch_circle", skH1Id, "0", "0", "12" });
        FinishSketch(new string[] { "nx_finish_sketch", skH1Id });
        string exH1 = Extrude(new string[] { "nx_extrude", skH1Id, "32", "0", "0", "subtract", bodyId2 });
        if (exH1.Contains("\"ok\":false")) return exH1;

        // 5. two 10 holes at (+-40, 0) through plate (12)
        string skH2 = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skH2Id = ExtractId(skH2, "sketch_id");
        SketchCircle(new string[] { "nx_sketch_circle", skH2Id, "-40", "0", "10" });
        SketchCircle(new string[] { "nx_sketch_circle", skH2Id, "40", "0", "10" });
        FinishSketch(new string[] { "nx_finish_sketch", skH2Id });
        string exH2 = Extrude(new string[] { "nx_extrude", skH2Id, "12", "0", "0", "subtract", bodyId2 });
        if (exH2.Contains("\"ok\":false")) return exH2;

        // 6. slot 40x12: middle rect 28x12 + two 12 circles at (+-14,0)
        string skS = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skSId = ExtractId(skS, "sketch_id");
        SketchRectangle(new string[] { "nx_sketch_rectangle", skSId, "0", "0", "28", "12" });
        SketchCircle(new string[] { "nx_sketch_circle", skSId, "-14", "0", "12" });
        SketchCircle(new string[] { "nx_sketch_circle", skSId, "14", "0", "12" });
        FinishSketch(new string[] { "nx_finish_sketch", skSId });
        string exS = Extrude(new string[] { "nx_extrude", skSId, "12", "0", "0", "subtract", bodyId2 });
        if (exS.Contains("\"ok\":false")) return exS;

        // 7. blend R8 (tolerant all edges), 8. blend R3, 9. chamfer C2
        int b8 = 0, b3 = 0, c2 = 0;
        try { b8 = BlendChamfer(_bodies[bodyId2], 8.0, null, true); } catch { }
        try { b3 = BlendChamfer(_bodies[bodyId2], 3.0, null, true); } catch { }
        try { c2 = BlendChamfer(_bodies[bodyId2], 2.0, null, false); } catch { }

        // 10. save + export
        SavePart();
        string exStep = ExportStep(new string[] { "nx_export_step", stepPath });

        return "{\"ok\":true,\"result\":\"plate test built\"," +
               "\"part\":\"" + Esc(prtPath) + "\"," +
               "\"step\":\"" + Esc(stepPath) + "\"," +
               "\"blend8\":" + b8 + ",\"blend3\":" + b3 + ",\"chamfer2\":" + c2 + "}";
    }

    // ---- helpers -------------------------------------------------------
    private static Sketch GetSketch(string id)
    {
        Sketch sk;
        if (!_sketches.TryGetValue(id, out sk)) throw new Exception("unknown sketch_id: " + id);
        return sk;
    }

    private static Body GetBody(string id)
    {
        Body b;
        if (!_bodies.TryGetValue(id, out b)) throw new Exception("unknown body_id: " + id);
        return b;
    }

    private static double D(string s)
    {
        return double.Parse(s, CultureInfo.InvariantCulture);
    }

    private static int[] ParseIndices(string csv)
    {
        string[] t = csv.Split(',');
        int[] r = new int[t.Length];
        for (int i = 0; i < t.Length; i++) r[i] = int.Parse(t[i], CultureInfo.InvariantCulture);
        return r;
    }

    private static string ExtractId(string json, string key)
    {
        string needle = "\"" + key + "\":\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) throw new Exception("missing " + key + " in " + json);
        int s = i + needle.Length;
        int e = json.IndexOf('"', s);
        return json.Substring(s, e - s);
    }

    private static string GetSketchPlaneName(string id)
    {
        string plane;
        if (_sketchPlanes.TryGetValue(id, out plane)) return plane;
        return "XY";
    }

    private static Point3d SketchPoint(string sketchId, double u, double v)
    {
        return SketchPointForPlane(GetSketchPlaneName(sketchId), u, v);
    }

    private static Point3d SketchPointForPlane(string plane, double u, double v)
    {
        switch (plane)
        {
            case "XZ": return new Point3d(u, 0.0, v);
            case "YZ": return new Point3d(0.0, u, v);
            default: return new Point3d(u, v, 0.0);
        }
    }

    private static Vector3d SketchVectorForPlane(string plane, double u, double v)
    {
        switch (plane)
        {
            case "XZ": return new Vector3d(u, 0.0, v);
            case "YZ": return new Vector3d(0.0, u, v);
            default: return new Vector3d(u, v, 0.0);
        }
    }

    private static Vector3d SketchPlaneNormal(string plane)
    {
        // Normals are right-handed with the local (u,v) mapping above.
        switch (plane)
        {
            case "XZ": return new Vector3d(0.0, -1.0, 0.0);
            case "YZ": return new Vector3d(1.0, 0.0, 0.0);
            default: return new Vector3d(0.0, 0.0, 1.0);
        }
    }

    private static Vector3d SketchLocalXAxis(string plane)
    {
        switch (plane)
        {
            case "YZ": return new Vector3d(0.0, 1.0, 0.0);
            default: return new Vector3d(1.0, 0.0, 0.0);
        }
    }

    private static Matrix3x3 SketchPlaneMatrix(string plane)
    {
        Matrix3x3 m = new Matrix3x3();
        if (plane == "XZ")
        {
            // local x -> +X, local y -> +Z, local normal -> -Y
            m.Xx = 1.0; m.Xy = 0.0;  m.Xz = 0.0;
            m.Yx = 0.0; m.Yy = 0.0;  m.Yz = 1.0;
            m.Zx = 0.0; m.Zy = -1.0; m.Zz = 0.0;
        }
        else if (plane == "YZ")
        {
            // local x -> +Y, local y -> +Z, local normal -> +X
            m.Xx = 0.0; m.Xy = 1.0; m.Xz = 0.0;
            m.Yx = 0.0; m.Yy = 0.0; m.Yz = 1.0;
            m.Zx = 1.0; m.Zy = 0.0; m.Zz = 0.0;
        }
        else
        {
            m.Xx = 1.0; m.Xy = 0.0; m.Xz = 0.0;
            m.Yx = 0.0; m.Yy = 1.0; m.Yz = 0.0;
            m.Zx = 0.0; m.Zy = 0.0; m.Zz = 1.0;
        }
        return m;
    }

    private static Vector3d SketchExtrudeAxis(string plane)
    {
        // Stable user-facing default: positive global axis for reverse=false.
        switch (plane)
        {
            case "XZ": return new Vector3d(0.0, 1.0, 0.0);
            case "YZ": return new Vector3d(1.0, 0.0, 0.0);
            default: return new Vector3d(0.0, 0.0, 1.0);
        }
    }

    private static void AddLine(Sketch sk, string sketchId, double x1, double y1, double x2, double y2)
    {
        var curve = _part.Curves.CreateLine(
            SketchPoint(sketchId, x1, y1),
            SketchPoint(sketchId, x2, y2));
        sk.AddGeometry(curve, Sketch.InferConstraintsOption.InferNoConstraints);
    }

    private static void ExtrudeSketchCore(Sketch sk, double startZ, double endZ,
        BooleanOperation.BooleanType bt, Body target)
    {
        var section = _part.Sections.CreateSection();
        var rule = _part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sk.Feature });
        section.AddToSection(
            new SelectionIntentRule[] { rule }, null, null, null,
            new Point3d(0.0, 0.0, 0.0), Section.Mode.Create, false);
        var dir = _part.Directions.CreateDirection(
            new Point3d(0.0, 0.0, 0.0), new Vector3d(0.0, 0.0, 1.0),
            SmartObject.UpdateOption.WithinModeling);
        var b = _part.Features.CreateExtrudeBuilder(null);
        try
        {
            b.Section = section;
            b.Direction = dir;
            b.Limits.StartExtend.Value.RightHandSide = startZ.ToString("0.###", CultureInfo.InvariantCulture);
            b.Limits.EndExtend.Value.RightHandSide = endZ.ToString("0.###", CultureInfo.InvariantCulture);
            b.BooleanOperation.Type = bt;
            if (target != null) b.BooleanOperation.SetTargetBodies(new Body[] { target });
            b.AllowSelfIntersectingSection(true);
            b.CommitFeature();
        }
        finally
        {
            b.Destroy();
        }
    }

    private static int BlendChamfer(Body body, double value, int[] indices, bool isBlend)
    {
        Edge[] edges = body.GetEdges();
        int done = 0;
        for (int i = 0; i < edges.Length; i++)
        {
            if (indices != null && Array.IndexOf(indices, i) < 0) continue;
            try
            {
                var col = _part.ScCollectors.CreateCollector();
                var rule = _part.ScRuleFactory.CreateRuleEdgeDumb(new Edge[] { edges[i] });
                col.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                if (isBlend)
                {
                    var b = _part.Features.CreateEdgeBlendBuilder(null);
                    try
                    {
                        b.AddChainset(col, value.ToString("0.###", CultureInfo.InvariantCulture));
                        b.CommitFeature();
                    }
                    finally { b.Destroy(); }
                }
                else
                {
                    var b = _part.Features.CreateChamferBuilder(null);
                    try
                    {
                        b.SmartCollector = col;
                        b.FirstOffset = value.ToString("0.###", CultureInfo.InvariantCulture);
                        b.Option = ChamferBuilder.ChamferOption.SymmetricOffsets;
                        b.CommitFeature();
                    }
                    finally { b.Destroy(); }
                }
                done++;
            }
            catch { }
        }
        return done;
    }

    // ---- legacy create_block (kept) ------------------------------------
    private static string CreateBlock(string[] parts)
    {
        if (parts.Length < 4) return ErrJson("usage: create_block W H D");
        double w = D(parts[1]), h = D(parts[2]), d = D(parts[3]);
        string ws = Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE") ?? "";
        string partName = string.IsNullOrEmpty(ws)
            ? "nx_mcp_block.prt"
            : Path.Combine(ws, "nx_mcp_block.prt");
        string cr = CreatePart(new string[] { "nx_create_part", partName });
        if (cr.Contains("\"ok\":false")) return cr;

        string sk = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string sid = ExtractId(sk, "sketch_id");
        SketchRectangle(new string[] { "nx_sketch_rectangle", sid, "0", "0", w.ToString("0.###", CultureInfo.InvariantCulture), h.ToString("0.###", CultureInfo.InvariantCulture) });
        FinishSketch(new string[] { "nx_finish_sketch", sid });
        string ex = Extrude(new string[] { "nx_extrude", sid, d.ToString("0.###", CultureInfo.InvariantCulture) });
        SavePart();
        return OkJson("block " + w + "x" + h + "x" + d + " saved=" + partName);
    }

    // ---- phase-2: remaining certified tools ----------------------------
    private static string OpenPart(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_open_part <path>");
        string path = parts[1];
        if (!File.Exists(path)) return ErrJson("part file not found: " + path);
        if (_session == null) _session = Session.GetSession();
        NXOpen.PartLoadStatus status = null;
        try
        {
            var load = _session.Parts.LoadOptions; load.ComponentLoadMethod=NXOpen.LoadOptions.LoadMethod.SearchDirectories; load.SetSearchDirectories(new string[]{Path.GetDirectoryName(path)},new bool[]{false}); var opened = _session.Parts.OpenBaseDisplay(path, out status);
            NXOpen.Part part = opened as NXOpen.Part;
            if(part==null) {var errors=new List<string>();if(status!=null)for(int i=0;i<status.NumberUnloadedParts;i++)errors.Add(status.GetPartName(i)+": "+status.GetStatusDescription(i));throw new IOException("NX did not load requested part: "+String.Join("; ",errors));}
            _part = part;
            _partPath = part.FullPath;
            ResetTaskState();
            // register existing bodies so GetBody works after nx_open_part
            try
            {
                foreach (Body b in _part.Bodies.ToArray())
                {
                    string bid = "";
                    try { bid = b.Name; } catch { }
                    if (string.IsNullOrEmpty(bid)) { try { bid = b.JournalIdentifier; } catch { } }
                    if (string.IsNullOrEmpty(bid)) bid = "BODY_" + (++_bodyCounter);
                    _bodies[bid] = b;
                }
            }
            catch { }
            return OkJson("part", _partPath, "opened");
        }
        finally
        {
            if (status != null) { try { status.Dispose(); } catch { } }
        }
    }

    private static string ClosePart(string[] parts)
    {
        bool save = parts.Length < 2 || parts[1] != "0";
        if (_part != null && save) SavePart();
        if (_part != null)
        {
            _part.Close(NXOpen.BasePart.CloseWholeTree.True,
                        NXOpen.BasePart.CloseModified.CloseModified, null);
            _part = null;
            _partPath = null;
        }
        ResetTaskState();
        return OkJson("closed part");
    }

    private static string ListObjects(string collection, string kind)
    {
        if (_part == null) return ErrJson("no active part");
        var names = new List<string>();
        try
        {
            if (collection == "Sketches")
            {
                foreach (NXOpen.Sketch o in _part.Sketches)
                {
                    string n = "";
                    try { n = o.Name; } catch { }
                    if (string.IsNullOrEmpty(n)) { try { n = o.JournalIdentifier; } catch { } }
                    names.Add(n);
                }
            }
            else if (collection == "Bodies")
            {
                foreach (NXOpen.Body o in _part.Bodies)
                {
                    string n = "";
                    try { n = o.Name; } catch { }
                    if (string.IsNullOrEmpty(n)) { try { n = o.JournalIdentifier; } catch { } }
                    names.Add(n);
                }
            }
            else if (collection == "Features")
            {
                foreach (NXOpen.Features.Feature o in _part.Features)
                {
                    string n = "";
                    try { n = o.GetFeatureName(); } catch { }
                    if (string.IsNullOrEmpty(n)) { try { n = o.JournalIdentifier; } catch { } }
                    names.Add(n);
                }
            }
        }
        catch { }
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"ok\":true,\"objects\":[");
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(Esc(names[i])).Append("\"");
        }
        sb.Append("],\"message\":\"Found ").Append(names.Count).Append(" ").Append(kind).Append("(s).\"}");
        return sb.ToString();
    }

    private static string Undo()
    {
        if (_session == null) _session = Session.GetSession();
        try
        {
            _session.UndoToLastVisibleMark();
            return OkJson("undone to last visible mark");
        }
        catch
        {
            // no visible undo mark (e.g. right after startup): no-op success,
            // matching the Python bridge behaviour of reporting no change
            return OkJson("nothing to undo");
        }
    }
    private static string Json(object value) { return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(value); }
    private static string InspectPart()
    {
        if (_part == null) throw new Exception("No active part");
        var uf = NXOpen.UF.UFSession.GetUFSession();
        var bodies = new List<object>();
        foreach (Body b in _part.Bodies)
        {
            int count; int[] faults; Tag[] bad;
            uf.Modl.AskBodyConsistency(b.Tag, out count, out faults, out bad);
            double[] box = new double[6]; uf.Modl.AskBoundingBox(b.Tag, box);
            double[] mass = new double[47], stats = new double[13], acc = new double[11]; acc[0] = 0.999;
            double? volume = null;
            if (b.IsSolidBody) {
                uf.Modl.AskMassProps3d(new Tag[]{b.Tag}, 1, 1, 4, 1.0, 1, acc, mass, stats);
                volume = mass[1]*1e9;
            }
            bodies.Add(new {tag=b.Tag.ToString(), solid=b.IsSolidBody, faces=b.GetFaces().Length, edges=b.GetEdges().Length, consistency_fault_count=count, faults=faults, bbox_mm=box, volume_mm3=volume, volume_applicable=b.IsSolidBody});
        }
        var features = new List<object>();
        foreach (Feature f in _part.Features) features.Add(new {name=f.GetFeatureName(), type=f.FeatureType, errors=f.GetFeatureErrorMessages(), warnings=f.GetFeatureWarningMessages()});
        var sketches = new List<object>();
        foreach (Sketch sk in _part.Sketches) { int dof; string state=sk.GetStatus(out dof).ToString(); sketches.Add(new {name=sk.Name, status=state, degrees_of_freedom=dof, curves=sk.GetAllGeometry().Length}); }
        var expressions = new List<object>();
        foreach (Expression e in _part.Expressions) expressions.Add(new {name=e.Name, formula=e.RightHandSide});
        var components = new List<object>();
        if (_part.ComponentAssembly.RootComponent != null)
            foreach(var c in _part.ComponentAssembly.RootComponent.GetChildren()) components.Add(new {name=c.Name, prototype=((BasePart)c.Prototype).FullPath});
        Tag[] failed; uf.Modl.AskFeatFailList(out failed);
        return Json(new {ok=true, part=_part.FullPath, units=_part.PartUnits.ToString(), bodies=bodies, features=features, sketches=sketches, expressions=expressions, components=components, failed_feature_count=failed.Length});
    }
    private static string StrictBlend(string[] args)
    {
        Body body=GetBody(args[1]); double radius=D(args[2]); int[] indices=ParseIndices(args[3]);
        var selected = new List<Edge>(); var edges=body.GetEdges();
        foreach(int i in indices) selected.Add(edges[i]);
        var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Lab blend transaction");
        var col=_part.ScCollectors.CreateCollector(); var builder=_part.Features.CreateEdgeBlendBuilder(null);
        try {
            col.ReplaceRules(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleEdgeDumb(selected.ToArray())},false);
            builder.AddChainset(col, radius.ToString(CultureInfo.InvariantCulture));
            Feature f=builder.CommitFeature();
            return Json(new {ok=true, feature=f.GetFeatureName(), radius_mm=radius});
        } catch(Exception e) {
            builder.Destroy(); builder=null;
            Session.GetSession().UndoToMark(mark,null);
            Log("BLEND_FAILURE_ROLLED_BACK: "+e.ToString());
            return Json(new {ok=false, error=e.Message, exception_type=e.GetType().FullName, nx_error_code=(e is NXException ? ((NXException)e).ErrorCode : 0), rolled_back=true});
        } finally {if(builder!=null) builder.Destroy();}
    }
    private static string DimensionRectangle(string[] args)
    {
        Sketch sk=LabEditingSketch(args[1]); NXObject[] geom=sk.GetAllGeometry();
        if(geom.Length!=4) throw new Exception("Expected four rectangle lines");
        for(int i=0;i<4;i++) {
            Sketch.ConstraintGeometry g=new Sketch.ConstraintGeometry(); g.Geometry=geom[i]; g.PointType=Sketch.ConstraintPointType.None;
            if(i%2==0) sk.CreateHorizontalConstraint(g); else sk.CreateVerticalConstraint(g);
            Sketch.ConstraintGeometry a=new Sketch.ConstraintGeometry(); a.Geometry=geom[i]; a.PointType=Sketch.ConstraintPointType.EndVertex;
            Sketch.ConstraintGeometry b=new Sketch.ConstraintGeometry(); b.Geometry=geom[(i+1)%4]; b.PointType=Sketch.ConstraintPointType.StartVertex;
            sk.CreateCoincidentConstraint(a,b);
        }
        for(int i=0;i<2;i++) {
            Sketch.DimensionGeometry a=new Sketch.DimensionGeometry(); a.Geometry=geom[i]; a.AssocType=Sketch.AssocType.StartPoint;
            Sketch.DimensionGeometry b=new Sketch.DimensionGeometry(); b.Geometry=geom[i]; b.AssocType=Sketch.AssocType.EndPoint;
            var exp=_part.Expressions.CreateExpression("Number",args[2]+(i==0?"_width=":"_height=")+args[3+i]);
            sk.CreateDimension(i==0?Sketch.ConstraintType.HorizontalDim:Sketch.ConstraintType.VerticalDim,a,b,new Point3d(0,-30-i*10,0),exp);
        }
        sk.Update(); return Json(new {ok=true, dimension_prefix=args[2]});
    }
    private static string AddComponentLab(string[] args)
    {
        SafePath(args[1]); var m=new Matrix3x3(); m.Xx=1;m.Yy=1;m.Zz=1;
        PartLoadStatus status;
        var c=_part.ComponentAssembly.AddComponent(args[1],"Entire Part",args[2],new Point3d(D(args[3]),D(args[4]),D(args[5])),m,-1,out status);
        try {return Json(new {ok=status.NumberUnloadedParts==0, component=c.Name, unloaded_parts=status.NumberUnloadedParts});} finally {status.Dispose();}
    }
    private static string InterferenceLab()
    {
        var bodies=new List<Body>(); var names=new List<string>();
        foreach(var c in _part.ComponentAssembly.RootComponent.GetChildren())
            foreach(Body b in ((Part)c.Prototype).Bodies) { var occurrence=c.FindOccurrence(b) as Body; if(occurrence!=null) {bodies.Add(occurrence); names.Add(c.Name);} }
        var pairs=new List<object>(); var uf=NXOpen.UF.UFSession.GetUFSession();
        for(int i=0;i<bodies.Count;i++) for(int j=i+1;j<bodies.Count;j++) {
            int[] result=new int[1]; uf.Modl.CheckInterference(bodies[i].Tag,1,new Tag[]{bodies[j].Tag},result);
            pairs.Add(new {a=names[i],b=names[j],nx_result=result[0],meaning=result[0]==1?"interference":result[0]==2?"clear":result[0]==3?"touching":"unknown"});
        }
        uf.Modl.Update(); return Json(new {ok=true, body_occurrences=bodies.Count,pairs=pairs});
    }
    private static string NxVersion()
    {
        return System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(Session).Assembly.Location).FileVersion;
    }
    private static string MoveComponentLab(string[] args)
    {
        NXOpen.Assemblies.Component target=null;
        foreach(var c in _part.ComponentAssembly.RootComponent.GetChildren())
            if(String.Equals(c.Name,args[1],StringComparison.OrdinalIgnoreCase)) target=c;
        if(target==null) throw new Exception("No such component in current assembly");
        Point3d before,after;Matrix3x3 orientation;
        target.GetPosition(out before,out orientation);
        var identity=new Matrix3x3();identity.Xx=1;identity.Yy=1;identity.Zz=1;
        _part.ComponentAssembly.MoveComponent(target,new Vector3d(D(args[2]),D(args[3]),D(args[4])),identity);
        target.GetPosition(out after,out orientation);
        return Json(new {ok=true,component=target.Name,before_mm=new double[]{before.X,before.Y,before.Z},after_mm=new double[]{after.X,after.Y,after.Z}});
    }
    private static string ImportStepLab(string[] args)
    {
        SafePath(args[1]);
        if(!File.Exists(args[1])) throw new IOException("Input STEP not found");
        if(_part==null || _part.Bodies.ToArray().Length!=0) throw new Exception("Import requires an empty experimental work part");
        var importer=Session.GetSession().DexManager.CreateStep214Importer();
        try {
            importer.InputFile=args[1];
            importer.SettingsFile=Path.Combine(Environment.GetEnvironmentVariable("UGII_BASE_DIR"),"STEP214UG","step214ug.def");
            importer.ImportTo=Step214Importer.ImportToOption.WorkPart;
            importer.FileOpenFlag=false;
            importer.FlattenAssembly=true;
            importer.ObjectTypes.Solids=true;
            importer.ObjectTypes.Surfaces=false;
            importer.ObjectTypes.Curves=false;
            importer.ProcessHoldFlag=true;
            importer.SimplifyGeometry=false;
            importer.Commit();
        } finally {importer.Destroy();}
        return InspectPart();
    }

    private static bool ModernLabPart() {return _part!=null && (_part.FullPath.StartsWith(LabPath(@"models\phase2_"),StringComparison.OrdinalIgnoreCase) || _part.FullPath.StartsWith(LabPath(@"models\phase3_"),StringComparison.OrdinalIgnoreCase) || _part.FullPath.StartsWith(LabPath(@"models\phase4_"),StringComparison.OrdinalIgnoreCase));}
    private static void Phase2Part()
    {
        if(_part==null || !ModernLabPart())
            throw new Exception("New operations may only modify a phase2_, phase3_ or phase4_ experimental part");
    }
    private static Curve LabCircle(Point3d center,Vector3d x,Vector3d y,double radius)
    {
        if(ModernLabPart())return _part.Curves.CreateArc(center,x,y,radius,0,2*Math.PI);
        return _part.Curves.CreateEllipse(center,x,y,radius,radius,0,2*Math.PI);
    }
    private static Sketch LabEditingSketch(string id)
    {
        var sk=GetSketch(id);if(ModernLabPart()) {var active=Session.GetSession().ActiveSketch;if(active==null || active.Tag!=sk.Tag)sk.Activate(Sketch.ViewReorient.True);}return sk;
    }
    private static void LockLabCircle(Sketch sketch,Curve circle)
    {
        if(!ModernLabPart())return;
        var g=new Sketch.ConstraintGeometry();g.Geometry=circle;g.PointType=Sketch.ConstraintPointType.None;sketch.CreateFixedConstraint(g);sketch.Update();
    }
    private static string MotionNew(string[] args)
    {
        Phase2Part();SafePath(args[1]);if(!args[1].EndsWith(".sim",StringComparison.OrdinalIgnoreCase))throw new Exception("NX12 Motion requires a .sim filename, not .prt");if(File.Exists(args[1]))throw new Exception("Output exists");
        var master=_part;string masterPath=master.FullPath;SavePart();var creator=Session.GetSession().Parts.FileNew();string stage="file_new";
        try {
            Log("Motion registered applications: "+String.Join(",",creator.GetApplicationNames()));stage="properties";
            creator.TemplateType=FileNewTemplateType.Item;creator.UseBlankTemplate=true;creator.ApplicationName="MotionTemplate";creator.Units=Part.Units.Millimeters;
            creator.MasterFileName=master.FullPath;creator.NewFileName=args[1];creator.MakeDisplayedPart=true;stage="commit";creator.Commit();
        } catch(Exception e){return Json(new {ok=false,stage=stage,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0});} finally {try {creator.Destroy();}catch(NXException e){Log("Motion FileNew cleanup: "+e.Message);}}
        _part=Session.GetSession().Parts.Work;_partPath=_part.FullPath;
        Session.GetSession().MotionSession.InitializeSimulation(_part);
        SavePart();if(!File.Exists(_part.FullPath))throw new Exception("NX Motion native file not found after creation");
        return Json(new {ok=true,master=masterPath,simulation=_part.FullPath,native_file_exists=true});
    }
    private static NXOpen.Assemblies.Component P2Component(string name)
    {
        if(_part==null || _part.ComponentAssembly.RootComponent==null) throw new Exception("No assembly root");
        var found=MotionComponent(name,_part.ComponentAssembly.RootComponent);if(found!=null)return found;
        throw new Exception("Component not found: "+name);
    }
    private static List<NXOpen.Positioning.ComponentConstraint> P2Constraints()
    {
        var list=new List<NXOpen.Positioning.ComponentConstraint>();var tags=new HashSet<Tag>();
        if(_part!=null && _part.ComponentAssembly.RootComponent!=null)
            foreach(var c in _part.ComponentAssembly.RootComponent.GetChildren()) foreach(var q in c.GetConstraints()) if(tags.Add(q.Tag)) list.Add(q);
        return list;
    }
    private static bool P2Bad(string s) {return s!="Solved" && s!="Suppressed";}
    private static string AssemblyState(string[] args)
    {
        var rows=new List<object>();var constraints=new List<object>();bool allLoaded=true;int bad=0;
        if(_part==null || _part.ComponentAssembly.RootComponent==null) throw new Exception("No assembly root");
        foreach(var c in _part.ComponentAssembly.RootComponent.GetChildren()) {var p=c.Prototype as Part;if(p!=null && !p.IsFullyLoaded){var ls=p.LoadFully();ls.Dispose();}}
        foreach(var c in _part.ComponentAssembly.RootComponent.GetChildren()) {
            var p=c.Prototype as Part;if(p==null || !p.IsFullyLoaded)allLoaded=false;
            var d=args.Length>1?c.GetDegreesOfFreedom(new NXOpen.Assemblies.Component[]{P2Component(args[1])}):c.GetDegreesOfFreedom();
            Point3d pos;Matrix3x3 mat;c.GetPosition(out pos,out mat);
            rows.Add(new {name=c.Name,prototype=p==null?"":p.FullPath,fully_loaded=p!=null && p.IsFullyLoaded,position_mm=new double[]{pos.X,pos.Y,pos.Z},orientation=mat,dof_result=d.Result.ToString(),translation_dof=d.NumTranslational,rotation_dof=d.NumRotational,instant_translation_dof=d.NumInstantaneousTranslational,instant_rotation_dof=d.NumInstantaneousRotational,constraint_count=c.GetConstraints().Length});
        }
        foreach(var q in P2Constraints()) {string s=q.GetConstraintStatus().ToString();if(P2Bad(s))bad++;constraints.Add(new {name=q.Name,tag=q.Tag.ToString(),type=q.ConstraintType.ToString(),status=s,suppressed=q.Suppressed,expression=q.Expression==null?null:q.Expression.RightHandSide});}
        return Json(new {ok=true,part=_part.FullPath,components=rows,constraints=constraints,unsatisfied_constraint_count=bad,all_prototypes_fully_loaded=allLoaded,dof_scope=args.Length>1?"relative_to_"+args[1]:"per_component_in_current_assembly",global_mechanism_dof=(object)null,note="Per-component DOF values are correlated; do not sum as global mechanism DOF."});
    }
    private static string ComponentGeometry(string[] args)
    {
        var c=P2Component(args[1]);var p=(Part)c.Prototype;var bodies=p.Bodies.ToArray();var rows=new List<object>();var erows=new List<object>();var uf=NXOpen.UF.UFSession.GetUFSession();
        for(int bi=0;bi<bodies.Length;bi++) {var faces=bodies[bi].GetFaces();for(int fi=0;fi<faces.Length;fi++) {
            var face=c.FindOccurrence(faces[fi]) as Face;if(face==null)throw new Exception("Face occurrence unavailable");
            int type,norm;double radius,radData;double[] point=new double[3],dir=new double[3],box=new double[6];
            uf.Modl.AskFaceData(face.Tag,out type,point,dir,box,out radius,out radData,out norm);
            rows.Add(new {reference=bi+":"+fi,kind=face.SolidFaceType.ToString(),uf_type=type,point_mm=point,direction=dir,bbox_mm=box,radius_mm=radius,normal_sense=norm});
        }var edges=bodies[bi].GetEdges();for(int ei=0;ei<edges.Length;ei++) {var edge=(Edge)c.FindOccurrence(edges[ei]);double[] box=new double[6];uf.Modl.AskBoundingBox(edge.Tag,box);erows.Add(new {reference="e"+bi+":"+ei,kind=edge.SolidEdgeType.ToString(),bbox_mm=box});}}
        return Json(new {ok=true,component=c.Name,faces=rows,edges=erows});
    }
    private static NXObject P2Geometry(NXOpen.Assemblies.Component c,string reference)
    {
        if(reference=="component")return c;
        if(reference.StartsWith("point:")){foreach(Point point in ((Part)c.Prototype).Points)if(point.Name==reference.Substring(6)){var occurrence=c.FindOccurrence(point) as NXObject;if(occurrence==null)throw new Exception("Point occurrence unavailable");return occurrence;}throw new Exception("Named native point not found");}
        if(reference.StartsWith("curve:")){foreach(Curve curve in ((Part)c.Prototype).Curves)if(curve.Name==reference.Substring(6)){var occurrence=c.FindOccurrence(curve) as NXObject;if(occurrence==null)throw new Exception("Curve occurrence unavailable");return occurrence;}throw new Exception("Named native curve not found");}
        if(reference.StartsWith("e")) {var ep=reference.Substring(1).Split(':');var edge=((Part)c.Prototype).Bodies.ToArray()[Int32.Parse(ep[0])].GetEdges()[Int32.Parse(ep[1])];return (NXObject)c.FindOccurrence(edge);}
        var parts=reference.Split(':');if(parts.Length!=2)throw new Exception("Face reference must be bodyIndex:faceIndex from nx_component_geometry");
        var f=((Part)c.Prototype).Bodies.ToArray()[Int32.Parse(parts[0])].GetFaces()[Int32.Parse(parts[1])];
        return (NXObject)c.FindOccurrence(f);
    }
    private static void P2Solve(NXOpen.Positioning.Constraint added=null)
    {
        foreach(var c in _part.ComponentAssembly.RootComponent.GetChildren()) {var p=c.Prototype as Part;if(p!=null && !p.IsFullyLoaded) {var ls=p.LoadFully();ls.Dispose();}}
        var pos=_part.ComponentAssembly.Positioner;pos.ClearNetwork();var network=(NXOpen.Positioning.ComponentNetwork)pos.EstablishNetwork();network.MoveObjectsState=true;network.NetworkArrangementsMode=NXOpen.Positioning.ComponentNetwork.ArrangementsMode.Existing;
        foreach(var q in P2Constraints()) if(!q.Suppressed)network.AddConstraint(q);
        if(added!=null && !P2Constraints().Exists(x=>x.Tag==added.Tag))network.AddConstraint(added);
        network.LoadReferencedGeometry();
        network.Solve();network.ApplyToModel();pos.ClearNetwork();
    }
    private static string CreateAssemblyConstraint(string[] args)
    {
        Phase2Part();if(args.Length<6)throw new Exception("nx_constraint TYPE NAME C1 FACE1 C2 FACE2 VALUE ALIGN");
        foreach(var old in P2Constraints())if(old.Name==args[2])throw new Exception("Constraint name already exists");
        string type=args[1].ToLowerInvariant();var c1=P2Component(args[3]);var pos=_part.ComponentAssembly.Positioner;
        pos.ClearNetwork();var network=(NXOpen.Positioning.ComponentNetwork)pos.EstablishNetwork();network.MoveObjectsState=true;network.NetworkArrangementsMode=NXOpen.Positioning.ComponentNetwork.ArrangementsMode.Existing;
        pos.BeginAssemblyConstraints();var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Phase2 assembly constraint");
        try {
            var q=pos.CreateConstraint(true);q.SetName(args[2]);
            var map=new Dictionary<string,NXOpen.Positioning.Constraint.Type>{{"fix",NXOpen.Positioning.Constraint.Type.Fix},{"axis",NXOpen.Positioning.Constraint.Type.Touch},{"touch",NXOpen.Positioning.Constraint.Type.Touch},{"concentric",NXOpen.Positioning.Constraint.Type.Concentric},{"align",NXOpen.Positioning.Constraint.Type.Parallel},{"distance",NXOpen.Positioning.Constraint.Type.Distance},{"angle",NXOpen.Positioning.Constraint.Type.Angle}};
            map["bond"]=NXOpen.Positioning.Constraint.Type.Bond;
            if(!map.ContainsKey(type))throw new Exception("Unsupported public constraint type");q.ConstraintType=map[type];
            if(args.Length>8)q.ConstraintAlignment=args[8]=="opposite"?NXOpen.Positioning.Constraint.Alignment.ContraAlign:NXOpen.Positioning.Constraint.Alignment.CoAlign;
            q.CreateConstraintReference(c1,P2Geometry(c1,args[4]),type=="axis",false);
            if(type!="fix") {var c2=P2Component(args[5]);q.CreateConstraintReference(c2,P2Geometry(c2,args[6]),type=="axis",false);}
            if(type=="distance" || type=="angle")q.Expression.RightHandSide=args[7];
            network.AddConstraint(q);network.LoadReferencedGeometry();network.Solve();network.ApplyToModel();string state=q.GetConstraintStatus().ToString();
            return Json(new {ok=!P2Bad(state),name=q.Name,type=q.ConstraintType.ToString(),solver_status=state,constraint_retained_for_diagnosis=true});
        } catch(Exception e) {Session.GetSession().UndoToMark(mark,null);return Json(new {ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally {pos.ClearNetwork();pos.EndAssemblyConstraints();}
    }
    private static string EditAssemblyConstraint(string[] args)
    {
        Phase2Part();var q=P2Constraints().Find(x=>x.Name==args[1]);if(q==null)throw new Exception("No constraint "+args[1]);
        if(args[0]=="nx_constraint_suppress")q.Suppressed=args[2]=="1";
        else if(args[0]=="nx_constraint_value")q.Expression.RightHandSide=args[2];
        else if(args[0]=="nx_constraint_alignment")q.ConstraintAlignment=args[2]=="opposite"?NXOpen.Positioning.Constraint.Alignment.ContraAlign:NXOpen.Positioning.Constraint.Alignment.CoAlign;
        else throw new Exception("Unsupported edit");
        P2Solve();string status=q.GetConstraintStatus().ToString();return Json(new {ok=!P2Bad(status),name=q.Name,solver_status=status,suppressed=q.Suppressed});
    }
    private static string MotionAvailability()
    {
        var uf=NXOpen.UF.UFSession.GetUFSession();bool active;uf.Motion.IsInitialized(out active);bool reserved=false;
        try {uf.UF.Reserve("mechanisms","NX12AILABPROBE");reserved=true;return Json(new {ok=true,mechanisms_license_checkout=true,motion_initialized=active,license_check="Official UF.Reserve mechanisms; probe reservation released",nx=NxVersion()});}
        catch(NXException e) {return Json(new {ok=false,mechanisms_license_checkout=false,nx_error_code=e.ErrorCode,error=e.Message,action="Pause licensed Motion features; continue public assembly APIs"});}
        finally {if(reserved)uf.UF.Release("mechanisms","NX12AILABPROBE");}
    }

    private static string MotionLegacyCreate()
    {
        Phase2Part();string masterPath=_part.FullPath;SavePart();string created=Session.GetSession().MotionSession.CreateSimulation(_part);_part=Session.GetSession().Parts.Work;_partPath=_part.FullPath;return Json(new {ok=true,master=masterPath,created=created,part=_part.FullPath,api="NX12-supported deprecated CreateSimulation"});
    }
    private static NXOpen.Assemblies.Component MotionComponent(string name,NXOpen.Assemblies.Component parent)
    {
        foreach(var c in parent.GetChildren()) {if(c.Name==name)return c;var found=MotionComponent(name,c);if(found!=null)return found;}return null;
    }
    private static NXOpen.Motion.Link MotionLinkByName(string name) {foreach(var x in _part.MotionManager.Links.ToArray())if(x.Name==name)return x;throw new Exception("Unknown link "+name);}
    private static NXOpen.Motion.Joint MotionJointByName(string name) {foreach(var x in _part.MotionManager.Joints.ToArray())if(x.Name==name)return x;throw new Exception("Unknown joint "+name);}
    private static NXOpen.Motion.MotionSolution MotionSolutionByName(string name) {foreach(var x in _part.MotionManager.MotionSolutions.ToArray())if(x.Name==name || x.GetName()==name)return x;throw new Exception("Unknown solution "+name);}
    private static string MotionSetup()
    {
        Phase2Part();Session.GetSession().MotionSession.InitializeSimulation(_part);
        var env=Session.GetSession().MotionSession.Environments;env.SetSolver(NXOpen.Motion.MotionEnvironment.Solver.Simcenter);env.SetAnalysisType(NXOpen.Motion.MotionEnvironment.Analysis.Kinematics);env.SetComponentBasedMechanism(true);
        return Json(new {ok=true,solver=env.GetSolver().ToString(),part=_part.FullPath});
    }
    private static string MotionLinkCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.Links.CreateLinkBuilder(null);
        try {b.Name=a[1];b.Fixed=a[2]=="1";for(int i=3;i<a.Length;i++) {var c=MotionComponent(a[i],_part.ComponentAssembly.RootComponent);if(c==null)throw new Exception("Component missing "+a[i]);var p=(Part)c.Prototype;if(!p.IsFullyLoaded){var s=p.LoadFully();s.Dispose();}b.Geometries.Add(c);}var obj=b.Commit();return Json(new {ok=true,link=obj.Name,tag=obj.Tag.ToString(),fixed_link=b.Fixed});}finally{b.Destroy();}
    }
    private static string MotionJointCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.Joints.CreateJointBuilder(null);
        try {var d=b.JointDefine;d.Name=a[1];d.JointType=(NXOpen.Motion.JointDefine.Type)Enum.Parse(typeof(NXOpen.Motion.JointDefine.Type),a[2],true);d.FirstLink=MotionLinkByName(a[3]);d.SecondLink=MotionLinkByName(a[4]);
            var p=new Point3d(double.Parse(a[5]),double.Parse(a[6]),double.Parse(a[7]));var v=new Vector3d(double.Parse(a[8]),double.Parse(a[9]),double.Parse(a[10]));
            d.FirstOrigin=_part.Points.CreatePoint(p);d.SecondOrigin=_part.Points.CreatePoint(p);d.FirstOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;d.SecondOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;
            d.FirstVector=_part.Directions.CreateDirection(p,v,SmartObject.UpdateOption.WithinModeling);d.SecondVector=_part.Directions.CreateDirection(p,v,SmartObject.UpdateOption.WithinModeling);d.SnapLinks=false;
            if(a.Length>12){d.LowerLimitDefined=true;d.UpperLimitDefined=true;if(d.JointType==NXOpen.Motion.JointDefine.Type.Revolute){d.LowerLimitAngleExpression.RightHandSide=a[11];d.UpperLimitAngleExpression.RightHandSide=a[12];}else{d.LowerLimitExpression.RightHandSide=a[11];d.UpperLimitExpression.RightHandSide=a[12];}}
            var obj=b.Commit();return Json(new {ok=true,joint=obj.Name,tag=obj.Tag.ToString(),joint_type=d.JointType.ToString()});
        }finally{b.Destroy();}
    }
    private static string MotionState()
    {
        var links=new List<object>();var joints=new List<object>();var drivers=new List<object>();var solutions=new List<object>();
        foreach(var x in _part.MotionManager.Links.ToArray()) {var b=_part.MotionManager.Links.CreateLinkBuilder(x);try{links.Add(new {name=x.Name,tag=x.Tag.ToString(),fixed_link=b.Fixed,geometry_count=b.Geometries.Size});}finally{b.Destroy();}}
        foreach(var x in _part.MotionManager.Joints.ToArray()){var b=_part.MotionManager.Joints.CreateJointBuilder(x);try{var d=b.JointDefine;joints.Add(new {name=x.Name,type=d.JointType.ToString(),first_link=d.FirstLink==null?null:d.FirstLink.Name,second_link=d.SecondLink==null?null:d.SecondLink.Name});}finally{b.Destroy();}}
        foreach(var x in _part.MotionManager.JointDrivers.ToArray())drivers.Add(new {name=x.Name,tag=x.Tag.ToString()});
        foreach(var x in _part.MotionManager.MotionSolutions.ToArray())solutions.Add(new {name=x.GetName(),gruebler_dof=x.CalculateGrueblerCount()});
        return Json(new {ok=true,part=_part.FullPath,solver=Session.GetSession().MotionSession.Environments.GetSolver().ToString(),links=links,joints=joints,drivers=drivers,solutions=solutions});
    }
    private static string MotionSolutionCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(null);
        try {b.Name=a[1];b.SolutionType=a[2]=="articulation"?NXOpen.Motion.MotionSolutionBuilder.SolutionTypes.Articulation:NXOpen.Motion.MotionSolutionBuilder.SolutionTypes.Normal;b.AnalysisType=NXOpen.Motion.MotionSolutionBuilder.AnalysisTypes.Dynamic;b.Step=int.Parse(a[4]);b.TimeExpression.RightHandSide=a[3];b.GravityValueExpression.RightHandSide="0";var obj=(NXOpen.Motion.MotionSolution)b.Commit();return Json(new {ok=true,solution=obj.GetName(),gruebler_dof=obj.CalculateGrueblerCount()});}finally{b.Destroy();}
    }
    private static string MotionSolve(string[] a) {Phase2Part();var s=MotionSolutionByName(a[1]);_part.MotionManager.MotionSolutions.SetActiveSolution(s);s.SolveNormalRunSolution();return Json(new {ok=true,solution=s.GetName(),solver_returned=true});}
    private static string MotionDriverCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(null);
        try {b.Name=a[1];b.Joint.Value=MotionJointByName(a[2]);var d=a[3]=="slider"?b.DriverMultiOperations.MotionTranslationZ:b.DriverMultiOperations.MotionEulerAngle1;d.TypeOption=NXOpen.Motion.DriverOperation.Type.Polynomial;d.IntegrationTypeOption=NXOpen.Motion.DriverOperation.IntegrationType.Displacement;d.DisplacementExpression.RightHandSide="0";d.VelocityExpression.RightHandSide=a[4];d.AccelerationExpression.RightHandSide="0";d.JerkExpression.RightHandSide="0";var obj=b.Commit();return Json(new {ok=true,driver=obj.Name,velocity=d.VelocityExpression.RightHandSide,velocity_unit=d.VelocityExpression.Units==null?null:d.VelocityExpression.Units.Name});}finally{b.Destroy();}
    }
    private static string MotionJointAxis(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.Joints.CreateJointBuilder(MotionJointByName(a[1]));try{var d=b.JointDefine;var v=new Vector3d(double.Parse(a[2]),double.Parse(a[3]),double.Parse(a[4]));d.FirstVector=_part.Directions.CreateDirection(d.FirstOrigin.Coordinates,v,SmartObject.UpdateOption.WithinModeling);d.SecondVector=_part.Directions.CreateDirection(d.SecondOrigin.Coordinates,v,SmartObject.UpdateOption.WithinModeling);b.Commit();return Json(new{ok=true,joint=a[1],axis=v});}finally{b.Destroy();}
    }
    private static string MotionAngle(string[] a)
    {
        Phase2Part();var uf=NXOpen.UF.UFSession.GetUFSession();var m=new NXOpen.UF.UFMotion.Measurement();uf.Motion.InitMeasurementStruct(out m);m.type=(NXOpen.UF.UFMotion.MeasurementType)2;m.object_1=P2Geometry(P2Component(a[1]),a[2]).Tag;m.object_2=P2Geometry(P2Component(a[3]),a[4]).Tag;Tag tag;uf.Motion.CreateMeasurement(ref m,out tag);try{double value;uf.Motion.Measure(tag,int.Parse(a[5]),out value);return Json(new{ok=true,step=int.Parse(a[5]),angle_native=value,source="UF_MOTION_measure angle between actual linked linear edges"});}finally{uf.Motion.DeleteMeasurement(tag);}
    }
    private static string Phase2Color(string[] a) {Phase2Part();int color=int.Parse(a[1]);if(color<1||color>216)throw new Exception("NX color index 1..216 required");
        if(_part.FullPath.StartsWith(LabPath(@"models\phase4_"),StringComparison.OrdinalIgnoreCase)){var d=Session.GetSession().DisplayManager.NewDisplayModification();try{d.ApplyToAllFaces=true;d.NewColor=color;var bodies=new List<DisplayableObject>();foreach(Body b in _part.Bodies)bodies.Add(b);d.Apply(bodies.ToArray());}finally{d.Dispose();}}
        else foreach(var b in _part.Bodies.ToArray())b.Color=color;return Json(new{ok=true,color=color});}
    private static string Phase2View(string[] a) {Phase2Part();var v=_part.ModelingViews.WorkView;v.Orient(a[1]=="top"?NXOpen.View.Canned.Top:NXOpen.View.Canned.Isometric,NXOpen.View.ScaleAdjustment.Fit);v.SetScale(v.Scale*double.Parse(a[2]));return Json(new {ok=true,view=a[1],scale=v.Scale});}
    private static string Phase2Application(string[] a) {string app=a[1]=="motion"?"UG_APP_MECHANISMS":"UG_APP_MODELING";Session.GetSession().ApplicationSwitchImmediate(app);return Json(new {ok=true,application=app});}
    private static NXOpen.Motion.AnimationControl _motionAnimation;
    private static string _motionAnimationPart,_motionAnimationSolution;
    private static string MotionFrame(string[] a)
    {
        Phase2Part();bool reacquired=false;
        if(_motionAnimation!=null) {try {var alive=_motionAnimation.Tag;}catch(NXException e){Log("Animation controller expired; reacquire native results: "+e.ErrorCode+" "+e.Message);_motionAnimation=null;reacquired=true;}}
        if(_motionAnimation!=null && (_motionAnimationPart!=_part.FullPath || _motionAnimationSolution!=a[1])) {_motionAnimation.Finish();_motionAnimation=null;}
        if(_motionAnimation==null){var s=MotionSolutionByName(a[1]);_part.MotionManager.MotionSolutions.SetActiveSolution(s);s.LoadResult();_motionAnimation=s.GetAnimationControl();_motionAnimationPart=_part.FullPath;_motionAnimationSolution=a[1];}
        if(a[2]=="finish"){_motionAnimation.StepToDesignPosition();_motionAnimation.Finish();_motionAnimation=null;return Json(new{ok=true,finished=true});}
        _motionAnimation.StepTo(int.Parse(a[2]));Session.GetSession().ListingWindow.CloseWindow();return Json(new{ok=true,solution=a[1],active_solution=_part.MotionManager.MotionSolutions.GetActiveSolution().GetName(),frame=_motionAnimation.CurrentStep,number_steps=_motionAnimation.NumberSteps,controller_reacquired=reacquired});
    }
    private class MotionPair {public string a,b;public Tag tag;}
    private static List<MotionPair> _motionPairs=new List<MotionPair>();
    private static string _motionCheckPart;
    private static Tag _motionCheckPartTag;
    private static string PairKey(Tag a,Tag b) {string x=a.ToString(),y=b.ToString();return String.CompareOrdinal(x,y)<0?x+":"+y:y+":"+x;}
    private static string MotionCollisionSetup()
    {
        Phase2Part();_motionPairs.Clear();_motionCheckPart=null;
        var uf=NXOpen.UF.UFSession.GetUFSession();var bodies=new List<Tuple<string,string,Tag>>();
        foreach(var link in _part.MotionManager.Links.ToArray()) {var builder=_part.MotionManager.Links.CreateLinkBuilder(link);try {
            foreach(var geo in builder.Geometries.GetArray()) {var c=geo as NXOpen.Assemblies.Component;if(c==null)throw new Exception("Collision tool requires component-based links");var p=(Part)c.Prototype;
                if(!p.IsFullyLoaded){var ls=p.LoadFully();ls.Dispose();}
                foreach(var body in p.Bodies.ToArray()) {var occ=c.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Body occurrence unavailable");bodies.Add(Tuple.Create(link.Name,c.Name,occ.Tag));}
            }
        }finally{builder.Destroy();}}
        // NX12 persists interference definitions in SIM. Reuse them after reopen;
        // creating duplicates can raise a native SEHException on this version.
        var existing=new Dictionary<string,Tag>();Tag cursor=Tag.Null;int found=0,reused=0,created=0;
        do {uf.Obj.CycleObjsInPart(_part.Tag,211,ref cursor);if(cursor!=Tag.Null){NXOpen.UF.UFMotion.InterferenceStruct data;uf.Motion.AskInterference(cursor,out data);string key=PairKey(data.solid_1,data.solid_2);if(!existing.ContainsKey(key))existing.Add(key,cursor);found++;}}while(cursor!=Tag.Null);
        var complete=new List<MotionPair>();
        for(int i=0;i<bodies.Count;i++)for(int j=i+1;j<bodies.Count;j++)if(bodies[i].Item1!=bodies[j].Item1){
            string key=PairKey(bodies[i].Item3,bodies[j].Item3);Tag tag;
            if(existing.TryGetValue(key,out tag))reused++;
            else {var data=new NXOpen.UF.UFMotion.InterferenceStruct();uf.Motion.InitInterferenceStruct(out data);data.solid_1=bodies[i].Item3;data.solid_2=bodies[j].Item3;uf.Motion.CreateInterference(ref data,out tag);created++;}
            complete.Add(new MotionPair{a=bodies[i].Item2,b=bodies[j].Item2,tag=tag});
        }
        _motionPairs=complete;_motionCheckPart=_part.FullPath;_motionCheckPartTag=_part.Tag;
        return Json(new{ok=true,pair_count=_motionPairs.Count,body_count=bodies.Count,persisted_definitions=found,reused_definitions=reused,created_definitions=created,source="NX UF_MOTION real component body occurrences; persistent definitions reused by actual body tags"});
    }
    private static string MotionCollisionCheck(string[] a)
    {
        Phase2Part();if(_motionCheckPart!=_part.FullPath || _motionCheckPartTag!=_part.Tag || _motionPairs.Count==0)throw new Exception("Call nx_motion_collision_setup first");int step=int.Parse(a[1]);var uf=NXOpen.UF.UFSession.GetUFSession();int count=0;var pairs=new List<object>();
        foreach(var pair in _motionPairs){int result;uf.Motion.Interference(pair.tag,step,out result);if(result!=0)count++;pairs.Add(new {a=pair.a,b=pair.b,interferes=result!=0,nx_result=result});}
        return Json(new{ok=true,step=step,pairs=pairs,interference_count=count,source="UF_MOTION_interference evaluated at native solver result step; discrete sampling"});
    }

    private static string MotionAudit()
    {
        var uf=NXOpen.UF.UFSession.GetUFSession();object solverParameters;
        try {NXOpen.UF.UFMotion.SolverParameters p;uf.Motion.AskSolverParameters(out p);solverParameters=p;}catch(NXException e){solverParameters=new {error=e.Message,code=e.ErrorCode};}
        var joints=new List<object>();var drivers=new List<object>();var solutions=new List<object>();
        foreach(var j in _part.MotionManager.Joints.ToArray()) {var b=_part.MotionManager.Joints.CreateJointBuilder(j);try {var d=b.JointDefine;joints.Add(new {name=j.Name,type=d.JointType.ToString(),first=d.FirstLink.Name,second=d.SecondLink.Name,origin1=d.FirstOrigin.Coordinates,origin2=d.SecondOrigin.Coordinates,axis1=d.FirstVector.Vector,axis2=d.SecondVector.Vector,lower_defined=d.LowerLimitDefined,upper_defined=d.UpperLimitDefined,lower_degrees=d.LowerLimitAngleExpression.Value,upper_degrees=d.UpperLimitAngleExpression.Value});}finally{b.Destroy();}}
        foreach(var j in _part.MotionManager.JointDrivers.ToArray()) {var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(j);try {var d=b.DriverMultiOperations.MotionEulerAngle1;drivers.Add(new {name=j.Name,joint=((NXObject)b.Joint.Value).Name,type=d.TypeOption.ToString(),integration=d.IntegrationTypeOption.ToString(),displacement=d.DisplacementExpression.RightHandSide,velocity=d.VelocityExpression.RightHandSide,acceleration=d.AccelerationExpression.RightHandSide,jerk=d.JerkExpression.RightHandSide,unit=d.VelocityExpression.Units.Name});}finally{b.Destroy();}}
        foreach(var s in _part.MotionManager.MotionSolutions.ToArray()){var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(s);try{solutions.Add(new {name=s.GetName(),analysis_type=b.AnalysisType.ToString(),solution_type=b.SolutionType.ToString(),gruebler_dof=s.CalculateGrueblerCount(),time=b.TimeExpression.Value,steps=b.Step,gravity=b.GravityValueExpression.Value});}finally{b.Destroy();}}
        return Json(new {ok=true,part=_part.FullPath,solver_parameters=solverParameters,joints=joints,drivers=drivers,solutions=solutions,body_contact_count=_part.MotionManager.BodyContacts.ToArray().Length,analysis_enum_documentation="NX12 Dynamic means Dynamic or Kinematic analysis; use solver parameters, DOF and native solver output together"});
    }
    private static string MotionTransforms(string[] a)
    {
        int frame=int.Parse(a[1]);var rows=new List<object>();var uf=NXOpen.UF.UFSession.GetUFSession();
        foreach(var link in _part.MotionManager.Links.ToArray()){var mat=new double[16];uf.Motion.AskLinkTransfromForGivenFrame(link.Tag,frame,mat);rows.Add(new {link=link.Name,matrix=mat});}
        return Json(new {ok=true,frame=frame,links=rows,source="NX12 UF_MOTION_ask_link_transfrom_for_given_frame native solver result"});
    }
    private static string MotionMarkerCreate(string[] a)
    {
        Phase2Part();var b=_part.Markers.CreateMarkerBuilder(null);var p=new Point3d(D(a[3]),D(a[4]),D(a[5]));var m=new Matrix3x3();m.Xx=1;m.Yy=1;m.Zz=1;
        try{b.Name=a[1];b.SelectLink.Value=MotionLinkByName(a[2]);b.OriginPoint=_part.Points.CreatePoint(p);b.Csys=_part.CoordinateSystems.CreateCoordinateSystem(p,m,false);var obj=b.Commit();return Json(new {ok=true,marker=obj.Name,origin=p});}finally{b.Destroy();}
    }
    private static string MotionMarkerResults(string[] a)
    {
        var uf=NXOpen.UF.UFSession.GetUFSession();var rows=new List<object>();
        foreach(var marker in _part.Markers.ToArray()) {
            if(!marker.Name.StartsWith(a[1],StringComparison.Ordinal))continue;var xyz=new List<object>();
            foreach(var component in new NXOpen.UF.UFMotion.VectorComponent[]{NXOpen.UF.UFMotion.VectorComponent.XComponent,NXOpen.UF.UFMotion.VectorComponent.YComponent,NXOpen.UF.UFMotion.VectorComponent.ZComponent}) {
                int count;double[] values;uf.Motion.AskTransDisplacementResults(marker.Tag,component,NXOpen.UF.UFMotion.ReferenceFrame.Absolute,out count,out values);xyz.Add(new {component=component.ToString(),count=count,values=values});
            }rows.Add(new {marker=marker.Name,coordinates=xyz});
        }return Json(new {ok=true,markers=rows,source="UF_MOTION_ask_trans_displacement_results native result series"});
    }
    private static string MotionJointResults()
    {
        var uf=NXOpen.UF.UFSession.GetUFSession();var rows=new List<object>();
        foreach(var joint in _part.MotionManager.Joints.ToArray()) {
            var angles=new List<object>();foreach(var angle in new NXOpen.UF.UFMotion.DispAngle[]{NXOpen.UF.UFMotion.DispAngle.EulerOne,NXOpen.UF.UFMotion.DispAngle.EulerTwo,NXOpen.UF.UFMotion.DispAngle.EulerThree}) {
                int count;double[] values;uf.Motion.AskRotDisplacementResults(joint.Tag,angle,NXOpen.UF.UFMotion.ReferenceFrame.SecondLink,out count,out values);angles.Add(new {angle=angle.ToString(),count=count,radians=values});
            }rows.Add(new {joint=joint.Name,angles=angles});
        }return Json(new {ok=true,joints=rows,source="UF_MOTION_ask_rot_displacement_results, SecondLink reference, 3-1-3 Euler radians"});
    }
    private static Vector3d P3Unit(Vector3d v) {double n=Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);if(n<1e-9)throw new Exception("Zero axis");return new Vector3d(v.X/n,v.Y/n,v.Z/n);}
    private static Vector3d P3Cross(Vector3d a,Vector3d b){return new Vector3d(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);}
    private static Point3d P3Point(Point3d o,Vector3d u,Vector3d v,double x,double y){return new Point3d(o.X+u.X*x+v.X*y,o.Y+u.Y*x+v.Y*y,o.Z+u.Z*x+v.Z*y);}
    private static string SpatialShape(string[] a)
    {
        if(_part==null || !(_part.FullPath.StartsWith(LabPath(@"models\phase3_"),StringComparison.OrdinalIgnoreCase) || _part.FullPath.StartsWith(LabPath(@"models\phase4_"),StringComparison.OrdinalIgnoreCase)))throw new Exception("Spatial modeling requires phase3_ or phase4_ part");
        string name=a[1],kind=a[2],mode=a[12];double w=D(a[9]),h=D(a[10]),depth=D(a[11]);if(w<=0 || h<=0 || depth<=0)throw new Exception("Positive sizes required");
        var o=new Point3d(D(a[3]),D(a[4]),D(a[5]));var n=P3Unit(new Vector3d(D(a[6]),D(a[7]),D(a[8])));
        var u=P3Unit(P3Cross(Math.Abs(n.Z)<0.9?new Vector3d(0,0,1):new Vector3d(0,1,0),n));var v=P3Cross(n,u);
        var m=new Matrix3x3();m.Xx=u.X;m.Xy=u.Y;m.Xz=u.Z;m.Yx=v.X;m.Yy=v.Y;m.Yz=v.Z;m.Zx=n.X;m.Zy=n.Y;m.Zz=n.Z;
        Body target=a.Length>13 && a[13]!="-"?GetBody(a[13]):null;
        var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Spatial feature "+name);
        try {
            var sb=_part.Sketches.CreateSketchInPlaceBuilder2(null);Sketch sk;
            try {sb.PlaneReference=_part.Planes.CreateFixedTypePlane(o,m,SmartObject.UpdateOption.WithinModeling);sb.AxisReference=_part.Directions.CreateDirection(o,u,SmartObject.UpdateOption.WithinModeling);sb.SketchOrigin=_part.Points.CreatePoint(o);sb.PlaneOption=Sketch.PlaneOption.ExistingPlane;sb.OriginOption=OriginMethod.SpecifyPoint;sk=(Sketch)sb.Commit();}finally{sb.Destroy();}
            sk.SetName(name+"_profile");sk.Activate(Sketch.ViewReorient.True);var curves=new List<Curve>();
            if(kind=="cylinder")curves.Add(_part.Curves.CreateArc(o,u,v,w/2,0,2*Math.PI));
            else if(kind=="box") {double[] xs={-w/2,w/2,w/2,-w/2};double[] ys={-h/2,-h/2,h/2,h/2};for(int i=0;i<4;i++)curves.Add(_part.Curves.CreateLine(P3Point(o,u,v,xs[i],ys[i]),P3Point(o,u,v,xs[(i+1)%4],ys[(i+1)%4])));}
            else throw new Exception("Unknown profile");
            foreach(var curve in curves){sk.AddGeometry(curve,Sketch.InferConstraintsOption.InferNoConstraints);var g=new Sketch.ConstraintGeometry();g.Geometry=curve;g.PointType=Sketch.ConstraintPointType.None;sk.CreateFixedConstraint(g);}
            sk.Update();sk.Deactivate(Sketch.ViewReorient.True,Sketch.UpdateLevel.Model);
            var section=_part.Sections.CreateSection();section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveFeature(new Feature[]{sk.Feature})},null,null,null,o,Section.Mode.Create,false);
            var eb=_part.Features.CreateExtrudeBuilder(null);NXOpen.Features.Extrude f;
            try {eb.Section=section;eb.Direction=_part.Directions.CreateDirection(o,n,SmartObject.UpdateOption.WithinModeling);eb.Limits.StartExtend.Value.RightHandSide="0";eb.Limits.EndExtend.Value.RightHandSide=depth.ToString(CultureInfo.InvariantCulture);eb.BooleanOperation.Type=mode=="subtract"?BooleanOperation.BooleanType.Subtract:target==null?BooleanOperation.BooleanType.Create:BooleanOperation.BooleanType.Unite;if(target!=null)eb.BooleanOperation.SetTargetBodies(new Body[]{target});f=(NXOpen.Features.Extrude)eb.CommitFeature();f.SetName(name);}finally{eb.Destroy();}
            Body body=f.GetBodies()[0];string id="BODY_"+(++_bodyCounter);_bodies[id]=body;string sid="SKETCH_"+(++_sketchCounter);_sketches[sid]=sk;
            return Json(new {ok=true,body_id=id,sketch_id=sid,feature=f.GetFeatureName(),origin=o,axis=n});
        }catch(Exception e){Session.GetSession().UndoToMark(mark,null);return Json(new {ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
    }



    // Read-only converged measurement; comparison thresholds are not changed.
    private static string AccurateVolume(string[] a)
    {
        if (_part==null) throw new Exception("No active part");
        double tol=D(a[1]); if(tol<0.9 || tol>1.0)throw new Exception("Integration accuracy must be 0.9..1.0");
        var rows=new List<object>();double total=0;
        foreach(Body b in _part.Bodies) {
            double[] acc=new double[11], mass=new double[47],stats=new double[13];acc[0]=tol;
            NXOpen.UF.UFSession.GetUFSession().Modl.AskMassProps3d(new Tag[]{b.Tag},1,1,4,1.0,1,acc,mass,stats);
            double volume=mass[1]*1e9; total+=volume;rows.Add(new {tag=b.Tag.ToString(),volume_mm3=volume,volume_range_error_mm3=stats[1]*1e9,statistics=stats});
        }
        return Json(new {ok=true,part=_part.FullPath,integration_accuracy=tol,volume_mm3=total,bodies=rows});
    }
    private static void MechanismPart() {Phase2Part();if(!_part.FullPath.StartsWith(LabPath(@"models\phase4_"),StringComparison.OrdinalIgnoreCase))throw new Exception("New mechanism operations require phase4 model");}
    private static string ProfileSolid(string[] a)
    {
        MechanismPart();string file=Path.GetFullPath(a[2]);SafePath(file);if(!file.StartsWith(Path.GetDirectoryName(_part.FullPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Profile data must be beside current part");
        double z=D(a[3]),depth=D(a[4]);if(depth<=0)throw new Exception("Positive depth required");
        var rows=File.ReadAllLines(file);bool boolean=a[0]=="nx_profile_boolean";Body target=boolean?GetBody(a[6]):null;bool polygon=boolean?(a.Length>7 && a[7]=="polyline"):(a.Length>5 && a[5]=="polyline");if(rows.Length<(polygon?3:8) || rows.Length>4096)throw new Exception("Invalid profile point count");
        var points=new NXOpen.UF.UFCurve.PtSlopeCrvatr[rows.Length];
        for(int i=0;i<rows.Length;i++){var xy=rows[i].Split(',');if(xy.Length!=2)throw new Exception("Expected X,Y per row");points[i].point=new double[]{D(xy[0]),D(xy[1]),z};points[i].slope_type=-1;points[i].crvatr_type=-1;points[i].slope=new double[3];points[i].crvatr=new double[3];foreach(double v in points[i].point)if(double.IsNaN(v)||double.IsInfinity(v)||Math.Abs(v)>10000)throw new Exception("Invalid profile point");}
        var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Mechanism spline "+a[1]);
        try {
            var o=new Point3d(0,0,z);var m=new Matrix3x3();m.Xx=1;m.Yy=1;m.Zz=1;
            // UF spline defining data is the native profile; it is not presented as a constrained sketch.
            var curves=new List<Curve>();bool self=false;
            if(polygon){for(int i=0;i<points.Length;i++){var p=points[i].point;var q=points[(i+1)%points.Length].point;curves.Add(_part.Curves.CreateLine(new Point3d(p[0],p[1],p[2]),new Point3d(q[0],q[1],q[2])));}}
            else {Tag tag;var uf=NXOpen.UF.UFSession.GetUFSession();uf.Curve.CreateSplineThruPts(3,1,points.Length,points,null,1,out tag);uf.Curve.IsSplineSelfInt(tag,out self);if(self)throw new Exception("NX spline self-intersects");curves.Add((Curve)NXOpen.Utilities.NXObjectManager.Get(tag));}
            curves[0].SetName(a[1]+"_working_curve");var section=_part.Sections.CreateSection();section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(curves.ToArray())},curves[0],null,null,new Point3d(points[0].point[0],points[0].point[1],z),Section.Mode.Create,false);
            var eb=_part.Features.CreateExtrudeBuilder(null);NXOpen.Features.Extrude f;
            if(!boolean)eb.BooleanOperation.Type=BooleanOperation.BooleanType.Create;
            try{eb.Section=section;eb.Direction=_part.Directions.CreateDirection(o,new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);eb.Limits.StartExtend.Value.RightHandSide="0";eb.Limits.EndExtend.Value.RightHandSide=a[4];if(boolean){eb.BooleanOperation.Type=a[5]=="subtract"?BooleanOperation.BooleanType.Subtract:BooleanOperation.BooleanType.Unite;eb.BooleanOperation.SetTargetBodies(new Body[]{target});}f=(NXOpen.Features.Extrude)eb.CommitFeature();f.SetName(a[1]);}finally{eb.Destroy();}
            string id="BODY_"+(++_bodyCounter);_bodies[id]=f.GetBodies()[0];return Json(new{ok=true,body_id=id,feature=f.GetFeatureName(),profile_points=points.Length,profile_kind=polygon?"polyline":"periodic_cubic_spline",self_intersecting=self});
        }catch(Exception e){Session.GetSession().UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,stack=e.StackTrace,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
    }
    private static string CurvePairCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.CurveOnCurves.CreateCurveOnCurveBuilder(null);
        try{b.Name=a[1];b.SetICurve(new NXObject[]{P2Geometry(MotionComponent(a[2],_part.ComponentAssembly.RootComponent),a[3])});b.SetJCurve(new NXObject[]{P2Geometry(MotionComponent(a[4],_part.ComponentAssembly.RootComponent),a[5])});b.LockSlip=a[6]=="1";var obj=b.Commit();return Json(new{ok=true,name=obj.Name,lock_slip=b.LockSlip,first_curves=b.GetICurve().Length,second_curves=b.GetJCurve().Length});}finally{b.Destroy();}
    }
    private static string HigherPairState()
    {
        var rows=new List<object>();foreach(var x in _part.MotionManager.CurveOnCurves.ToArray()){var b=_part.MotionManager.CurveOnCurves.CreateCurveOnCurveBuilder(x);try{rows.Add(new{name=x.Name,type="CurveOnCurve",lock_slip=b.LockSlip,first_curve_count=b.GetICurve().Length,second_curve_count=b.GetJCurve().Length});}finally{b.Destroy();}}
        foreach(var x in _part.MotionManager.PointOnCurves.ToArray()){var b=_part.MotionManager.PointOnCurves.CreatePointOnCurveBuilder(x);try{rows.Add(new{name=x.Name,type="PointOnCurve",link=b.Link.Value.Name,curve_count=b.GetCurves().Length,point=b.Point.Coordinates,parameterization=b.CurveParameterizedType.ToString(),spacing=b.SpacingExpression.Value,spacing_unit=b.SpacingExpression.Units.TypeName});}finally{b.Destroy();}}
        return Json(new{ok=true,pairs=rows});
    }
    private static string CollisionVolumes(string[] a)
    {
        MechanismPart();if(_motionCheckPart!=_part.FullPath || _motionCheckPartTag!=_part.Tag || _motionPairs.Count==0)throw new Exception("Call collision setup");int frame=int.Parse(a[1]);var rows=new List<object>();var uf=NXOpen.UF.UFSession.GetUFSession();
        foreach(var pair in _motionPairs){int hit;uf.Motion.Interference(pair.tag,frame,out hit);if(hit==0)continue;
            var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Invisible,"Temporary native intersection inspection");
            try{int count;Tag[] tags;uf.Motion.CreateInterferenceBody(pair.tag,frame,NXOpen.UF.UFMotion.ReferenceFrame.Absolute,out count,out tags);double volume=0;var bodies=new List<object>();
                foreach(Tag tag in tags){var body=NXOpen.Utilities.NXObjectManager.Get(tag) as Body;if(body==null)throw new Exception("Intersection is not body");double[] mass=new double[47],stats=new double[13],acc=new double[11];acc[0]=1;uf.Modl.AskMassProps3d(new Tag[]{tag},1,1,4,1,1,acc,mass,stats);volume+=mass[1]*1e9;bodies.Add(new{solid=body.IsSolidBody,volume_mm3=mass[1]*1e9});}
                rows.Add(new{a=pair.a,b=pair.b,nx_interference=hit,body_count=count,volume_mm3=volume,bodies=bodies});
            }catch(NXException e){rows.Add(new{a=pair.a,b=pair.b,nx_interference=hit,error=e.Message,nx_error_code=e.ErrorCode});}
            finally{Session.GetSession().UndoToMark(mark,null);Session.GetSession().DeleteUndoMark(mark,null);}
        }
        return Json(new{ok=true,frame=frame,intersections=rows,source="Native UF_MOTION_create_interference_body; temporary bodies undone, original hits retained"});
    }
    private static string TightenSolution(string[] a)
    {
        MechanismPart();double value=D(a[2]);if(value<1e-12 || value>1e-6)throw new Exception("Only tighter kinematic accuracy 1e-12..1e-6 supported");
        var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(MotionSolutionByName(a[1]));
        try{var p=b.LmsSolverProperty;double oldA=p.AssemblyTolerance.Value,oldK=p.KinematicSolutionTolerance.Value;if(value>oldA || value>oldK)throw new Exception("Refusing to loosen solver accuracy");p.AssemblyTolerance.RightHandSide=a[2];p.KinematicSolutionTolerance.RightHandSide=a[2];b.Commit();return Json(new{ok=true,solution=a[1],before_assembly=oldA,before_kinematic=oldK,after_assembly=p.AssemblyTolerance.Value,after_kinematic=p.KinematicSolutionTolerance.Value});}finally{b.Destroy();}
    }

    private static string IntegratorRefine(string[] a)
    {
        MechanismPart();double tol=D(a[2]),step=D(a[3]);if(tol<1e-12 || tol>1e-6 || step<1e-6 || step>0.01)throw new Exception("Bounded integration refinement required");
        var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(MotionSolutionByName(a[1]));
        try{var p=b.LmsSolverProperty;double oldTol=p.DynamicIntegrationTolerance.Value,oldSol=p.DynamicSolutionTolerance.Value,oldStep=p.DynamicMaxIntegrationStep.Value;if(tol>oldTol || tol>oldSol || step>oldStep)throw new Exception("Refusing coarser integration");p.DynamicIntegrationTolerance.RightHandSide=a[2];p.DynamicSolutionTolerance.RightHandSide=a[2];p.DynamicMaxIntegrationStep.RightHandSide=a[3];b.Commit();return Json(new{ok=true,solution=a[1],before_integration_tolerance=oldTol,before_solution_tolerance=oldSol,before_max_step=oldStep,after_integration_tolerance=p.DynamicIntegrationTolerance.Value,after_solution_tolerance=p.DynamicSolutionTolerance.Value,after_max_step=p.DynamicMaxIntegrationStep.Value,environment_analysis_query="NX12 exposes SetAnalysisType without a matching getter; inspect generated native input"});}finally{b.Destroy();}
    }
    private static string PointCurveSampling(string[] a)
    {
        MechanismPart();NXOpen.Motion.PointOnCurve obj=null;foreach(NXOpen.Motion.PointOnCurve x in _part.MotionManager.PointOnCurves.ToArray())if(x.Name==a[1])obj=x;if(obj==null)throw new Exception("PointOnCurve not found");
        double spacing=D(a[2]);if(spacing<0.001 || spacing>1)throw new Exception("Spacing refinement bounded to 0.001..1 mm");
        var b=_part.MotionManager.PointOnCurves.CreatePointOnCurveBuilder(obj);
        try{double old=b.SpacingExpression.Value;string mode=b.CurveParameterizedType.ToString();if(b.CurveParameterizedType==NXOpen.Motion.CurveParameterizedTypes.Spacing && spacing>=old)throw new Exception("New spacing must be finer than current spacing");b.CurveParameterizedType=NXOpen.Motion.CurveParameterizedTypes.Spacing;b.SpacingExpression.RightHandSide=a[2];b.Commit();return Json(new{ok=true,name=obj.Name,before_parameterization=mode,before_spacing=old,after_spacing=b.SpacingExpression.Value,unit=b.SpacingExpression.Units.TypeName});}finally{b.Destroy();}
    }
    private static string PointCurveCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.PointOnCurves.CreatePointOnCurveBuilder(null);
        try{b.Name=a[1];b.Link.Value=MotionLinkByName(a[2]);b.Point=_part.Points.CreatePoint(new Point3d(D(a[3]),D(a[4]),D(a[5])));b.SetCurves(new NXObject[]{P2Geometry(MotionComponent(a[6],_part.ComponentAssembly.RootComponent),a[7])});var obj=b.Commit();return Json(new{ok=true,name=obj.Name,link=b.Link.Value.Name,curve_count=b.GetCurves().Length,point=b.Point.Coordinates});}finally{b.Destroy();}
    }

    private static string ProfileCurve(string[] a)
    {
        MechanismPart();SafePath(a[2]);string file=Path.GetFullPath(a[2]);if(!file.StartsWith(Path.GetDirectoryName(_part.FullPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Curve data must be beside part");
        var rows=File.ReadAllLines(file);if(rows.Length<8 || rows.Length>4096)throw new Exception("Invalid curve count");var points=new NXOpen.UF.UFCurve.PtSlopeCrvatr[rows.Length];
        for(int i=0;i<rows.Length;i++){var xy=rows[i].Split(',');points[i].point=new double[]{D(xy[0]),D(xy[1]),D(a[3])};points[i].slope_type=-1;points[i].crvatr_type=-1;points[i].slope=new double[3];points[i].crvatr=new double[3];}
        Tag tag;NXOpen.UF.UFSession.GetUFSession().Curve.CreateSplineThruPts(3,1,points.Length,points,null,1,out tag);var curve=(Curve)NXOpen.Utilities.NXObjectManager.Get(tag);curve.SetName(a[1]);return Json(new{ok=true,name=curve.Name,tag=tag.ToString(),point_count=points.Length});
    }
    private static string MechanismLine(string[] a)
    {
        MechanismPart();var p=new Point3d(D(a[2]),D(a[3]),D(a[4]));var q=new Point3d(D(a[5]),D(a[6]),D(a[7]));
        foreach(double v in new double[]{p.X,p.Y,p.Z,q.X,q.Y,q.Z})if(double.IsNaN(v)||double.IsInfinity(v)||Math.Abs(v)>10000)throw new Exception("Finite bounded line required");
        if(Math.Abs(p.X-q.X)+Math.Abs(p.Y-q.Y)+Math.Abs(p.Z-q.Z)<1e-8)throw new Exception("Distinct endpoints required");
        var curve=_part.Curves.CreateLine(p,q);curve.SetName(a[1]);return Json(new{ok=true,name=curve.Name,tag=curve.Tag.ToString(),start=p,end=q,source="Actual NX12 CurveCollection.CreateLine"});
    }
    private static string DriverVelocity(string[] a)
    {
        MechanismPart();foreach(var x in _part.MotionManager.JointDrivers.ToArray())if(x.Name==a[1]){var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(x);try{var d=b.DriverMultiOperations.MotionEulerAngle1;string before=d.VelocityExpression.RightHandSide;d.VelocityExpression.RightHandSide=a[2];b.Commit();return Json(new{ok=true,name=x.Name,before=before,after=d.VelocityExpression.RightHandSide});}finally{b.Destroy();}}throw new Exception("Driver not found");
    }
    private static string GearPairCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.Couplers.CreateCouplerGearBuilder(null);
        try{b.Name=a[1];b.FirstJoint.Value=MotionJointByName(a[2]);b.SecondJoint.Value=MotionJointByName(a[3]);b.FirstRadiusExpression.RightHandSide=a[4];b.SecondRadiusExpression.RightHandSide=a[5];b.ContactPoint=_part.Points.CreatePoint(new Point3d(D(a[6]),D(a[7]),D(a[8])));var obj=b.Commit();return Json(new{ok=true,name=obj.Name,first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_radius=b.FirstRadiusExpression.Value,second_radius=b.SecondRadiusExpression.Value,ratio=b.RatioExpression.Value});}finally{b.Destroy();}
    }
    private static string MotionAnalysisSelect(string[] a)
    {
        MechanismPart();if(a[1]!="kinematics" && a[1]!="dynamics")throw new Exception("Explicit kinematics or dynamics required");
        try{Session.GetSession().MotionSession.Environments.SetAnalysisType(a[1]=="dynamics"?NXOpen.Motion.MotionEnvironment.Analysis.Dynamics:NXOpen.Motion.MotionEnvironment.Analysis.Kinematics);return Json(new{ok=true,requested=a[1],setter_returned=true,scope="Setter result only; solver completion and physical data must be verified separately"});}catch(NXException e){return Json(new{ok=false,requested=a[1],nx_error_code=e.ErrorCode,error=e.Message});}
    }
    private static string SessionPartSafetyState()
    {
        var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
        var rows=new List<object>();bool anyModified=false,allKnown=true;
        foreach(var part in session.Parts.ToArray())
        {
            try{bool modified=uf.Part.IsModified(part.Tag);anyModified|=modified;
                rows.Add(new{path=part.FullPath,tag=part.Tag.ToString(),is_modified=modified,is_read_only=part.IsReadOnly,is_fully_loaded=part.IsFullyLoaded});}
            catch(Exception e){allKnown=false;rows.Add(new{path=part.FullPath,error=e.Message});}
        }
        return Json(new{ok=true,read_only_query=true,nx=NxVersion(),process_id=System.Diagnostics.Process.GetCurrentProcess().Id,bridge_location=System.Reflection.Assembly.GetExecutingAssembly().Location,process_temp_directory=Path.GetTempPath(),all_modified_flags_known=allKnown,any_modified=anyModified,parts=rows,scope="Actual loaded-part paths and UF_PART_is_modified; no saving, closing or relocation performed"});
    }
    private static string JointTorqueCreate(string[] a)
    {
        MechanismPart();double moment=D(a[3]);if(double.IsNaN(moment)||double.IsInfinity(moment)||Math.Abs(moment)>100000)throw new Exception("Bounded torque in N mm required");
        var b=_part.MotionManager.ScalarTorques.CreateScalarTorqueBuilder(null);
        try{b.Name=a[1];b.TorqueType=NXOpen.Motion.ScalarTorqueBuilder.ScalarTorqueType.Joint;b.Joint.Value=MotionJointByName(a[2]);b.ValueType=NXOpen.Motion.ForceValueTypes.Constant;b.ApplicationType=NXOpen.Motion.ScalarTorqueBuilder.AppType.ActionReaction;b.Direction=NXOpen.Motion.ScalarTorqueBuilder.DirectionType.Z;
            string unit=b.ValueExpression.Units.TypeName;double value;if(unit=="NewtonMilliMeter" || unit=="NewtonMillimeter")value=moment;else if(unit=="NewtonMeter")value=moment/1000;else throw new Exception("Unverified native torque unit "+unit+"; builder not committed");
            b.ValueExpression.RightHandSide=value.ToString(System.Globalization.CultureInfo.InvariantCulture);var obj=b.Commit();return Json(new{ok=true,name=obj.Name,joint=b.Joint.Value.Name,requested_N_mm=moment,native_value=b.ValueExpression.GetValueUsingUnits(NXOpen.Expression.UnitsOption.Expression),native_base_value=b.ValueExpression.Value,native_rhs=b.ValueExpression.RightHandSide,native_unit=unit,application=b.ApplicationType.ToString(),scope="Experimental constant joint preload; not a material spring or stress validation"});
        }finally{b.Destroy();}
    }
    private static string MotionContactApi()
    {
        MechanismPart();var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(null);
        try{var rows=new List<object>();foreach(var p in b.GetType().GetProperties())if(p.Name=="FirstContactGeometry" || p.Name=="SecondContactGeometry" || p.Name=="LmsParameters") {var methods=new List<string>();foreach(var method in p.PropertyType.GetMethods())methods.Add(method.ToString());rows.Add(new{name=p.Name,type=p.PropertyType.FullName,methods=methods});}return Json(new{ok=true,builder=b.GetType().FullName,properties=rows,committed=false,scope="Actual NX12 builder availability; no contact simulation accepted"});}finally{b.Destroy();}
    }
    private static object ContactExpression(NXOpen.Expression e) {return new{expression=e.RightHandSide,value_in_expression_units=e.GetValueUsingUnits(NXOpen.Expression.UnitsOption.Expression),expression_unit=e.Units==null?null:e.Units.TypeName,value_in_base_units=e.Value};}
    private static string BodyContactState(string[] a)
    {
        MechanismPart();var rows=new List<object>();foreach(var x in _part.MotionManager.BodyContacts.ToArray()){var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(x);try{var p=b.LmsParameters;rows.Add(new{name=x.Name,force_model=p.ForceModel.ToString(),stiffness=ContactExpression(p.Stiffness),damping=ContactExpression(p.MaterialDamping),exponent=ContactExpression(p.ForceExponent),max_penetration=ContactExpression(p.MaxPenetrationDepth)});}finally{b.Destroy();}}return Json(new{ok=true,contacts=rows,scope="NX Expression.Value is base-part units; expression units are read explicitly with GetValueUsingUnits"});
    }
    private static string BodyContactModelEdit(string[] a)
    {
        MechanismPart();foreach(var x in _part.MotionManager.BodyContacts.ToArray())if(x.Name==a[1]){var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(x);try{string before=b.LmsParameters.ForceModel.ToString();if(a[2]=="midplane")b.LmsParameters.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.Midplane;else if(a[2]=="projectedvertex")b.LmsParameters.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.ProjectedVertex;else throw new Exception("Explicit supported contact model required");b.Commit();return Json(new{ok=true,name=x.Name,before=before,after=b.LmsParameters.ForceModel.ToString()});}finally{b.Destroy();}}throw new Exception("Contact not found");
    }
    private static string MotionMassState()
    {
        MechanismPart();var rows=new List<object>();foreach(var link in _part.MotionManager.Links.ToArray()){var b=_part.MotionManager.Links.CreateLinkBuilder(link);try{var p=b.MassProperty;rows.Add(new{name=link.Name,fixed_link=b.Fixed,mass_type=p.MassType.ToString(),mass=p.MassExpression.Value,mass_unit=p.MassExpression.Units==null?null:p.MassExpression.Units.TypeName,ixx=p.IxxExpression.Value,iyy=p.IyyExpression.Value,izz=p.IzzExpression.Value,inertia_unit=p.IxxExpression.Units==null?null:p.IxxExpression.Units.TypeName,center=p.MassCenter==null?(object)null:p.MassCenter.Coordinates});}finally{b.Destroy();}}return Json(new{ok=true,links=rows});
    }
    private static string PreciseMotionMass(string[] a)
    {
        MechanismPart();if(a.Length!=3 || (a[2]!="0" && a[2]!="1"))throw new Exception("link apply_0_or_1 required");
        var wcs=_part.WCS.CoordinateSystem;var wm=wcs.Orientation.Element;var wo=wcs.Origin;
        if(Math.Abs(wo.X)+Math.Abs(wo.Y)+Math.Abs(wo.Z)+Math.Abs(wm.Xx-1)+Math.Abs(wm.Yy-1)+Math.Abs(wm.Zz-1)+Math.Abs(wm.Xy)+Math.Abs(wm.Xz)+Math.Abs(wm.Yx)+Math.Abs(wm.Yz)+Math.Abs(wm.Zx)+Math.Abs(wm.Zy)>1e-10)throw new Exception("Verified identity analysis WCS required");
        var b=_part.MotionManager.Links.CreateLinkBuilder(MotionLinkByName(a[1]));
        try{
            if(b.Fixed)throw new Exception("Moving link required");var tags=new List<Tag>();
            foreach(var item in b.Geometries.GetArray()){var c=item as NXOpen.Assemblies.Component;if(c==null)throw new Exception("Component geometry required");foreach(Body body in ((Part)c.Prototype).Bodies){var occ=c.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Body occurrence unavailable");tags.Add(occ.Tag);}}
            if(tags.Count==0)throw new Exception("No bodies");double[] lower=new double[47],higher=new double[47],sl=new double[13],sh=new double[13],acc=new double[11];
            var uf=NXOpen.UF.UFSession.GetUFSession();acc[0]=.99999;uf.Modl.AskMassProps3d(tags.ToArray(),tags.Count,1,4,1,1,acc,lower,sl);acc[0]=1;uf.Modl.AskMassProps3d(tags.ToArray(),tags.Count,1,4,1,1,acc,higher,sh);
            foreach(int i in new int[]{1,2,31,32,33})if(!(higher[i]>0) || Math.Abs(lower[i]-higher[i])>Math.Max(1e-14,Math.Abs(higher[i])*1e-7))throw new Exception("Mass/inertia integration has not converged");
            var p=b.MassProperty;var before=new{type=p.MassType.ToString(),mass=ContactExpression(p.MassExpression),ixx=ContactExpression(p.IxxExpression),iyy=ContactExpression(p.IyyExpression),izz=ContactExpression(p.IzzExpression)};
            if(a[2]=="1"){
                if(p.MassExpression.Units.TypeName!="Kilogram" || p.IxxExpression.Units.TypeName!="KilogramMilliMeterSquared")throw new Exception("Unverified native mass/inertia units");
                var x=P3Unit(new Vector3d(higher[22],higher[23],higher[24]));var y=P3Unit(new Vector3d(higher[25],higher[26],higher[27]));var z=P3Unit(P3Cross(x,y));
                if(Math.Abs(x.X*y.X+x.Y*y.Y+x.Z*y.Z)>1e-8 || Math.Abs(z.X*higher[28]+z.Y*higher[29]+z.Z*higher[30])<.99999999)throw new Exception("Invalid principal basis");
                var matrix=new Matrix3x3();matrix.Xx=x.X;matrix.Xy=x.Y;matrix.Xz=x.Z;matrix.Yx=y.X;matrix.Yy=y.Y;matrix.Yz=y.Z;matrix.Zx=z.X;matrix.Zy=z.Y;matrix.Zz=z.Z;
                var center=new Point3d(higher[3]*1000,higher[4]*1000,higher[5]*1000);p.MassType=NXOpen.Motion.LinkMassProperty.MassPropertyType.UserDefined;p.MassCenter=_part.Points.CreatePoint(center);p.InertiaCsys=_part.CoordinateSystems.CreateCoordinateSystem(center,_part.NXMatrices.Create(matrix),false);
                p.MassExpression.RightHandSide=higher[2].ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IxxExpression.RightHandSide=(higher[31]*1e6).ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IyyExpression.RightHandSide=(higher[32]*1e6).ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IzzExpression.RightHandSide=(higher[33]*1e6).ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IxyExpression.RightHandSide="0";p.IxzExpression.RightHandSide="0";p.IyzExpression.RightHandSide="0";b.Commit();
            }
            return Json(new{ok=true,link=a[1],applied=a[2]=="1",before=before,lower=lower,higher=higher,lower_statistics=sl,higher_statistics=sh,uf_units="kg,m; volume[1],mass[2],COM[3:5],principal_axes[22:30],principal_moments[31:33]",scope="Measured from real body occurrences with original body density; user-defined Motion values use converged native principal mass properties, not arbitrary calibration"});
        }finally{b.Destroy();}
    }
    private static string Phase4Density(string[] a)
    {
        MechanismPart();double rho=D(a[1]);if(rho<=0 || rho>30000)throw new Exception("Density kg/m^3 outside bounded material range");var rows=new List<object>();foreach(Body body in _part.Bodies){double before=body.Density;body.Density=rho;rows.Add(new{tag=body.Tag.ToString(),before_kg_m3=before,after_kg_m3=body.Density});}if(rows.Count==0)throw new Exception("No work-part bodies; do not modify assembly prototypes implicitly");return Json(new{ok=true,bodies=rows,source="NX12 Body.Density documented kg/m^3; explicit experimental material assumption"});
    }
    private static string AnalyticalSphereExtrusion(string[] a)
    {
        MechanismPart();if((a.Length!=14 && a.Length!=15) || !_part.FullPath.StartsWith(LabPath(@"models\phase4_finalization_"),StringComparison.OrdinalIgnoreCase))throw new Exception("Analytical contact experiment requires finalization model and explicit geometry");
        if(a.Length==15 && a[14]!="0" && a[14]!="1")throw new Exception("Material side must be 0 or 1");
        double radius=D(a[6]),angle=D(a[10]);if(radius<=0 || radius>1000 || angle<0 || angle>180)throw new Exception("Invalid radius or partial-sphere angle");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Analytical contact experiment");
        string stage="create_property";var cleanupErrors=new List<string>();NXOpen.Motion.AnalyticalContactPropertyBuilder pb=null;NXOpen.Motion.AnalyticalContactBuilder b=null;
        try{
            pb=_part.MotionManager.AnalyticalContactPropertys.CreateAnalyticalContactPropertyBuilder(null);pb.Name=a[1]+"Material";
            foreach(var material in new[]{pb.FirstCustomizedMaterial,pb.SecondCustomizedMaterial}){material.IsCustomizedMaterial=true;material.Density.Value=7.8e-6;material.YoungModulus.Value=2e8;material.PoissonRatio.Value=.3;}
            pb.RestitutionCoefficient.RightHandSide="0.1";pb.FrictionCoefficientValue.RightHandSide="0";
            var property=(NXOpen.Motion.AnalyticalContactProperty)pb.Commit();
            stage="create_contact_builder";b=_part.MotionManager.AnalyticalContacts.CreateAnalyticalContactBuilder(null);b.Name=a[1];b.ContactType=NXOpen.Motion.AnalyticalContactBuilder.ContactTypeOption.SphereToExtrusion;b.ContactProperty.Value=property;
            b.SphereLink.Value=MotionLinkByName(a[2]);b.SpherePoint=_part.Points.CreatePoint(new Point3d(D(a[3]),D(a[4]),D(a[5])));b.SphereRadius.RightHandSide=a[6];b.ExtrusionLink.Value=MotionLinkByName(a[7]);
            var component=P2Component(a[8]);object sheetEvidence=null;
            if(a[9].StartsWith("sheet:",StringComparison.Ordinal)){
                var face=P2Geometry(component,a[9].Substring(6)) as Face;if(face==null)throw new Exception("Actual planar extrusion end face required");var edges=face.GetEdges();if(edges.Length<3)throw new Exception("Closed physical profile required");
                stage="sheet_copy_native_edge_curves";var curves=new List<Curve>();foreach(var edge in edges){Tag curveTag;NXOpen.UF.UFSession.GetUFSession().Modl.CreateCurveFromEdge(edge.Tag,out curveTag);curves.Add((Curve)NXOpen.Utilities.NXObjectManager.Get(curveTag));}Point3d helpA,helpB;edges[0].GetVertices(out helpA,out helpB);stage="sheet_section_from_native_curves";var section=_part.Sections.CreateSection();section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(curves.ToArray())},curves[0],null,null,helpA,Section.Mode.Create,false);
                stage="sheet_extrude_builder";var extrude=_part.Features.CreateExtrudeBuilder(null);Body sheet;
                try{extrude.Section=section;extrude.FeatureOptions.BodyType=NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Sheet;extrude.Direction=_part.Directions.CreateDirection(new Point3d(0,0,27.6),new Vector3d(0,0,-1),SmartObject.UpdateOption.WithinModeling);extrude.Limits.StartExtend.Value.RightHandSide="0";extrude.Limits.EndExtend.Value.RightHandSide="7.2";extrude.BooleanOperation.Type=BooleanOperation.BooleanType.Create;stage="sheet_commit";var feature=(NXOpen.Features.BodyFeature)extrude.CommitFeature();feature.SetName(a[1]+"PhysicalProfileSheet");var bodies=feature.GetBodies();if(bodies.Length!=1 || bodies[0].IsSolidBody)throw new Exception("Expected one native sheet contact body");sheet=bodies[0];sheetEvidence=new{feature=feature.GetFeatureName(),source_face=a[9].Substring(6),source_edge_count=edges.Length,sheet_faces=sheet.GetFaces().Length,solid=sheet.IsSolidBody,depth_mm=7.2,scope="Native sheet extruded directly from actual wheel upper-face edges; original solid unchanged"};}finally{try{extrude.Destroy();}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}}
                stage="select_sheet";b.ExtrusionBodies.Add(sheet);
            }else if(a[9]=="body"){var bodies=((Part)component.Prototype).Bodies.ToArray();if(bodies.Length!=1)throw new Exception("Exactly one extrusion body required");b.ExtrusionBodies.Add((Body)component.FindOccurrence(bodies[0]));}else b.ExtrusionBodies.Add(P2Geometry(component,a[9]));
            b.ExtrusionGeometryData=false;b.InfiniteExtrusion=false;b.FindAllRoots=true;b.EffectiveContactDepth.RightHandSide="7.2";b.MaxTesselationLength.RightHandSide="0.1";if(a.Length==15)b.ExtrusionMaterialSide=a[14]=="1";
            b.PartialSphereOption=angle>0;if(angle>0){b.PartialSphereAngle.RightHandSide=a[10];b.PartialSphereVector=_part.Directions.CreateDirection(new Point3d(D(a[3]),D(a[4]),D(a[5])),new Vector3d(D(a[11]),D(a[12]),D(a[13])),SmartObject.UpdateOption.WithinModeling);}
            stage="contact_validate";bool valid=b.Validate();if(!valid)throw new Exception("Native analytical contact validation rejected supplied geometry");stage="contact_commit";var obj=b.Commit();
            return Json(new{ok=true,name=obj.Name,type=obj.GetType().Name,validated=valid,sphere_link=b.SphereLink.Value.Name,extrusion_link=b.ExtrusionLink.Value.Name,sheet_geometry=sheetEvidence,radius=ContactExpression(b.SphereRadius),partial_sphere=b.PartialSphereOption,partial_angle=ContactExpression(b.PartialSphereAngle),extrusion_material_side=b.ExtrusionMaterialSide,max_penetration_depth=ContactExpression(b.EffectiveContactDepth),young_modulus=ContactExpression(pb.FirstCustomizedMaterial.YoungModulus),density=ContactExpression(pb.FirstCustomizedMaterial.Density),poisson_ratio=pb.FirstCustomizedMaterial.PoissonRatio.Value,restitution=pb.RestitutionCoefficient.Value,scope="Experimental native sphere-to-extrusion analytical contact; spherical proxy and material assumptions must be declared separately from actual-body collision acceptance. Not a measured force or stress validation."});
        }catch(Exception e){
            if(b!=null){try{b.Destroy();}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}b=null;}
            if(pb!=null){try{pb.Destroy();}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}pb=null;}
            bool rolledBack=false;try{session.UndoToMark(mark,null);rolledBack=true;}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}
            return Json(new{ok=false,error=e.Message,stage=stage,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=rolledBack,cleanup_errors=cleanupErrors});}
        finally{if(b!=null){try{b.Destroy();}catch{}}if(pb!=null){try{pb.Destroy();}catch{}}}
    }
    private static string MotionExportInput(string[] a)
    {
        MechanismPart();if(a.Length!=2 || !_part.FullPath.StartsWith(LabPath(@"models\phase4_finalization_"),StringComparison.OrdinalIgnoreCase))throw new Exception("Export diagnostics require a fresh finalization model and solution name");
        string dir=Path.GetDirectoryName(_part.FullPath),stem=Path.GetFileNameWithoutExtension(_part.FullPath)+"-";
        foreach(string file in Directory.GetFiles(dir))if(Path.GetFileName(file).StartsWith(stem,StringComparison.OrdinalIgnoreCase))throw new Exception("Refusing to replace existing native solver diagnostics");
        var solution=MotionSolutionByName(a[1]);_part.MotionManager.MotionSolutions.SetActiveSolution(solution);solution.ExportSolverInputFile();
        var rows=new List<object>();foreach(string file in Directory.GetFiles(dir)){var info=new FileInfo(file);if(info.Name.StartsWith(stem,StringComparison.OrdinalIgnoreCase))rows.Add(new{path=info.FullName,bytes=info.Length});}
        return Json(new{ok=true,solution=solution.GetName(),files=rows,scope="Actual NX12 solver input export only; no solve or acceptance implied"});
    }
    private static string BodyContactCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(null);
        try{b.Name=a[1];b.ContactType=NXOpen.Motion.BodyContactBuilder.ContactTypeOption.CADToCADContact;
            var c1=MotionComponent(a[2],_part.ComponentAssembly.RootComponent);var c2=MotionComponent(a[3],_part.ComponentAssembly.RootComponent);if(c1==null || c2==null || c1==c2)throw new Exception("Two distinct actual components required");
            foreach(Body body in ((Part)c1.Prototype).Bodies){var occ=c1.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Contact body occurrence unavailable");b.FirstContactGeometry.Add(occ);}
            foreach(Body body in ((Part)c2.Prototype).Bodies){var occ=c2.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Contact body occurrence unavailable");b.SecondContactGeometry.Add(occ);}
            var p=b.LmsParameters;p.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.ProjectedVertex;p.CoulombFrictionOption=NXOpen.Motion.BodyContactLms.CoulombFriction.Off;p.ForceExponent.RightHandSide="1";p.Stiffness.RightHandSide=a[4];p.MaterialDamping.RightHandSide=a[5];p.MaxPenetrationDepth.RightHandSide=a[6];p.ActionDistanceTolerance.RightHandSide="0.001";p.BaseDistanceTolerance.RightHandSide="0.001";p.ActionAngleTolerance.RightHandSide="1";p.BaseAngleTolerance.RightHandSide="1";
            var obj=b.Commit();return Json(new{ok=true,name=obj.Name,type=obj.GetType().Name,first_component=c1.Name,second_component=c2.Name,force_model=p.ForceModel.ToString(),exponent=p.ForceExponent.Value,stiffness=ContactExpression(p.Stiffness),damping=ContactExpression(p.MaterialDamping),max_penetration=ContactExpression(p.MaxPenetrationDepth),friction=p.CoulombFrictionOption.ToString(),scope="Penalty contact; creation is not contact-force or interference acceptance"});
        }finally{b.Destroy();}
    }
    private static string BodyFaceContactCreate(string[] a)
    {
        MechanismPart();if(a.Length!=9)throw new Exception("name component1 face_refs component2 face_refs stiffness damping depth required");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Experimental face contact "+a[1]);
        var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(null);
        try{
            b.Name=a[1];b.ContactType=NXOpen.Motion.BodyContactBuilder.ContactTypeOption.CADToCADContact;
            var c1=P2Component(a[2]);var c2=P2Component(a[4]);if(c1==c2)throw new Exception("Distinct components required");
            var first=a[3].Split(',');var second=a[5].Split(',');
            foreach(string reference in first){var face=P2Geometry(c1,reference) as Face;if(face==null)throw new Exception("Actual face occurrence required");b.FirstContactGeometry.Add(face);}
            foreach(string reference in second){var face=P2Geometry(c2,reference) as Face;if(face==null)throw new Exception("Actual face occurrence required");b.SecondContactGeometry.Add(face);}
            var p=b.LmsParameters;p.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.Midplane;p.CoulombFrictionOption=NXOpen.Motion.BodyContactLms.CoulombFriction.Off;
            p.ForceExponent.RightHandSide="1";p.Stiffness.RightHandSide=a[6];p.MaterialDamping.RightHandSide=a[7];p.MaxPenetrationDepth.RightHandSide=a[8];
            p.ActionDistanceTolerance.RightHandSide="0.001";p.BaseDistanceTolerance.RightHandSide="0.001";p.ActionAngleTolerance.RightHandSide="1";p.BaseAngleTolerance.RightHandSide="1";
            var obj=b.Commit();return Json(new{ok=true,name=obj.Name,first_component=c1.Name,second_component=c2.Name,first_face_references=first,second_face_references=second,force_model=p.ForceModel.ToString(),scope="Experimental actual Face selections; native solver support and full-body collision coverage must be independently verified"});
        }catch(Exception e){session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{b.Destroy();}
    }
    private static string RackPinionCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.Couplers.CreateCouplerRckpnBuilder(null);
        try{b.Name=a[1];b.FirstJoint.Value=MotionJointByName(a[3]);b.SecondJoint.Value=MotionJointByName(a[2]);b.FirstRadiusExpression.RightHandSide=a[4];b.SecondRadiusExpression.RightHandSide=a[4];b.RatioExpression.RightHandSide=(-D(a[4])).ToString(System.Globalization.CultureInfo.InvariantCulture);b.ContactPoint=_part.Points.CreatePoint(new Point3d(D(a[5]),D(a[6]),D(a[7])));var obj=b.Commit();return Json(new{ok=true,name=obj.Name,type=obj.GetType().Name,first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_radius=b.FirstRadiusExpression.Value,second_radius=b.SecondRadiusExpression.Value,ratio=b.RatioExpression.Value,contact=b.ContactPoint.Coordinates});}finally{b.Destroy();}
    }
    private static string MechanismCouplerState()
    {
        MechanismPart();var active=_part.MotionManager.MotionSolutions.GetActiveSolution();var rows=new List<object>();
        foreach(var obj in _part.MotionManager.Couplers.ToArray()){
            var rack=obj as NXOpen.Motion.CouplerRckpn;
            if(rack!=null){var rb=_part.MotionManager.Couplers.CreateCouplerRckpnBuilder(rack);try{rows.Add(new{name=obj.Name,type="RackPinion",first_joint=rb.FirstJoint.Value.Name,second_joint=rb.SecondJoint.Value.Name,ratio=rb.RatioExpression.Value,first_radius=rb.FirstRadiusExpression.Value,second_radius=rb.SecondRadiusExpression.Value,contact_point=rb.ContactPoint.Coordinates,suppressed=active.GetIsSuppressed(obj)});}finally{rb.Destroy();}continue;}
            var gear=obj as NXOpen.Motion.CouplerGear;if(gear==null){rows.Add(new{name=obj.Name,type=obj.GetType().Name,suppressed=active.GetIsSuppressed(obj)});continue;}
            var b=_part.MotionManager.Couplers.CreateCouplerGearBuilder(gear);try{rows.Add(new{name=obj.Name,type="Gear",first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_radius=b.FirstRadiusExpression.Value,second_radius=b.SecondRadiusExpression.Value,contact_point=b.ContactPoint.Coordinates,suppressed=active.GetIsSuppressed(obj)});}finally{b.Destroy();}
        }
        foreach(var obj in _part.MotionManager.GeneralCouplers.ToArray()){var b=_part.MotionManager.GeneralCouplers.CreateGeneralCouplerBuilder(obj);try{rows.Add(new{name=obj.Name,type="General",attachment=b.Attachment.ToString(),first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_type=b.FirstType.ToString(),second_type=b.SecondType.ToString(),first_scale=b.FirstScaleExpression.Value,second_scale=b.SecondScaleExpression.Value,suppressed=active.GetIsSuppressed(obj)});}finally{b.Destroy();}}
        var sb=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(active);try{return Json(new{ok=true,active_solution=active.GetName(),couplers=rows,assembly_tolerance=sb.LmsSolverProperty.AssemblyTolerance.Value,kinematic_tolerance=sb.LmsSolverProperty.KinematicSolutionTolerance.Value,gruebler_dof=active.CalculateGrueblerCount()});}finally{sb.Destroy();}
    }
    private static string GeneralCouplerCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.GeneralCouplers.CreateGeneralCouplerBuilder(null);
        try{b.Name=a[1];b.Attachment=NXOpen.Motion.GeneralCouplerBuilder.AttachmentType.Two;b.FirstJoint.Value=MotionJointByName(a[2]);b.SecondJoint.Value=MotionJointByName(a[3]);b.FirstType=NXOpen.Motion.GcouplerMovementTypes.Rotation;b.SecondType=NXOpen.Motion.GcouplerMovementTypes.Rotation;b.FirstScaleExpression.RightHandSide=a[4];b.SecondScaleExpression.RightHandSide=a[5];var obj=b.Commit();return Json(new{ok=true,name=obj.Name,first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_scale=b.FirstScaleExpression.Value,second_scale=b.SecondScaleExpression.Value});}finally{b.Destroy();}
    }
    private static string SphereSolid(string[] a)
    {
        MechanismPart();double diameter=D(a[5]);if(diameter<=0 || diameter>10000)throw new Exception("Positive bounded diameter required");var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Mechanism sphere");
        try{var b=_part.Features.CreateSphereBuilder(null);try{b.Type=NXOpen.Features.SphereBuilder.Types.CenterPointAndDiameter;b.CenterPoint=_part.Points.CreatePoint(new Point3d(D(a[2]),D(a[3]),D(a[4])));b.Diameter.RightHandSide=a[5];if(a.Length>7 && a[7]!="-"){b.BooleanOption.Type=a[6]=="subtract"?BooleanOperation.BooleanType.Subtract:BooleanOperation.BooleanType.Unite;b.BooleanOption.SetTargetBodies(new Body[]{GetBody(a[7])});}var feature=(NXOpen.Features.BodyFeature)b.CommitFeature();feature.SetName(a[1]);string id="BODY_"+(++_bodyCounter);_bodies[id]=feature.GetBodies()[0];return Json(new{ok=true,body_id=id,feature=feature.GetFeatureName(),diameter=diameter});}finally{b.Destroy();}}
        catch(Exception e){Session.GetSession().UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
    }
    private static string JointReference(string[] a)
    {
        MechanismPart();var p=new Point3d(D(a[2]),D(a[3]),D(a[4]));var axis=P3Unit(new Vector3d(D(a[5]),D(a[6]),D(a[7])));var point=_part.Points.CreatePoint(p);point.SetName(a[1]+"_P");point.SetVisibility(SmartObject.VisibilityOption.Visible);var line=_part.Curves.CreateLine(p,new Point3d(p.X+10*axis.X,p.Y+10*axis.Y,p.Z+10*axis.Z));line.SetName(a[1]+"_A");return Json(new{ok=true,point_name=point.Name,curve_name=line.Name,origin=p,axis=axis});
    }
    private static string ComponentDatums(string[] a)
    {
        var component=P2Component(a[1]);var part=(Part)component.Prototype;var points=new List<object>();var curves=new List<object>();foreach(Point p in part.Points)if(!String.IsNullOrEmpty(p.Name))points.Add(new{name=p.Name,coordinates=p.Coordinates,occurrence_available=component.FindOccurrence(p)!=null});foreach(Curve c in part.Curves)if(!String.IsNullOrEmpty(c.Name))curves.Add(new{name=c.Name,type=c.GetType().Name,occurrence_available=component.FindOccurrence(c)!=null});return Json(new{ok=true,component=component.Name,prototype_points=points,prototype_curves=curves});
    }
    private static string UniversalJointCreate(string[] a)
    {
        MechanismPart();var p=new Point3d(D(a[4]),D(a[5]),D(a[6]));var v=P3Unit(new Vector3d(D(a[7]),D(a[8]),D(a[9])));var w=P3Unit(new Vector3d(D(a[10]),D(a[11]),D(a[12])));if(Math.Abs(v.X*w.X+v.Y*w.Y+v.Z*w.Z)>1e-8)throw new Exception("Universal cross-pin axes must be orthogonal");var b=_part.MotionManager.Joints.CreateJointBuilder(null);
        try{var d=b.JointDefine;d.Name=a[1];d.JointType=NXOpen.Motion.JointDefine.Type.Universal;d.AxisType=NXOpen.Motion.JointDefine.UniversalJointAxisType.CrossPin;d.FirstLink=MotionLinkByName(a[2]);d.SecondLink=MotionLinkByName(a[3]);d.FirstOrigin=_part.Points.CreatePoint(p);d.SecondOrigin=_part.Points.CreatePoint(p);d.FirstOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;d.SecondOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;d.FirstVector=_part.Directions.CreateDirection(p,v,SmartObject.UpdateOption.WithinModeling);d.SecondVector=_part.Directions.CreateDirection(p,w,SmartObject.UpdateOption.WithinModeling);d.SnapLinks=false;var obj=b.Commit();return Json(new{ok=true,name=obj.Name,type=d.JointType.ToString(),axis_type=d.AxisType.ToString(),first_axis=d.FirstVector.Vector,second_axis=d.SecondVector.Vector,origin=p});}finally{b.Destroy();}
    }
    private static object DriverOperationInfo(string coordinate,NXOpen.Motion.DriverOperation d)
    {
        if(d.TypeOption==NXOpen.Motion.DriverOperation.Type.Undefined)return new{coordinate=coordinate,type="Undefined"};
        return new {coordinate=coordinate,type=d.TypeOption.ToString(),integration=d.IntegrationTypeOption.ToString(),displacement=d.DisplacementExpression.RightHandSide,velocity=d.VelocityExpression.RightHandSide,velocity_unit=d.VelocityExpression.Units==null?null:d.VelocityExpression.Units.Name,harmonic_offset=d.HarmonicDisplacementExpression.RightHandSide,amplitude=d.AmplitudeExpression.RightHandSide,amplitude_unit=d.AmplitudeExpression.Units==null?null:d.AmplitudeExpression.Units.Name,frequency=d.FrequencyExpression.RightHandSide,frequency_unit=d.FrequencyExpression.Units==null?null:d.FrequencyExpression.Units.Name,phase=d.PhaseAngleExpression.RightHandSide,phase_unit=d.PhaseAngleExpression.Units==null?null:d.PhaseAngleExpression.Units.Name};
    }
    private static string HarmonicDriverCreate(string[] a)
    {
        MechanismPart();if(a.Length!=8 || (a[3]!="revolute" && a[3]!="slider") || !(D(a[4])>0) || !(D(a[5])>0))throw new Exception("name joint revolute|slider positive_amplitude positive_frequency phase offset required");
        var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(null);
        try{b.Name=a[1];b.Joint.Value=MotionJointByName(a[2]);var d=a[3]=="slider"?b.DriverMultiOperations.MotionTranslationZ:b.DriverMultiOperations.MotionEulerAngle1;d.TypeOption=NXOpen.Motion.DriverOperation.Type.Harmonic;d.IntegrationTypeOption=NXOpen.Motion.DriverOperation.IntegrationType.Displacement;d.AmplitudeExpression.RightHandSide=a[4];d.FrequencyExpression.RightHandSide=a[5];d.PhaseAngleExpression.RightHandSide=a[6];d.HarmonicDisplacementExpression.RightHandSide=a[7];var obj=b.Commit();return Json(new{ok=true,name=obj.Name,operation=DriverOperationInfo(a[3],d),note="Native harmonic input; validate expression units and actual result frequency before acceptance"});}finally{b.Destroy();}
    }
    private static string ResearchProfileDriver(string[] a)
    {
        MechanismPart();
        if((a.Length!=4 && a.Length!=5) || !_part.FullPath.StartsWith(LabPath(@"models\phase4_research_"),StringComparison.OrdinalIgnoreCase))throw new Exception("Research profile driver requires name, joint, local CSV and optional linear|akima in a research model");
        if(a.Length==5 && a[4]!="linear" && a[4]!="akima")throw new Exception("Only native linear or Akima interpolation supported by this experiment");
        string file=Path.GetFullPath(a[3]);if(Path.GetDirectoryName(file)!=Path.GetDirectoryName(_part.FullPath) || Path.GetExtension(file)!=".csv")throw new Exception("Profile CSV must be beside the current simulation");
        var values=new List<double>();double last=-1;
        foreach(string line in File.ReadAllLines(file)){if(String.IsNullOrWhiteSpace(line))continue;var cells=line.Split(',');if(cells.Length!=2)throw new Exception("time,angle_deg rows required");double t=D(cells[0]),q=D(cells[1]);if(Double.IsNaN(t)||Double.IsInfinity(t)||Double.IsNaN(q)||Double.IsInfinity(q)||t<=last||t<0||Math.Abs(q)>10000)throw new Exception("Invalid bounded profile");last=t;values.Add(t);values.Add(q);}
        if(values.Count<6 || values.Count>200002 || values[0]!=0)throw new Exception("3..100001 samples starting at zero required");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Research ideal profile");NXOpen.Motion.JointDriverBuilder builder=null;
        try{
            var timeUnit=_part.UnitCollection.FindObject("Second");var angleUnit=_part.UnitCollection.FindObject("Degrees");
            var table=_part.FieldManager.CreateFieldTableFromData(a[1]+"_Table",timeUnit,angleUnit,NXOpen.Fields.FieldVariable.ValueType.Real,values.ToArray());table.InterpolationMethod=a.Length==5 && a[4]=="akima"?NXOpen.Fields.FieldEvaluator.InterpolationEnum.Akima1d:NXOpen.Fields.FieldEvaluator.InterpolationEnum.Linear1d;
            var field=_part.MotionManager.FieldDatas.CreateFieldData(table);
            builder=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(null);builder.Name=a[1];builder.Joint.Value=MotionJointByName(a[2]);var driver=builder.DriverMultiOperations.MotionEulerAngle1;
            driver.TypeOption=NXOpen.Motion.DriverOperation.Type.Profile;driver.IntegrationTypeOption=NXOpen.Motion.DriverOperation.IntegrationType.Displacement;driver.Profile.Value=field;
            bool valid=builder.Validate();if(!valid)throw new Exception("Native profile builder rejected input");var obj=builder.Commit();
            return Json(new{ok=true,name=obj.Name,validated=valid,type=driver.TypeOption.ToString(),joint=a[2],samples=values.Count/2,time_unit=timeUnit.TypeName,angle_unit=angleUnit.TypeName,interpolation=table.InterpolationMethod.ToString(),profile_type=driver.Profile.Value.GetType().FullName,scope="Prescribed ideal kinematic profile. No unilateral contact or force validation. Must cross-check actual native result independently."});
        }catch(Exception e){if(builder!=null){try{builder.Destroy();}catch{}builder=null;}session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{if(builder!=null)builder.Destroy();}
    }
    private static string ResearchFaceNormal(string[] a)
    {
        MechanismPart();if(a.Length!=6 || !_part.FullPath.StartsWith(LabPath(@"models\phase4_research_"),StringComparison.OrdinalIgnoreCase))throw new Exception("Read-only research face normal: component faceRef x y z");
        var face=P2Geometry(P2Component(a[1]),a[2]) as Face;if(face==null)throw new Exception("Face required");
        var uf=NXOpen.UF.UFSession.GetUFSession();double[] given={D(a[3]),D(a[4]),D(a[5])},uv=new double[2],nearest=new double[3],point=new double[3],u1=new double[3],v1=new double[3],u2=new double[3],v2=new double[3],normal=new double[3],radii=new double[2];
        uf.Modl.AskFaceParm(face.Tag,given,uv,nearest);uf.Modl.AskFaceProps(face.Tag,uv,point,u1,v1,u2,v2,normal,radii);
        double[] plus=new double[3],minus=new double[3];for(int i=0;i<3;i++){plus[i]=point[i]+0.001*normal[i];minus[i]=point[i]-0.001*normal[i];}
        int on,positive,negative;Tag body=face.GetBody().Tag;uf.Modl.AskPointContainment(point,body,out on);uf.Modl.AskPointContainment(plus,body,out positive);uf.Modl.AskPointContainment(minus,body,out negative);
        return Json(new{ok=true,read_only=true,component=a[1],reference=a[2],requested_point=given,surface_point=point,unit_face_normal=normal,u_derivative=u1,v_derivative=v1,point_on_body=on,plus_normal_point=plus,plus_normal_containment=positive,minus_normal_point=minus,minus_normal_containment=negative,containment_codes="1 inside; 2 outside; 3 on body",source="NX12 UF_MODL_ask_face_props and UF_MODL_ask_point_containment; no model mutation"});
    }
    private static string ResearchAnalyticalState()
    {
        MechanismPart();var rows=new List<object>();
        foreach(var contact in _part.MotionManager.AnalyticalContacts.ToArray()){
            var b=_part.MotionManager.AnalyticalContacts.CreateAnalyticalContactBuilder(contact);
            try{rows.Add(new{name=contact.Name,type=b.ContactType.ToString(),sphere_link=b.SphereLink.Value.Name,extrusion_link=b.ExtrusionLink.Value.Name,sphere_point=b.SpherePoint.Coordinates,sphere_radius=ContactExpression(b.SphereRadius),material_side=b.ExtrusionMaterialSide,partial_sphere=b.PartialSphereOption,partial_angle=ContactExpression(b.PartialSphereAngle),find_all_roots=b.FindAllRoots,max_penetration_depth=ContactExpression(b.EffectiveContactDepth),infinite_extrusion=b.InfiniteExtrusion,geometry_from_file=b.ExtrusionGeometryData,geometry_object_count=b.ExtrusionBodies.GetArray().Length});}
            finally{b.Destroy();}
        }
        return Json(new{ok=true,contacts=rows,scope="Read actual native analytical contact definitions; not force/normal convergence or acceptance"});
    }
    private static string MotionDriverDetails()
    {
        MechanismPart();var rows=new List<object>();foreach(var driver in _part.MotionManager.JointDrivers.ToArray()){var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(driver);try{rows.Add(new{name=driver.Name,joint=((NXObject)b.Joint.Value).Name,rotational=DriverOperationInfo("rotation",b.DriverMultiOperations.MotionEulerAngle1),translational=DriverOperationInfo("translation",b.DriverMultiOperations.MotionTranslationZ)});}finally{b.Destroy();}}return Json(new{ok=true,drivers=rows});
    }
    private static string MotionSuppression(string[] a)
    {
        MechanismPart();NXObject obj=null;foreach(var x in _part.MotionManager.Couplers.ToArray())if(x.Name==a[2])obj=x;foreach(var x in _part.MotionManager.JointDrivers.ToArray())if(x.Name==a[2])obj=x;if(obj==null)throw new Exception("Named coupler or driver not found");
        var solution=MotionSolutionByName(a[1]);if(a[3]=="1")solution.AddSuppressedObject(obj);else solution.RemoveSuppressedObject(obj);
        return Json(new{ok=true,solution=solution.GetName(),object_name=obj.Name,suppressed=solution.GetIsSuppressed(obj),gruebler_dof=solution.CalculateGrueblerCount()});
    }

    // NX12-native surface experiments. Inputs and output parts stay in fresh lab models.
    private static string SurfaceLoft(string[] a)
    {
        MechanismPart();
        if(a.Length!=4 || (a[3]!="solid" && a[3]!="sheet"))throw new Exception("nx_surface_loft NAME CSV solid|sheet");
        string file=Path.GetFullPath(a[2]);SafePath(file);
        if(!String.Equals(Path.GetDirectoryName(file),Path.GetDirectoryName(_part.FullPath),StringComparison.OrdinalIgnoreCase))throw new Exception("Sections must be beside current part");
        string[] rows=File.ReadAllLines(file);if(rows.Length<2 || rows.Length>32)throw new Exception("Expected 2..32 sections");
        var data=new List<double[]>();double lastX=Double.NegativeInfinity;
        foreach(string row in rows){string[] cells=row.Split(',');if(cells.Length!=5)throw new Exception("Each row is X,Y,Z,RY,RZ");var values=new double[5];for(int i=0;i<5;i++){values[i]=D(cells[i]);if(Double.IsNaN(values[i])||Double.IsInfinity(values[i])||Math.Abs(values[i])>10000)throw new Exception("Invalid section value");}if(values[0]<=lastX || values[3]<values[4] || values[4]<=0)throw new Exception("Increasing X and RY>=RZ>0 required");lastX=values[0];data.Add(values);}
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Surface loft "+a[1]);
        NXOpen.Features.ThroughCurvesBuilder builder=null;
        try {
            builder=_part.Features.CreateThroughCurvesBuilder(null);
            builder.BodyPreference=a[3]=="solid"?NXOpen.Features.ThroughCurvesBuilder.BodyPreferenceTypes.Solid:NXOpen.Features.ThroughCurvesBuilder.BodyPreferenceTypes.Sheet;
            builder.PatchType=NXOpen.Features.ThroughCurvesBuilder.PatchTypes.Single;
            builder.PositionTolerance=0.001;
            var curveTags=new List<string>();
            foreach(double[] row in data){
                var origin=new Point3d(row[0],row[1],row[2]);
                var curve=_part.Curves.CreateEllipse(origin,new Vector3d(0,1,0),new Vector3d(0,0,1),row[3],row[4],0,2*Math.PI);
                curve.SetName(a[1]+"_section_"+curveTags.Count);curveTags.Add(curve.Tag.ToString());
                var section=_part.Sections.CreateSection();
                section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(new Curve[]{curve})},curve,null,null,new Point3d(row[0],row[1]+row[3],row[2]),Section.Mode.Create,false);
                builder.SectionsList.Append(section);
            }
            var feature=(BodyFeature)builder.CommitFeature();feature.SetName(a[1]);
            Body[] bodies=feature.GetBodies();if(bodies.Length!=1)throw new Exception("Expected exactly one loft body");
            if(bodies[0].IsSolidBody!=(a[3]=="solid"))throw new Exception("NX output body type differs from requested type");
            string id="BODY_"+(++_bodyCounter);_bodies[id]=bodies[0];
            return Json(new {ok=true,body_id=id,feature=feature.GetFeatureName(),solid=bodies[0].IsSolidBody,sections=curveTags,position_tolerance_mm=0.001,definition="Native through-curves feature with ellipse sections; dimensions are CSV-defined, not sketch expressions"});
        }catch(Exception e){if(builder!=null){builder.Destroy();builder=null;}session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{if(builder!=null)builder.Destroy();}
    }
    private static string SurfaceShell(string[] a)
    {
        MechanismPart();if(a.Length!=4 || (a[3]!="xmin" && a[3]!="both"))throw new Exception("nx_surface_shell BODY_ID THICKNESS xmin|both");
        Body body=GetBody(a[1]);double thickness=D(a[2]);if(!body.IsSolidBody || Double.IsNaN(thickness)||Double.IsInfinity(thickness)||thickness<=0)throw new Exception("Solid body and finite positive thickness required");
        var uf=NXOpen.UF.UFSession.GetUFSession();double[] box=new double[6];uf.Modl.AskBoundingBox(body.Tag,box);
        var selected=new List<Face>();var selectedTags=new List<string>();
        foreach(Face face in body.GetFaces()){
            if(face.SolidFaceType!=Face.FaceType.Planar)continue;
            int type,norm;double radius,radData;var p=new double[3];var d=new double[3];var fb=new double[6];
            uf.Modl.AskFaceData(face.Tag,out type,p,d,fb,out radius,out radData,out norm);
            if(Math.Abs(d[0])>0.999999 && (Math.Abs(p[0]-box[0])<0.001 || (a[3]=="both" && Math.Abs(p[0]-box[3])<0.001))){selected.Add(face);selectedTags.Add(face.Tag.ToString());}
        }
        if(selected.Count!=(a[3]=="both"?2:1))throw new Exception("Cannot unambiguously identify requested planar end faces");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Surface shell");NXOpen.Features.ShellBuilder builder=null;
        try {
            builder=_part.Features.CreateShellBuilder(null);builder.Body=body;
            bool initialThicknessNull=builder.DefaultThickness==null,initialCollectorNull=builder.RemovedFacesCollector==null;
            builder.SetDefaultThickness(a[2]);builder.DefaultThicknessFlip=false;builder.Tolerance=0.001;builder.UseSurfaceApproximation=false;
            if(builder.RemovedFacesCollector==null)builder.RemovedFacesCollector=_part.ScCollectors.CreateCollector();
            builder.RemovedFacesCollector.ReplaceRules(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleFaceDumb(selected.ToArray())},false);
            var feature=(BodyFeature)builder.CommitFeature();feature.SetName("surface_shell");Body[] bodies=feature.GetBodies();
            if(bodies.Length!=1 || !bodies[0].IsSolidBody)throw new Exception("Shell did not produce one solid");
            string id="BODY_"+(++_bodyCounter);_bodies[id]=bodies[0];
            return Json(new{ok=true,body_id=id,feature=feature.GetFeatureName(),thickness_mm=thickness,removed_faces=selectedTags,tolerance_mm=0.001,surface_approximation=false,initial_thickness_null=initialThicknessNull,initial_collector_null=initialCollectorNull});
        }catch(Exception e){if(builder!=null){builder.Destroy();builder=null;}session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{if(builder!=null)builder.Destroy();}
    }
    private static string SurfaceStep(string[] a)
    {
        MechanismPart();if(a.Length!=2)throw new Exception("nx_export_step_precise NEW_PATH");SafePath(a[1]);if(File.Exists(a[1]))throw new IOException("Refusing STEP overwrite");
        string settingsPath=a[1]+".def";if(File.Exists(settingsPath))throw new IOException("Refusing export settings overwrite");
        string defaultSettings=File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("UGII_BASE_DIR"),"STEP214UG","ugstep214.def"));
        if(!defaultSettings.Contains("PA_EDGE_FACE_BSPLINE_APPROX_TOL = 0.0254"))throw new Exception("Unexpected NX export defaults; inspect before adapting");
        File.WriteAllText(settingsPath,defaultSettings.Replace("PA_EDGE_FACE_BSPLINE_APPROX_TOL = 0.0254","PA_EDGE_FACE_BSPLINE_APPROX_TOL = 0.00001"));
        var creator=Session.GetSession().DexManager.CreateStep214Creator();
        try {
            creator.SettingsFile=settingsPath;
            creator.OutputFile=a[1];creator.InputFile=_part.FullPath;creator.ObjectTypes.Solids=true;
            creator.BsplineTol=0.00001;creator.ProcessHoldFlag=true;creator.Commit();
            return Json(new{ok=true,step=a[1],requested_bspline_approximation_mm=0.00001,settings_file=settingsPath,scope="Native AP214 export; verify effective tolerance in translator log and compare readback separately"});
        }finally{creator.Destroy();}
    }
    private static string SurfaceState()
    {
        if(_part==null)throw new Exception("No active part");var uf=NXOpen.UF.UFSession.GetUFSession();var bodies=new List<object>();
        foreach(Body body in _part.Bodies){int count;int[] faults;Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out count,out faults,out bad);var box=new double[6];uf.Modl.AskBoundingBox(body.Tag,box);bodies.Add(new{tag=body.Tag.ToString(),solid=body.IsSolidBody,blanked=body.IsBlanked,faces=body.GetFaces().Length,edges=body.GetEdges().Length,consistency_fault_count=count,faults=faults,bbox_mm=box});}
        var features=new List<object>();foreach(Feature f in _part.Features)features.Add(new{name=f.GetFeatureName(),type=f.FeatureType,errors=f.GetFeatureErrorMessages(),warnings=f.GetFeatureWarningMessages()});
        var curves=new List<object>();foreach(Curve c in _part.Curves)curves.Add(new{tag=c.Tag.ToString(),name=c.Name,type=c.GetType().FullName,is_occurrence=c.IsOccurrence});
        return Json(new{ok=true,part=_part.FullPath,bodies=bodies,features=features,curves=curves,scope="Native sheet/solid topology and consistency; sheet volume is not reported"});
    }
    private static string SurfaceSew(string[] a)
    {
        MechanismPart();if(a.Length!=2)throw new Exception("nx_surface_sew BODY_ID_CSV");var ids=a[1].Split(',');if(ids.Length<2 || ids.Length>16)throw new Exception("Expected 2..16 sheets");var bodies=new List<Body>();foreach(string id in ids){Body body=GetBody(id);if(body.IsSolidBody || bodies.Contains(body))throw new Exception("Distinct sheets required");bodies.Add(body);}
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Surface sew");NXOpen.Features.SewBuilder builder=null;
        try{builder=_part.Features.CreateSewBuilder(null);builder.Type=NXOpen.Features.SewBuilder.Types.Sheet;builder.BodyPreference=NXOpen.Features.SewBuilder.BodyPreferenceTypes.Sheet;builder.Tolerance=0.001;builder.TargetBodies.Add(bodies[0]);for(int i=1;i<bodies.Count;i++)builder.ToolBodies.Add(bodies[i]);var f=(BodyFeature)builder.CommitFeature();f.SetName("surface_sew");var result=f.GetBodies();if(result.Length!=1)throw new Exception("Sew did not produce exactly one body");string id="BODY_"+(++_bodyCounter);_bodies[id]=result[0];return Json(new{ok=true,body_id=id,feature=f.GetFeatureName(),solid=result[0].IsSolidBody,tolerance_mm=0.001,input_sheets=bodies.Count});}
        catch(Exception e){if(builder!=null){builder.Destroy();builder=null;}session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{if(builder!=null)builder.Destroy();}
    }
    private static string SurfaceThicken(string[] a)
    {
        MechanismPart();if(a.Length!=3)throw new Exception("nx_surface_thicken SHEET_BODY_ID THICKNESS");Body body=GetBody(a[1]);double thickness=D(a[2]);if(body.IsSolidBody || Double.IsNaN(thickness)||Double.IsInfinity(thickness)||thickness<=0)throw new Exception("Sheet and positive finite thickness required");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Surface thicken");NXOpen.Features.ThickenBuilder builder=null;
        try{builder=_part.Features.CreateThickenBuilder(null);builder.FaceCollector.ReplaceRules(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleFaceDumb(body.GetFaces())},false);builder.FirstOffset.RightHandSide=a[2];builder.SecondOffset.RightHandSide="0";builder.Tolerance=0.001;builder.BooleanOperation.Type=BooleanOperation.BooleanType.Create;var f=(BodyFeature)builder.CommitFeature();f.SetName("surface_thicken");var bodies=f.GetBodies();if(bodies.Length!=1 || !bodies[0].IsSolidBody)throw new Exception("Expected one thickened solid");string id="BODY_"+(++_bodyCounter);_bodies[id]=bodies[0];return Json(new{ok=true,body_id=id,feature=f.GetFeatureName(),thickness_mm=thickness,tolerance_mm=0.001,offset_note="NX12 internally uses approximate offset; no exact-offset claim"});}
        catch(Exception e){if(builder!=null){builder.Destroy();builder=null;}session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{if(builder!=null)builder.Destroy();}
    }

// Append inside the existing loader class. Source candidate; not native acceptance.
private static void SweepToken(string value)
{
    if(!System.Text.RegularExpressions.Regex.IsMatch(value,@"^[A-Za-z][A-Za-z0-9_]{0,63}$"))
        throw new Exception("Expected ASCII identifier, 1..64 characters");
}
private static void SweepPart()
{
    MechanismPart();
    if(_part.PartUnits!=BasePart.Units.Millimeters || String.IsNullOrEmpty(_part.FullPath))
        throw new Exception("Saved-path millimeter experiment part required");
    SafePath(_part.FullPath);
    if(!Path.GetFileNameWithoutExtension(_part.FullPath).StartsWith("phase4_sweep_",StringComparison.Ordinal))
        throw new Exception("Dedicated NEW phase4_sweep_* experiment part required");
}
private static Curve SweepCurve(string name)
{
    SweepToken(name); Curve found=null;
    foreach(Curve c in _part.Curves) if(String.Equals(c.Name,name,StringComparison.OrdinalIgnoreCase)){
        if(found!=null)throw new Exception("Ambiguous curve name: "+name);found=c;
    }
    if(found==null || found.IsOccurrence)throw new Exception("Unique local curve required: "+name);
    return found;
}
private static void SweepUnused(string name)
{
    SweepToken(name);
    foreach(Curve c in _part.Curves)if(String.Equals(c.Name,name,StringComparison.OrdinalIgnoreCase))throw new Exception("Curve name collision: "+name);
    foreach(Feature f in _part.Features)if(String.Equals(f.Name,name,StringComparison.OrdinalIgnoreCase) || String.Equals(f.GetFeatureName(),name,StringComparison.OrdinalIgnoreCase))throw new Exception("Feature name collision: "+name);
    foreach(Body b in _part.Bodies)if(String.Equals(b.Name,name,StringComparison.OrdinalIgnoreCase))throw new Exception("Body name collision: "+name);
}
private static double SweepDot(Vector3d a,Vector3d b){return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
private static Vector3d SweepDelta(Point3d a,Point3d b){return new Vector3d(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
private static Vector3d SweepUnit(Vector3d v)
{
    double n=Math.Sqrt(SweepDot(v,v));
    if(Double.IsNaN(n)||Double.IsInfinity(n)||n<1e-10)throw new Exception("Invalid curve tangent");
    return new Vector3d(v.X/n,v.Y/n,v.Z/n);
}
private static void SweepEval(Curve c,double fraction,out Point3d p,out Vector3d tangent)
{
    var ev=NXOpen.UF.UFSession.GetUFSession().Eval;IntPtr handle=IntPtr.Zero;
    try{
        ev.Initialize(c.Tag,out handle);var limits=new double[2];ev.AskLimits(handle,limits);
        if(Double.IsNaN(limits[0])||Double.IsNaN(limits[1])||Double.IsInfinity(limits[0])||Double.IsInfinity(limits[1])||limits[1]<=limits[0])
            throw new Exception("Invalid curve parameter limits");
        var xyz=new double[3];var d=new double[3];
        ev.Evaluate(handle,1,limits[0]+fraction*(limits[1]-limits[0]),xyz,d);
        foreach(double x in xyz)if(Double.IsNaN(x)||Double.IsInfinity(x)||Math.Abs(x)>10000)throw new Exception("Curve outside experiment bounds");
        p=new Point3d(xyz[0],xyz[1],xyz[2]);tangent=SweepUnit(new Vector3d(d[0],d[1],d[2]));
    }finally{if(handle!=IntPtr.Zero)ev.Free(handle);}
}
private static void SweepSection(Section section,Curve curve,Point3d point,Vector3d direction,
    bool closed,string role,ref string phase)
{
    phase=role+".allow_self_intersection";
    section.AllowSelfIntersection(false);
    phase=role+".add_to_section";
    section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(new Curve[]{curve})},
        curve,null,null,point,Section.Mode.Create,false);
    // NX12 Section direction is selected geometrically, not by a parent-curve identity.
    phase=role+".align_direction";
    section.AlignDirectionOfLoop(point,direction);
    phase=role+".read_direction";
    int loop=section.GetLoopIndex(point,0.001);
    Point3d actualStart;Vector3d actualDirection;
    section.GetStartAndDirectionOfLoop(loop,out actualStart,out actualDirection);
    actualDirection=SweepUnit(actualDirection);
    // A closed one-curve profile has no unique endpoint. Its seam is NX-owned.
    // The open profile/guide must begin at the requested endpoint and follow its tangent.
    if(!closed){
        Vector3d delta=SweepDelta(actualStart,point);double distance2=SweepDot(delta,delta);
        double alignment=SweepDot(actualDirection,SweepUnit(direction));
        if(Double.IsNaN(distance2)||Double.IsInfinity(distance2)||distance2>1e-6 ||
            Double.IsNaN(alignment)||Double.IsInfinity(alignment)||alignment<1-1e-8)
            throw new Exception("NX section start/direction differs from requested open-curve traversal");
    }
}
private static string SweepFailure(Exception error,Session session,Session.UndoMarkId mark,
    string phase="fixture",string context=null,string cleanupError=null)
{
    bool rolledBack=false;string rollbackError=null;
    try{session.UndoToMark(mark,null);rolledBack=true;}catch(Exception e){rollbackError=e.Message;}
    return Json(new{ok=false,error=error.Message,nx_error_code=error is NXException?((NXException)error).ErrorCode:0,
        stage=phase,context=context,cleanup_error=cleanupError,
        rolled_back=rolledBack,rollback_error=rollbackError,safe_pause_required=!rolledBack || cleanupError!=null});
}
private static string SurfaceSweep(string[] a)
{
    SweepPart();
    if(a.Length!=6 || (a[4]!="solid"&&a[4]!="sheet") || (a[5]!="forward"&&a[5]!="reverse"))
        throw new Exception("nx_surface_sweep NAME SECTION_CURVE_NAME GUIDE_CURVE_NAME solid|sheet forward|reverse");
    SweepUnused(a[1]);Curve profile=SweepCurve(a[2]),guide=SweepCurve(a[3]);
    if(profile.Tag==guide.Tag)throw new Exception("Section and guide must be distinct");
    if(!(profile is Ellipse)&&!(profile is Arc))throw new Exception("Only ellipse/circular-arc sections supported");
    if(!(guide is Arc)&&!(guide is Line))throw new Exception("Only open circular arc/line guides supported");
    var conic=profile as Conic;if(conic==null)throw new Exception("Section must expose native conic placement");
    double begin,end,radius;
    if(profile is Ellipse){var e=(Ellipse)profile;begin=e.StartAngle;end=e.EndAngle;radius=e.MajorRadius;if(!(e.MinorRadius>0.001))throw new Exception("Section too small");}
    else{var c=(Arc)profile;begin=c.StartAngle;end=c.EndAngle;radius=c.Radius;}
    double span=end-begin;
    if(Double.IsNaN(span)||Double.IsInfinity(span)||span<0.01||span>2*Math.PI+1e-8||!(radius>0.001&&radius<1000))
        throw new Exception("Invalid section dimensions/span");
    bool closed=Math.Abs(span-2*Math.PI)<1e-8;
    if(a[4]=="solid"&&!closed)throw new Exception("Solid requires closed section");
    if(guide is Arc){var g=(Arc)guide;
        if(!(g.EndAngle-g.StartAngle>0.01&&g.EndAngle-g.StartAngle<=Math.PI&&g.Radius>4*radius&&g.Radius<10000))
            throw new Exception("Arc guide needs span <=180deg and radius >4x section radius");
    }
    Point3d p0,p1,start,profilePoint;Vector3d t0,t1,direction,profileTangent;
    SweepEval(guide,0,out p0,out t0);SweepEval(guide,1,out p1,out t1);
    if(SweepDot(SweepDelta(p1,p0),SweepDelta(p1,p0))<0.01)throw new Exception("Open nondegenerate guide required");
    bool reverse=a[5]=="reverse";start=reverse?p1:p0;direction=reverse?new Vector3d(-t1.X,-t1.Y,-t1.Z):t0;
    Vector3d offset=SweepDelta(conic.CenterPoint,start);
    double offset2=SweepDot(offset,offset);if(Double.IsNaN(offset2)||Double.IsInfinity(offset2))throw new Exception("Invalid section center");
    var m=conic.Matrix.Element;Vector3d normal=SweepUnit(new Vector3d(m.Zx,m.Zy,m.Zz));
    if(SweepDot(offset,offset)>1e-6 || Math.Abs(SweepDot(normal,direction))<1-1e-8)
        throw new Exception("Place section center at selected guide endpoint and its plane normal along guide tangent");
    // Avoid the coincident parameter endpoints of a full ellipse/circle.
    SweepEval(profile,closed?0.5:0,out profilePoint,out profileTangent);
    var oldBodies=new System.Collections.Generic.HashSet<Tag>();foreach(Body body in _part.Bodies)oldBodies.Add(body.Tag);
    var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Sweep "+a[1]);
    NXOpen.Features.SweepAlongGuideBuilder builder=null;string registered=null;
    string phase="builder.configure";
    string context="feature="+a[1]+";section="+profile.Name+";section_tag="+profile.Tag.ToString()+
        ";guide="+guide.Name+";guide_tag="+guide.Tag.ToString()+";body_type="+a[4]+";direction="+a[5];
    try{
        builder=_part.Features.CreateSweepAlongGuideBuilder(null);
        builder.DistanceTolerance=0.001;builder.ChainingTolerance=0.0005;
        builder.BooleanOperation.Type=BooleanOperation.BooleanType.Create;
        builder.FeatureOptions.BodyType=a[4]=="solid"?NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Solid:NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Sheet;
        builder.FirstOffset.RightHandSide="0";builder.SecondOffset.RightHandSide="0";
        phase="section.access";
        SweepSection(builder.Section,profile,profilePoint,profileTangent,closed,"section",ref phase);
        phase="guide.access";
        SweepSection(builder.Guide,guide,start,direction,false,"guide",ref phase);
        phase="commit";
        var feature=builder.CommitFeature() as BodyFeature;
        phase="post_commit.validation";
        if(feature==null || !(feature is NXOpen.Features.SweepAlongGuide))throw new Exception("Expected native SweepAlongGuide feature");
        feature.SetName(a[1]);Body[] bodies=feature.GetBodies();
        if(bodies.Length!=1 || bodies[0].IsSolidBody!=(a[4]=="solid") || oldBodies.Contains(bodies[0].Tag) || bodies[0].GetFaces().Length==0)
            throw new Exception("Expected one new body of requested type");
        if(feature.GetFeatureErrorMessages().Length!=0 || feature.GetFeatureWarningMessages().Length!=0)
            throw new Exception("Native feature reported errors or warnings");
        int count;int[] faults;Tag[] bad;NXOpen.UF.UFSession.GetUFSession().Modl.AskBodyConsistency(bodies[0].Tag,out count,out faults,out bad);
        if(count!=0)throw new Exception("Native body consistency failed");
        int total=0;foreach(Body body in _part.Bodies){total++;if(body.Tag!=bodies[0].Tag&&!oldBodies.Contains(body.Tag))throw new Exception("Unexpected extra body");}
        if(total!=oldBodies.Count+1)throw new Exception("Preexisting body set changed");
        phase="builder.destroy";
        builder.Destroy();builder=null;
        phase="response_and_registration";
        string id="BODY_"+(++_bodyCounter);
        string response=Json(new{ok=true,body_id=id,feature_name=feature.Name,feature=feature.GetFeatureName(),feature_type=feature.FeatureType,
            section_name=profile.Name,section_id=profile.Tag.ToString(),guide_name=guide.Name,guide_id=guide.Tag.ToString(),
            body_tag=bodies[0].Tag.ToString(),body_type=a[4],solid=bodies[0].IsSolidBody,direction=a[5],
            position="existing conic center at selected guide endpoint",orientation="existing section plane normal to initial tangent; native along-guide transport",
            tolerance_mm=0.001,consistency_fault_count=count});
        _bodies.Add(id,bodies[0]);registered=id;return response;
    }catch(Exception e){
        if(registered!=null)_bodies.Remove(registered);
        string cleanupError=null;
        if(builder!=null){try{builder.Destroy();}catch(Exception cleanup){cleanupError=cleanup.Message;}builder=null;}
        return SweepFailure(e,session,mark,phase,context,cleanupError);
    }
}
private static string SurfaceSweepFixture(string[] a)
{
    SweepPart();
    if(a.Length!=4 || (a[2]!="solid"&&a[2]!="sheet") || (a[3]!="forward"&&a[3]!="reverse"))
        throw new Exception("nx_surface_sweep_fixture PREFIX solid|sheet forward|reverse");
    SweepToken(a[1]);string profileName=a[1]+"_section",guideName=a[1]+"_guide";
    SweepUnused(a[1]);SweepUnused(profileName);SweepUnused(guideName);
    double angle=Math.PI/3,t=a[3]=="reverse"?angle:0;
    var center=new Point3d(80*Math.Sin(t),80-80*Math.Cos(t),0);
    var radial=new Vector3d(-Math.Sin(t),Math.Cos(t),0);var vertical=new Vector3d(0,0,1);
    var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Sweep fixture "+a[1]);
    try{
        var guide=_part.Curves.CreateArc(new Point3d(0,80,0),new Vector3d(0,-1,0),new Vector3d(1,0,0),80,0,angle);
        guide.SetName(guideName);Curve profile;
        if(a[2]=="solid")profile=_part.Curves.CreateEllipse(center,radial,vertical,4,2.5,0,2*Math.PI);
        else profile=_part.Curves.CreateArc(center,radial,vertical,4,-Math.PI/3,Math.PI/3);
        profile.SetName(profileName);
        return Json(new{ok=true,fixture_only=true,section_name=profile.Name,guide_name=guide.Name,
            section_id=profile.Tag.ToString(),guide_id=guide.Tag.ToString(),body_type=a[2],direction=a[3],
            guide_radius_mm=80,guide_angle_deg=60,section_center=center,
            purpose="Nonfunctional curved ornamental casing rail test; curves only, no sweep claimed"});
    }catch(Exception e){return SweepFailure(e,session,mark);}
}

// Source candidate only. Append inside loader; existing using directives/helpers retained.
// Route every mutating dispatch through the durable operation ledger and single-writer lease.
private static string _surfaceRefToken=null,_surfaceRefState=null;
private static void SurfacePartGuard()
{
    MechanismPart();
    if(_part.PartUnits!=BasePart.Units.Millimeters || String.IsNullOrEmpty(_part.FullPath))
        throw new Exception("Saved millimeter part required");
    SafePath(_part.FullPath);
    if(!Path.GetFileNameWithoutExtension(_part.FullPath).StartsWith("phase4_surface_",StringComparison.Ordinal))
        throw new Exception("Dedicated fresh phase4_surface_* part required");
    // Coordinator must authorize/create this NEW directory before native testing.
    string root=LabPath(@"models\phase4_phase5_staged_20261108_212227\phase4_surface_trim_extend");
    if(!String.Equals(Path.GetFullPath(Path.GetDirectoryName(_part.FullPath)),root,StringComparison.OrdinalIgnoreCase))
        throw new Exception("Dedicated trim/extend fixture directory required");
}
private static double SurfaceNumber(string s,double low,double high)
{
    double x=D(s);
    if(Double.IsNaN(x)||Double.IsInfinity(x)||x<low||x>high)throw new Exception("Number outside bounded range");
    return x;
}
private static string SurfaceBodyStamp(Body b)
{
    var box=new double[6];NXOpen.UF.UFSession.GetUFSession().Modl.AskBoundingBox(b.Tag,box);
    var s=new System.Text.StringBuilder();
    s.Append(b.Tag).Append("|").Append(b.Name).Append("|").Append(b.IsSolidBody).Append("|");
    foreach(double x in box)s.Append(x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append(",");
    foreach(Face f in b.GetFaces())s.Append("F").Append(f.Tag);
    foreach(Edge e in b.GetEdges())s.Append("E").Append(e.Tag);
    return s.ToString();
}
private static string SurfaceStamp()
{
    var s=new System.Text.StringBuilder(_part.FullPath);
    foreach(Body b in _part.Bodies)s.Append("\n").Append(SurfaceBodyStamp(b));
    foreach(Feature f in _part.Features)s.Append("\n").Append(f.Tag).Append(":").Append(f.Name);
    return s.ToString();
}
private static Body SurfaceSheet(string id)
{
    Body b=GetBody(id);bool current=false;
    foreach(Body x in _part.Bodies)if(x.Tag==b.Tag)current=true;
    if(!current||b.IsOccurrence||b.IsSolidBody||b.GetFaces().Length==0)
        throw new Exception("Current registered local sheet required");
    return b;
}
private static void SurfaceFresh(string token)
{
    if(token!=_surfaceRefToken || _surfaceRefState!=SurfaceStamp())
        throw new Exception("Stale topology; query nx_surface_refs again");
    _surfaceRefToken=null;_surfaceRefState=null; // consume once, even if following validation fails
}
private static string SurfaceRefs(string[] a)
{
    SurfacePartGuard();if(a.Length!=2)throw new Exception("nx_surface_refs BODY_ID");
    Body b=SurfaceSheet(a[1]);var edges=new List<object>();var faces=new List<object>();
    foreach(Edge e in b.GetEdges()){
        Point3d p,q;e.GetVertices(out p,out q);
        edges.Add(new{tag=e.Tag.ToString(),adjacent_faces=e.GetFaces().Length,start=p,end=q,length_mm=e.GetLength()});
    }
    foreach(Face f in b.GetFaces())faces.Add(new{tag=f.Tag.ToString(),type=f.SolidFaceType.ToString()});
    _surfaceRefState=SurfaceStamp();_surfaceRefToken=Guid.NewGuid().ToString("N");
    return Json(new{ok=true,part=_part.FullPath,body_id=a[1],body_tag=b.Tag.ToString(),
        token=_surfaceRefToken,edges=edges,faces=faces,scope="Session-local references; query again after mutation/reopen"});
}
private static void SurfaceHealthy(Body b)
{
    int n;int[] faults;Tag[] bad;
    NXOpen.UF.UFSession.GetUFSession().Modl.AskBodyConsistency(b.Tag,out n,out faults,out bad);
    if(n!=0)throw new Exception("Body consistency faults");
}
private static double SurfaceDistance(NXObject a,NXObject b)
{
    Point3d p,q;double accuracy;
    double d=Session.GetSession().Measurement.GetMinimumDistance(a,b,out p,out q,out accuracy);
    if(Double.IsNaN(d)||Double.IsInfinity(d)||d<0 || Double.IsNaN(accuracy)||Double.IsInfinity(accuracy)
        || accuracy<0 || accuracy>0.0001)throw new Exception("Distance query precision inadequate");
    return d;
}
private static string SurfaceTransaction(string name,System.Type expected,
    Func<Builder> configure,Action<Body> validate,object parameters)
{
    SweepUnused(name);SweepUnused(name+"_body");
    var before=new Dictionary<Tag,string>();
    foreach(Body b in _part.Bodies){SurfaceHealthy(b);before.Add(b.Tag,SurfaceBodyStamp(b));}
    var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Surface "+name);
    Builder builder=null;string registered=null,phase="configure";
    // configure must assign _surfacePendingBuilder immediately after factory returns,
    // so failures during configuration cannot leak the builder.
    _surfacePendingBuilder=null;
    try{
        builder=configure();_surfacePendingBuilder=null;phase="commit";
        var fb=builder as NXOpen.Features.FeatureBuilder;
        if(fb==null)throw new Exception("FeatureBuilder required");
        var f=fb.CommitFeature() as BodyFeature;phase="validate";
        if(f==null||!expected.IsInstanceOfType(f))throw new Exception("Unexpected native feature type");
        f.SetName(name);
        if(f.GetFeatureErrorMessages().Length!=0||f.GetFeatureWarningMessages().Length!=0)
            throw new Exception("Native feature errors/warnings");
        Body[] output=f.GetBodies();
        if(output.Length!=1||output[0].IsSolidBody||before.ContainsKey(output[0].Tag)||output[0].GetFaces().Length==0)
            throw new Exception("Expected exactly one NEW nonempty sheet; source preservation required");
        Body result=output[0];result.SetName(name+"_body");SurfaceHealthy(result);
        int count=0;
        foreach(Body b in _part.Bodies){
            count++;
            if(before.ContainsKey(b.Tag)){
                SurfaceHealthy(b);
                if(before[b.Tag]!=SurfaceBodyStamp(b))throw new Exception("Preexisting body changed");
            }else if(b.Tag!=result.Tag)throw new Exception("Unexpected additional body");
        }
        if(count!=before.Count+1)throw new Exception("Source body lost");
        validate(result);phase="destroy";
        builder.Destroy();builder=null;
        phase="register";string id="BODY_"+(++_bodyCounter);
        string response=Json(new{ok=true,body_id=id,body_tag=result.Tag.ToString(),feature_name=f.Name,
            feature=f.GetFeatureName(),feature_type=f.FeatureType,parameters=parameters,
            solid=false,volume_mm3=(double?)null,consistency_fault_count=0,
            scope="Native operation receipt only; independent dimension/reopen/STEP validation required"});
        _bodies.Add(id,result);registered=id;
        _surfaceRefToken=null;_surfaceRefState=null;return response;
    }catch(Exception e){
        if(registered!=null)_bodies.Remove(registered);
        if(builder==null)builder=_surfacePendingBuilder;
        _surfacePendingBuilder=null;string cleanup=null;
        if(builder!=null){try{builder.Destroy();}catch(Exception x){cleanup=x.Message;}}
        _surfaceRefToken=null;_surfaceRefState=null;
        return SweepFailure(e,session,mark,phase,name,cleanup);
    }
}
private static Builder _surfacePendingBuilder=null;
// Bounded native dependency traversal; never infer associativity from geometry alone.
private static bool SurfaceHasAncestor(Feature child,Feature expected)
{
    var pending=new Queue<Feature>();var seen=new System.Collections.Generic.HashSet<Tag>();
    foreach(Feature p in child.GetParents())pending.Enqueue(p);
    while(pending.Count!=0){
        Feature f=pending.Dequeue();
        if(!seen.Add(f.Tag))continue;
        if(seen.Count>128)throw new Exception("Dependency traversal exceeds candidate limit");
        if(f.Tag==expected.Tag)return true;
        foreach(Feature p in f.GetParents())pending.Enqueue(p);
    }
    return false;
}
private static string SurfaceTrim(string[] a)
{
    SurfacePartGuard();
    if(a.Length!=12||(a[3]!="curve"&&a[3]!="face")||(a[9]!="keep"&&a[9]!="discard"))
        throw new Exception("nx_surface_trim NAME BODY_ID curve|face TOOL FACE_TAG_OR_DASH X Y Z keep|discard TOKEN TOL");
    SweepUnused(a[1]);SweepUnused(a[1]+"_witness");Body target=SurfaceSheet(a[2]);SurfaceFresh(a[10]);
    double tol=SurfaceNumber(a[11],0.00001,0.001);
    Point3d witness=new Point3d(SurfaceNumber(a[6],-10000,10000),SurfaceNumber(a[7],-10000,10000),SurfaceNumber(a[8],-10000,10000));
    NXObject boundary=null;
    Body boundarySheet=null; // repair1: selector input is separate from measurement geometry
    if(a[3]=="curve"){
        if(a[5]!="-")throw new Exception("Curve face tag must be dash");
        boundary=SweepCurve(a[4]);
        // Restrict first release to on-sheet curves. Projection is explicitly FaceNormal.
    }else{
        Body tool=SurfaceSheet(a[4]);
        if(tool.Tag==target.Tag)throw new Exception("Distinct tool sheet required");
        foreach(Face f in tool.GetFaces())if(f.Tag.ToString()==a[5])boundary=f;
        if(boundary==null)throw new Exception("Current tool face tag required");
        // Whole-sheet selection must not silently include other tool faces.
        if(tool.GetFaces().Length!=1)throw new Exception("Repair1 face boundary requires a single-face tool sheet");
        boundarySheet=tool;
    }
    bool keep=a[9]=="keep";Point seed=null;
    Curve intersectionCut=null;
    NXOpen.Features.IntersectionCurve intersectionFeature=null;
    var intersectionEvidence=new List<object>();
    if(a[3]=="face"){
        SweepUnused(a[1]+"_intersection");
        if(tol!=0.001)throw new Exception("Surface intersection candidate requires fixed tolerance 0.001 mm");
    }
    return SurfaceTransaction(a[1],typeof(NXOpen.Features.TrimSheet),delegate{
        // Both commits are inside SurfaceTransaction's ORIGINAL undo mark.
        // Track the current builder immediately; outer rollback owns failure cleanup.
        if(a[3]=="face"){
            if(boundarySheet==null || !(boundary is Face))
                throw new Exception("Missing single-face sheet boundary");
            var ib=_part.Features.CreateIntersectionCurveBuilder(null);
            _surfacePendingBuilder=ib;
            ib.Associative=true;ib.Tolerance=tol;
            ib.FirstFace.ReplaceRules(new SelectionIntentRule[]{
                _part.ScRuleFactory.CreateRuleFaceDumb(target.GetFaces())},false);
            ib.SecondFace.ReplaceRules(new SelectionIntentRule[]{
                _part.ScRuleFactory.CreateRuleFaceDumb(new Face[]{(Face)boundary})},false);
            intersectionFeature=ib.CommitFeature() as NXOpen.Features.IntersectionCurve;
            if(intersectionFeature==null)throw new Exception("Expected native associative IntersectionCurve");
            intersectionFeature.SetName(a[1]+"_intersection");
            if(intersectionFeature.GetFeatureErrorMessages().Length!=0 ||
                intersectionFeature.GetFeatureWarningMessages().Length!=0)
                throw new Exception("Intersection feature errors/warnings");
            var generated=new List<Curve>();
            foreach(NXObject entity in intersectionFeature.GetEntities()){
                Curve c=entity as Curve;
                if(c==null || c.IsOccurrence)throw new Exception("Intersection returned unsupported/nonlocal entity");
                foreach(Curve prior in generated)if(prior.Tag==c.Tag)
                    throw new Exception("Duplicate intersection curve");
                generated.Add(c);
            }
            // First release deliberately excludes disconnected/multiple/closed results.
            if(generated.Count!=1)throw new Exception("Expected one unambiguous native intersection curve");
            intersectionCut=generated[0];
            Point3d p0,p1,pm;Vector3d t0,t1,tm;
            SweepEval(intersectionCut,0,out p0,out t0);
            SweepEval(intersectionCut,1,out p1,out t1);
            SweepEval(intersectionCut,0.5,out pm,out tm);
            if(SweepDot(SweepDelta(p1,p0),SweepDelta(p1,p0))<=100*tol*tol)
                throw new Exception("Closed/degenerate/short intersection excluded");
            if(SurfaceDistance(intersectionCut,target)>tol ||
                SurfaceDistance(intersectionCut,boundary)>tol)
                throw new Exception("Intersection curve misses input geometry");
            if(intersectionFeature.GetParents().Length==0)
                throw new Exception("Intersection has no native parent dependencies");
            intersectionEvidence.Add(new{
                feature_name=intersectionFeature.Name,
                feature_tag=intersectionFeature.Tag.ToString(),
                feature_type=intersectionFeature.FeatureType,
                curve_tag=intersectionCut.Tag.ToString(),
                curve_type=intersectionCut.GetType().FullName,
                associative=true,tolerance_mm=tol,
                parent_count=intersectionFeature.GetParents().Length,
                help_point=pm});
            ib.Destroy();_surfacePendingBuilder=null;
        }
        var b=_part.Features.CreateTrimsheetBuilder(null);_surfacePendingBuilder=b;
        seed=_part.Points.CreatePoint(witness);seed.SetName(a[1]+"_witness");
        double on=SurfaceDistance(seed,target);
        if(on>tol)throw new Exception("Witness is not on the actual trimmed sheet");
        foreach(Edge e in target.GetEdges())if(SurfaceDistance(seed,e)<=10*tol)
            throw new Exception("Witness too close to existing edge");
        if(SurfaceDistance(seed,boundary)<=10*tol)throw new Exception("Witness too close to tool");
        if(SurfaceDistance(target,boundary)>tol)throw new Exception("Tool must intersect/touch target; remote projection not supported");
        b.ApplyToCopyOfSheets=true;b.AllowTargetEdgesAsToolObjects=false;
        b.ExtendBoundaryObject=false;b.OutputExactGeometry=true;b.ImprintBothSide=true;
        b.KeepDiscardMethod=keep?NXOpen.Features.TrimSheetBuilder.KeepDiscardOption.Keep:NXOpen.Features.TrimSheetBuilder.KeepDiscardOption.Discard;
        b.Tolerance=tol;
        b.ProjectionDirection.ProjectDirectionMethod=NXOpen.GeometricUtilities.ProjectionOptions.DirectionType.FaceNormal;
        // No raw Face/Body selector retry. Surface mode uses the actual native
        // associative intersection output in the already verified Section path.
        Curve cut=a[3]=="curve"?(Curve)boundary:intersectionCut;
        if(cut==null)throw new Exception("Missing actual boundary curve");
        if(SurfaceDistance(seed,cut)<=10*tol)
            throw new Exception("Witness too close to actual boundary curve");
        Point3d help;Vector3d tangent;
        // SweepEval already uses UF Eval on Curve.Tag without conic type casts.
        // Unsupported native evaluators fail and roll back; no invented help point.
        SweepEval(cut,0.5,out help,out tangent);
        Section cutSection=_part.Sections.CreateSection();
        cutSection.DistanceTolerance=tol;
        cutSection.ChainingTolerance=tol*0.5;
        cutSection.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        cutSection.AllowSelfIntersection(false);
        cutSection.AddToSection(
            new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(new Curve[]{cut})},
            cut,null,null,help,Section.Mode.Create,false);
        TaggedObject selectedBoundary=cutSection;
        if(!b.TargetBodies.Add(target))
            throw new Exception("Native target selection insertion rejected");
        if(!b.BoundaryObjects.Add(selectedBoundary))
            throw new Exception("Native boundary selection insertion rejected: "+selectedBoundary.GetType().FullName);
        // List insertion is not proof of TrimSheet semantic validity.
        var selected=b.BoundaryObjects.GetArray();
        if(selected.Length!=1 || selected[0].Tag!=selectedBoundary.Tag)
            throw new Exception("Native trim boundary selection differs from requested container");
        b.Regions.Append(_part.CreateRegionPoint(seed,target));return b;
    },delegate(Body result){
        if(intersectionFeature!=null){
            Feature trim=null;
            foreach(Feature f in _part.Features)if(f.Name==a[1]){
                if(trim!=null)throw new Exception("Ambiguous output feature");
                trim=f;
            }
            if(trim==null || !SurfaceHasAncestor(trim,intersectionFeature))
                throw new Exception("Trim lacks native intersection dependency");
            if(intersectionFeature.GetFeatureErrorMessages().Length!=0 ||
                intersectionFeature.GetFeatureWarningMessages().Length!=0)
                throw new Exception("Intersection unhealthy after trim");
        }
        double d=SurfaceDistance(seed,result);
        if((keep&&d>tol)||(!keep&&d<=10*tol))throw new Exception("Result contradicts witness keep/discard");
        // Require contact with tool and clearance of retained witness from result boundary.
        if(keep)foreach(Edge e in result.GetEdges())if(SurfaceDistance(seed,e)<=10*tol)
            throw new Exception("Retained witness too close to resulting trim boundary");
        // Contact alone does not prove a nonzero cut. Independent area/dimension test is mandatory.
        bool newBoundary=false;
        foreach(Edge e in result.GetEdges()){
            Point3d p,q;e.GetVertices(out p,out q);
            // Native result edge on the actual tool is required.
            if(SurfaceDistance(e,boundary)<=tol)newBoundary=true;
        }
        if(!newBoundary)throw new Exception("No output edge reaches trimming tool");
    },new{target=a[2],tool=a[4],tool_kind=a[3],face_tag=a[5],witness=witness,keep=keep,
        copy=true,projection="FaceNormal",tolerance_mm=tol,
        selector_repair=a[3]=="curve"?"trim-native-repair1":"trim-native-repair2-intersection",
        boundary_container="NXOpen.Section",
        boundary_scope=a[3]=="curve"?"single_curve_rule":"single_native_intersection_curve",
        method=a[3]=="curve"?"Section -> TrimSheet":"Associative IntersectionCurve -> Section -> TrimSheet",
        principal_feature_count=a[3]=="curve"?1:2,automatic_extract_features_excluded_from_count=true,
        direct_face_trim_supported=false,intersection=intersectionEvidence});
}
private static string SurfaceExtend(string[] a)
{
    SurfacePartGuard();
    if(a.Length!=9||(a[5]!="tangent"&&a[5]!="curvature"&&a[5]!="mirrored")||(a[6]!="forward"&&a[6]!="reverse"))
        throw new Exception("nx_surface_extend NAME BODY_ID EDGE_TAG DIST tangent|curvature|mirrored forward|reverse TOKEN TOL");
    SweepUnused(a[1]);Body target=SurfaceSheet(a[2]);SurfaceFresh(a[7]);
    double distance=SurfaceNumber(a[4],0.01,100),tol=SurfaceNumber(a[8],0.00001,0.001);
    Edge edge=null;foreach(Edge e in target.GetEdges())if(e.Tag.ToString()==a[3])edge=e;
    if(edge==null||edge.IsOccurrence||edge.GetFaces().Length!=1)throw new Exception("Current one-face sheet boundary edge required");
    Point3d p,q;edge.GetVertices(out p,out q);
    if(SweepDot(SweepDelta(p,q),SweepDelta(p,q))<1e-8)
        throw new Exception("Closed/seam/degenerate edge excluded from first release");
    return SurfaceTransaction(a[1],typeof(NXOpen.Features.ExtendSheet),delegate{
        var b=_part.Features.TrimFeatureCollection.CreateExtendSheetBuilder(null);_surfacePendingBuilder=b;
        b.CopyOriginal=true;b.BodyOutputOption=NXOpen.Features.ExtendSheetBuilder.BodyOutput.ExtendasNewSheet;
        b.LimitMethod=NXOpen.Features.ExtendSheetBuilder.Limit.Offset;
        b.Offset.RightHandSide=distance.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
        b.DistanceTolerance=tol;b.SideEdgeShapeOption=NXOpen.Features.ExtendSheetBuilder.SideEdgeShape.Automatic;
        b.SurfaceShapeOption=a[5]=="tangent"?NXOpen.Features.ExtendSheetBuilder.SurfaceShape.NaturalTangent:
            (a[5]=="curvature"?NXOpen.Features.ExtendSheetBuilder.SurfaceShape.NaturalCurvature:NXOpen.Features.ExtendSheetBuilder.SurfaceShape.Mirrored);
        b.TargetBoundaryEdges.ReplaceRules(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleEdgeDumb(new Edge[]{edge})},false);
        if(a[6]=="reverse")b.ReverseExtensionSide();return b;
    },delegate(Body result){
        if(SurfaceDistance(edge,result)>tol)throw new Exception("Extension disconnected from selected edge");
    },new{target=a[2],edge_tag=a[3],distance_mm=distance,shape=a[5],direction=a[6],
        copy=true,output="new_sheet",tolerance_mm=tol,collision_checked=false});
}
private static string SurfaceFixture(string[] a)
{
    SurfacePartGuard();
    if(a.Length!=3||(a[2]!="planar"&&a[2]!="tool"&&a[2]!="curved"))
        throw new Exception("nx_surface_fixture PREFIX planar|tool|curved");
    string name=a[1];SweepUnused(name);
    foreach(string suffix in new[]{"_c0","_c1","_c2","_c3","_cut","_section","_guide","_body"})SweepUnused(name+suffix);
    return SurfaceTransaction(name,a[2]=="curved"?typeof(NXOpen.Features.SweepAlongGuide):typeof(NXOpen.Features.BoundedPlane),delegate{
        if(a[2]=="curved"){
            var b=_part.Features.CreateSweepAlongGuideBuilder(null);_surfacePendingBuilder=b;
            var guide=_part.Curves.CreateArc(new Point3d(0,80,0),new Vector3d(0,-1,0),new Vector3d(1,0,0),80,0,Math.PI/3);
            guide.SetName(name+"_guide");
            var profile=_part.Curves.CreateArc(new Point3d(0,0,0),new Vector3d(0,1,0),new Vector3d(0,0,1),4,-Math.PI/3,Math.PI/3);
            profile.SetName(name+"_section");
            b.DistanceTolerance=0.001;b.ChainingTolerance=0.0005;
            b.BooleanOperation.Type=BooleanOperation.BooleanType.Create;
            b.FeatureOptions.BodyType=NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Sheet;
            b.FirstOffset.RightHandSide="0";b.SecondOffset.RightHandSide="0";
            Point3d ps,pg;Vector3d ts,tg;SweepEval(profile,0,out ps,out ts);SweepEval(guide,0,out pg,out tg);
            string phase="fixture";SweepSection(b.Section,profile,ps,ts,false,"section",ref phase);
            SweepSection(b.Guide,guide,pg,tg,false,"guide",ref phase);return b;
        }
        var plane=_part.Features.CreateBoundedPlaneBuilder(null);_surfacePendingBuilder=plane;
        Point3d[] pts=a[2]=="planar"?
            new[]{new Point3d(0,0,0),new Point3d(40,0,0),new Point3d(40,20,0),new Point3d(0,20,0)}:
            new[]{new Point3d(20,-5,-10),new Point3d(20,25,-10),new Point3d(20,25,10),new Point3d(20,-5,10)};
        var curves=new Curve[4];
        for(int i=0;i<4;i++){curves[i]=_part.Curves.CreateLine(pts[i],pts[(i+1)%4]);curves[i].SetName(name+"_c"+i);}
        // BoundingCurves is expected to be Section; CLR type remains a compile gate.
        plane.BoundingCurves.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(curves)},
            curves[0],null,null,pts[0],Section.Mode.Create,false);
        if(a[2]=="planar"){
            var cut=_part.Curves.CreateLine(new Point3d(20,-5,0),new Point3d(20,25,0));cut.SetName(name+"_cut");
        }
        return plane;
    },delegate(Body body){},new{fixture=a[2],cut_curve=a[2]=="planar"?name+"_cut":null,
        planar_expected_area_mm2=a[2]=="planar"?(double?)800:null,
        tool_expected_area_mm2=a[2]=="tool"?(double?)600:null,
        curved_guide_radius_mm=a[2]=="curved"?(double?)80:null,
        curved_guide_angle_deg=a[2]=="curved"?(double?)60:null});
}


    private static string LabPath(string relative)
    {
        string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(NX_MCP_Loader).Assembly.Location), @"..\.."));
        string configured=Environment.GetEnvironmentVariable("NX_AI_LAB_ROOT");
        if(!String.IsNullOrEmpty(configured) && !String.Equals(Path.GetFullPath(configured),root,StringComparison.OrdinalIgnoreCase))throw new IOException("Mixed project roots are forbidden");
        return Path.Combine(root,relative);
    }

// Append INSIDE existing NX12 loader class. Candidate only: compile/native tests NOT RUN.
// Uses existing System/Linq/Collections.Generic/IO/NXOpen/Features imports and helpers.
// No CSV edits, new curves, new features, save, close, export, or automatic retry.
private const string ParamRoot = @"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\advanced_parametric_20261109";
private static bool _paramUnsafe = false;
private sealed class ParamCurveReceipt {
    public string tag, name, type;
    public double[] parameters, orientation;
}
private sealed class ParamFeatureReceipt {
    public string tag, name, type;
    public string[] parents, children, errors, warnings;
    public bool suppressed;
    public double[] geometry;
}
private sealed class ParamReceipt {
    public string part;
    public ParamCurveReceipt[] curves;
    public ParamFeatureReceipt[] features;
    public string[] bodyTags;
    public double[] geometry;
}
private static bool ParamFinite(double n) {
    return !Double.IsNaN(n) && !Double.IsInfinity(n);
}
private static double[] ParamMatrix(Conic c) {
    Matrix3x3 m=c.Matrix.Element;
    return new double[]{m.Xx,m.Xy,m.Xz,m.Yx,m.Yy,m.Yz,m.Zx,m.Zy,m.Zz};
}
private static double[] ParamValues(Curve c) {
    var e=c as Ellipse;
    if(e!=null) {
        double major,minor,start,end,rotation;Point3d center;
        e.GetParameters(out major,out minor,out start,out end,out rotation,out center);
        return new double[]{center.X,center.Y,center.Z,major,minor,start,end,rotation};
    }
    var a=c as Arc;
    if(a!=null) {
        Point3d p=a.CenterPoint;
        return new double[]{p.X,p.Y,p.Z,a.Radius,a.StartAngle,a.EndAngle};
    }
    throw new Exception("Only native ellipse and arc controls are supported");
}
private static bool ParamNear(double[] a,double[] b,double tol) {
    if(a.Length!=b.Length)return false;
    for(int i=0;i<a.Length;i++)
        if(!ParamFinite(a[i])||!ParamFinite(b[i])||Math.Abs(a[i]-b[i])>tol)return false;
    return true;
}
private static void ParamGuard(bool writing) {
    if(_part==null)throw new Exception("No active part");
    string full=Path.GetFullPath(_part.FullPath);
    SafePath(full);
    string stem=Path.GetFileNameWithoutExtension(full);
    if(!String.Equals(Path.GetDirectoryName(full),ParamRoot,StringComparison.OrdinalIgnoreCase) ||
       !(stem.StartsWith("phase4_param_",StringComparison.Ordinal) ||
         stem.StartsWith("phase4_sweep_param_",StringComparison.Ordinal)) ||
       !String.Equals(Path.GetExtension(full),".prt",StringComparison.OrdinalIgnoreCase))
        throw new Exception("Exact new parametric directory and approved ASCII part prefix required");
    SweepToken(stem);
    // Reject directory aliases/junctions, including parents.
    for(DirectoryInfo d=new DirectoryInfo(Path.GetDirectoryName(full));d!=null;d=d.Parent)
        if((d.Attributes & FileAttributes.ReparsePoint)!=0)
            throw new Exception("Reparse-point directory is outside this narrow experiment");
    if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
        throw new Exception("Reparse-point part rejected");
    if(_part.PartUnits!=BasePart.Units.Millimeters)
        throw new Exception("Millimeter part required");
    if(writing) {
        if(_paramUnsafe)throw new Exception("Previous rollback unverified: coordinator recovery required");
        MechanismPart();
        var s=Session.GetSession();
        if(s.Parts.Work!=_part || s.Parts.Display!=_part)
            throw new Exception("Work and display must be the same experimental part");
        int count=0;foreach(BasePart p in s.Parts) {
            count++;if(p.Tag!=_part.Tag)throw new Exception("Only experimental part may be loaded during update");
        }
        if(count!=1)throw new Exception("Exactly one loaded part required");
        if(s.UpdateManager.GetUpdateLock() || s.UpdateManager.ErrorList.Length!=0)
            throw new Exception("Resolve existing update lock/error list before edit; not cleared here");
    }
}
private static double[] ParamBodyGeometry(Body body) {
    var uf=NXOpen.UF.UFSession.GetUFSession();
    if(body.IsOccurrence || body.OwningPart.Tag!=_part.Tag)
        throw new Exception("Local body required");
    int count;int[] faults;Tag[] bad;
    uf.Modl.AskBodyConsistency(body.Tag,out count,out faults,out bad);
    if(count!=0)throw new Exception("UF body consistency failed for "+body.Tag);
    double[] box=new double[6];uf.Modl.AskBoundingBox(body.Tag,box);
    var v=new List<double>(box);
    v.Add(body.IsSolidBody?1:0);v.Add(body.GetFaces().Length);v.Add(body.GetEdges().Length);
    // Conservative geometry witness, not a complete shape comparison or volume proof.
    var lengths=body.GetEdges().Select(e=>e.GetLength()).OrderBy(x=>x).ToArray();
    v.AddRange(lengths);
    if(v.Any(x=>!ParamFinite(x)))throw new Exception("Nonfinite geometry witness");
    return v.ToArray();
}
private static double[] ParamBodiesGeometry(IEnumerable<Body> bodies) {
    var v=new List<double>();
    foreach(Body b in bodies.OrderBy(x=>x.Tag.ToString(),StringComparer.Ordinal)) {
        double[] g=ParamBodyGeometry(b);v.Add(g.Length);v.AddRange(g);
    }
    return v.ToArray();
}
private static ParamReceipt ParamSnapshot() {
    var cs=new List<ParamCurveReceipt>();
    foreach(Curve c in _part.Curves) {
        if(!(c is Ellipse) && !(c is Arc))continue;
        if(c.IsOccurrence || c.OwningPart.Tag!=_part.Tag)
            throw new Exception("Occurrence control rejected");
        cs.Add(new ParamCurveReceipt{tag=c.Tag.ToString(),name=c.Name,
            type=c.GetType().FullName,parameters=ParamValues(c),orientation=ParamMatrix((Conic)c)});
    }
    var fs=new List<ParamFeatureReceipt>();
    foreach(Feature f in _part.Features) {
        if(f.IsOccurrence || f.OwningPart.Tag!=_part.Tag)
            throw new Exception("Occurrence feature rejected");
        var bf=f as BodyFeature;
        fs.Add(new ParamFeatureReceipt{
            tag=f.Tag.ToString(),name=f.Name,type=f.FeatureType,suppressed=f.Suppressed,
            parents=f.GetParents().Select(p=>p.Tag.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
            children=f.GetChildren().Select(p=>p.Tag.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
            errors=f.GetFeatureErrorMessages()??new string[0],
            warnings=f.GetFeatureWarningMessages()??new string[0],
            geometry=bf==null?new double[0]:ParamBodiesGeometry(bf.GetBodies())});
    }
    Body[] bs=_part.Bodies.ToArray();
    return new ParamReceipt{part=_part.FullPath,
        curves=cs.OrderBy(x=>x.tag,StringComparer.Ordinal).ToArray(),
        features=fs.OrderBy(x=>x.tag,StringComparer.Ordinal).ToArray(),
        bodyTags=bs.Select(x=>x.Tag.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
        geometry=ParamBodiesGeometry(bs)};
}
private static void ParamHealthy(ParamReceipt r) {
    if(r.bodyTags.Length==0)throw new Exception("A downstream body is required");
    if(r.features.Any(f=>f.suppressed||f.errors.Length!=0||f.warnings.Length!=0))
        throw new Exception("Suppressed feature or native error/warning rejected");
}
private static bool ParamIdentity(ParamReceipt a,ParamReceipt b) {
    if(a.part!=b.part || a.curves.Length!=b.curves.Length || a.features.Length!=b.features.Length)return false;
    for(int i=0;i<a.curves.Length;i++)
        if(a.curves[i].tag!=b.curves[i].tag || a.curves[i].name!=b.curves[i].name ||
           a.curves[i].type!=b.curves[i].type)return false;
    for(int i=0;i<a.features.Length;i++) {
        var x=a.features[i];var y=b.features[i];
        if(x.tag!=y.tag||x.name!=y.name||x.type!=y.type||x.suppressed!=y.suppressed||
           !x.parents.SequenceEqual(y.parents)||!x.children.SequenceEqual(y.children))return false;
    }
    return true;
}
private static bool ParamRestored(ParamReceipt a,ParamReceipt b) {
    if(!ParamIdentity(a,b)||!a.bodyTags.SequenceEqual(b.bodyTags)||
       !ParamNear(a.geometry,b.geometry,1e-8))return false;
    for(int i=0;i<a.curves.Length;i++)
        if(!ParamNear(a.curves[i].parameters,b.curves[i].parameters,1e-9)||
           !ParamNear(a.curves[i].orientation,b.curves[i].orientation,1e-12))return false;
    for(int i=0;i<a.features.Length;i++)
        if(!ParamNear(a.features[i].geometry,b.features[i].geometry,1e-8))return false;
    return true;
}
private static object ParamRefreshBodies() {
    // Invalidate all old handles: topology may have changed. Do NOT ResetTaskState.
    _bodies.Clear();
    var handles=new List<object>();
    foreach(Body b in _part.Bodies) {
        string id="BODY_"+(++_bodyCounter);_bodies[id]=b;
        handles.Add(new{body_id=id,tag=b.Tag.ToString()});
    }
    return handles;
}
private static string SurfaceParametricState(string[] a) {
    if(a.Length!=1)throw new Exception("nx_surface_parametric_state");
    ParamGuard(false);var snapshot=ParamSnapshot();
    return Json(new{ok=true,read_only=true,state=snapshot,
        health_ok=snapshot.features.All(f=>!f.suppressed&&f.errors.Length==0&&f.warnings.Length==0),
        unsafe_edit_latch=_paramUnsafe,volume_mm3=(double?)null,
        volume_note="Independent native accuracy=2 measurement required; not calculated by this minimal command"});
}
private static string SurfaceEditBasic(string[] a,bool ellipse) {
    if(a.Length!=(ellipse?7:8))throw new Exception(ellipse?
        "nx_surface_edit_ellipse CURVENAME X Y Z MAJOR MINOR":
        "nx_surface_edit_arc CURVENAME CX CY CZ R START END");
    ParamGuard(true);
    Curve c=SweepCurve(a[1]);
    if(c.OwningPart.Tag!=_part.Tag || (ellipse?!(c is Ellipse):!(c is Arc)))
        throw new Exception("Expected unique local native curve type");
    // Direct editing of feature-produced controls is outside the raw-curve fixture contract.
    foreach(Feature f in _part.Features)
        if(f.GetEntities().Any(e=>e.Tag==c.Tag))
            throw new Exception("Feature-owned control requires its own builder; direct edit rejected");
    double[] p=a.Skip(2).Select(x=>D(x)).ToArray();
    if(p.Any(x=>!ParamFinite(x)) || p.Take(3).Any(x=>Math.Abs(x)>10000))
        throw new Exception("Finite values and bounded coordinates required");
    if(p[3]<=0 || p[3]>10000 ||
       (ellipse?(p[4]<=0||p[4]>p[3]):(Math.Abs(p[5]-p[4])<=0||Math.Abs(p[5]-p[4])>2*Math.PI)))
        throw new Exception("Invalid radii or arc span");
    var before=ParamSnapshot();ParamHealthy(before);
    string curveTag=c.Tag.ToString();
    var old=before.curves.Single(x=>x.tag==curveTag);
    double[] wanted=(double[])old.parameters.Clone();
    Array.Copy(p,wanted,p.Length);
    if(ParamNear(old.parameters,wanted,1e-10))
        throw new Exception("No-op request rejected before transaction");
    // Narrow acceptance requires Shell/Thicken after loft, or a native sweep body.
    Func<ParamFeatureReceipt,bool> target=f=>ellipse?
        (f.type.IndexOf("SHELL",StringComparison.OrdinalIgnoreCase)>=0||
         f.type.IndexOf("THICK",StringComparison.OrdinalIgnoreCase)>=0)&&f.parents.Length>0:
        (f.type=="SWP104" || f.type.IndexOf("SWEEP",StringComparison.OrdinalIgnoreCase)>=0);
    if(!before.features.Any(f=>target(f)&&f.geometry.Length>0))
        throw new Exception("Required downstream native feature is missing");
    var s=Session.GetSession();var mark=s.SetUndoMark(Session.MarkVisibility.Visible,"Native basic curve edit");
    try {
        Point3d center=new Point3d(p[0],p[1],p[2]);
        if(ellipse) {
            ((Ellipse)c).SetParameters(p[3],p[4],old.parameters[5],old.parameters[6],old.parameters[7],center);
        } else {
            var arc=(Arc)c;
            NXMatrix orientation=arc.Matrix;
            arc.SetParameters(p[3],center,p[4],p[5],orientation);
        }
        s.UpdateManager.LogForUpdate(c);
        if(s.UpdateManager.DoUpdate(mark)!=0)throw new Exception("DoUpdate returned errors");
        s.UpdateManager.MakeUpToDate(_part.Features.ToArray().Cast<NXObject>().ToArray(),mark);
        if(s.UpdateManager.DoUpdate(mark)!=0 || s.UpdateManager.ErrorList.Length!=0)
            throw new Exception("Downstream update error");
        var after=ParamSnapshot();ParamHealthy(after);
        if(!ParamIdentity(before,after))throw new Exception("Control/feature identity or parent-chain changed");
        for(int i=0;i<before.curves.Length;i++) {
            var b=before.curves[i];var n=after.curves[i];
            if(!ParamNear(n.parameters,b.tag==curveTag?wanted:b.parameters,1e-8)||
               !ParamNear(n.orientation,b.orientation,1e-12))
                throw new Exception("Control readback or preserved orientation mismatch");
        }
        var changed=new List<string>();
        foreach(var f in before.features.Where(target)) {
            var n=after.features.Single(x=>x.tag==f.tag);
            if(!ParamNear(f.geometry,n.geometry,1e-7))changed.Add(f.tag);
        }
        if(changed.Count==0 || ParamNear(before.geometry,after.geometry,1e-7))
            throw new Exception("No observable downstream geometry change; no success claim");
        object handles=ParamRefreshBodies();
        return Json(new{ok=true,curve_tag=curveTag,before=before,after=after,
            downstream_changed_feature_tags=changed,body_handles=handles,old_body_handles_invalidated=true,
            saved=false,native_acceptance="NOT_RUN_BY_SOURCE_STAGE",
            scope="Local parameter and finite geometry witnesses only; independent volume, chain review and cold reopen still required"});
    } catch(Exception error) {
        bool restored=false;string rollbackError=null;ParamReceipt actual=null;object handles=null;
        try {
            s.UndoToMark(mark,null);
            actual=ParamSnapshot();ParamHealthy(actual);
            restored=ParamRestored(before,actual);
            if(!restored)rollbackError="Snapshot mismatch after undo";
            handles=ParamRefreshBodies();
        } catch(Exception re) {rollbackError=re.Message;}
        if(!restored) {_paramUnsafe=true;_bodies.Clear();}
        return Json(new{ok=false,error=error.Message,
            nx_error_code=error is NXException?((NXException)error).ErrorCode:0,
            rolled_back=restored,rollback_verified_by="identity, controls, topology, bbox/edge-length witnesses; not full geometric equivalence",
            rollback_error=rollbackError,before=before,actual=actual,body_handles=handles,
            safe_pause_required=true,retry_automatically=false});
    }
}

// Append inside loader; NX12 source candidate, compile/native acceptance NOT RUN.
// Requires existing Param* helpers, SweepToken, SweepSection and System.Linq.
private static readonly double[] SpKnots = {0,0,0,0,1,1,1,1};
private sealed class SpReceipt {
    public string feature, name, curve;
    public string[] owners, pointTags, pointNames, allPoints, allCurves;
    public int degree;
    public bool periodic, rational;
    public double[] knots, poles4, poles3, controls, samples, limits;
}
private static double[] SpXYZ(Point3d p) {return new double[]{p.X,p.Y,p.Z};}
private static void SpRequire(bool yes,string why) {if(!yes)throw new Exception(why);}
private static void SpGuard(bool write) {
    ParamGuard(write);
    if(write)throw new Exception("Spline fixture validation failed after three bounded repair rounds; mutations disabled. See Surface_Parametric_Rebuild_Acceptance.md. No fourth attempt authorized.");
    SpRequire(String.Equals(Path.GetFullPath(_part.FullPath),
        Path.Combine(ParamRoot,"phase4_param_spline.prt"),StringComparison.OrdinalIgnoreCase),
        "Only exact fresh phase4_param_spline.prt is supported");
}
private static Feature SpFeature(string name) {
    SweepToken(name);
    var fs=_part.Features.ToArray().Where(f=>f.Name==name).ToArray();
    SpRequire(fs.Length==1,"Expected one named StudioSpline feature");
    return fs[0];
}
// NX12 Points collection omitted actual non-displayable spline controls in repair1.
// UF cycle_all includes native objects; filter type before obtaining managed Point.
private static Point[] SpAllPoints() {
    var uf=NXOpen.UF.UFSession.GetUFSession();var points=new List<Point>();
    Tag tag=Tag.Null;var seen=new HashSet<Tag>();
    for(int i=0;i<100000;i++) {
        tag=uf.Obj.CycleAll(_part.Tag,tag);
        if(tag==Tag.Null)return points.ToArray();
        SpRequire(seen.Add(tag),"Object cycle repeated");
        int type,subtype;uf.Obj.AskTypeAndSubtype(tag,out type,out subtype);
        if(type==NXOpen.UF.UFConstants.UF_point_type) {
            var point=NXOpen.Utilities.NXObjectManager.Get(tag) as Point;
            SpRequire(point!=null,"Native point has no Point wrapper");points.Add(point);
        }
    }
    throw new Exception("Bounded native point cycle exceeded");
}
private static string[] SpPointInventory() {
    return SpAllPoints().Select(p=>p.Tag.ToString()+":"+p.Name)
        .OrderBy(x=>x,StringComparer.Ordinal).ToArray();
}
private static string[] SpCurveInventory() {
    return _part.Curves.ToArray().Select(p=>p.Tag.ToString()+":"+p.GetType().FullName)
        .OrderBy(x=>x,StringComparer.Ordinal).ToArray();
}
private static string[] SpObjectInventory() {
    var uf=NXOpen.UF.UFSession.GetUFSession();var tags=new HashSet<string>();
    Tag tag=Tag.Null;
    for(int i=0;i<100000;i++) {
        tag=uf.Obj.CycleAll(_part.Tag,tag);
        if(tag==Tag.Null)return tags.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        SpRequire(tags.Add(tag.ToString()),"Unexpected object-cycle repetition");
    }
    throw new Exception("Object inventory exceeded bounded fixture limit");
}
private static Spline SpCurve(Feature f) {
    var curves=f.GetEntities().OfType<Spline>().ToArray();
    SpRequire(curves.Length==1,"Expected exactly one feature-owned Spline");
    var c=curves[0];
    SpRequire(!c.IsOccurrence && c.OwningPart.Tag==_part.Tag,"Local spline required");
    return c;
}
private static Point[] SpPoints(Feature f) {
    var all=SpAllPoints();
    var ps=new Point[4];
    for(int i=0;i<4;i++) {
        var found=all.Where(p=>p.Name==f.Name+"_P"+i).ToArray();
        SpRequire(found.Length==1,"Missing or duplicate named control point");
        ps[i]=found[0];
        SpRequire(!ps[i].IsOccurrence && ps[i].OwningPart.Tag==_part.Tag,"Local point required");
    }
    return ps;
}
private static SpReceipt SpRead(Feature f) {return SpReadControls(f,SpPoints(f));}
private static SpReceipt SpReadControls(Feature f,Point[] ps) {
    var c=SpCurve(f);
    var uf=NXOpen.UF.UFSession.GetUFSession();
    IntPtr ev=IntPtr.Zero;double[] limits=new double[2];var samples=new List<double>();
    try {
        uf.Eval.Initialize(c.Tag,out ev);uf.Eval.AskLimits(ev,limits);
        SpRequire(limits.All(ParamFinite)&&limits[1]>limits[0],"Invalid curve limits");
        for(int i=0;i<=4;i++) {
            double[] p=new double[3];
            uf.Eval.Evaluate(ev,0,limits[0]+(limits[1]-limits[0])*i/4.0,p,new double[3]);
            samples.AddRange(p);
        }
    } finally {if(ev!=IntPtr.Zero)uf.Eval.Free(ev);}
    return new SpReceipt {
        feature=f.Tag.ToString(),name=f.Name,curve=c.Tag.ToString(),
        owners=_part.Features.ToArray().Where(x=>x.GetEntities().Any(e=>e.Tag==c.Tag))
            .Select(x=>x.Tag.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
        pointTags=ps.Select(p=>p.Tag.ToString()).ToArray(),pointNames=ps.Select(p=>p.Name).ToArray(),
        allPoints=SpPointInventory(),allCurves=SpCurveInventory(),
        degree=c.Order-1,periodic=c.Periodic,rational=c.Rational,knots=c.GetKnots(),
        poles4=c.GetPoles().SelectMany(p=>new double[]{p.X,p.Y,p.Z,p.W}).ToArray(),
        poles3=c.Get3DPoles().SelectMany(SpXYZ).ToArray(),
        controls=ps.SelectMany(p=>SpXYZ(p.Coordinates)).ToArray(),
        samples=samples.ToArray(),limits=limits};
}
private static void SpBasis(SpReceipt r) {
    SpRequire(r.degree==3&&!r.periodic&&!r.rational,"Only nonperiodic nonrational cubic supported");
    SpRequire(ParamNear(r.knots,SpKnots,1e-12)&&r.poles4.Length==16&&r.poles3.Length==12,
        "Exactly four poles and unit clamped knots required");
    SpRequire(r.owners.Length==1&&r.owners[0]==r.feature,"Unique StudioSpline owner required");
    SpRequire(r.pointTags.Length==4&&r.pointTags.Distinct().Count()==4&&
        Enumerable.Range(0,4).All(i=>r.allPoints.Contains(r.pointTags[i]+":"+r.pointNames[i])),
        "Exactly four distinct actual control points must be in native inventory");
    SpRequire(r.allCurves.Length==1,"No standalone or extra curves permitted in this fixture");
    SpRequire(r.controls.All(x=>ParamFinite(x)&&Math.Abs(x)<=10000),"Invalid control coordinates");
    SpRequire(ParamNear(r.poles3,r.controls,1e-8),"Actual normalized pole order differs from named controls");
    for(int i=0;i<4;i++) {
        SpRequire(Math.Abs(r.poles4[4*i+3]-1)<1e-12,"Non-unit weight rejected");
        for(int j=0;j<3;j++)
            SpRequire(Math.Abs(r.poles4[4*i+j]-r.controls[3*i+j])<1e-8,"Homogeneous pole mismatch");
    }
    for(int k=0;k<=4;k++) {
        double t=k/4.0,u=1-t;
        double[] weights={u*u*u,3*u*u*t,3*u*t*t,t*t*t};
        for(int j=0;j<3;j++) {
            double expected=0;for(int i=0;i<4;i++)expected+=weights[i]*r.controls[3*i+j];
            SpRequire(ParamFinite(r.samples[k*3+j])&&Math.Abs(expected-r.samples[k*3+j])<1e-7,
                "UF curve samples disagree with independent cubic Bernstein evaluation");
        }
    }
}
private static void SpBuilderCheck(StudioSplineBuilderEx b,Feature f,SpReceipt r) {
    SpRequire(b.Type==StudioSplineBuilderEx.Types.ByPoles&&b.Degree==3&&
        b.IsAssociative&&!b.IsPeriodic&&!b.HasPlaneConstraint&&
        !b.HasProportionalUpdate,"Unsupported StudioSpline builder configuration");
    SpRequire(ParamNear(b.GetKnots(),r.knots,1e-12),"Builder/curve knot mismatch");
    var cs=b.ConstraintManager.GetContents();
    SpRequire(cs.Length==4,"Exactly four existing G0 constraints required");
    for(int i=0;i<4;i++) {
        var d=cs[i];var p=d.Point;
        SpRequire(p!=null&&p.Tag.ToString()==r.pointTags[i]&&
            d.AutomaticConstraintType==GeometricConstraintData.AutoConstraintType.None&&
            !d.CanInferConstraintFromAttachmentParent&&!d.HasSymmetricModelingConstraint&&
            d.TangentDirection==null&&d.TangentMagnitude==null&&
            d.Curvature==null&&d.CurvatureDerivative==null,"Attached or constrained pole rejected");
        // Conservative: any reported smart-object parent (or query failure) rejects editing.
        var so=NXOpen.UF.UFSession.GetUFSession().So;
        bool smart;so.IsSo(p.Tag,out smart);
        if(smart) {
            int count;Tag[] parents;so.AskParents(p.Tag,64,out count,out parents);
            SpRequire(count==0&&(parents==null||parents.Length==0),"Control point is not free");
        }
        // Native non-smart points cannot have smart-object dependency parents.
        // Single segment is enforced by degree=3/four poles/unit clamped knots in SpBasis;
        // NX12 editing builder resets IsSingleSegment false for this actual curve.

    }
}
// Diagnostic values are materialized JSON strings BEFORE Destroy/Undo; no live NX handles escape.
private static object SpTryDiagnostic(Func<object> read) {
    try {return read();} catch(Exception e) {
        return new {diagnostic_error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0};
    }
}
private static object SpDiagnosticPoint(Point p) {
    if(p==null)return null;
    return new {tag=p.Tag.ToString(),name=p.Name,xyz=SpXYZ(p.Coordinates),
        occurrence=p.IsOccurrence,part=p.OwningPart.Tag.ToString()};
}
private static object SpDiagnosticConstraints(StudioSplineBuilderEx b) {
    object constraints=SpTryDiagnostic(()=>b.ConstraintManager.GetContents()
        .Select((d,i)=>SpTryDiagnostic(()=>new {
            index=i,point=SpTryDiagnostic(()=>SpDiagnosticPoint(d.Point)),
            automatic=d.AutomaticConstraintType.ToString(),infer=d.CanInferConstraintFromAttachmentParent,
            symmetric=d.HasSymmetricModelingConstraint,
            tangent_direction=d.TangentDirection==null?null:d.TangentDirection.Tag.ToString(),
            tangent_magnitude=d.TangentMagnitude==null?null:d.TangentMagnitude.Tag.ToString(),
            curvature=d.Curvature==null?null:d.Curvature.Tag.ToString(),
            flow=d.CurvatureDerivative==null?null:d.CurvatureDerivative.Tag.ToString(),
            parents=SpTryDiagnostic(()=>{
                var p=d.Point;if(p==null)return null;
                int n;Tag[] tags;NXOpen.UF.UFSession.GetUFSession().So.AskParents(p.Tag,64,out n,out tags);
                return new {count=n,tags=tags==null?null:tags.Select(t=>t.ToString()).ToArray()};
            })
        })).ToArray());
    object configuration=SpTryDiagnostic(()=>new {type=b.Type.ToString(),degree=b.Degree,
        associative=b.IsAssociative,periodic=b.IsPeriodic,single_segment=b.IsSingleSegment,
        plane=b.HasPlaneConstraint,proportional=b.HasProportionalUpdate,knots=b.GetKnots()});
    return new {configuration=configuration,constraints=constraints};
}
private static void SpCapture(List<string> trace,string phase,Feature f,StudioSplineBuilderEx live) {
    // Diagnostic query failures are evidence, never a replacement for the original error or rollback.
    try {
        object points=SpTryDiagnostic(()=>SpAllPoints()
            .Select(p=>SpTryDiagnostic(()=>SpDiagnosticPoint(p))).ToArray());
        object curves=SpTryDiagnostic(()=>_part.Curves.ToArray().Select(c=>SpTryDiagnostic(()=>new {
            tag=c.Tag.ToString(),name=c.Name,type=c.GetType().FullName,
            native=c is Spline?SpTryDiagnostic(()=>{
                var sp=(Spline)c;return new {degree=sp.Order-1,periodic=sp.Periodic,rational=sp.Rational,
                    knots=sp.GetKnots(),poles3=sp.Get3DPoles().SelectMany(SpXYZ).ToArray(),
                    poles4=sp.GetPoles().SelectMany(p=>new double[]{p.X,p.Y,p.Z,p.W}).ToArray()};
            }):null
        })).ToArray());
        object features=SpTryDiagnostic(()=>_part.Features.ToArray().Select(x=>SpTryDiagnostic(()=>new {
            tag=x.Tag.ToString(),name=x.Name,type=x.FeatureType,
            entities=x.GetEntities().Select(e=>new {tag=e.Tag.ToString(),name=e.Name,type=e.GetType().FullName}).ToArray(),
            parents=x.GetParents().Select(p=>p.Tag.ToString()).ToArray(),
            children=x.GetChildren().Select(p=>p.Tag.ToString()).ToArray(),
            errors=x.GetFeatureErrorMessages(),warnings=x.GetFeatureWarningMessages()
        })).ToArray());
        object constraints=null;
        if(live!=null)constraints=SpTryDiagnostic(()=>SpDiagnosticConstraints(live));
        else if(f!=null)constraints=SpTryDiagnostic(()=>{
            var q=_part.Features.CreateStudioSplineBuilderEx(f);
            try {return SpDiagnosticConstraints(q);} finally {q.Destroy();}
        });
        trace.Add(Json(new {phase=phase,points=points,curves=curves,features=features,
            constraints=constraints,inspection_only_no_commit=true}));
    } catch(Exception e) {trace.Add(Json(new {phase=phase,diagnostic_error=e.Message}));}
}
private static SpReceipt SpInitializeCommittedControls(Feature f,double[] expected,string[] baselinePoints,List<string> trace) {
    var q=_part.Features.CreateStudioSplineBuilderEx(f);
    try {
        SpCapture(trace,"fixture.committed.before_names",f,q);
        var cs=q.ConstraintManager.GetContents();
        SpRequire(cs.Length==4,"Exactly four committed constraints required");
        var ps=cs.Select(d=>d.Point).ToArray();
        SpRequire(ps.All(p=>p!=null&&!p.IsOccurrence&&p.OwningPart.Tag==_part.Tag),
            "Committed controls must be existing local points");
        SpRequire(ps.Select(p=>p.Tag.ToString()).Distinct().Count()==4,"Duplicate committed controls rejected");
        var all=SpAllPoints();
        // UF cycle_all also returns NX view/camera points (16 in actual empty fixture).
        // Preserve the entire pre-existing inventory and require exactly four new controls.
        string[] added=SpPointInventory().Except(baselinePoints).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        string[] expectedAdded=ps.Select(p=>p.Tag.ToString()+":"+p.Name).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        SpRequire(all.Length==baselinePoints.Length+4 && baselinePoints.All(x=>SpPointInventory().Contains(x))&&
            added.SequenceEqual(expectedAdded),"New native points must be exactly the four controls; baseline must remain unchanged");
        // Validate actual order; do not sort/reorder points to manufacture a match.
        var raw=SpReadControls(f,ps);SpBasis(raw);SpBuilderCheck(q,f,raw);
        SpRequire(ParamNear(raw.controls,expected,1e-8),"Committed controls differ from intended ordered fixture");
        for(int i=0;i<4;i++)ps[i].SetName(f.Name+"_P"+i);
        var named=SpRead(f);SpBasis(named);SpBuilderCheck(q,f,named);
        SpRequire(raw.pointTags.SequenceEqual(named.pointTags),"Naming changed control identity/order");
        SpCapture(trace,"fixture.committed.after_names",f,q);
        return named;
    } catch(Exception) {SpCapture(trace,"fixture.initialize.failed_before_destroy",f,q);throw;}
    finally {q.Destroy();}
}

private static void SpUpdate(Session s,Session.UndoMarkId mark) {
    SpRequire(s.UpdateManager.DoUpdate(mark)==0,"Native update failed");
    s.UpdateManager.MakeUpToDate(_part.Features.ToArray().Cast<NXObject>().ToArray(),mark);
    SpRequire(s.UpdateManager.DoUpdate(mark)==0&&s.UpdateManager.ErrorList.Length==0,
        "Downstream native update failed");
}
private static bool SpSame(SpReceipt a,SpReceipt b,bool exactCurve) {
    return a.feature==b.feature&&a.name==b.name&&(!exactCurve||a.curve==b.curve)&&
        a.owners.SequenceEqual(b.owners)&&a.pointTags.SequenceEqual(b.pointTags)&&
        a.pointNames.SequenceEqual(b.pointNames)&&a.allPoints.SequenceEqual(b.allPoints)&&
        (!exactCurve||a.allCurves.SequenceEqual(b.allCurves))&&
        a.degree==b.degree&&a.periodic==b.periodic&&a.rational==b.rational&&
        ParamNear(a.knots,b.knots,1e-12)&&ParamNear(a.limits,b.limits,1e-12);
}
private static string SurfaceSplineState(string[] a) {
    SpRequire(a.Length==1,"nx_surface_spline_state");SpGuard(false);
    var fs=_part.Features.ToArray().Where(f=>f.GetEntities().OfType<Spline>().Any()).ToArray();
    var states=fs.Select(SpRead).ToArray();
    return Json(new{ok=true,read_only=true,splines=states,state=ParamSnapshot(),native_points=SpAllPoints().Select(p=>new{tag=p.Tag.ToString(),name=p.Name,xyz=SpXYZ(p.Coordinates)}).ToArray(),
        unsafe_edit_latch=_paramUnsafe,volume_mm3=(double?)null,
        sample_fractions=new double[]{0,.25,.5,.75,1},
        note="Actual NX queries only; no builder created or committed; finite samples are not global bounds"});
}
private static string SpFailure(Exception error,Session s,Session.UndoMarkId mark,
    ParamReceipt before,SpReceipt spline,string[] points,string[] curves,string[] objects,
    string phase,List<string> diagnostics,Feature diagnosticFeature) {
    SpCapture(diagnostics,phase+".before_undo",diagnosticFeature,null);
    bool restored=false;string rollbackError=null;ParamReceipt actual=null;SpReceipt actualSpline=null;
    object handles=null;
    try {
        s.UndoToMark(mark,null);actual=ParamSnapshot();
        restored=ParamRestored(before,actual)&&points.SequenceEqual(SpPointInventory())&&
            curves.SequenceEqual(SpCurveInventory())&&objects.SequenceEqual(SpObjectInventory())&&
            s.UpdateManager.ErrorList.Length==0;
        if(spline!=null) {
            actualSpline=SpRead(SpFeature(spline.name));SpBasis(actualSpline);ParamHealthy(actual);
            restored=restored&&SpSame(spline,actualSpline,true)&&
                ParamNear(spline.controls,actualSpline.controls,1e-9)&&
                ParamNear(spline.poles3,actualSpline.poles3,1e-9)&&
                ParamNear(spline.poles4,actualSpline.poles4,1e-9)&&
                ParamNear(spline.samples,actualSpline.samples,1e-8);
        }
        if(!restored)rollbackError="Post-undo inventory/basis/control/geometry mismatch";
        if(restored)handles=ParamRefreshBodies();
    } catch(Exception e) {restored=false;rollbackError=e.Message;}
    if(!restored){_paramUnsafe=true;_bodies.Clear();}
    return Json(new{ok=false,error=error.Message,
        nx_error_code=error is NXException?((NXException)error).ErrorCode:0,
        rolled_back=restored,rollback_error=rollbackError,before=before,actual=actual,
        spline_before=spline,spline_actual=actualSpline,body_handles=handles,
        failure_phase=phase,pre_undo_diagnostics_json=diagnostics.ToArray(),
        rollback_scope="Exact recorded identities and numeric witnesses, not full shape equivalence",
        safe_pause_required=true,retry_automatically=false});
}
private static string SurfaceSplineFixture(string[] a) {
    SpRequire(a.Length==2,"nx_surface_spline_fixture NAME");SpGuard(true);SweepToken(a[1]);
    SpRequire(a[1].Length<=50,"Name too long for generated control identifiers");
    var before=ParamSnapshot();var points=SpPointInventory();var curves=SpCurveInventory();var objects=SpObjectInventory();
    SpRequire(before.features.Length==0&&before.bodyTags.Length==0&&_part.Points.ToArray().Length==0&&curves.Length==0,
        "Fresh empty part required; duplicate fixture rejected");
    var s=Session.GetSession();var mark=s.SetUndoMark(Session.MarkVisibility.Visible,"Spline fixture");
    string phase="fixture.create_builder";Feature f=null;var diagnostics=new List<string>();
    Point3d[] xyz={new Point3d(0,0,0),new Point3d(25,8,0),new Point3d(55,-8,0),new Point3d(80,0,0)};
    try {
        var b=_part.Features.CreateStudioSplineBuilderEx(null);
        try {
            b.Type=StudioSplineBuilderEx.Types.ByPoles;b.Degree=3;b.IsAssociative=true;
            b.IsPeriodic=false;b.IsSingleSegment=true;b.HasProportionalUpdate=false;b.HasPlaneConstraint=false;
            phase="fixture.initial_controls";
            for(int i=0;i<4;i++) {
                var p=_part.Points.CreatePoint(xyz[i]);p.SetName(a[1]+"_P"+i);
                var d=b.ConstraintManager.CreateGeometricConstraintData();d.Point=p;
                d.AutomaticConstraintType=GeometricConstraintData.AutoConstraintType.None;
                d.CanInferConstraintFromAttachmentParent=false;d.HasSymmetricModelingConstraint=false;
                b.ConstraintManager.Append(d);
            }
            b.SetKnots(SpKnots);SpCapture(diagnostics,"fixture.initial.before_commit",null,b);
            phase="fixture.commit";f=b.CommitFeature();
            SpCapture(diagnostics,"fixture.after_commit.before_destroy",f,b);
            phase="fixture.name_feature";f.SetName(a[1]);
            SpRequire(f.Name==a[1],"Feature name readback mismatch");
        } catch(Exception) {SpCapture(diagnostics,phase+".failed_before_destroy",f,b);throw;}
        finally {b.Destroy();}
        phase="fixture.update_spline";SpUpdate(s,mark);
        phase="fixture.initialize_committed_controls";
        var receipt=SpInitializeCommittedControls(f,xyz.SelectMany(SpXYZ).ToArray(),points,diagnostics);
        phase="fixture.create_extrude";
        BodyFeature extrude;var eb=_part.Features.CreateExtrudeBuilder(null);
        try {
            eb.Section=_part.Sections.CreateSection(0.001,0.001,0.5);
            phase="spline.extrude.section";
            SweepSection(eb.Section,SpCurve(f),new Point3d(0,0,0),new Vector3d(25,8,0),false,"section",ref phase);
            eb.Direction=_part.Directions.CreateDirection(new Point3d(0,0,0),new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);
            eb.FeatureOptions.BodyType=NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Sheet;
            eb.BooleanOperation.Type=NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create;
            eb.Limits.StartExtend.Value.RightHandSide="0";eb.Limits.EndExtend.Value.RightHandSide="12";
            eb.AllowSelfIntersectingSection(false);
            phase="fixture.extrude.commit";extrude=(BodyFeature)eb.CommitFeature();extrude.SetName(a[1]+"_EXTRUDE");
        } finally {eb.Destroy();}
        phase="fixture.extrude.update_and_verify";SpUpdate(s,mark);var after=ParamSnapshot();ParamHealthy(after);
        var bodies=extrude.GetBodies();
        SpRequire(bodies.Length==1&&!bodies[0].IsSolidBody,"Expected one native sheet extrusion");
        SpRequire(extrude.GetParents().Any(p=>p.Tag==f.Tag),"Native spline -> Extrude dependency missing");
        SpRequire(after.features.Length==2,"Unexpected extra features");
        receipt=SpRead(f);SpBasis(receipt);
        return Json(new{ok=true,state=after,spline=receipt,initial_and_committed_diagnostics_json=diagnostics.ToArray(),body_handles=ParamRefreshBodies(),
            saved=false,native_acceptance="NOT_RUN_BY_SOURCE_STAGE",next="Separate nx_surface_thicken BODY_ID 2"});
    } catch(Exception e) {return SpFailure(e,s,mark,before,null,points,curves,objects,phase,diagnostics,f);}
}
private static string SurfaceEditPole(string[] a) {
    SpRequire(a.Length==6,"nx_surface_edit_pole FEATURE INDEX X Y Z");SpGuard(true);
    int index;SpRequire(Int32.TryParse(a[2],out index)&&index>=0&&index<4,"Index must be 0..3");
    double[] xyz=a.Skip(3).Select(x=>D(x)).ToArray();
    SpRequire(xyz.All(x=>ParamFinite(x)&&Math.Abs(x)<=10000),"Finite bounded coordinates required");
    // This fixture is planar; out-of-plane experiments are not authorized by this command.
    SpRequire(Math.Abs(xyz[2])<1e-12,"Planar fixture requires Z=0");
    Feature f=SpFeature(a[1]);var before=ParamSnapshot();ParamHealthy(before);
    var old=SpRead(f);SpBasis(old);
    var points=SpPointInventory();var curves=SpCurveInventory();var objects=SpObjectInventory();
    var wanted=(double[])old.controls.Clone();Array.Copy(xyz,0,wanted,index*3,3);
    SpRequire(!ParamNear(wanted,old.controls,1e-10),"No-op edit rejected");
    var ex=_part.Features.ToArray().OfType<BodyFeature>()
        .Where(x=>x.Name==f.Name+"_EXTRUDE"&&x.GetParents().Any(p=>p.Tag==f.Tag)).ToArray();
    SpRequire(ex.Length==1,"Expected existing native dependent Extrude");
    var thick=_part.Features.ToArray().OfType<BodyFeature>().Where(x=>
        x.FeatureType.IndexOf("THICK",StringComparison.OrdinalIgnoreCase)>=0&&
        x.GetParents().Any(p=>p.Tag==ex[0].Tag)&&x.GetBodies().Any(b=>b.IsSolidBody)).ToArray();
    SpRequire(thick.Length==1,"Separate downstream native Thicken required before editing");
    var s=Session.GetSession();var mark=s.SetUndoMark(Session.MarkVisibility.Visible,"Edit native spline pole");
    string phase="edit.create_builder";var diagnostics=new List<string>();
    try {
        var b=_part.Features.CreateStudioSplineBuilderEx(f);
        try {
            phase="edit.validate_existing";SpBuilderCheck(b,f,old);
            SpCapture(diagnostics,"edit.before_set_coordinates",f,b);
            phase="edit.set_coordinates";
            var p=b.ConstraintManager.GetContents()[index].Point;
            p.SetCoordinates(new Point3d(xyz[0],xyz[1],xyz[2]));
            phase="edit.commit";var committed=b.CommitFeature();
            SpCapture(diagnostics,"edit.after_commit.before_destroy",committed,b);
            SpRequire(committed.Tag==f.Tag,"StudioSpline feature identity changed");
        } catch(Exception) {SpCapture(diagnostics,phase+".failed_before_destroy",f,b);throw;}
        finally {b.Destroy();}
        phase="edit.update";SpUpdate(s,mark);
        phase="edit.verify";
        var after=ParamSnapshot();ParamHealthy(after);var next=SpRead(SpFeature(a[1]));SpBasis(next);
        var check=_part.Features.CreateStudioSplineBuilderEx(f);
        try {SpBuilderCheck(check,f,next);} finally {check.Destroy();}
        SpRequire(ParamIdentity(before,after)&&SpSame(old,next,false),"Feature/point identity or dependency changed");
        SpRequire(ParamNear(next.controls,wanted,1e-8),"Control coordinates did not match requested edit");
        foreach(var target in new BodyFeature[]{ex[0],thick[0]}) {
            var tag=target.Tag.ToString();
            SpRequire(!ParamNear(before.features.Single(x=>x.tag==tag).geometry,
                after.features.Single(x=>x.tag==tag).geometry,1e-7),"Downstream feature geometry did not change");
        }
        SpRequire(!ParamNear(before.geometry,after.geometry,1e-7),"Terminal body geometry did not change");
        return Json(new{ok=true,before=before,after=after,spline_before=old,spline_after=next,
            edit_diagnostics_json=diagnostics.ToArray(),
            owned_spline_tag_replaced=old.curve!=next.curve,body_handles=ParamRefreshBodies(),
            old_body_handles_invalidated=true,saved=false,native_acceptance="NOT_RUN_BY_SOURCE_STAGE",
            scope="Native readback and finite witnesses; independent accuracy=2 volume and cold reopen required"});
    } catch(Exception e) {return SpFailure(e,s,mark,before,old,points,curves,objects,phase,diagnostics,f);}
}

// Append inside loader class; existing helpers remain unchanged. SOURCE ONLY / NOTRUN.
// Requires phase5_surface.cs and phase5_parametric.cs (hashes in artifact).
private const string GsRoot=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\advanced_general_sweep_20261109";
private static bool _gsUnsafe;
private sealed class GsControl {
 public string featureTag,featureName,featureType,scaling,blending,orientation,law,bodyStyle,location,alignment;
 public string[] entities,parents,children;
 public double startScale,endScale,startAngle,endAngle,g0,g1;
 public int sections,guides;
 public bool preserveShape,preserveGuide;
}
private sealed class GsSnapshot {
 public ParamReceipt model;
 public string[] objects,expressions;
 public GsControl[] controls;
}
private static void GsGuard(bool write) {
 if(_part==null||String.IsNullOrEmpty(_part.FullPath))throw new Exception("Saved experiment part required");
 string full=Path.GetFullPath(_part.FullPath);SafePath(full);
 if(!String.Equals(Path.GetDirectoryName(full),GsRoot,StringComparison.OrdinalIgnoreCase)||
 !Path.GetFileNameWithoutExtension(full).StartsWith("phase4_gs_",StringComparison.Ordinal)||
 !String.Equals(Path.GetExtension(full),".prt",StringComparison.OrdinalIgnoreCase))
 throw new Exception("Dedicated general sweep directory and phase4_gs_* required");
 SweepToken(Path.GetFileNameWithoutExtension(full));
 if(!File.Exists(full)||_part.PartUnits!=BasePart.Units.Millimeters)throw new Exception("Existing saved MM experiment required");
 for(DirectoryInfo d=new DirectoryInfo(Path.GetDirectoryName(full));d!=null;d=d.Parent)
 if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new Exception("Reparse directory rejected");
 if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new Exception("Reparse file rejected");
 var s=Session.GetSession();int n=0;
 foreach(BasePart p in s.Parts){n++;if(p.Tag!=_part.Tag)throw new Exception("Only dedicated part may be loaded");}
 if(n!=1||s.Parts.Work!=_part||s.Parts.Display!=_part)throw new Exception("Exactly one work/display part required");
 if(write) {
  if(_gsUnsafe)throw new Exception("Unsafe latch: coordinator recovery required; no automatic reset");
  MechanismPart();
  if(Convert.ToInt32(NXOpen.UF.UFSession.GetUFSession().Part.IsModified(_part.Tag))!=0)
   throw new Exception("Save dedicated part before each mutation; this command never saves");
  if(s.UpdateManager.GetUpdateLock()||s.UpdateManager.ErrorList.Length!=0)throw new Exception("Pending update state rejected");
 }
}
private static string[] GsObjects() {
 var uf=NXOpen.UF.UFSession.GetUFSession();var tags=new List<string>();var seen=new HashSet<Tag>();Tag t=Tag.Null;
 while((t=uf.Obj.CycleAll(_part.Tag,t))!=Tag.Null) {
  if(!seen.Add(t)||seen.Count>100000)throw new Exception("Invalid/excessive UF object cycle");
  int type,sub;uf.Obj.AskTypeAndSubtype(t,out type,out sub);
  tags.Add(t.ToString()+":"+type+":"+sub);
 }
 return tags.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
}
private static GsControl GsRead(NXOpen.Features.Swept f) {
 NXOpen.Features.SweptBuilder b=null;
 try {
  b=_part.Features.CreateSweptBuilder(f);
  var sm=b.ScalingMethod;var om=b.OrientationMethod;var law=om.AngularLaw;
  return new GsControl{
   featureTag=f.Tag.ToString(),featureName=f.Name,featureType=f.FeatureType,
   scaling=sm.ScalingOption.ToString(),blending=sm.BlendingFunctionType.ToString(),
   orientation=om.OrientationOption.ToString(),law=law.LawType.ToString(),
   bodyStyle=b.BodyPreference.BodyType.ToString(),location=b.SectionLocation.ToString(),
   alignment=b.AlignmentMethod.AlignType.ToString(),startScale=sm.StartBlendScaleFactor,
   endScale=sm.EndBlendScaleFactor,startAngle=law.StartValue.GetValueUsingUnits(Expression.UnitsOption.Expression),
   endAngle=law.EndValue.GetValueUsingUnits(Expression.UnitsOption.Expression),
   g0=b.G0Tolerance,g1=b.G1Tolerance,sections=b.SectionList.Length,guides=b.GuideList.Length,
   preserveShape=b.PreserveShapeOption,preserveGuide=b.PreserveGuideShapeOption,
   entities=f.GetEntities().Select(x=>x.Tag.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
   parents=f.GetParents().Select(x=>x.Tag.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
   children=f.GetChildren().Select(x=>x.Tag.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray()
  };
 } finally {if(b!=null)b.Destroy();}
}
private static GsSnapshot GsCapture() {
 var controls=new List<GsControl>();
 foreach(Feature f in _part.Features) {
  if(!(f is NXOpen.Features.Swept))throw new Exception("Only this initial Swept feature family allowed");
  if(f.Suppressed||f.GetFeatureErrorMessages().Length!=0||f.GetFeatureWarningMessages().Length!=0)
   throw new Exception("Native feature health failed");
  controls.Add(GsRead((NXOpen.Features.Swept)f));
 }
 foreach(Curve c in _part.Curves)if(!(c is Ellipse)&&!(c is Arc))throw new Exception("Unsupported curve in isolated part");
 return new GsSnapshot{model=ParamSnapshot(),controls=controls.OrderBy(x=>x.featureTag,StringComparer.Ordinal).ToArray(),
  objects=GsObjects(),expressions=_part.Expressions.ToArray().Select(x=>x.Tag+":"+x.Name+":"+x.RightHandSide+":"+
  x.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).OrderBy(x=>x,StringComparer.Ordinal).ToArray()};
}
private static bool GsRestored(GsSnapshot a,GsSnapshot b) {
 return ParamRestored(a.model,b.model)&&a.objects.SequenceEqual(b.objects)&&
 a.expressions.SequenceEqual(b.expressions)&&Json(a.controls)==Json(b.controls);
}
private static string GsAbort(Exception e,Session s,Session.UndoMarkId mark,GsSnapshot before,string cleanup) {
 GsSnapshot actual=null;bool restored=false;string failure=null;
 try {
  s.UndoToMark(mark,null);actual=GsCapture();
  restored=GsRestored(before,actual)&&s.UpdateManager.ErrorList.Length==0&&cleanup==null;
  if(!restored)failure="Undo snapshot/control/object/health mismatch or cleanup failure";
 }catch(Exception r){failure=r.Message;}
 if(!restored)_gsUnsafe=true;
 _bodies.Clear();_surfaceRefToken=null;_surfaceRefState=null;
 return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,
  before=before,actual=actual,rolled_back=restored,rollback_error=failure,cleanup_error=cleanup,
  unsafe_latched=_gsUnsafe,safe_pause_required=true,retry_automatically=false,
  rollback_scope="NX undo plus all UF object identities, expressions, native controls, dependencies and finite geometry witnesses; not mathematical shape equivalence"});
}
private static void GsUpdate(Session s,Session.UndoMarkId mark) {
 if(s.UpdateManager.DoUpdate(mark)!=0)throw new Exception("DoUpdate failed");
 s.UpdateManager.MakeUpToDate(_part.Features.ToArray().Cast<NXObject>().ToArray(),mark);
 if(s.UpdateManager.DoUpdate(mark)!=0||s.UpdateManager.ErrorList.Length!=0)throw new Exception("Downstream update failed");
}
private static void GsControls(NXOpen.Features.SweptBuilder b,double scale,double twist) {
 b.ScalingMethod.ScalingOption=NXOpen.GeometricUtilities.ScalingMethodBuilder.ScalingOptions.ByBlendingFunction;
 b.ScalingMethod.BlendingFunctionType=NXOpen.GeometricUtilities.ScalingMethodBuilder.BlendingFunctionTypes.Linear;
 b.ScalingMethod.StartBlendScaleFactor=1;b.ScalingMethod.EndBlendScaleFactor=scale;
 b.OrientationMethod.OrientationOption=NXOpen.GeometricUtilities.OrientationMethodBuilder.OrientationOptions.ByAngularLaw;
 var law=b.OrientationMethod.AngularLaw;law.LawType=NXOpen.GeometricUtilities.LawBuilder.Type.Linear;
 // Explicit unit expression; readback below checks expression units are degrees.
 if(law.StartValue.Units==null||law.EndValue.Units==null||
 !String.Equals(law.StartValue.Units.Name,"Degrees",StringComparison.OrdinalIgnoreCase)||
 !String.Equals(law.EndValue.Units.Name,"Degrees",StringComparison.OrdinalIgnoreCase))
 throw new Exception("Unsupported angular expression unit: require native Degrees; do not guess conversion");
 law.StartValue.RightHandSide="0";
 law.EndValue.RightHandSide=twist.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
}
private static void GsCheckControl(GsControl r,double scale,double twist,bool solid) {
 if(r.scaling!="ByBlendingFunction"||r.blending!="Linear"||r.orientation!="ByAngularLaw"||r.law!="Linear"||
 r.sections!=1||r.guides!=1||r.location!="EndsOfGuides"||r.alignment!="Parameter"||
 r.bodyStyle!=(solid?"Solid":"Sheet")||r.preserveShape||
 !ParamNear(new double[]{r.startScale,r.endScale,r.startAngle,r.endAngle},new double[]{1,scale,0,twist},1e-9))
 throw new Exception("Reopened native builder controls differ from requested contract");
}
private static Body GsBody(NXOpen.Features.Swept f,bool solid) {
 Body[] bs=f.GetBodies();
 if(bs.Length!=1||bs[0].IsSolidBody!=solid||bs[0].GetFaces().Length==0||
 _part.Bodies.ToArray().Length!=1||f.GetFeatureErrorMessages().Length!=0||
 f.GetFeatureWarningMessages().Length!=0||f.Suppressed)throw new Exception("Native Swept body/type/feature health failure");
 SurfaceHealthy(bs[0]);return bs[0];
}
private static void GsInputs(Curve profile,Arc guide,bool solid) {
 if(guide==null||profile.Tag==guide.Tag||profile.OwningPart.Tag!=_part.Tag||guide.OwningPart.Tag!=_part.Tag)
 throw new Exception("Distinct current local profile and circular arc guide required");
 if(solid?!(profile is Ellipse):!(profile is Arc))throw new Exception("Initial scope: closed ellipse solid or open arc sheet");
 var c=(Conic)profile;
 double radius=profile is Ellipse?((Ellipse)profile).MajorRadius:((Arc)profile).Radius;
 double span=profile is Ellipse?((Ellipse)profile).EndAngle-((Ellipse)profile).StartAngle:
 ((Arc)profile).EndAngle-((Arc)profile).StartAngle;
 if(!(radius>=1&&radius<=10)||!(guide.Radius>=80&&guide.Radius<=150)||
 !(guide.EndAngle-guide.StartAngle>=0.1&&guide.EndAngle-guide.StartAngle<=Math.PI/2)||
 (solid?Math.Abs(span-2*Math.PI)>1e-8:!(span>=0.1&&span<=Math.PI)))
 throw new Exception("Unsupported profile/guide bounds");
 if(profile is Ellipse&&!(((Ellipse)profile).MinorRadius>=1&&((Ellipse)profile).MinorRadius<=radius))
 throw new Exception("Invalid minor radius");
 Point3d p;Vector3d t;SweepEval(guide,0,out p,out t);
 var delta=SweepDelta(c.CenterPoint,p);var m=c.Matrix.Element;
 if(SweepDot(delta,delta)>1e-6||Math.Abs(SweepDot(SweepUnit(new Vector3d(m.Zx,m.Zy,m.Zz)),t))<1-1e-8)
 throw new Exception("Profile must be centered at forward guide start and normal to initial tangent");
}
private static string SurfaceSweepGeneralFixture(string[] a) {
 if(a.Length!=3||(a[2]!="solid"&&a[2]!="sheet"))throw new Exception("nx_surface_sweep_general_fixture NAME solid|sheet");
 GsGuard(true);SweepToken(a[1]);SweepUnused(a[1]);SweepUnused(a[1]+"_section");SweepUnused(a[1]+"_guide");
 if(_part.Curves.ToArray().Length!=0||_part.Features.ToArray().Length!=0||_part.Bodies.ToArray().Length!=0)
 throw new Exception("Fixture requires empty fresh part");
 var before=GsCapture();var s=Session.GetSession();var mark=s.SetUndoMark(Session.MarkVisibility.Visible,"General sweep fixture");
 try {
  var g=_part.Curves.CreateArc(new Point3d(0,100,0),new Vector3d(0,-1,0),new Vector3d(1,0,0),100,0,Math.PI/3);
  g.SetName(a[1]+"_guide");
  Curve p=a[2]=="solid"?(Curve)_part.Curves.CreateEllipse(new Point3d(0,0,0),new Vector3d(0,1,0),new Vector3d(0,0,1),8,3,0,2*Math.PI):
   (Curve)_part.Curves.CreateArc(new Point3d(0,0,0),new Vector3d(0,1,0),new Vector3d(0,0,1),8,-Math.PI/3,Math.PI/3);
  p.SetName(a[1]+"_section");GsInputs(p,g,a[2]=="solid");GsUpdate(s,mark);
  var after=GsCapture();
  if(after.model.curves.Length!=2||after.model.features.Length!=0||after.model.bodyTags.Length!=0)throw new Exception("Unexpected fixture content");
  return Json(new{ok=true,fixture_only=true,section_name=p.Name,guide_name=g.Name,actual=after,saved=false,
   native_acceptance="NOTRUN",purpose="Original nonfunctional ornamental curved rail; no sweep yet"});
 }catch(Exception e){return GsAbort(e,s,mark,before,null);}
}
private static string SurfaceSweepGeneral(string[] a) {
 if(a.Length!=7||(a[4]!="solid"&&a[4]!="sheet"))
 throw new Exception("nx_surface_sweep_general NAME SECTION GUIDE solid|sheet END_SCALE TWIST_DEG");
 GsGuard(true);SweepUnused(a[1]);bool solid=a[4]=="solid";
 double scale=SurfaceNumber(a[5],0.5,1.5),twist=SurfaceNumber(a[6],-30,30);
 bool vectorFixture=String.Equals(Path.GetFileNameWithoutExtension(_part.FullPath),"phase4_gs_frame_r1",StringComparison.OrdinalIgnoreCase);
 if(vectorFixture&&(!solid||twist!=0))throw new Exception("Vector diagnostic requires solid and zero twist");
 Curve profile=SweepCurve(a[2]);Arc guide=SweepCurve(a[3]) as Arc;GsInputs(profile,guide,solid);
 if(_part.Curves.ToArray().Length!=2||_part.Features.ToArray().Length!=0||_part.Bodies.ToArray().Length!=0)
 throw new Exception("Initial scope requires exactly two fixture curves and no existing body/features");
 var before=GsCapture();var s=Session.GetSession();var mark=s.SetUndoMark(Session.MarkVisibility.Visible,"General native Swept");
 NXOpen.Features.SweptBuilder b=null;Section sec=null,gs=null;
 try {
  b=_part.Features.CreateSweptBuilder(null);b.G0Tolerance=0.001;b.G1Tolerance=0.5;
  b.SectionLocation=NXOpen.Features.SweptBuilder.SectionLocationTypes.EndsOfGuides;
  b.AlignmentMethod.AlignType=NXOpen.GeometricUtilities.AlignmentMethodBuilder.Type.Parameter;
  b.BodyPreference.BodyType=solid?NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Solid:NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Sheet;
  b.PreserveShapeOption=false;b.PreserveGuideShapeOption=false;
  sec=_part.Sections.CreateSection();gs=_part.Sections.CreateSection();
  b.SectionList.Append(sec);b.GuideList.Append(gs);
  Point3d p,q;Vector3d t,u;SweepEval(profile,solid?0.5:0,out p,out t);SweepEval(guide,0,out q,out u);
  string phase="configure";SweepSection(sec,profile,p,t,solid,"section",ref phase);
  SweepSection(gs,guide,q,u,false,"guide",ref phase);GsControls(b,scale,twist);
  if(vectorFixture) {
   b.OrientationMethod.OrientationOption=NXOpen.GeometricUtilities.OrientationMethodBuilder.OrientationOptions.ByVectorDirection;
   b.OrientationMethod.OrientationVector=_part.Directions.CreateDirection(q,new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);
  }
  var f=b.CommitFeature() as NXOpen.Features.Swept;
  if(f==null)throw new Exception("Expected actual native Swept feature");
  f.SetName(a[1]);b.Destroy();b=null;GsUpdate(s,mark);
  Body body=GsBody(f,solid);var actual=GsRead(f);
  if(vectorFixture) {
   if(actual.orientation!="ByVectorDirection"||actual.sections!=1||actual.guides!=1||actual.bodyStyle!="Solid"||
      actual.scaling!="ByBlendingFunction"||actual.blending!="Linear"||
      !ParamNear(new double[]{actual.startScale,actual.endScale},new double[]{1,scale},1e-9))
    throw new Exception("Vector fixture native controls mismatch");
  } else GsCheckControl(actual,scale,twist,solid);
  var after=GsCapture();
  if(Json(before.model.curves)!=Json(after.model.curves)||after.model.features.Length!=1)
   throw new Exception("Fixture curves changed or unexpected feature");
  return Json(new{ok=true,feature_name=f.Name,feature_tag=f.Tag.ToString(),native_controls=actual,
   body_tag=body.Tag.ToString(),solid=body.IsSolidBody,before=before,after=after,body_handles=ParamRefreshBodies(),
   saved=false,native_acceptance="NOTRUN",scope="Native receipt only; continuous scaling/twist, volume and cold reopen require independent validation"});
 }catch(Exception e) {
  string cleanup=null;if(b!=null)try{b.Destroy();b=null;}catch(Exception d){cleanup=d.Message;}
  // Sections and temporary objects belong to the undo scope; verify entire UF inventory after undo.
  return GsAbort(e,s,mark,before,cleanup);
 }
}
private static string SurfaceSweepGeneralEdit(string[] a) {
 if(a.Length!=4)throw new Exception("nx_surface_sweep_general_edit FEATURE END_SCALE TWIST_DEG");
 GsGuard(true);SweepToken(a[1]);
 double scale=SurfaceNumber(a[2],0.5,1.5),twist=SurfaceNumber(a[3],-30,30);
 var fs=_part.Features.ToArray();
 if(fs.Length!=1||!(fs[0] is NXOpen.Features.Swept)||fs[0].Name!=a[1]||fs[0].GetChildren().Length!=0)
 throw new Exception("Initial edit scope: sole named native Swept, no downstream children");
 var f=(NXOpen.Features.Swept)fs[0];var before=GsCapture();var old=before.controls.Single();
 bool solid=old.bodyStyle=="Solid";GsCheckControl(old,old.endScale,old.endAngle,solid);
 if(Math.Abs(old.endScale-scale)<1e-10&&Math.Abs(old.endAngle-twist)<1e-10)throw new Exception("No-op rejected");
 GsBody(f,solid);
 var s=Session.GetSession();var mark=s.SetUndoMark(Session.MarkVisibility.Visible,"Edit native Swept laws");
 NXOpen.Features.SweptBuilder b=null;
 try {
  b=_part.Features.CreateSweptBuilder(f);GsControls(b,scale,twist);
  var committed=b.CommitFeature();
  if(committed==null||committed.Tag!=f.Tag)throw new Exception("Edit replaced native feature identity");
  b.Destroy();b=null;GsUpdate(s,mark);Body body=GsBody(f,solid);
  var actual=GsRead(f);GsCheckControl(actual,scale,twist,solid);var after=GsCapture();
  if(!ParamIdentity(before.model,after.model)||Json(before.model.curves)!=Json(after.model.curves))
   throw new Exception("Feature dependency or control curve identity changed");
  if(ParamNear(before.model.geometry,after.model.geometry,1e-7))
   throw new Exception("No observable native geometry change; conservative rejection");
  return Json(new{ok=true,feature_tag=f.Tag.ToString(),native_controls=actual,solid=body.IsSolidBody,
   before=before,after=after,body_handles=ParamRefreshBodies(),saved=false,native_acceptance="NOTRUN",
   scope="Actual builder edit and finite geometry change; independent section/volume/cold evidence still required"});
 }catch(Exception e) {
  string cleanup=null;if(b!=null)try{b.Destroy();b=null;}catch(Exception d){cleanup=d.Message;}
  return GsAbort(e,s,mark,before,cleanup);
 }
}
private static string SurfaceSweepGeneralState(string[] a) {
 if(a.Length!=1)throw new Exception("nx_surface_sweep_general_state");
 GsGuard(false);var objects=GsObjects();int modified=Convert.ToInt32(NXOpen.UF.UFSession.GetUFSession().Part.IsModified(_part.Tag));
 try {
  // NX12 editor builders mark parts modified even without Commit. Never create one in a read-only query.
  var actual=new{model=ParamSnapshot(),expressions=_part.Expressions.ToArray().Select(x=>new{name=x.Name,rhs=x.RightHandSide,value=x.Value}).ToArray(),
   native_law_readback="Not queried: editing-builder creation mutates NX modified state; actual law receipts are produced by create/edit transactions"};
  bool unchanged=objects.SequenceEqual(GsObjects())&&modified==Convert.ToInt32(NXOpen.UF.UFSession.GetUFSession().Part.IsModified(_part.Tag));
  if(!unchanged){_gsUnsafe=true;throw new Exception("Read builder lifecycle altered object inventory/modified state");}
  return Json(new{ok=true,read_only=true,actual=actual,unsafe_latched=_gsUnsafe,
   saved=modified==0,scope="Native geometry/expressions only; no builder, setters, Commit, Update or Save; object inventory and modified flag checked"});
 }catch{_gsUnsafe=true;throw;}
}


// A1 source candidate: append inside existing loader class. NOT COMPILED / NOTRUN.
// One fixed experiment; no mode, scale, twist, path or profile arguments.
private static string SurfaceSweepArcCircleTrial(string[] a) {
 if(a.Length!=1)throw new Exception("nx_surface_sweep_arc_circle_trial takes no arguments");
 GsGuard(true);
 if(!String.Equals(Path.GetFileName(_part.FullPath),
    "phase4_gs_a1_arc_circle_v1.prt",StringComparison.Ordinal))
  throw new Exception("Only NEW saved phase4_gs_a1_arc_circle_v1.prt is authorized");
 if(_part.Curves.ToArray().Length!=0||_part.Features.ToArray().Length!=0||
    _part.Bodies.ToArray().Length!=0)
  throw new Exception("Empty isolated saved part required; no replay or edit");
 SweepUnused("A1_AC_GUIDE");SweepUnused("A1_AC_SECTION");SweepUnused("A1_AC_BODY");
 var before=GsCapture();var s=Session.GetSession();
 var mark=s.SetUndoMark(Session.MarkVisibility.Visible,"A1 new arc circle fixed vector trial");
 NXOpen.Features.SweptBuilder b=null;
 try {
  var guide=_part.Curves.CreateArc(new Point3d(0,100,0),
   new Vector3d(0,-1,0),new Vector3d(1,0,0),100,0,Math.PI/3);
  var profile=_part.Curves.CreateArc(new Point3d(0,0,0),
   new Vector3d(0,1,0),new Vector3d(0,0,1),4,0,2*Math.PI);
  guide.SetName("A1_AC_GUIDE");profile.SetName("A1_AC_SECTION");
  Point3d pg,ps;Vector3d tg,ts;
  SweepEval(guide,0,out pg,out tg);SweepEval(profile,0.5,out ps,out ts);
  if(!ParamNear(new double[]{pg.X,pg.Y,pg.Z,tg.X,tg.Y,tg.Z},
                new double[]{0,0,0,1,0,0},1e-8))
   throw new Exception("Unexpected native rail start/tangent");
  var cm=profile.Matrix.Element;
  if(Math.Abs(SweepDot(SweepUnit(new Vector3d(cm.Zx,cm.Zy,cm.Zz)),tg))<1-1e-8||
     Math.Abs(profile.EndAngle-profile.StartAngle-2*Math.PI)>1e-8||
     Math.Abs(profile.Radius-4)>1e-8)
   throw new Exception("Circle pose/radius/closure mismatch");
  var inputs=ParamSnapshot();
  if(inputs.curves.Length!=2||inputs.features.Length!=0||inputs.bodyTags.Length!=0)
   throw new Exception("Unexpected fixture content");
  b=_part.Features.CreateSweptBuilder(null);
  b.G0Tolerance=0.001;b.G1Tolerance=0.5;
  b.SectionLocation=NXOpen.Features.SweptBuilder.SectionLocationTypes.EndsOfGuides;
  b.AlignmentMethod.AlignType=NXOpen.GeometricUtilities.AlignmentMethodBuilder.Type.Parameter;
  b.BodyPreference.BodyType=NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Solid;
  b.PreserveShapeOption=false;b.PreserveGuideShapeOption=false;
  var sec=_part.Sections.CreateSection();var gs=_part.Sections.CreateSection();
  b.SectionList.Append(sec);b.GuideList.Append(gs);
  string phase="configure";
  SweepSection(sec,profile,ps,ts,true,"section",ref phase);
  SweepSection(gs,guide,pg,tg,false,"guide",ref phase);
  b.ScalingMethod.ScalingOption=NXOpen.GeometricUtilities.ScalingMethodBuilder.ScalingOptions.ByBlendingFunction;
  b.ScalingMethod.BlendingFunctionType=NXOpen.GeometricUtilities.ScalingMethodBuilder.BlendingFunctionTypes.Linear;
  b.ScalingMethod.StartBlendScaleFactor=1;b.ScalingMethod.EndBlendScaleFactor=0.8;
  // FIXED_VECTOR is our test label, NOT NX OrientationOptions.Fixed.
  // Do NOT call GsControls: that helper selects ByAngularLaw.
  b.OrientationMethod.OrientationOption=NXOpen.GeometricUtilities.OrientationMethodBuilder.OrientationOptions.ByVectorDirection;
  b.OrientationMethod.OrientationVector=_part.Directions.CreateDirection(pg,
   new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);
  var f=b.CommitFeature() as NXOpen.Features.Swept;
  if(f==null)throw new Exception("Expected actual Swept feature");
  f.SetName("A1_AC_BODY");b.Destroy();b=null;
  GsUpdate(s,mark);Body body=GsBody(f,true);var control=GsRead(f);
  if(control.orientation!="ByVectorDirection"||control.scaling!="ByBlendingFunction"||
     control.blending!="Linear"||control.sections!=1||control.guides!=1||
     control.bodyStyle!="Solid"||control.location!="EndsOfGuides"||
     control.alignment!="Parameter"||control.preserveShape||control.preserveGuide||
     !ParamNear(new double[]{control.startScale,control.endScale},new double[]{1,0.8},1e-9))
   throw new Exception("Native controls differ from arc-circle trial contract");
  var after=GsCapture();
  if(after.model.features.Length!=1||after.model.curves.Length!=2||
     after.model.bodyTags.Length!=1||Json(inputs.curves)!=Json(after.model.curves))
   throw new Exception("Native content or control curve identity changed");
  return Json(new{ok=true,trial="A1_ARC_CIRCLE_FIXED_VECTOR",mode="FIXED_VECTOR",
   feature_tag=f.Tag.ToString(),body_tag=body.Tag.ToString(),
   body_handles=ParamRefreshBodies(),native_controls=control,
   requested_reference=new double[]{0,0,1},before=before,after=after,
   saved=false,native_acceptance="NOTRUN",twist_observable=false,
   scope="Native construction receipt only; no station/scale/transport/twist/save/cold acceptance"});
 }catch(Exception e) {
  string cleanup=null;
  if(b!=null)try{b.Destroy();b=null;}catch(Exception d){cleanup=d.Message;}
  // Curves, sections, selection rules and direction are inside this undo mark.
  // Existing GsAbort compares UF inventory, expressions, controls and geometry;
  // cleanup/undo mismatch latches _gsUnsafe. Never reset it here.
  return GsAbort(e,s,mark,before,cleanup);
 }
}

// Append inside NX_MCP_Loader. SOURCE ONLY: compile and native acceptance NOTRUN.
// Unique IvBox2609 prefix; only existing _part and Json are reused.
private const double IvBox2609Tol=1e-7;
private sealed class IvBox2609Edge {
 public NXOpen.Edge edge; public string tag; public double[] a,b;
 public int i,j; public string[] faces;
}
private static void IvBox2609Need(bool ok,string why) {
 if(!ok)throw new System.InvalidOperationException(why);
}
private static double[] IvBox2609V(NXOpen.Point3d p) {return new double[]{p.X,p.Y,p.Z};}
private static double[] IvBox2609Sub(double[] a,double[] b) {
 return new double[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};
}
private static double[] IvBox2609Add(double[] a,double[] b,double t) {
 return new double[]{a[0]+t*b[0],a[1]+t*b[1],a[2]+t*b[2]};
}
private static double IvBox2609Dot(double[] a,double[] b) {
 return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
}
private static double[] IvBox2609Cross(double[] a,double[] b) {
 return new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
}
private static double IvBox2609Norm(double[] a) {return Math.Sqrt(IvBox2609Dot(a,a));}
private static double[] IvBox2609Unit(double[] a) {
 double n=IvBox2609Norm(a);
 IvBox2609Need(!Double.IsNaN(n)&&!Double.IsInfinity(n)&&n>1e-15,"Invalid normal/derivative");
 return new double[]{a[0]/n,a[1]/n,a[2]/n};
}
private static void IvBox2609Finite(double[] p) {
 foreach(double v in p)IvBox2609Need(!Double.IsNaN(v)&&!Double.IsInfinity(v)&&Math.Abs(v)<=1e6,"Coordinate range unsupported");
}
private static int IvBox2609Corner(System.Collections.Generic.List<double[]> vertices,double[] p) {
 IvBox2609Finite(p);int found=-1;
 for(int i=0;i<vertices.Count;i++) {
  double d=IvBox2609Norm(IvBox2609Sub(p,vertices[i]));
  if(d<=IvBox2609Tol){IvBox2609Need(found<0,"Ambiguous corner clustering");found=i;}
  else IvBox2609Need(d>100*IvBox2609Tol,"Near coincident distinct corners unsupported");
 }
 if(found<0){found=vertices.Count;vertices.Add(p);}
 return found;
}
private static string IvBox2609Hash(string p) {
 using(var f=new System.IO.FileStream(p,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.Read))
 using(var h=System.Security.Cryptography.SHA256.Create())
 return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant();
}
private static string[] IvBox2609Inventory(NXOpen.Part p) {
 var uf=NXOpen.UF.UFSession.GetUFSession();
 var seen=new System.Collections.Generic.HashSet<NXOpen.Tag>();
 var rows=new System.Collections.Generic.List<string>();NXOpen.Tag t=NXOpen.Tag.Null;
 while((t=uf.Obj.CycleAll(p.Tag,t))!=NXOpen.Tag.Null) {
  IvBox2609Need(seen.Add(t)&&seen.Count<=100000,"Invalid/excessive object cycle");
  int type,sub;uf.Obj.AskTypeAndSubtype(t,out type,out sub);
  rows.Add(t.ToString()+":"+type+":"+sub);
 }
 rows.Sort(StringComparer.Ordinal);return rows.ToArray();
}
private static void IvBox2609Guard(NXOpen.Part p) {
 IvBox2609Need(p!=null&&!String.IsNullOrEmpty(p.FullPath),"Saved part required");
 string full=System.IO.Path.GetFullPath(p.FullPath);
 string root=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\independent_volume_20261109";
 IvBox2609Need(String.Equals(System.IO.Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)&&
 System.IO.Path.GetFileNameWithoutExtension(full).StartsWith("phase4_iv_",StringComparison.Ordinal)&&
 String.Equals(System.IO.Path.GetExtension(full),".prt",StringComparison.OrdinalIgnoreCase),"Dedicated box path required");
 IvBox2609Need(System.IO.File.Exists(full)&&p.IsFullyLoaded&&p.PartUnits==NXOpen.BasePart.Units.Millimeters,"Saved fully loaded MM part required");
 for(var d=new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(full));d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&System.IO.FileAttributes.ReparsePoint)==0,"Reparse directory rejected");
 IvBox2609Need((System.IO.File.GetAttributes(full)&System.IO.FileAttributes.ReparsePoint)==0,"Reparse file rejected");
 var s=NXOpen.Session.GetSession();int n=0;
 foreach(NXOpen.BasePart q in s.Parts){n++;IvBox2609Need(q.Tag==p.Tag,"Other loaded part rejected");}
 IvBox2609Need(n==1&&s.Parts.Work==p&&s.Parts.Display==p,"Sole work/display part required");
 IvBox2609Need(p.ComponentAssembly.RootComponent==null,"Assembly rejected");
}
private static string IvBox2609Read(string[] args) {
 NXOpen.Part p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string beforeHash=null,afterHash=null,error=null,auditError=null;
 string[] beforeObjects=null,afterObjects=null;int? beforeModified=null,afterModified=null;
 object boundary=null;bool unchanged=false;
 var faceEvidence=new System.Collections.Generic.List<object>();
 var edgeEvidence=new System.Collections.Generic.List<object>();
 var featureEvidence=new System.Collections.Generic.List<object>();
 try {
  IvBox2609Need(args.Length==1,"nx_independent_box_boundary accepts no arguments");
  IvBox2609Guard(p);
  beforeHash=IvBox2609Hash(p.FullPath);
  beforeModified=Convert.ToInt32(uf.Part.IsModified(p.Tag));
  beforeObjects=IvBox2609Inventory(p);
  IvBox2609Need(beforeModified==0,"Unsaved modifications rejected");
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();
  foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid required");
  NXOpen.Body body=bodies[0];int faultsCount;int[] faults;NXOpen.Tag[] bad;
  uf.Modl.AskBodyConsistency(body.Tag,out faultsCount,out faults,out bad);
  IvBox2609Need(faultsCount==0,"Native body consistency failure");
  foreach(NXOpen.Features.Feature f in p.Features) {
   string[] errors=f.GetFeatureErrorMessages(),warnings=f.GetFeatureWarningMessages();
   featureEvidence.Add(new{tag=f.Tag.ToString(),type=f.FeatureType,errors=errors,warnings=warnings,suppressed=f.Suppressed});
   IvBox2609Need(!f.Suppressed&&errors.Length==0&&warnings.Length==0,"Feature health failure");
  }
  NXOpen.Tag[] failed;uf.Modl.AskFeatFailList(out failed);
  IvBox2609Need(failed.Length==0,"Native failed-feature list nonempty");
  NXOpen.Face[] faces=body.GetFaces();NXOpen.Edge[] edges=body.GetEdges();
  IvBox2609Need(faces.Length==6&&edges.Length==12,"Six faces/twelve edges required");
  var faceTags=new System.Collections.Generic.HashSet<string>();
  foreach(var f in faces)IvBox2609Need(faceTags.Add(f.Tag.ToString()),"Duplicate face");
  var records=new System.Collections.Generic.Dictionary<string,IvBox2609Edge>();
  var vertices=new System.Collections.Generic.List<double[]>();
  var pairKeys=new System.Collections.Generic.HashSet<string>();
  double minLength=Double.MaxValue,nativeTol;
  uf.Modl.AskDistanceTolerance(out nativeTol);
  IvBox2609Need(!Double.IsNaN(nativeTol)&&!Double.IsInfinity(nativeTol)&&nativeTol>0,"Unknown native tolerance");
  foreach(var e in edges) {
   IvBox2609Need(e.SolidEdgeType==NXOpen.Edge.EdgeType.Linear&&e.GetBody().Tag==body.Tag,"Nonlinear/foreign edge");
   NXOpen.Point3d a,b;e.GetVertices(out a,out b);
   var r=new IvBox2609Edge{edge=e,tag=e.Tag.ToString(),a=IvBox2609V(a),b=IvBox2609V(b)};
   r.i=IvBox2609Corner(vertices,r.a);r.j=IvBox2609Corner(vertices,r.b);
   IvBox2609Need(r.i!=r.j,"Degenerate edge");
   double len=IvBox2609Norm(IvBox2609Sub(r.b,r.a));minLength=Math.Min(minLength,len);
   IvBox2609Need(len>Math.Max(1e-3,100*nativeTol),"Edge too small relative to native containment tolerance");
   string pair=Math.Min(r.i,r.j)+":"+Math.Max(r.i,r.j);
   IvBox2609Need(pairKeys.Add(pair),"Duplicate geometric edge");
   NXOpen.Face[] ef=e.GetFaces();
   IvBox2609Need(ef.Length==2&&ef[0].Tag!=ef[1].Tag,"Edge must have exactly two distinct face incidences");
   r.faces=new string[]{ef[0].Tag.ToString(),ef[1].Tag.ToString()};
   foreach(string ft in r.faces)IvBox2609Need(faceTags.Contains(ft),"Foreign edge face");
   IvBox2609Need(!records.ContainsKey(r.tag),"Duplicate native edge");records.Add(r.tag,r);
   edgeEvidence.Add(new{edge_tag=r.tag,type=e.SolidEdgeType.ToString(),vertices=new double[][]{r.a,r.b},
    corner_indices=new int[]{r.i,r.j},face_tags=r.faces,length_mm=len});
  }
  IvBox2609Need(vertices.Count==8,"Eight corners required");
  int[] degree=new int[8];foreach(var r in records.Values){degree[r.i]++;degree[r.j]++;}
  foreach(int d in degree)IvBox2609Need(d==3,"Non-box corner valence");
  var edgeUses=new System.Collections.Generic.Dictionary<string,int>();
  var orientedUses=new System.Collections.Generic.Dictionary<string,int>();
  foreach(string et in records.Keys){edgeUses.Add(et,0);orientedUses.Add(et,0);}
  var patches=new System.Collections.Generic.List<object>();
  var senses=new System.Collections.Generic.Dictionary<string,int>();
  var normals=new System.Collections.Generic.List<double[]>();
  var center=new double[3];foreach(var v in vertices)center=IvBox2609Add(center,v,0.125);
  int centerStatus;uf.Modl.AskPointContainment(center,body.Tag,out centerStatus);
  IvBox2609Need(centerStatus==1,"Corner centroid not strictly inside solid");
  double offset=Math.Max(8*nativeTol,100*IvBox2609Tol);
  IvBox2609Need(offset<minLength/10,"Containment offsets too large");
  foreach(var f in faces) {
   string ft=f.Tag.ToString();NXOpen.Edge[] fe=f.GetEdges();
   IvBox2609Need(fe.Length==4,"Face must have exactly four boundary edges; holes unsupported");
   var local=new System.Collections.Generic.List<IvBox2609Edge>();
   var unique=new System.Collections.Generic.HashSet<string>();
   var adjacency=new System.Collections.Generic.Dictionary<int,System.Collections.Generic.List<int>>();
   foreach(var e in fe) {
    string et=e.Tag.ToString();
    IvBox2609Need(unique.Add(et)&&records.ContainsKey(et),"Repeated/foreign face edge");
    var r=records[et];IvBox2609Need(Array.IndexOf(r.faces,ft)>=0,"Nonreciprocal edge/face incidence");
    local.Add(r);edgeUses[et]++;
    foreach(int v in new int[]{r.i,r.j})if(!adjacency.ContainsKey(v))adjacency.Add(v,new System.Collections.Generic.List<int>());
    adjacency[r.i].Add(r.j);adjacency[r.j].Add(r.i);
   }
   IvBox2609Need(adjacency.Count==4,"Face not four-corner rectangle");
   int start=Int32.MaxValue;
   foreach(var kv in adjacency){IvBox2609Need(kv.Value.Count==2,"Face boundary not a simple cycle");start=Math.Min(start,kv.Key);}
   int[] loop=new int[4];loop[0]=start;loop[1]=Math.Min(adjacency[start][0],adjacency[start][1]);
   for(int k=2;k<4;k++){var ns=adjacency[loop[k-1]];loop[k]=ns[0]==loop[k-2]?ns[1]:ns[0];}
   IvBox2609Need(new System.Collections.Generic.HashSet<int>(loop).Count==4&&adjacency[loop[3]].Contains(start),"Disconnected/nonclosed face boundary");
   double[] origin=vertices[loop[0]],du=IvBox2609Sub(vertices[loop[1]],origin),dv=IvBox2609Sub(vertices[loop[3]],origin);
   double[] fourth=IvBox2609Add(IvBox2609Add(origin,du,1),dv,1);
   double lu=IvBox2609Norm(du),lv=IvBox2609Norm(dv);
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(fourth,vertices[loop[2]]))<=IvBox2609Tol,"Opposite rectangle edges mismatch");
   IvBox2609Need(Math.Abs(IvBox2609Dot(du,dv))<=IvBox2609Tol*Math.Min(lu,lv),"Nonorthogonal rectangle");
   int type,normDir;double radius,radData;double[] point=new double[3],dir=new double[3],box=new double[6];
   uf.Modl.AskFaceData(f.Tag,out type,point,dir,box,out radius,out radData,out normDir);
   IvBox2609Need(type==22&&(normDir==1||normDir==-1),"Only native bounded planes with known sense supported");
   double[] planeNormal=IvBox2609Unit(dir),patchNormal=IvBox2609Unit(IvBox2609Cross(du,dv));
   IvBox2609Need(Math.Abs(IvBox2609Dot(planeNormal,patchNormal))>1-1e-10,"Plane and rectangle normals disagree");
   foreach(int v in loop)IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Sub(vertices[v],point),planeNormal))<=IvBox2609Tol,"Corner off native plane");
   double[] mid=IvBox2609Add(IvBox2609Add(origin,du,.5),dv,.5);
   double[] uv=new double[2],projected=new double[3],evaluated=new double[3],u1=new double[3],v1=new double[3],
    u2=new double[3],v2=new double[3],normal=new double[3],radii=new double[2];
   uf.Modl.AskFaceParm2(f.Tag,mid,uv,projected);
   uf.Modl.AskFaceProps(f.Tag,uv,evaluated,u1,v1,u2,v2,normal,radii);
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(mid,projected))<=IvBox2609Tol&&
    IvBox2609Norm(IvBox2609Sub(mid,evaluated))<=IvBox2609Tol,"Native surface evaluation not at rectangle interior");
   normal=IvBox2609Unit(normal);
   double[] surfaceNormal=IvBox2609Unit(IvBox2609Cross(u1,v1));
   IvBox2609Need(normDir*IvBox2609Dot(surfaceNormal,normal)>1-1e-10&&
    Math.Abs(IvBox2609Dot(normal,patchNormal))>1-1e-10,"Native face sense/derivative/patch mismatch");
   int sense=IvBox2609Dot(patchNormal,normal)>0?1:-1;
   var containment=new System.Collections.Generic.List<object>();
   foreach(double u in new double[]{.25,.5,.75})foreach(double v in new double[]{.25,.5,.75}) {
    double[] x=IvBox2609Add(IvBox2609Add(origin,du,u),dv,v);int fc,bc;
    uf.Modl.AskPointContainment(x,f.Tag,out fc);uf.Modl.AskPointContainment(x,body.Tag,out bc);
    IvBox2609Need(fc==1&&bc==3,"Interior sample not in actual trimmed face/on solid boundary");
    foreach(double d in new double[]{offset/2,offset}) {
     double[] inside=IvBox2609Add(x,normal,-d),outside=IvBox2609Add(x,normal,d);int si,so;
     uf.Modl.AskPointContainment(inside,body.Tag,out si);uf.Modl.AskPointContainment(outside,body.Tag,out so);
     containment.Add(new{u=u,v=v,point=x,face_status=fc,body_status=bc,offset_mm=d,
      inward_point=inside,outward_point=outside,inward_status=si,outward_status=so});
     IvBox2609Need(si==1&&so==2,"Face normal not independently outward at both offset scales");
    }
   }
   // For this six-rectangle family, reject concave/folded/overlapping configurations.
   IvBox2609Need(IvBox2609Dot(IvBox2609Sub(center,mid),normal)<-offset,"Box centroid not behind face");
   foreach(var v in vertices)IvBox2609Need(IvBox2609Dot(IvBox2609Sub(v,mid),normal)<=IvBox2609Tol,"Nonconvex box boundary");
   normals.Add(normal);
   var orderedTags=new string[4];
   for(int k=0;k<4;k++) {
    int a=loop[k],b=loop[(k+1)%4];IvBox2609Edge found=null;
    foreach(var r in local)if((r.i==a&&r.j==b)||(r.i==b&&r.j==a)){IvBox2609Need(found==null,"Ambiguous loop edge");found=r;}
    IvBox2609Need(found!=null,"Missing loop edge");orderedTags[k]=found.tag;
    orientedUses[found.tag]+=sense*(found.i==a?1:-1);
   }
   string name="face_"+ft;senses.Add(name,sense);
   var domain=new{kind="full_rectangle",no_exclusions=true,single_cover=true,
    source="NX bounded plane + complete four-linear-edge simple rectangular boundary + native body consistency",
    coordinates="affine edge-derived u,v; not native surface UV"};
   patches.Add(new{name=name,origin=origin,du=du,dv=dv,sense=sense,bounds=new double[]{0,1,0,1},domain=domain});
   var orderedVertices=new double[][]{vertices[loop[0]],vertices[loop[1]],vertices[loop[2]],vertices[loop[3]]};
   faceEvidence.Add(new{face_tag=ft,face_type=type,native_norm_dir=normDir,native_plane_point=point,
    native_plane_direction=dir,native_uv=uv,native_evaluated_point=evaluated,native_du=u1,native_dv=v1,
    native_face_normal=normal,affine_patch_sense=sense,edge_tags=orderedTags,
    corner_indices=loop,vertices=orderedVertices,containment=containment,
    full_rectangle_verified=true,single_boundary_cycle=true});
  }
  foreach(string et in records.Keys)IvBox2609Need(edgeUses[et]==2&&orientedUses[et]==0,"Boundary not closed and coherently oriented");
  // Each rectangular-box normal must have exactly one opposite and four perpendicular peers.
  for(int i=0;i<6;i++){int opposite=0;
   for(int j=0;j<6;j++)if(i!=j){double d=IvBox2609Dot(normals[i],normals[j]);
    if(d<-1+1e-10)opposite++;else IvBox2609Need(Math.Abs(d)<1e-10,"Not an orthogonal box");}
   IvBox2609Need(opposite==1,"Missing/duplicate opposite box face");
  }
  boundary=new{schema_version=1,units="mm",body_tag=body.Tag.ToString(),patches=patches,
   topology=new{closed=true,oriented=true,complete=true,senses=senses,source="nx_independent_box_boundary:v1",
    actual_two_face_incidence_verified=true,oriented_edge_cancellation_verified=true,
    rectangular_domains_verified=true,corner_count=8,edge_count=12,face_count=6,euler_characteristic=2},
   corners=vertices,consistency_fault_count=faultsCount,failed_feature_count=failed.Length,
   center=center,center_containment=centerStatus,native_distance_tolerance_mm=nativeTol,
   geometric_comparison_tolerance_mm=IvBox2609Tol,volume_computed=false,native_volume_verified=false};
 } catch(Exception e) {error=e.Message;}
 // Audit also on rejected geometry. No undo/update/save: any change requires coordinator inspection.
 if(beforeHash!=null&&beforeObjects!=null)try {
  IvBox2609Guard(p);afterModified=Convert.ToInt32(uf.Part.IsModified(p.Tag));
  afterObjects=IvBox2609Inventory(p);afterHash=IvBox2609Hash(p.FullPath);
  unchanged=beforeHash==afterHash&&beforeModified==0&&afterModified==0&&
   String.Join("\n",beforeObjects)==String.Join("\n",afterObjects);
  if(!unchanged)auditError="Read-only invariant failed";
 }catch(Exception e){auditError=e.Message;}
 bool ok=error==null&&auditError==null&&unchanged&&boundary!=null;
 return Json(new{ok=ok,command="nx_independent_box_boundary",read_only_query=true,error=error,audit_error=auditError,
  boundary=ok?boundary:null,face_evidence=faceEvidence,edge_evidence=edgeEvidence,features=featureEvidence,
  audit=new{sha256_before=beforeHash,sha256_after=afterHash,modified_before=beforeModified,modified_after=afterModified,
   objects_before=beforeObjects,objects_after=afterObjects,unchanged=unchanged},
  safe_pause_required=auditError!=null,retry_automatically=false,
  volume_computed=false,native_volume_verified=false,
  limitations="Only well-conditioned orthogonal six-planar-rectangle solids; tolerance-based geometric checks, not exact arithmetic or integration error bounds"});
}

// Append inside NX_MCP_Loader AFTER unchanged phase5_independent_box.cs.
// SOURCE ONLY / NOTRUN. Dispatch: case "nx_independent_analytic_boundary": return IvAn2609Read(parts);
private sealed class IvAn2609Face {
 public NXOpen.Face f; public int type,sense;
 public double[] point=new double[3],dir=new double[3],box=new double[6];
 public double radius,minor;
}
private static IvAn2609Face IvAn2609Data(NXOpen.Face f) {
 var q=new IvAn2609Face();q.f=f;
 NXOpen.UF.UFSession.GetUFSession().Modl.AskFaceData(f.Tag,out q.type,q.point,q.dir,q.box,out q.radius,out q.minor,out q.sense);
 IvBox2609Finite(q.point);IvBox2609Finite(q.box);
 IvBox2609Need(q.sense==1||q.sense==-1,"Unknown native face sense");
 return q;
}
// NX12 XML confirms AskArc signature, C header defines these field names.
// Fail closed if managed struct layout differs; never infer alternative fields.
private static T IvAn2609Field<T>(object value,string name) {
 var f=value.GetType().GetField(name);
 IvBox2609Need(f!=null&&f.FieldType==typeof(T),"Unsupported NX12 Arc field: "+name);
 return (T)f.GetValue(value);
}
private static double[] IvAn2609Array(object value,string name,int n) {
 var a=IvAn2609Field<double[]>(value,name);
 IvBox2609Need(a!=null&&a.Length==n,"Invalid Arc array: "+name);
 IvBox2609Finite(a);return a;
}
private sealed class IvAn2609Circle {
 public NXOpen.Edge e;public double[] center,x,y,limits,evalLimits;
 public double radius;public bool periodic;
}
private static IvAn2609Circle IvAn2609Arc(NXOpen.Edge e) {
 var uf=NXOpen.UF.UFSession.GetUFSession();IntPtr handle=IntPtr.Zero;
 var c=new IvAn2609Circle();c.e=e;
 try {
  uf.Eval.Initialize(e.Tag,out handle);
  IvBox2609Need(handle!=IntPtr.Zero,"No curve evaluator");
  bool isArc;uf.Eval.IsArc(handle,out isArc);IvBox2609Need(isArc,"Noncircular edge unsupported");
  NXOpen.UF.UFEval.Arc arc;uf.Eval.AskArc(handle,out arc);
  c.periodic=IvAn2609Field<bool>(arc,"is_periodic");
  c.radius=IvAn2609Field<double>(arc,"radius");
  c.center=IvAn2609Array(arc,"center",3);c.x=IvAn2609Array(arc,"x_axis",3);c.y=IvAn2609Array(arc,"y_axis",3);
  c.limits=IvAn2609Array(arc,"limits",2);c.evalLimits=new double[2];uf.Eval.AskLimits(handle,c.evalLimits);
  IvBox2609Finite(c.evalLimits);
  IvBox2609Need(c.periodic&&Math.Abs(c.limits[1]-c.limits[0]-2*Math.PI)<1e-10&&
   Math.Abs(c.evalLimits[1]-c.evalLimits[0]-2*Math.PI)<1e-10,"Partial/repeated circle unsupported");
  IvBox2609Need(Math.Abs(IvBox2609Norm(c.x)-1)<1e-10&&Math.Abs(IvBox2609Norm(c.y)-1)<1e-10&&
   Math.Abs(IvBox2609Dot(c.x,c.y))<1e-10,"Invalid circle frame");
  IvBox2609Need(c.radius>0&&c.radius<=1e6,"Invalid circle radius");
  NXOpen.Point3d a,b;e.GetVertices(out a,out b);
  IvBox2609Need(IvBox2609Norm(IvBox2609Sub(IvBox2609V(a),IvBox2609V(b)))<=IvBox2609Tol,"Open circular edge");
  return c;
 } finally {if(handle!=IntPtr.Zero)uf.Eval.Free(handle);}
}
private static object IvAn2609Witness(IvAn2609Face f,NXOpen.Body body,double[] x,double[] outward,double offset) {
 var uf=NXOpen.UF.UFSession.GetUFSession();
 double[] uv=new double[2],projected=new double[3],p=new double[3],du=new double[3],dv=new double[3],
  du2=new double[3],dv2=new double[3],normal=new double[3],radii=new double[2];
 uf.Modl.AskFaceParm2(f.f.Tag,x,uv,projected);
 uf.Modl.AskFaceProps(f.f.Tag,uv,p,du,dv,du2,dv2,normal,radii);
 IvBox2609Finite(uv);IvBox2609Finite(p);IvBox2609Finite(projected);
 IvBox2609Finite(du);IvBox2609Finite(dv);IvBox2609Finite(normal);
 IvBox2609Need(IvBox2609Norm(IvBox2609Sub(x,p))<=IvBox2609Tol&&
  IvBox2609Norm(IvBox2609Sub(x,projected))<=IvBox2609Tol,"Analytic/native position mismatch");
 var n=IvBox2609Unit(normal);var cross=IvBox2609Unit(IvBox2609Cross(du,dv));
 IvBox2609Need(f.sense*IvBox2609Dot(cross,n)>1-1e-9&&IvBox2609Dot(n,outward)>1-1e-9,"Derivative/sense/outward mismatch");
 int fc,bc;uf.Modl.AskPointContainment(x,f.f.Tag,out fc);uf.Modl.AskPointContainment(x,body.Tag,out bc);
 IvBox2609Need(fc==1&&bc==3,"Witness outside trimmed face or off body");
 var offsets=new System.Collections.Generic.List<object>();
 foreach(double d in new double[]{offset/2,offset}) {
  var inside=IvBox2609Add(x,n,-d);var outside=IvBox2609Add(x,n,d);int si,so;
  uf.Modl.AskPointContainment(inside,body.Tag,out si);uf.Modl.AskPointContainment(outside,body.Tag,out so);
  offsets.Add(new{offset_mm=d,inside=inside,outside=outside,inside_status=si,outside_status=so});
  IvBox2609Need(si==1&&so==2,"Native normal does not separate inside/outside");
 }
 return new{point=x,native_uv=uv,projected=projected,evaluated=p,native_du=du,native_dv=dv,
  native_normal=normal,native_sense=f.sense,analytic_outward=outward,face_status=fc,body_status=bc,offsets=offsets};
}
private static object IvAn2609Patch(string name,string kind,double[] o,double[] a,double[] b,double[] axis,double r,double h,int sense,string proof) {
 double[] bounds=kind=="sphere"?new double[]{0,2*Math.PI,-Math.PI/2,Math.PI/2}:
  new double[]{0,2*Math.PI,0,kind=="cylinder"?h:r};
 return new{name=name,kind=kind,origin=o,a=a,b=b,axis=axis,radius=r,height=h,sense=sense,bounds=bounds,
  domain=new{kind="full_rectangle",no_exclusions=true,single_cover=true,source=proof,
   coordinates="analytic reparameterization from native geometry, not native UV",
   periodic_seam="theta=0 and theta=2*pi identified; endpoint duplication has zero area",
   singularities=kind=="sphere"?"latitude endpoints: zero area poles":kind=="disk"?"rho=0: zero area center":"none"}};
}
private static string IvAn2609Read(string[] args) {
 NXOpen.Part p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string bh=null,ah=null,error=null,auditError=null;string[] bi=null,ai=null;int? bm=null,am=null;
 object boundary=null;bool unchanged=false;
 var evidence=new System.Collections.Generic.List<object>();var circles=new System.Collections.Generic.List<object>();
 var features=new System.Collections.Generic.List<object>();
 try {
  IvBox2609Need(args.Length==1,"nx_independent_analytic_boundary accepts no arguments");IvBox2609Guard(p);
  bh=IvBox2609Hash(p.FullPath);bm=Convert.ToInt32(uf.Part.IsModified(p.Tag));bi=IvBox2609Inventory(p);
  IvBox2609Need(bm==0,"Unsaved modifications rejected");
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid required");var body=bodies[0];
  int nf;int[] faults;NXOpen.Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out nf,out faults,out bad);
  IvBox2609Need(nf==0,"Body consistency failure");
  foreach(NXOpen.Features.Feature f in p.Features) {
   var errors=f.GetFeatureErrorMessages();var warnings=f.GetFeatureWarningMessages();
   features.Add(new{tag=f.Tag.ToString(),type=f.FeatureType,errors=errors,warnings=warnings,suppressed=f.Suppressed});
   IvBox2609Need(!f.Suppressed&&errors.Length==0&&warnings.Length==0,"Feature health failure");
  }
  NXOpen.Tag[] failed;uf.Modl.AskFeatFailList(out failed);IvBox2609Need(failed.Length==0,"Failed feature list nonempty");
  var faces=body.GetFaces();var edges=body.GetEdges();
  var data=new System.Collections.Generic.List<IvAn2609Face>();
  var ft=new System.Collections.Generic.HashSet<NXOpen.Tag>();
  foreach(var f in faces){IvBox2609Need(ft.Add(f.Tag),"Duplicate face");data.Add(IvAn2609Data(f));}
  double tol;uf.Modl.AskDistanceTolerance(out tol);
  IvBox2609Need(tol>0&&!Double.IsInfinity(tol),"Unknown distance tolerance");
  double offset=Math.Max(8*tol,100*IvBox2609Tol);
  var patches=new System.Collections.Generic.List<object>();var senses=new System.Collections.Generic.Dictionary<string,int>();
  string family,proof;double[] center;double radius,height=0;
  if(faces.Length==1&&edges.Length==0&&data[0].type==18) {
   family="sphere";var f=data[0];
   IvBox2609Need(f.f.GetEdges().Length==0,"Sphere seam/trim unsupported");
   center=f.point;radius=f.radius;
   IvBox2609Need(radius>10*offset&&radius<=1e6,"Sphere radius ill-conditioned");
   proof="Sole consistent solid; sole native type18 spherical face; complete body and face edge sets empty. A proper trimmed subset requires a boundary; seam-bearing representations rejected. Native witnesses corroborate orientation, not coverage.";
   double[] a={1,0,0},b={0,1,0},axis={0,0,1};
   var witnesses=new System.Collections.Generic.List<object>();
   foreach(double theta in new double[]{.37,2.1,4.3})foreach(double lat in new double[]{-.43,.19,.57}) {
    double[] n={Math.Cos(lat)*Math.Cos(theta),Math.Cos(lat)*Math.Sin(theta),Math.Sin(lat)};
    witnesses.Add(IvAn2609Witness(f,body,IvBox2609Add(center,n,radius),n,offset));
   }
   string name="face_"+f.f.Tag;senses.Add(name,1);
   patches.Add(IvAn2609Patch(name,"sphere",center,a,b,axis,radius,0,1,proof));
   evidence.Add(new{face_tag=f.f.Tag.ToString(),native_type=f.type,native_point=f.point,native_direction=f.dir,
    native_radius=f.radius,native_bounds=f.box,native_sense=f.sense,edge_tags=new string[0],witnesses=witnesses});
  } else {
   family="cylinder";IvBox2609Need(faces.Length==3&&edges.Length==2,"Only unseamed pure cylinder or sphere supported");
   IvAn2609Face side=null;var caps=new System.Collections.Generic.List<IvAn2609Face>();
   foreach(var f in data) {if(f.type==16){IvBox2609Need(side==null,"Multiple cylinders");side=f;}
    else {IvBox2609Need(f.type==22,"Nonplane cap unsupported");caps.Add(f);}}
   IvBox2609Need(side!=null&&caps.Count==2,"Cylinder and two planar caps required");
   var axis=IvBox2609Unit(side.dir);radius=side.radius;
   IvBox2609Need(radius>10*offset&&radius<=1e6,"Cylinder radius ill-conditioned");
   var ce=new IvAn2609Circle[2];var et=new System.Collections.Generic.HashSet<NXOpen.Tag>();
   for(int k=0;k<2;k++) {
    var e=edges[k];IvBox2609Need(et.Add(e.Tag)&&e.GetBody().Tag==body.Tag,"Duplicate/foreign edge");
    ce[k]=IvAn2609Arc(e);
    var ef=e.GetFaces();IvBox2609Need(ef.Length==2&&ef[0].Tag!=ef[1].Tag,"Two distinct incidences required");
    bool hasSide=false,hasCap=false;
    foreach(var f in ef){IvBox2609Need(ft.Contains(f.Tag),"Foreign incident face");if(f.Tag==side.f.Tag)hasSide=true;else hasCap=true;}
    IvBox2609Need(hasSide&&hasCap,"Cap/side incidence required");
    IvBox2609Need(Math.Abs(ce[k].radius-radius)<=IvBox2609Tol,"Circle/cylinder radius mismatch");
    double z=IvBox2609Dot(IvBox2609Sub(ce[k].center,side.point),axis);
    IvBox2609Need(IvBox2609Norm(IvBox2609Sub(ce[k].center,IvBox2609Add(side.point,axis,z)))<=IvBox2609Tol&&
     Math.Abs(IvBox2609Dot(IvBox2609Cross(ce[k].x,ce[k].y),axis))>1-1e-10,"Circle not coaxial");
    circles.Add(new{edge_tag=e.Tag.ToString(),center=ce[k].center,radius=ce[k].radius,x_axis=ce[k].x,y_axis=ce[k].y,
     periodic=ce[k].periodic,arc_limits=ce[k].limits,evaluator_limits=ce[k].evalLimits,
     face_tags=new string[]{ef[0].Tag.ToString(),ef[1].Tag.ToString()}});
   }
   var uses=new System.Collections.Generic.Dictionary<NXOpen.Tag,int>();foreach(var e in edges)uses.Add(e.Tag,0);
   var capCircles=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Circle>();
   foreach(var f in data) {
    var fe=f.f.GetEdges();IvBox2609Need(fe.Length==(f==side?2:1),"Extra trim/hole/seam rejected");
    var local=new System.Collections.Generic.HashSet<NXOpen.Tag>();
    foreach(var e in fe) {
     IvBox2609Need(et.Contains(e.Tag)&&local.Add(e.Tag),"Unknown/duplicate face edge");
     bool reciprocal=false;foreach(var q in e.GetFaces())if(q.Tag==f.f.Tag)reciprocal=true;
     IvBox2609Need(reciprocal,"Nonreciprocal incidence");uses[e.Tag]++;
     if(f!=side)capCircles.Add(f.f.Tag,ce[0].e.Tag==e.Tag?ce[0]:ce[1]);
    }
   }
   foreach(var n in uses.Values)IvBox2609Need(n==2,"Incomplete incidence");
   IvBox2609Need(capCircles[caps[0].f.Tag].e.Tag!=capCircles[caps[1].f.Tag].e.Tag,"Caps share same loop");
   if(IvBox2609Dot(IvBox2609Sub(ce[1].center,ce[0].center),axis)<0){var t=ce[0];ce[0]=ce[1];ce[1]=t;}
   var origin=ce[0].center;height=IvBox2609Dot(IvBox2609Sub(ce[1].center,origin),axis);
   IvBox2609Need(height>10*offset&&height<=1e6,"Height ill-conditioned");
   var a=ce[0].x;var b=IvBox2609Unit(IvBox2609Cross(axis,a));center=IvBox2609Add(origin,axis,height/2);
   proof="Sole consistent solid; complete reciprocal 3-face/2-edge incidence; two distinct periodic full 2*pi native circular loops coaxial with type16 side; each type22 cap has exactly one loop. Bounded planar disks and periodic cylindrical band give complete boundary; no extra holes, seams or sector edges. Finite witnesses only corroborate orientation.";
   foreach(var f in data) {
    var witnesses=new System.Collections.Generic.List<object>();string name="face_"+f.f.Tag;
    bool isSide=f==side;var o=isSide?origin:capCircles[f.f.Tag].center;
    int sense=1;
    if(!isSide) {
     var pn=IvBox2609Unit(f.dir);
     IvBox2609Need(Math.Abs(IvBox2609Dot(pn,axis))>1-1e-10&&
      Math.Abs(IvBox2609Dot(IvBox2609Sub(o,f.point),pn))<=IvBox2609Tol,"Cap plane mismatch");
     sense=capCircles[f.f.Tag].e.Tag==ce[0].e.Tag?1:-1; // theta,rho cross = -axis
    }
    foreach(double theta in new double[]{.37,2.1,4.3})foreach(double t in new double[]{.27,.63}) {
     var radial=IvBox2609Add(new double[3],a,Math.Cos(theta));radial=IvBox2609Add(radial,b,Math.Sin(theta));
     var x=IvBox2609Add(o,radial,isSide?radius:radius*t);
     if(isSide)x=IvBox2609Add(x,axis,height*t);
     var n=isSide?radial:IvBox2609Add(new double[3],axis,-sense);
     witnesses.Add(IvAn2609Witness(f,body,x,n,offset));
    }
    senses.Add(name,sense);patches.Add(IvAn2609Patch(name,isSide?"cylinder":"disk",o,a,b,axis,radius,height,sense,proof));
    var tags=new System.Collections.Generic.List<string>();foreach(var e in f.f.GetEdges())tags.Add(e.Tag.ToString());
    evidence.Add(new{face_tag=f.f.Tag.ToString(),native_type=f.type,native_point=f.point,native_direction=f.dir,
     native_radius=f.radius,native_bounds=f.box,native_sense=f.sense,edge_tags=tags,witnesses=witnesses});
   }
  }
  int inside;uf.Modl.AskPointContainment(center,body.Tag,out inside);IvBox2609Need(inside==1,"Analytic center not strictly inside");
  boundary=new{schema_version=1,units="mm",family=family,body_tag=body.Tag.ToString(),patches=patches,
   center=center,radius=radius,height=height,center_containment=inside,native_distance_tolerance_mm=tol,
   geometric_comparison_tolerance_mm=IvBox2609Tol,consistency_fault_count=nf,failed_feature_count=failed.Length,
   topology=new{closed=true,oriented=true,complete=true,senses=senses,source="nx_independent_analytic_boundary:v1",
    proof=proof,face_count=faces.Length,edge_count=edges.Length,
    orientation_proof="Outward analytic patches checked against native derivative cross times face sense and two-scale containment; periodic seams cancel; cap/side circle orientations oppose."},
   volume_computed=false,native_verified=false};
 }catch(Exception e){error=e.Message;}
 if(bh!=null&&bi!=null)try {
  IvBox2609Guard(p);am=Convert.ToInt32(uf.Part.IsModified(p.Tag));ai=IvBox2609Inventory(p);ah=IvBox2609Hash(p.FullPath);
  unchanged=bh==ah&&bm==0&&am==0&&String.Join("\n",bi)==String.Join("\n",ai);
  if(!unchanged)auditError="Read-only invariant failed";
 }catch(Exception e){auditError=e.Message;}
 bool ok=error==null&&auditError==null&&unchanged&&boundary!=null;
 return Json(new{ok=ok,command="nx_independent_analytic_boundary",read_only_query=true,error=error,audit_error=auditError,
  boundary=ok?boundary:null,face_evidence=evidence,edge_evidence=circles,features=features,
  audit=new{sha256_before=bh,sha256_after=ah,modified_before=bm,modified_after=am,objects_before=bi,objects_after=ai,unchanged=unchanged},
  safe_pause_required=auditError!=null,retry_automatically=false,volume_computed=false,native_verified=false,
  limitations="Only consistent unseamed analytic sphere or pure capped cylinder. Tolerance-based geometry; no rigorous geometric or quadrature error bound. Native compilation/testing NOTRUN."});
}

// Append INSIDE NX_MCP_Loader after unchanged box and analytic helpers.
// SOURCE ONLY: compilation and native execution NOTRUN.
// Dispatch: case "nx_independent_annular_caps": return IvTrim2610Read(parts);
private const string IvTrim2610Path=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\independent_volume_20261109\phase4_iv_b2_annular_20261110_v1.prt";
private static void IvTrim2610Guard(NXOpen.Part p) {
 IvBox2609Guard(p);
 IvBox2609Need(String.Equals(System.IO.Path.GetFullPath(p.FullPath),IvTrim2610Path,StringComparison.OrdinalIgnoreCase),"Exact B2 fixture required");
}
private static int IvTrim2610Status(double[] x,NXOpen.Tag t) {
 int s;NXOpen.UF.UFSession.GetUFSession().Modl.AskPointContainment(x,t,out s);return s;
}
private static object IvTrim2610Loop(IvAn2609Circle c,string role,double[] n) {
 double sign=IvBox2609Dot(IvBox2609Cross(c.x,c.y),n);
 IvBox2609Need(Math.Abs(sign)>1-1e-10,"Circle/plane axes disagree");
 bool forward=(sign>0)==(role=="outer");
 return new{edge_tag=c.e.Tag.ToString(),role=role,center=c.center,radius=c.radius,
 x_axis=c.x,y_axis=c.y,periodic=c.periodic,arc_limits=c.limits,evaluator_limits=c.evalLimits,
 start=forward?0.0:2*Math.PI,end=forward?2*Math.PI:0.0,
 parameterization="analytic theta from native full circle; not evaluator parameter",
 closure_evidence="Native periodic arc and evaluator spans both 2*pi; coincident edge vertices; one use of this edge.",
 sense_evidence="Traversal selected from dot(cross(x_axis,y_axis),outward_normal); outer CCW, hole CW."};
}
private static string IvTrim2610Read(string[] args) {
 var p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string bh=null,ah=null,error=null,auditError=null;string[] bi=null,ai=null;int? bm=null,am=null;
 bool unchanged=false;object boundary=null;
 try {
  IvBox2609Need(args.Length==1,"No query arguments allowed");IvTrim2610Guard(p);
  bh=IvBox2609Hash(p.FullPath);bm=Convert.ToInt32(uf.Part.IsModified(p.Tag));bi=IvBox2609Inventory(p);
  IvBox2609Need(bm==0,"Saved fixture required");
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid required");var body=bodies[0];
  int nf;int[] faults;NXOpen.Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out nf,out faults,out bad);
  IvBox2609Need(nf==0,"Body faults");
  foreach(NXOpen.Features.Feature f in p.Features)
   IvBox2609Need(!f.Suppressed&&f.GetFeatureErrorMessages().Length==0&&f.GetFeatureWarningMessages().Length==0,"Unhealthy feature");
  NXOpen.Tag[] failed;uf.Modl.AskFeatFailList(out failed);IvBox2609Need(failed.Length==0,"Failed features");
  var faces=body.GetFaces();var edges=body.GetEdges();
  IvBox2609Need(faces.Length==4&&edges.Length==4,"Only unseamed four-face/four-circle hollow fixture supported");
  var data=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Face>();
  var circles=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Circle>();
  var uses=new System.Collections.Generic.Dictionary<NXOpen.Tag,int>();
  var incidence=new System.Collections.Generic.List<object>();int capCount=0,sideCount=0;
  foreach(var f in faces) {
   IvBox2609Need(!data.ContainsKey(f.Tag),"Duplicate face");var d=IvAn2609Data(f);data.Add(f.Tag,d);
   if(d.type==22)capCount++;else {IvBox2609Need(d.type==16,"Nonanalytic side");sideCount++;}
  }
  IvBox2609Need(capCount==2&&sideCount==2,"Two planes and two cylinders required");
  foreach(var e in edges) {
   IvBox2609Need(e.GetBody().Tag==body.Tag&&!circles.ContainsKey(e.Tag),"Foreign/duplicate edge");
   circles.Add(e.Tag,IvAn2609Arc(e));uses.Add(e.Tag,0);
   var fs=e.GetFaces();IvBox2609Need(fs.Length==2&&fs[0].Tag!=fs[1].Tag,"Two distinct incidences required");
   IvBox2609Need(data.ContainsKey(fs[0].Tag)&&data.ContainsKey(fs[1].Tag),"Foreign incidence");
   IvBox2609Need((data[fs[0].Tag].type==22)!=(data[fs[1].Tag].type==22),"Cap/side incidence required");
   incidence.Add(new{edge_tag=e.Tag.ToString(),face_tags=new string[]{fs[0].Tag.ToString(),fs[1].Tag.ToString()}});
  }
  foreach(var f in faces) {
   var fe=f.GetEdges();IvBox2609Need(fe.Length==2,"Extra trims/seams rejected");
   var seen=new System.Collections.Generic.HashSet<NXOpen.Tag>();
   foreach(var e in fe) {
    IvBox2609Need(circles.ContainsKey(e.Tag)&&seen.Add(e.Tag),"Duplicate/unknown face edge");
    bool reciprocal=false;foreach(var q in e.GetFaces())if(q.Tag==f.Tag)reciprocal=true;
    IvBox2609Need(reciprocal,"Nonreciprocal incidence");uses[e.Tag]++;
   }
  }
  foreach(int count in uses.Values)IvBox2609Need(count==2,"Incomplete incidence");
  double tol;uf.Modl.AskDistanceTolerance(out tol);
  IvBox2609Need(tol>0&&!Double.IsInfinity(tol),"Invalid model tolerance");double offset=Math.Max(8*tol,100*IvBox2609Tol);
  var caps=new System.Collections.Generic.List<object>();
  foreach(var f in data.Values)if(f.type==22) {
   var fe=f.f.GetEdges();var inner=circles[fe[0].Tag];var outer=circles[fe[1].Tag];
   if(inner.radius>outer.radius){var t=inner;inner=outer;outer=t;}
   IvBox2609Need(inner.radius>10*offset&&outer.radius-inner.radius>20*offset,"Unresolved annular gap");
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(inner.center,outer.center))<=IvBox2609Tol,"Nonconcentric loops");
   // AskFaceData direction can already carry face sense. Establish outward independently.
   var axisNormal=IvBox2609Unit(f.dir);
   var mid=IvBox2609Add(outer.center,outer.x,(inner.radius+outer.radius)/2);
   int plus=IvTrim2610Status(IvBox2609Add(mid,axisNormal,offset),body.Tag);
   int minus=IvTrim2610Status(IvBox2609Add(mid,axisNormal,-offset),body.Tag);
   IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Cap normal does not separate solid and exterior");
   var n=IvBox2609Add(new double[3],axisNormal,plus==2?1:-1);
   foreach(var c in new IvAn2609Circle[]{outer,inner}) {
    IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Sub(c.center,f.point),n))<=IvBox2609Tol&&
     Math.Abs(IvBox2609Dot(IvBox2609Cross(c.x,c.y),n))>1-1e-10,"Noncoplanar circle");
    foreach(var ef in c.e.GetFaces())if(ef.Tag!=f.f.Tag) {
     var side=data[ef.Tag];var axis=IvBox2609Unit(side.dir);
     double z=IvBox2609Dot(IvBox2609Sub(c.center,side.point),axis);
     IvBox2609Need(Math.Abs(side.radius-c.radius)<=IvBox2609Tol&&
      Math.Abs(IvBox2609Dot(axis,n))>1-1e-10&&
      IvBox2609Norm(IvBox2609Sub(c.center,IvBox2609Add(side.point,axis,z)))<=IvBox2609Tol,"Side/circle mismatch");
    }
   }
   var witnesses=new System.Collections.Generic.List<object>();var exclusions=new System.Collections.Generic.List<object>();
   foreach(double theta in new double[]{.37,2.1,4.3}) {
    var radial=IvBox2609Add(IvBox2609Add(new double[3],outer.x,Math.Cos(theta)),outer.y,Math.Sin(theta));
    foreach(double fraction in new double[]{.27,.63}) {
     var x=IvBox2609Add(outer.center,radial,inner.radius+(outer.radius-inner.radius)*fraction);
     witnesses.Add(IvAn2609Witness(f,body,x,n,offset));
    }
    foreach(double radius in new double[]{inner.radius/2,outer.radius+10*offset}) {
     var x=IvBox2609Add(outer.center,radial,radius);
     int fs=IvTrim2610Status(x,f.f.Tag),bs=IvTrim2610Status(x,body.Tag);
     IvBox2609Need(fs==2&&bs==2,"Hole/exterior not excluded");
     exclusions.Add(new{point=x,face_status=fs,body_status=bs});
    }
   }
   string proof="Bounded planar face has exactly two distinct full circular edges, each one closed simple loop; concentric coplanar radii are strictly ordered. Complete reciprocal edge inventory excludes additional boundary components. Native annular witnesses lie inside the face; hole and exterior witnesses lie outside. This establishes only this connected annular cap under the checked analytic topology.";
   caps.Add(new{face_tag=f.f.Tag.ToString(),point=outer.center,normal=n,native_point=f.point,native_direction=f.dir,native_sense=f.sense,direction_plus_status=plus,direction_minus_status=minus,
    loops=new object[]{IvTrim2610Loop(outer,"outer",n),IvTrim2610Loop(inner,"hole",n)},witnesses=witnesses,exclusions=exclusions,
    topology=new{coverage_evidence=proof,simplicity_evidence="Each positive-radius full circle is simple and traversed once.",
     disjoint_evidence="Concentric circles with strictly separated radii do not intersect.",
     containment_evidence="Smaller measured radius lies strictly inside larger measured radius; native hole exclusion corroborates."}});
  }
  boundary=new{schema_version=1,family="annular_planar_caps_only",units="mm",part_path=p.FullPath,body_tag=body.Tag.ToString(),
   caps=caps,incidence=incidence,consistency_fault_count=nf,failed_feature_count=failed.Length,
   native_distance_tolerance_mm=tol,geometric_comparison_tolerance_mm=IvBox2609Tol,
   whole_body_closure_proven=false,side_faces_integrated=false,native_mass_used=false};
 }catch(Exception e){error=e.Message;}
 if(bh!=null&&bi!=null)try {
  IvTrim2610Guard(p);am=Convert.ToInt32(uf.Part.IsModified(p.Tag));ai=IvBox2609Inventory(p);ah=IvBox2609Hash(p.FullPath);
  unchanged=bh==ah&&bm==0&&am==0&&String.Join("\n",bi)==String.Join("\n",ai);
  if(!unchanged)auditError="Read-only invariant failed";
 }catch(Exception e){auditError=e.Message;}
 bool ok=error==null&&auditError==null&&unchanged&&boundary!=null;
 return Json(new{ok=ok,command="nx_independent_annular_caps",read_only_query=true,error=error,audit_error=auditError,
  boundary=ok?boundary:null,audit=new{sha256_before=bh,sha256_after=ah,modified_before=bm,modified_after=am,
   objects_before=bi,objects_after=ai,unchanged=unchanged},safe_pause_required=auditError!=null,retry_automatically=false,
  volume_computed=false,native_verified=false});
}

// Append INSIDE NX_MCP_Loader AFTER existing annular/analytic helpers.
// SOURCE CANDIDATE ONLY: not compiled, deployed or executed.
// Dispatch ONE new readonly query:
// case "nx_independent_annular_periodic": return IvPer2610Read(parts);
private static double[] IvPer2610Add(double[] a,double[] b,double k) {
 return new double[]{a[0]+k*b[0],a[1]+k*b[1],a[2]+k*b[2]};
}
private static void IvPer2610Near(double[] a,double[] b,double tol,string why) {
 IvBox2609Need(IvBox2609Norm(IvBox2609Sub(a,b))<=tol,why);
}
private static double[] IvPer2610Unit(double[] a) {
 double n=IvBox2609Norm(a);IvBox2609Need(n>1e-12,"Degenerate vector");
 return new double[]{a[0]/n,a[1]/n,a[2]/n};
}
private static double[] IvPer2610Circle(IvAn2609Circle c,double t) {
 return IvPer2610Add(IvPer2610Add(c.center,c.x,c.radius*Math.Cos(t)),c.y,c.radius*Math.Sin(t));
}
private static string IvPer2610Read(string[] args) {
 var p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string before=null,error=null,auditError=null;string[] inventory=null;
 bool unchanged=false;object result=null;
 try {
  IvBox2609Need(args.Length==1,"No query arguments");IvTrim2610Guard(p);
  before=IvBox2609Hash(p.FullPath);inventory=IvBox2609Inventory(p);
  IvBox2609Need(Convert.ToInt32(uf.Part.IsModified(p.Tag))==0,"Saved fixture required");
  // Preserve existing cap query and its full evidence verbatim, not reimplement it.
  string capReceipt=IvTrim2610Read(args);
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();
  foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid");
  var body=bodies[0];var faces=body.GetFaces();var edges=body.GetEdges();
  int nf;int[] faults;NXOpen.Tag[] bad;
  uf.Modl.AskBodyConsistency(body.Tag,out nf,out faults,out bad);
  IvBox2609Need(nf==0&&faces.Length==4&&edges.Length==4,"Four-face/four-circle healthy fixture only");
  var fd=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Face>();
  var ec=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Circle>();
  var uses=new System.Collections.Generic.Dictionary<NXOpen.Tag,int>();
  var caps=new System.Collections.Generic.List<IvAn2609Face>();
  var sides=new System.Collections.Generic.List<IvAn2609Face>();
  foreach(var f in faces) {
   var d=IvAn2609Data(f);fd.Add(f.Tag,d);
   if(d.type==22)caps.Add(d);else {IvBox2609Need(d.type==16,"Nonanalytic face");sides.Add(d);}
  }
  IvBox2609Need(caps.Count==2&&sides.Count==2,"Two caps/two cylinders");
  foreach(var e in edges) {
   IvBox2609Need(e.GetBody().Tag==body.Tag,"Foreign edge");
   ec.Add(e.Tag,IvAn2609Arc(e));uses.Add(e.Tag,0);
   var adj=e.GetFaces();IvBox2609Need(adj.Length==2&&adj[0].Tag!=adj[1].Tag,"Two distinct edge incidences");
   IvBox2609Need(fd.ContainsKey(adj[0].Tag)&&fd.ContainsKey(adj[1].Tag),"Foreign face");
   IvBox2609Need((fd[adj[0].Tag].type==22)!=(fd[adj[1].Tag].type==22),"Cap/side adjacency");
  }
  foreach(var f in faces) {
   var seen=new System.Collections.Generic.HashSet<NXOpen.Tag>();var fe=f.GetEdges();
   IvBox2609Need(fe.Length==2,"Extra trims or topological seams rejected");
   foreach(var e in fe) {
    IvBox2609Need(ec.ContainsKey(e.Tag)&&seen.Add(e.Tag),"Unknown/repeated edge");
    bool back=false;foreach(var a in e.GetFaces())if(a.Tag==f.Tag)back=true;
    IvBox2609Need(back,"Nonreciprocal incidence");uses[e.Tag]++;
   }
  }
  foreach(var u in uses)IvBox2609Need(u.Value==2,"Incomplete edge coverage");
  // Two concentric circular loops on each actual plane, with common cylinder axis.
  foreach(var cap in caps) {
   var ce=cap.f.GetEdges();var a=ec[ce[0].Tag];var b=ec[ce[1].Tag];
   IvPer2610Near(a.center,b.center,1e-7,"Nonconcentric cap");
   IvBox2609Need(Math.Abs(a.radius-b.radius)>1e-5,"Zero wall");
   var n=IvPer2610Unit(cap.dir);
   IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Sub(a.center,cap.point),n))<1e-7,"Circle off plane");
   IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Cross(a.x,a.y),n))>1-1e-9
    &&Math.Abs(IvBox2609Dot(IvBox2609Cross(b.x,b.y),n))>1-1e-9,"Cap circle axes");
  }
  var rows=new System.Collections.Generic.List<object>();var cancellations=new System.Collections.Generic.List<object>();
  foreach(var side in sides) {
   var se=side.f.GetEdges();var c0=ec[se[0].Tag];var c1=ec[se[1].Tag];
   var delta=IvBox2609Sub(c1.center,c0.center);double height=IvBox2609Norm(delta);
   var axis=IvPer2610Unit(delta);var z=IvBox2609Cross(c0.x,c0.y);
   IvBox2609Need(height>1e-5&&Math.Abs(c0.radius-c1.radius)<1e-7
    &&Math.Abs(side.radius-c0.radius)<1e-7,"Cylinder circle radii/span");
   IvBox2609Need(Math.Abs(IvBox2609Dot(z,axis))>1-1e-9
    &&Math.Abs(IvBox2609Dot(IvPer2610Unit(side.dir),axis))>1-1e-9
    &&Math.Abs(IvBox2609Dot(IvBox2609Cross(c1.x,c1.y),axis))>1-1e-9,"Cylinder/loop axis mismatch");
   var off=IvBox2609Sub(c0.center,side.point);
   IvPer2610Near(off,new double[]{axis[0]*IvBox2609Dot(off,axis),axis[1]*IvBox2609Dot(off,axis),axis[2]*IvBox2609Dot(off,axis)},1e-7,"Circle off cylinder axis");
   double[] bounds=new double[4];uf.Modl.AskFaceUvMinmax(side.f.Tag,bounds);IvBox2609Finite(bounds);
   int us,vs;double up,vp;uf.Modl.AskFacePeriodicity(side.f.Tag,out us,out up,out vs,out vp);
   // Positive finite native period selects axis; status retained for coordinator review.
   bool pu=us==1&&up>0&&!Double.IsInfinity(up)&&!Double.IsNaN(up);
   bool pv=vs==1&&vp>0&&!Double.IsInfinity(vp)&&!Double.IsNaN(vp);
   IvBox2609Need(pu!=pv,"Exactly one native periodic parameter");
   int pi=pu?0:1,qi=1-pi;double period=pu?up:vp;
   IvBox2609Need(Math.Abs(bounds[2*pi+1]-bounds[2*pi]-period)<1e-8*Math.Max(1,period),"UV not one period");
   var samples=new System.Collections.Generic.List<object>();int orientation=0;
   double nativeTol;uf.Modl.AskDistanceTolerance(out nativeTol);
   double eps=Math.Max(8*nativeTol,100*IvBox2609Tol);
   IvBox2609Need(eps<Math.Min(height,c0.radius)/10,"Unsafe containment witness offset");
   for(int i=0;i<=16;i++)for(int j=0;j<=4;j++) {
    double t=2*Math.PI*i/16.0,s=j/4.0;
    var x=IvPer2610Add(IvPer2610Circle(c0,t),delta,s);
    double[] uv=new double[2],projected=new double[3],q=new double[3],du=new double[3],dv=new double[3],normal=new double[3];
    uf.Modl.AskFaceParm2(side.f.Tag,x,uv,projected);
    uf.Modl.AskFaceProps(side.f.Tag,uv,q,du,dv,new double[3],new double[3],normal,new double[2]);
    IvBox2609Finite(uv);IvBox2609Finite(du);IvBox2609Finite(dv);IvBox2609Finite(normal);
    IvPer2610Near(x,projected,1e-7,"Inverse projection mismatch");IvPer2610Near(x,q,1e-7,"Native correspondence mismatch");
    var dt=new double[]{c0.radius*(-c0.x[0]*Math.Sin(t)+c0.y[0]*Math.Cos(t)),
     c0.radius*(-c0.x[1]*Math.Sin(t)+c0.y[1]*Math.Cos(t)),
     c0.radius*(-c0.x[2]*Math.Sin(t)+c0.y[2]*Math.Cos(t))};
    var cross=IvBox2609Cross(dt,delta);var n=IvPer2610Unit(cross);
    var nativeCross=IvPer2610Unit(IvBox2609Cross(du,dv));
    IvBox2609Need(Math.Abs(IvBox2609Dot(nativeCross,n))>1-1e-8,"Derivative tangent-plane disagreement");
    var dtheta=pu?du:dv;var daxial=pu?dv:du;
    IvBox2609Need(Math.Abs(IvBox2609Dot(IvPer2610Unit(dtheta),IvPer2610Unit(dt)))>1-1e-8
     &&Math.Abs(IvBox2609Dot(IvPer2610Unit(daxial),axis))>1-1e-8,"Native UV axes not separable cylinder");
    IvBox2609Need(Math.Abs(IvBox2609Norm(dtheta)*period-2*Math.PI*c0.radius)<1e-6,"Periodic derivative scaling");
    IvBox2609Need(Math.Abs(IvBox2609Norm(daxial)*(bounds[2*qi+1]-bounds[2*qi])-height)<1e-6,"Axial derivative scaling");
    int trim=IvTrim2610Status(q,side.f.Tag);IvBox2609Need(trim==1||trim==3,"Point outside side trim");
    int plus=0,minus=0,sgn=0;
    if(j>0&&j<4) {
     plus=IvTrim2610Status(IvPer2610Add(q,n,eps),body.Tag);
     minus=IvTrim2610Status(IvPer2610Add(q,n,-eps),body.Tag);
     IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Containment cannot orient");
     sgn=plus==2?1:-1;if(orientation==0)orientation=sgn;
     IvBox2609Need(orientation==sgn,"Inconsistent side orientation");
    }
    // Seam endpoints evaluated at both native period bounds, not merely inverse-mapped.
    var ua=(double[])uv.Clone();var ub=(double[])uv.Clone();
    ua[pi]=bounds[2*pi];ub[pi]=bounds[2*pi+1];
    double[] qa=new double[3],qb=new double[3],au=new double[3],av=new double[3],bu=new double[3],bv=new double[3];
    uf.Modl.AskFaceProps(side.f.Tag,ua,qa,au,av,new double[3],new double[3],new double[3],new double[2]);
    uf.Modl.AskFaceProps(side.f.Tag,ub,qb,bu,bv,new double[3],new double[3],new double[3],new double[2]);
    IvPer2610Near(qa,qb,1e-7,"Open parameter seam");IvPer2610Near(au,bu,1e-7,"Seam du mismatch");IvPer2610Near(av,bv,1e-7,"Seam dv mismatch");
    samples.Add(new{theta=t,s=s,uv=uv,point=q,du=du,dv=dv,native_normal=normal,dt=dt,ds=delta,
     trim_status=trim,plus_status=plus,minus_status=minus,orientation=sgn,offset_mm=eps,
     seam_uv_a=ua,seam_uv_b=ub,seam_point_a=qa,seam_point_b=qb,seam_du_a=au,seam_du_b=bu,seam_dv_a=av,seam_dv_b=bv});
   }
   // Oriented patch boundary: at s=0 +theta, at s=1 -theta (times orientation).
   // Compare this induced direction with independently outward-oriented planar annulus.
   for(int k=0;k<2;k++) {
    var c=k==0?c0:c1;IvAn2609Face cap=null;
    foreach(var a in c.e.GetFaces())if(fd[a.Tag].type==22)cap=fd[a.Tag];
    IvBox2609Need(cap!=null,"Missing cap");
    var ce=cap.f.GetEdges();var other=ec[ce[0].Tag==c.e.Tag?ce[1].Tag:ce[0].Tag];
    bool outer=c.radius>other.radius;double rmid=(c.radius+other.radius)*0.5;
    var test=IvPer2610Add(c.center,c0.x,rmid);var n=IvPer2610Unit(cap.dir);
    int plus=IvTrim2610Status(IvPer2610Add(test,n,eps),body.Tag);
    int minus=IvTrim2610Status(IvPer2610Add(test,n,-eps),body.Tag);
    IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Cap normal ambiguous");
    if(plus==1)n=new double[]{-n[0],-n[1],-n[2]};
    int capSense=(IvBox2609Dot(z,n)>0?1:-1)*(outer?1:-1);
    int sideSense=orientation*(k==0?1:-1);
    IvBox2609Need(capSense+sideSense==0,"Cap/side oriented boundary does not cancel");
    cancellations.Add(new{edge_tag=c.e.Tag.ToString(),cap_tag=cap.f.Tag.ToString(),side_tag=side.f.Tag.ToString(),
     cap_sense_in_common_circle_frame=capSense,side_sense_in_common_circle_frame=sideSense,
     cap_normal=n,cap_plus_status=plus,cap_minus_status=minus});
   }
   rows.Add(new{face_tag=side.f.Tag.ToString(),type=side.type,native_sense_metadata=side.sense,
    edge_tags=new string[]{c0.e.Tag.ToString(),c1.e.Tag.ToString()},center=c0.center,x_axis=c0.x,y_axis=c0.y,
    radius=c0.radius,delta=delta,height=height,orientation=orientation,uv_bounds=bounds,
    u_status=us,u_period=up,v_status=vs,v_period=vp,periodic_axis=pi,
    witnesses=samples,domain="theta=[0,2*pi],s=[0,1]; analytic circle translation; seam identified once"});
  }
  result=new{schema_version=1,family="checked_annular_analytic_fixture",part_path=p.FullPath,body_tag=body.Tag.ToString(),
   source_sha256=before,cap_receipt_json=capReceipt,sides=rows,boundary_cancellation=cancellations,
   closure_scope="Only exact guarded healthy four-face/four-full-circle analytic fixture. Complete edge inventory, reciprocal cap/side incidences, concentric planar rings, coaxial cylinder circles, one periodic cover and induced orientation cancellation. Witness grid alone is not a general trim proof.",
   native_volume_used_in_integrand=false};
 }catch(Exception e){error=e.Message;}
 finally {
  try {unchanged=before!=null&&IvBox2609Hash(p.FullPath)==before
    &&Convert.ToInt32(uf.Part.IsModified(p.Tag))==0
    &&System.Linq.Enumerable.SequenceEqual(inventory,IvBox2609Inventory(p));}
  catch(Exception e){auditError=e.Message;}
 }
 return Json(new{ok=error==null&&auditError==null&&unchanged,error=error,audit_error=auditError,
  unchanged=unchanged,read_only_query=true,boundary=result,
  scope="Read-only periodic side boundary and orientation evidence."});
}

// Coordinator independent acceptance probe. No builders, mutation, mass evaluation or saves.
private static string IndependentSurfaceProbe(string[] args) {
 if(args.Length!=1 || _part==null || !_part.IsFullyLoaded)throw new Exception("One loaded saved part required");
 string path=Path.GetFullPath(_part.FullPath);
 string root=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227";
 bool allowed=path==root+@"\complex_probe_copy.prt" || path==root+@"\complex_offline_attempt3_readback.prt" ||
  (String.Equals(Path.GetDirectoryName(path),GsRoot,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(path).StartsWith("phase4_gs_",StringComparison.Ordinal));
 if(!allowed || !File.Exists(path) || _part.PartUnits!=BasePart.Units.Millimeters)throw new Exception("Only exact saved diagnostic or GS parts permitted");
 var s=Session.GetSession();int count=0;foreach(BasePart part in s.Parts){count++;if(part.Tag!=_part.Tag)throw new Exception("Other part loaded");}
 if(count!=1||s.Parts.Work!=_part||s.Parts.Display!=_part)throw new Exception("Sole work/display required");
 var uf=NXOpen.UF.UFSession.GetUFSession();if(Convert.ToInt32(uf.Part.IsModified(_part.Tag))!=0)throw new Exception("Unsaved part refused");
 string hash=IvBox2609Hash(path);var inventory=IvBox2609Inventory(_part);var faces=new List<object>();var edges=new List<object>();
 var bodies=_part.Bodies.ToArray();if(bodies.Length!=1)throw new Exception("Exactly one local body required");
 var body=bodies[0];string error=null;
 try {
  int n;int[] faults;Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out n,out faults,out bad);if(n!=0)throw new Exception("Body consistency faults");
  foreach(Face f in body.GetFaces()) {
   double[] lim=new double[4];uf.Modl.AskFaceUvMinmax(f.Tag,lim);
   int type,dirSense;double radius,radData;var pt=new double[3];var axis=new double[3];var bb=new double[6];
   uf.Modl.AskFaceData(f.Tag,out type,pt,axis,bb,out radius,out radData,out dirSense);
   int us,vs;double up,vp;uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
   var samples=new List<object>();
   for(int i=0;i<=16;i++)for(int j=0;j<=8;j++) {
    var uv=new double[]{lim[0]+(lim[1]-lim[0])*i/16.0,lim[2]+(lim[3]-lim[2])*j/8.0};
    var p=new double[3];var du=new double[3];var dv=new double[3];var normal=new double[3];
    uf.Modl.AskFaceProps(f.Tag,uv,p,du,dv,new double[3],new double[3],normal,new double[2]);
    int trim;uf.Modl.AskPointContainment(p,f.Tag,out trim);
    samples.Add(new{uv=uv,point=p,du=du,dv=dv,normal=normal,trim_status=trim});
   }
   faces.Add(new{tag=f.Tag.ToString(),type=type,native_type=f.SolidFaceType.ToString(),point=pt,axis=axis,radius=radius,rad_data=radData,norm_dir=dirSense,
    uv_bounds=lim,u_status=us,u_period=up,v_status=vs,v_period=vp,edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),samples=samples});
  }
  foreach(Edge e in body.GetEdges()) {
   IntPtr h=IntPtr.Zero;try {
    uf.Eval.Initialize(e.Tag,out h);var lim=new double[2];uf.Eval.AskLimits(h,lim);var points=new List<object>();
    for(int i=0;i<=128;i++){double t=lim[0]+(lim[1]-lim[0])*i/128.0;var p=new double[3];var d=new double[3];uf.Eval.Evaluate(h,1,t,p,d);points.Add(new{parameter=t,point=p,derivative=d});}
    edges.Add(new{tag=e.Tag.ToString(),type=e.SolidEdgeType.ToString(),limits=lim,faces=e.GetFaces().Select(f=>f.Tag.ToString()).ToArray(),samples=points});
   }finally{if(h!=IntPtr.Zero)uf.Eval.Free(h);}
  }
 }catch(Exception e){error=e.Message;}
 bool unchanged=IvBox2609Hash(path)==hash&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&inventory.SequenceEqual(IvBox2609Inventory(_part));
 return Json(new{ok=error==null&&unchanged,error=error,part=path,sha256=hash,unchanged=unchanged,read_only_query=true,faces=faces,edges=edges,
  scope="Finite actual native surface/edge observations only; UV bounds are NOT asserted to equal trimmed domains; no independent integral or global error bound"});
}

// Scoped analytic evidence for the saved, hash-verified two-hole test copy.
private static string IvMulti2610Read(string[] args) {
 var p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(args.Length==1,"No query arguments");IvBox2609Guard(p);
 IvBox2609Need(Path.GetFileName(p.FullPath)=="phase4_iv_multihole_integral_20261110.prt","Exact test copy required");
 string hash=IvBox2609Hash(p.FullPath);
 IvBox2609Need(hash=="128a3b26d74e1737c939402ed9aba748a662201ff34ce523dda31a04eac5b6f7"&&Convert.ToInt32(uf.Part.IsModified(p.Tag))==0,"Saved verified copy required");
 var inventory=IvBox2609Inventory(p);var body=p.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"Solid required");
 var faces=body.GetFaces();var edges=body.GetEdges();
 IvBox2609Need(faces.Length==8&&edges.Length==16,"Unexpected fixture topology");
 var edgeRows=new List<object>();var faceRows=new List<object>();
 var circles=new Dictionary<Tag,IvAn2609Circle>();
 foreach(var edge in edges) {
  var adj=edge.GetFaces();IvBox2609Need(adj.Length==2&&adj[0].Tag!=adj[1].Tag&&edge.GetBody().Tag==body.Tag,"Invalid edge adjacency");
  Point3d a,b;edge.GetVertices(out a,out b);
  var kind=edge.SolidEdgeType.ToString();object circle=null;
  if(kind=="Circular") {
   var c=IvAn2609Arc(edge);circles.Add(edge.Tag,c);
   circle=new{center=c.center,x=c.x,y=c.y,radius=c.radius,limits=c.limits,eval_limits=c.evalLimits,periodic=c.periodic};
  }else IvBox2609Need(kind=="Linear","Unsupported edge");
  edgeRows.Add(new{tag=edge.Tag.ToString(),kind=kind,start=IvBox2609V(a),end=IvBox2609V(b),faces=adj.Select(f=>f.Tag.ToString()).ToArray(),circle=circle});
 }
 double tol;uf.Modl.AskDistanceTolerance(out tol);double eps=Math.Max(8*tol,100*IvBox2609Tol);
 foreach(var f in faces) {
  var info=IvAn2609Data(f);var fe=f.GetEdges();object periodic=null;double[] witness,n;
  IvBox2609Need(fe.All(e=>edges.Any(q=>q.Tag==e.Tag)&&e.GetFaces().Any(q=>q.Tag==f.Tag)),"Nonreciprocal face incidence");
  if(info.type==22) {
   var points=new List<double[]>();foreach(var e in fe)if(!circles.ContainsKey(e.Tag)){Point3d a,b;e.GetVertices(out a,out b);points.Add(IvBox2609V(a));points.Add(IvBox2609V(b));}
   IvBox2609Need(points.Count==8,"Plane must have four linear edges");
   witness=Enumerable.Range(0,3).Select(k=>points.Average(q=>q[k])).ToArray();n=IvBox2609Unit(info.dir);
  }else {
   IvBox2609Need(info.type==16&&fe.Length==2&&fe.All(e=>circles.ContainsKey(e.Tag)),"Unsupported side topology");
   var c=circles[fe[0].Tag];var d=circles[fe[1].Tag];
   n=c.x;witness=IvBox2609Add(IvBox2609Add(c.center,IvBox2609Sub(d.center,c.center),.5),n,c.radius);
   double[] uv=new double[4];int us,vs;double up,vp;uf.Modl.AskFaceUvMinmax(f.Tag,uv);uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
   IvBox2609Need((us==1)!=(vs==1),"Missing unique periodic parameter");int k=us==1?0:1;int j=1-k;double period=k==0?up:vp;
   IvBox2609Need(period>0&&Math.Abs(uv[2*k+1]-uv[2*k]-period)<1e-8,"Unexpected period span");
   var aa=new double[2];var bb=new double[2];aa[k]=uv[2*k];bb[k]=uv[2*k+1];aa[j]=bb[j]=(uv[2*j]+uv[2*j+1])/2;
   var x=new double[3];var y=new double[3];var xu=new double[3];var xv=new double[3];var yu=new double[3];var yv=new double[3];
   uf.Modl.AskFaceProps(f.Tag,aa,x,xu,xv,new double[3],new double[3],new double[3],new double[2]);
   uf.Modl.AskFaceProps(f.Tag,bb,y,yu,yv,new double[3],new double[3],new double[3],new double[2]);
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(x,y))<1e-7&&IvBox2609Norm(IvBox2609Sub(xu,yu))<1e-7&&IvBox2609Norm(IvBox2609Sub(xv,yv))<1e-7,"Seam mismatch");
   periodic=new{bounds=uv,u_status=us,v_status=vs,u_period=up,v_period=vp,seam_start=x,seam_end=y,du_start=xu,du_end=yu,dv_start=xv,dv_end=yv};
  }
  int plus=IvTrim2610Status(IvBox2609Add(witness,n,eps),body.Tag),minus=IvTrim2610Status(IvBox2609Add(witness,n,-eps),body.Tag);
  IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Outward witness ambiguous");
  n=IvBox2609Add(new double[3],n,plus==2?1:-1);var checkedWitness=IvAn2609Witness(info,body,witness,n,eps);
  faceRows.Add(new{tag=f.Tag.ToString(),type=info.type,native_sense=info.sense,axis=info.dir,axis_point=info.point,radius=info.radius,edges=fe.Select(e=>e.Tag.ToString()).ToArray(),point=witness,normal=n,witness=checkedWitness,periodic=periodic});
 }
 int faults;int[] faultCodes;Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out faults,out faultCodes,out bad);
 bool same=hash==IvBox2609Hash(p.FullPath)&&inventory.SequenceEqual(IvBox2609Inventory(p))&&Convert.ToInt32(uf.Part.IsModified(p.Tag))==0;
 return Json(new{ok=same&&faults==0,unchanged=same,sha256=hash,part=p.FullPath,faces=faceRows,edges=edgeRows,consistency_fault_count=faults,scope="Actual analytic boundary evidence; ordered loops and volume require independent host validation"});
}

// An isolated, single-use NX 12 native topology acceptance probe.
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-reader-safety-20261110\nurbs-native-brep-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_brep_topology_once(uint face,string path);
private static bool IvBrep2610Attempted;
private static string IvBrep2610Trial(string[] a) {
 const string fixture=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\phase4_nurbs_native_safety_20261110\phase4_nurbs_brep_isolated_v1.prt";
 const string output=@"@NX_LAB_ROOT@\logs\phase5\nurbs-reader-safety-20261110\native-brep-runtime0.log";
 IvBox2609Need(a.Length==1&&!IvBrep2610Attempted&&!File.Exists(output),"One native test only");
 IvBox2609Need(Environment.GetEnvironmentVariable("NX_NURBS_BREP_TRIAL")=="single-sheet-20261110","Independent trial process required");
 IvBox2609Need(_part!=null&&String.Equals(_part.FullPath,fixture,StringComparison.OrdinalIgnoreCase)&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters,"Exact saved sheet copy required");
 for(DirectoryInfo d=new DirectoryInfo(Path.GetDirectoryName(fixture));d!=null;d=d.Parent)IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse path rejected");
 IvBox2609Need((File.GetAttributes(fixture)&FileAttributes.ReparsePoint)==0,"Reparse file rejected");
 var s=Session.GetSession();int parts=0;foreach(BasePart p in s.Parts){parts++;IvBox2609Need(p.Tag==_part.Tag,"Other part loaded");}
 IvBox2609Need(parts==1&&s.Parts.Work==_part&&s.Parts.Display==_part,"Sole work/display part required");
 var uf=NXOpen.UF.UFSession.GetUFSession();int before=Convert.ToInt32(uf.Part.IsModified(_part.Tag));string hash=IvBox2609Hash(fixture);
 IvBox2609Need(before==0&&hash=="12d26f73c58eb28dd664ac74b8566ac11ec611a3515313704fdfc7f6b522e748","Verified unmodified copy required");
 var body=_part.Bodies.ToArray().Single();IvBox2609Need(!body.IsSolidBody,"Sheet required");var face=body.GetFaces().Single();
 IvBox2609Need(face.SolidFaceType.ToString()=="Parametric"&&face.GetEdges().Length==2,"Expected parametric sheet topology");
 var inventory=IvBox2609Inventory(_part);int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 File.WriteAllText(output+".preflight.txt","pid="+pid+" nx=12.0.2.9 face="+face.Tag+" hash="+hash+" objects="+inventory.Length+" modified="+before+"\n");
 IvBrep2610Attempted=true;int rc=nx12_brep_topology_once(Convert.ToUInt32(face.Tag),output);
 var after=IvBox2609Inventory(_part);string afterHash=IvBox2609Hash(fixture);int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 bool unchanged=hash==afterHash&&modified==0&&inventory.SequenceEqual(after);
 return Json(new{ok=rc==0&&unchanged,rc=rc,pid=pid,face=face.Tag.ToString(),native_type=face.SolidFaceType.ToString(),native_edges=face.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),object_count_before=inventory.Length,object_count_after=after.Length,modified_before=before,modified_after=modified,sha256_before=hash,sha256_after=afterHash,unchanged=unchanged,native_log=output,geometry_extraction=false});
}

private static bool FinGeometryAttempted;
private static string FinGeometryProbe(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_fin_geometry_20261112";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\fin-geometry-20261112";
 string mode=Environment.GetEnvironmentVariable("NX_FIN_GEOMETRY_TRIAL");
 IvBox2609Need(args.Length==1&&!FinGeometryAttempted&&(mode=="original"||mode=="step"||mode=="original_cold"||mode=="step_cold"||mode=="offset_stencil"||mode=="step_file_probe"),"Isolated probe required");
 bool step=mode.StartsWith("step",StringComparison.Ordinal);
 string family=step?"step":"original";
 string path=Path.Combine(root,mode+".prt");
 string source=LabPath(@"models\phase4_phase5_staged_20261108_212227\"+(step?"complex_offline_attempt3_readback.prt":"complex_probe_copy.prt"));
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path),"Exact saved copy required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display required");
 string hash=IvBox2609Hash(path);
 IvBox2609Need(hash==IvBox2609Hash(source)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Copy provenance or dirty state");
 var inventory=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"Single solid required");
 var faces=body.GetFaces().ToDictionary(f=>f.Tag.ToString());
 var edges=body.GetEdges().ToDictionary(e=>e.Tag.ToString());
 IvBox2609Need(faces.Count==4&&edges.Count==4,"Unexpected diagnostic fixture");
 var lines=File.ReadAllLines(Path.Combine(logs,(mode=="offset_stencil"||mode=="step_file_probe"?mode:family)+"-requests.tsv"));
 IvBox2609Need(lines.Length>0&&lines.Length<=20000,"Bounded geometry request");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<string,IntPtr>();
 var bounds=new Dictionary<string,double[]>();
 foreach(var f in faces) {
  var uv=new double[4];uf.Modl.AskFaceUvMinmax(f.Value.Tag,uv);bounds[f.Key]=uv;
 }
 FinGeometryAttempted=true;
 string output=Path.Combine(logs,mode+"-native.jsonl");
 using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  try {
   foreach(var edge in edges){IntPtr h;uf.Eval.Initialize(edge.Value.Tag,out h);handles.Add(edge.Key,h);}
   for(int i=0;i<lines.Length;++i) {
    string[] a=lines[i].Split('\t');
    double t=Double.Parse(a[4],culture);
    var h=handles[a[3]];
    if(a[0]=="E") {
     var xyz=new double[3];var d=new double[6];uf.Eval.Evaluate(h,2,t,xyz,d);
     writer.WriteLine(Json(new{kind="E",i=i,point=xyz,d=d}));
    } else if(a[0]=="F") {
     double u=Double.Parse(a[5],culture),v=Double.Parse(a[6],culture);
     var lim=bounds[a[2]];
     if(u<lim[0]-1e-8||u>lim[1]+1e-8||v<lim[2]-1e-8||v>lim[3]+1e-8) {
      writer.WriteLine(Json(new{kind="F",i=i,outside_parameter_box=true}));continue;
     }
     var uv=new[]{Math.Max(lim[0],Math.Min(lim[1],u)),Math.Max(lim[2],Math.Min(lim[3],v))};
     var xyz=new double[3];var du=new double[3];var dv=new double[3];var uu=new double[3];var vv=new double[3];var normal=new double[3];
     uf.Modl.AskFaceProps(faces[a[2]].Tag,uv,xyz,du,dv,uu,vv,normal,new double[2]);
     double phase;var closest=new double[3];var d=new double[6];
     uf.Eval.EvaluateClosestPoint(h,xyz,out phase,closest);
     uf.Eval.Evaluate(h,2,phase,closest,d);
     writer.WriteLine(Json(new{kind="F",i=i,uv=uv,point=xyz,du=du,dv=dv,uu=uu,vv=vv,normal=normal,
      edge_parameter=phase,edge_point=closest,edge_d=d}));
    } else if(a[0]=="X") {
     var xyz=new[]{Double.Parse(a[5],culture),Double.Parse(a[6],culture),Double.Parse(a[7],culture)};
     double phase;var closest=new double[3];var d=new double[6];
     uf.Eval.EvaluateClosestPoint(h,xyz,out phase,closest);
     uf.Eval.Evaluate(h,2,phase,closest,d);
     writer.WriteLine(Json(new{kind="X",i=i,parameter=phase,point=closest,d=d}));
    } else {
     IvBox2609Need(a[0]=="P","Unexpected request kind");
     var p=new[]{Double.Parse(a[5],culture),Double.Parse(a[6],culture),Double.Parse(a[7],culture)};
     var uv=new double[2];var q=new double[3];
     var face=faces[a[2]].Tag;
     uf.Modl.AskFaceParm2(face,p,uv,q);
     int state;uf.Modl.AskPointContainment(q,face,out state);
     writer.WriteLine(Json(new{kind="P",i=i,uv=uv,point=q,trim_status=state}));
    }
   }
  } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
  writer.Flush();file.Flush(true);
 }
 bool clean=hash==IvBox2609Hash(path)&&inventory.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=clean,sha256=hash,pid=System.Diagnostics.Process.GetCurrentProcess().Id,objects=inventory.Length,requests=lines.Length,modified=0});
}

private static bool StepDriftAttempted;
private static string StepDriftCapture(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_STEP_DRIFT_TRIAL");
 bool template=mode!=null&&mode.StartsWith("template_capture_",StringComparison.Ordinal);
 if(template)mode=mode.Substring("template_capture_".Length);
 var valid=template?new[]{"import_control","import_test","ap242_import"}:
  new[]{"original_tol","default","approx_001","approx_00005","sew_on"};
 IvBox2609Need(args.Length==1&&!StepDriftAttempted&&!String.IsNullOrEmpty(mode),"Isolated STEP capture required");
 bool cold=mode.EndsWith("_cold",StringComparison.Ordinal);
 string name=cold?mode.Substring(0,mode.Length-5):mode;
 IvBox2609Need(valid.Contains(name),"Unexpected STEP fixture");
 string root=LabPath(template?@"models\phase4_step_template_fin_20261112":@"models\phase4_step_import_drift_20261112");
 string logs=LabPath(template?@"logs\phase5\step-template-fin-20261112":@"logs\phase5\step-import-drift-20261112");
 string path=Path.Combine(root,name+".prt");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path),"Exact saved fixture required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display required");
 string hash=IvBox2609Hash(path);
 IvBox2609Need(hash==File.ReadAllText(Path.Combine(logs,name+"-expected-sha256.txt")).Trim()&&
  Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Fixture provenance or dirty state");
 var inventory=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 var faces=body.GetFaces();var edges=body.GetEdges();
 IvBox2609Need(body.IsSolidBody&&faces.Length==4&&edges.Length==4,"Expected four-face/four-edge solid");
 int faults;int[] faultCodes;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out faultCodes,out faulty);
 double modelTolerance;uf.Modl.AskDistanceTolerance(out modelTolerance);
 StepDriftAttempted=true;
 string output=Path.Combine(logs,mode+"-native.jsonl");
 using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  Action<object> emit=x=>writer.WriteLine(Json(x));
  emit(new{kind="solid",modeling_tolerance_mm=modelTolerance,consistency_faults=faults,fault_codes=faultCodes});
  foreach(var f in faces) {
   var uv=new double[4];int us,vs;double up,vp;
   uf.Modl.AskFaceUvMinmax(f.Tag,uv);uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
   var features=new List<object>();int faceFeatureError=0;
   if(name=="original_tol") {
    try {
     Tag[] tags;uf.Modl.AskFaceFeats(f.Tag,out tags);
     foreach(var feature in tags) {
      try {
       bool exists;double tolerance;
       uf.Modl.AskFeatTolerance(feature,out exists,out tolerance);
       var feat=_part.Features.ToArray().FirstOrDefault(x=>x.Tag==feature);
       features.Add(new{tag=feature.ToString(),name=feat==null?null:feat.Name,feature_type=feat==null?null:feat.FeatureType,
        exists=exists,tolerance_mm=exists?(double?)tolerance:null});
      } catch(NXException ex) {features.Add(new{tag=feature.ToString(),error=ex.ErrorCode});}
     }
    } catch(NXException ex) {faceFeatureError=ex.ErrorCode;}
   }
   int type,sense;double rad,rad2;var point=new double[3];var axis=new double[3];var box=new double[6];
   uf.Modl.AskFaceData(f.Tag,out type,point,axis,box,out rad,out rad2,out sense);
   emit(new{kind="face",tag=f.Tag.ToString(),type=f.SolidFaceType.ToString(),uv=uv,sense=sense,box=box,
    u_status=us,u_period=up,v_status=vs,v_period=vp,features=features,face_feature_error=faceFeatureError,
    edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray()});
  }
  string[] requests=name=="original_tol"?new string[0]:File.ReadAllLines(Path.Combine(logs,name=="ap242_import"?"ap242-samples.tsv":"step-samples.tsv"));
  IvBox2609Need(requests.Length<=4000,"Bounded source witness set");
  foreach(var edge in edges) {
   double tolerance;uf.Modl.AskEdgeTolerance(edge.Tag,out tolerance);
   var adjacent=edge.GetFaces();
   if(name=="original_tol") {
    emit(new{kind="edge",tag=edge.Tag.ToString(),tolerance_mm=tolerance,faces=adjacent.Select(x=>x.Tag.ToString()).ToArray()});
    continue;
   }
   IntPtr evaluator=IntPtr.Zero;
   try {
    uf.Eval.Initialize(edge.Tag,out evaluator);var limits=new double[2];uf.Eval.AskLimits(evaluator,limits);
    bool spline;uf.Eval.IsSpline(evaluator,out spline);object splineData=null;
    if(spline) {
     NXOpen.UF.UFEval.Spline data;uf.Eval.AskSpline(evaluator,out data);
     int nk,np;double[] knots,poles;
     uf.Eval.AskSplineKnots(evaluator,out nk,out knots);uf.Eval.AskSplineControlPts(evaluator,out np,out poles);
     IvBox2609Need(knots.Length==nk&&poles.Length==4*np,"Native edge spline array size");
     splineData=new{order=data.order,knots=knots,poles=poles,rational=data.is_rational,periodic=data.is_periodic};
    }
    emit(new{kind="edge",tag=edge.Tag.ToString(),tolerance_mm=tolerance,limits=limits,spline=splineData,
      faces=adjacent.Select(x=>x.Tag.ToString()).ToArray()});
    for(int i=0;i<=512;++i) {
     double parameter=limits[0]+(limits[1]-limits[0])*i/512.0;
     var point=new double[3];var derivatives=new double[6];
     uf.Eval.Evaluate(evaluator,2,parameter,point,derivatives);
     var supports=new List<object>();
     foreach(var face in adjacent) {
      var uv=new double[2];var q=new double[3];
      uf.Modl.AskFaceParm2(face.Tag,point,uv,q);
      int state;uf.Modl.AskPointContainment(q,face.Tag,out state);
      supports.Add(new{face=face.Tag.ToString(),uv=uv,point=q,trim_status=state});
     }
     emit(new{kind="edge_sample",tag=edge.Tag.ToString(),i=i,t=parameter,point=point,derivatives=derivatives,supports=supports});
    }
    for(int i=0;i<requests.Length;++i) {
     var xyz=requests[i].Split('\t').Select(x=>Double.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
     IvBox2609Need(xyz.Length==3,"Expected XYZ witness");
     double parameter;var q=new double[3];var derivatives=new double[6];
     uf.Eval.EvaluateClosestPoint(evaluator,xyz,out parameter,q);
     uf.Eval.Evaluate(evaluator,2,parameter,q,derivatives);
     emit(new{kind="file_projection",tag=edge.Tag.ToString(),i=i,t=parameter,point=q,derivatives=derivatives});
    }
   } finally {if(evaluator!=IntPtr.Zero)uf.Eval.Free(evaluator);}
  }
  writer.Flush();file.Flush(true);
 }
 int? brepCode=null;
 if(name!="original_tol"&&!cold) {
  int rc=nx12_offset_fin_geometry(Convert.ToUInt32(body.Tag),1,Path.Combine(logs,mode+"-brep.log"));
  brepCode=rc;
  if(rc!=0)return Json(new{ok=false,rc=rc,quarantine=true,save_permitted=false});
 }
 bool clean=hash==IvBox2609Hash(path)&&inventory.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=clean,sha256=hash,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
  objects=inventory.Length,consistency_faults=faults,brep_code=brepCode,modified=0});
}

private static bool StepTemplateAttempted;
private static string StepTemplateExperiment(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_STEP_TEMPLATE_TRIAL");
 var modes=new[]{"official","template_control_0p01","prepare_test","template_test_0p001","ap242_export","ap242_native_import"};
 IvBox2609Need(args.Length==1&&!StepTemplateAttempted&&modes.Contains(mode),"Isolated STEP template experiment required");
 string root=LabPath(@"models\phase4_step_template_fin_20261112");
 string logs=LabPath(@"logs\phase5\step-template-fin-20261112");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 string official=@"@NX_INSTALL_ROOT@\STEP214UG\step214ugnullnx120_mm.prt";
 string seed=LabPath(@"models\phase4_phase5_staged_20261108_212227\complex_probe_copy.prt");
 string name=mode=="prepare_test"?"template_test_0p001":mode=="ap242_export"?"ap242_source":mode;
 string path=mode=="official"?official:mode=="ap242_native_import"?Path.Combine(root,"ap242_exchange.step"):Path.Combine(root,name+".prt");
 IvBox2609Need(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Exact fixture required");
 string officialHash=IvBox2609Hash(official),hash=IvBox2609Hash(path);
 IvBox2609Need(officialHash==File.ReadAllText(Path.Combine(logs,"official-expected-sha256.txt")).Trim(),"Official template hash changed");
 if(mode=="prepare_test"||mode=="template_control_0p01")IvBox2609Need(hash==officialHash,"Unchanged official copy required");
 if(mode=="ap242_export")IvBox2609Need(hash==IvBox2609Hash(seed),"Independent copy of original geometry required");
 var session=Session.GetSession();
 if(mode=="ap242_native_import") {
  IvBox2609Need(!session.Parts.Cast<BasePart>().Any(),"New part AP242 import requires empty session");
  var input=Path.Combine(root,"ap242_exchange.step");
  var target=Path.Combine(root,"ap242_import.prt");
  IvBox2609Need(File.Exists(input)&&!File.Exists(target)&&(File.GetAttributes(input)&FileAttributes.ReparsePoint)==0,
   "Verified STEP input and new output required");
  string inputHash=IvBox2609Hash(input);
  IvBox2609Need(inputHash==File.ReadAllText(Path.Combine(logs,"ap242-expected-step-sha256.txt")).Trim(),
   "AP242 input hash changed");
  StepTemplateAttempted=true;
  var importer=session.DexManager.CreateStep242Importer();
  try {
   importer.SettingsFile=@"@NX_INSTALL_ROOT@\TRANSLATORS\step242\step242ug.def";
   importer.InputFile=input;
   importer.OutputFile=target;
   importer.ImportTo=Step242Importer.ImportToOption.NewPart;
   importer.ImportToTeamcenter=false;
   importer.FileOpenFlag=false;
   importer.ProcessHoldFlag=true;
   importer.ObjectTypes.Solids=true;
   importer.ObjectTypes.Surfaces=true;
   importer.Commit();
  } finally { importer.Destroy(); }
  bool outputExists=File.Exists(target);
  return Json(new{ok=outputExists&&inputHash==IvBox2609Hash(input)&&officialHash==IvBox2609Hash(official),
   mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,input_sha256=inputHash,output=target,
   output_sha256=outputExists?IvBox2609Hash(target):null,official_sha256=officialHash,save_called=false});
 }
 var uf=NXOpen.UF.UFSession.GetUFSession();
 if(mode=="official") {
  IvBox2609Need(!session.Parts.Cast<BasePart>().Any(),"Official read-only query requires empty session");
  PartLoadStatus load;
  session.Parts.OpenBaseDisplay(path,out load);
  load.Dispose();
 }
 var part=session.Parts.Work as Part;
 IvBox2609Need(part!=null&&part.IsFullyLoaded&&String.Equals(part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&
   session.Parts.Display==part&&session.Parts.Cast<BasePart>().Count()==1&&part.PartUnits==BasePart.Units.Millimeters,
   "Only the specified millimeter work/display fixture may be open");
 StepTemplateAttempted=true;
 int before=Convert.ToInt32(uf.Part.IsModified(part.Tag));
 double tolerance,angle;
 uf.Modl.AskDistanceTolerance(out tolerance);uf.Modl.AskAngleTolerance(out angle);
 var inventory=IvBox2609Inventory(part);
 IvBox2609Need(before==0&&(mode=="ap242_export"?part.Bodies.ToArray().Length==1:part.Bodies.ToArray().Length==0),
  "Saved model state and expected body count required");
 double original=tolerance;
 int modifiedAfterSet=0;
 string output=null,settings=null;
 double? splineTol=null;
 if(mode=="prepare_test") {
  IvBox2609Need(Math.Abs(tolerance-.01)<1e-12,"Expected control tolerance before one-factor change");
  double value=.001;
  uf.Modl.SetDistanceTolerance(ref value);
  uf.Modl.AskDistanceTolerance(out tolerance);
  modifiedAfterSet=Convert.ToInt32(uf.Part.IsModified(part.Tag));
  IvBox2609Need(Math.Abs(tolerance-.001)<1e-12&&inventory.SequenceEqual(IvBox2609Inventory(part)),"Tolerance set/readback or inventory mismatch");
  using(var save=part.Save(BasePart.SaveComponents.False,BasePart.CloseAfterSave.False)) {}
 } else if(mode=="ap242_export") {
  output=Path.Combine(root,"ap242_exchange.step");
  settings=@"@NX_INSTALL_ROOT@\TRANSLATORS\step242\ugstep242.def";
  IvBox2609Need(!File.Exists(output)&&File.Exists(settings),"New STEP output and installed settings required");
  var creator=session.DexManager.CreateStepCreator();
  try {
   creator.SettingsFile=settings;
   creator.ExportAs=StepCreator.ExportAsOption.Ap242;
   creator.InputFile=path;
   creator.OutputFile=output;
   creator.ObjectTypes.Solids=true;
   creator.ObjectTypes.Surfaces=true;
   splineTol=creator.BsplineTol;
   IvBox2609Need(creator.ExportAs==StepCreator.ExportAsOption.Ap242,"AP242 protocol readback failed");
   creator.Commit();
  } finally { creator.Destroy(); }
 }
 int modified=Convert.ToInt32(uf.Part.IsModified(part.Tag));
 double angleAfter;
 uf.Modl.AskAngleTolerance(out angleAfter);
 uf.Modl.AskDistanceTolerance(out tolerance);
 string after=IvBox2609Hash(path);
 bool clean=officialHash==IvBox2609Hash(official)&&modified==0&&angle==angleAfter&&inventory.SequenceEqual(IvBox2609Inventory(part));
 clean=clean&&(mode=="prepare_test"?(Math.Abs(tolerance-.001)<1e-12&&after!=hash):after==hash);
 return Json(new{ok=clean,mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,path=path,
  units=part.PartUnits.ToString(),distance_before_mm=original,distance_after_mm=tolerance,angle_before_deg=angle,
  angle_after_deg=angleAfter,modified_before=before,modified_after_set=modifiedAfterSet,modified_after=modified,
  before_sha256=hash,after_sha256=after,official_sha256=officialHash,output=output,settings=settings,
  export_bspline_tol=splineTol,save_called=mode=="prepare_test"});
}

private static bool FinRepresentationAttempted;
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\uf-brep-fin-20261112\fin-representation-probe.dll",
 CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl,CharSet=System.Runtime.InteropServices.CharSet.Ansi,ExactSpelling=true)]
private static extern int nx12_fin_representation_geometry(uint body,int variant,int edgeMode,string path);

[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\uf-brep-fin-matrix-20261112\fin-matrix-probe.dll",
 CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl,CharSet=System.Runtime.InteropServices.CharSet.Ansi,ExactSpelling=true)]
private static extern int nx12_fin_edge3d_supplement(uint body,int variant,string path);

private static string FinRepresentationAudit(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_FIN_REP_TRIAL");
 var modes=new[]{"b","c","b_cold","c_cold","intersection","intersection_peak","a","d"};
 IvBox2609Need(args.Length==1&&!FinRepresentationAttempted&&modes.Contains(mode),"Isolated Fin representation audit required");
 bool supplement=mode=="a"||mode=="d";
 string folder=LabPath(supplement?@"models\phase4_uf_brep_fin_matrix_20261112":@"models\phase4_uf_brep_fin_20261112");
 string logs=LabPath(supplement?@"logs\phase5\uf-brep-fin-matrix-20261112":@"logs\phase5\uf-brep-fin-20261112");
 string path=Path.Combine(folder,(mode.StartsWith("intersection",StringComparison.Ordinal)?mode:mode.Substring(0,1))+".prt");
 string seed=LabPath(@"models\phase4_phase5_staged_20261108_212227\complex_probe_copy.prt");
 for(DirectoryInfo d=new DirectoryInfo(folder);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)&&
  (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Exact millimeter fixture required");
 var session=Session.GetSession();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(path);
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
  hash==IvBox2609Hash(seed)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Saved unmodified original copy required");
 var body=_part.Bodies.ToArray().Single();
 var faces=body.GetFaces();var edges=body.GetEdges();
 IvBox2609Need(body.IsSolidBody&&faces.Length==4&&edges.Length==4,"Unexpected four-face solid");
 var inventory=IvBox2609Inventory(_part);
 int faults;int[] faultCodes;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out faultCodes,out faulty);
 FinRepresentationAttempted=true;
 if(mode.StartsWith("intersection",StringComparison.Ordinal)) {
  int samples=VerifyFinIntersectionWitnesses(uf,faces,edges,logs,mode);
  bool unchanged=hash==IvBox2609Hash(path)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&inventory.SequenceEqual(IvBox2609Inventory(_part));
  return Json(new{ok=unchanged&&faults==0,mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
   sha256=hash,consistency_faults=faults,samples=samples,modified=0});
 }
 int? fin=null;
 int edgeCode;
 string edgeOutput=Path.Combine(logs,mode+"_edge3d-native.log");
 if(supplement) {
  edgeCode=nx12_fin_edge3d_supplement(Convert.ToUInt32(body.Tag),mode=="a"?0:3,edgeOutput);
 } else {
  int variant=mode[0]=='b'?1:2;
  fin=nx12_fin_representation_geometry(Convert.ToUInt32(body.Tag),variant,0,Path.Combine(logs,mode+"_brep-native.log"));
  if(fin!=0)return Json(new{ok=false,mode=mode,fin_rc=fin,quarantine=true,save_permitted=false});
  edgeCode=nx12_fin_representation_geometry(Convert.ToUInt32(body.Tag),variant,1,edgeOutput);
 }
 if(edgeCode!=0)return Json(new{ok=false,mode=mode,fin_rc=fin,edge_rc=edgeCode,quarantine=true,save_permitted=false});
 string witnesses=Path.Combine(logs,mode+"-native.jsonl");
 using(var file=new FileStream(witnesses,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  foreach(var face in faces) {
   int u,v;double up,vp;var uv=new double[4];
   uf.Modl.AskFaceUvMinmax(face.Tag,uv);uf.Modl.AskFacePeriodicity(face.Tag,out u,out up,out v,out vp);
   writer.WriteLine(Json(new{kind="face",tag=face.Tag.ToString(),face_type=face.SolidFaceType.ToString(),uv=uv,
    u_status=u,u_period=up,v_status=v,v_period=vp,edges=face.GetEdges().Select(x=>x.Tag.ToString()).ToArray()}));
  }
  foreach(var edge in edges) {
   double tolerance;
   uf.Modl.AskEdgeTolerance(edge.Tag,out tolerance);
   IntPtr handle;
   uf.Eval.Initialize(edge.Tag,out handle);
   try {
    var limits=new double[2];uf.Eval.AskLimits(handle,limits);
    writer.WriteLine(Json(new{kind="edge",tag=edge.Tag.ToString(),edge_tolerance_mm=tolerance,range=limits,
     faces=edge.GetFaces().Select(x=>x.Tag.ToString()).ToArray()}));
    for(int i=0;i<=64;++i) {
     double t=limits[0]+(limits[1]-limits[0])*i/64.0;
     var xyz=new double[3];var d=new double[3];
     uf.Eval.Evaluate(handle,1,t,xyz,d);
     writer.WriteLine(Json(new{kind="UF_EVAL",tag=edge.Tag.ToString(),t=t,point=xyz,first_derivative=d}));
    }
   } finally {uf.Eval.Free(handle);}
  }
  writer.Flush();file.Flush(true);
 }
 bool clean=hash==IvBox2609Hash(path)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&inventory.SequenceEqual(IvBox2609Inventory(_part));
 return Json(new{ok=clean&&faults==0,mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
  sha256=hash,fin_rc=fin,edge_rc=edgeCode,consistency_faults=faults,objects=inventory.Length,modified=0});
}

private static int VerifyFinIntersectionWitnesses(NXOpen.UF.UFSession uf,Face[] faces,Edge[] edges,string logs,string mode) {
 var input=File.ReadAllLines(Path.Combine(logs,mode+"-requests.tsv"));
 IvBox2609Need(input.Length>=8&&input.Length<=68,"Bounded intersection witnesses required");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 using(var file=new FileStream(Path.Combine(logs,mode+"-native.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  foreach(string line in input) {
   string[] a=line.Split('\t');
   IvBox2609Need(a.Length==4,"Malformed intersection witness");
   var face=faces.Single(f=>f.Tag.ToString()==a[0]);var edge=edges.Single(e=>e.Tag.ToString()==a[1]);
   IvBox2609Need(edge.GetFaces().Any(f=>f.Tag==face.Tag),"Witness edge/face use mismatch");
   var uv=new[]{Double.Parse(a[2],culture),Double.Parse(a[3],culture)};
   var box=new double[4];uf.Modl.AskFaceUvMinmax(face.Tag,box);
   IvBox2609Need(uv[0]>=box[0]-1e-9&&uv[0]<=box[1]+1e-9&&uv[1]>=box[2]-1e-9&&uv[1]<=box[3]+1e-9,
     "Intersection witness outside native parameter box");
   var p=new double[3];var du=new double[3];var dv=new double[3];var normal=new double[3];
   uf.Modl.AskFaceProps(face.Tag,uv,p,du,dv,new double[3],new double[3],normal,new double[2]);
   int containment;uf.Modl.AskPointContainment(p,face.Tag,out containment);
   IntPtr h;
   uf.Eval.Initialize(edge.Tag,out h);
   try {
    double t;var point=new double[3];var d=new double[3];
    uf.Eval.EvaluateClosestPoint(h,p,out t,point);uf.Eval.Evaluate(h,1,t,point,d);
    writer.WriteLine(Json(new{face=a[0],edge=a[1],uv=uv,point=p,du=du,dv=dv,normal=normal,
      trim_status=containment,edge_t=t,edge_point=point,edge_derivative=d}));
   } finally {uf.Eval.Free(h);}
  }
  writer.Flush();file.Flush(true);
 }
 return input.Length;
}

private static bool FinspFormationAttempted;
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\finsp-formation-20261112\finsp-formation-probe.dll",
 CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl,CharSet=System.Runtime.InteropServices.CharSet.Ansi,ExactSpelling=true)]
private static extern int nx12_finsp_formation(uint body,uint first,uint second,string path);

private static string FinspFormationProbe(string[] args) {
 IvBox2609Need(args.Length==1&&!FinspFormationAttempted&&Environment.GetEnvironmentVariable("NX_FINSP_FORMATION_TRIAL")=="formation",
  "Isolated FINSP representation experiment required");
 string folder=LabPath(@"models\phase4_finsp_formation_20261112");
 string logs=LabPath(@"logs\phase5\finsp-formation-20261112");
 string path=Path.Combine(folder,"formation.prt");
 string original=LabPath(@"models\phase4_phase5_staged_20261108_212227\complex_probe_copy.prt");
 for(DirectoryInfo d=new DirectoryInfo(folder);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)&&
  (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Exact fixture required");
 string hash=IvBox2609Hash(path);
 var session=Session.GetSession();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
  hash=="85e82314c0d86f75cae625b153029f599b71383d87d00aad73c54152c35b3f05"&&hash==IvBox2609Hash(original)&&
  Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Unmodified saved source copy required");
 var inventory=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 var faces=body.GetFaces();
 IvBox2609Need(body.IsSolidBody&&faces.Length==4&&body.GetEdges().Length==4,"Unexpected fixture");
 var offset=faces.Single(f=>f.SolidFaceType.ToString()=="Offset");
 var planes=new Dictionary<string,Face>();
 var edges=new Dictionary<string,Edge>();
 foreach(var edge in offset.GetEdges()) {
  var plane=edge.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Planar");
  var box=new double[6];uf.Modl.AskBoundingBox(plane.Tag,box);
  double x=(box[0]+box[3])/2;
  IvBox2609Need(Math.Abs(x)<.001||Math.Abs(x-180)<.001,"Unrecognized planar support");
  string role=Math.Abs(x)<.001?"xmin":"xmax";
  planes.Add(role,plane);edges.Add(role,edge);
 }
 IvBox2609Need(edges.Count==2&&planes.Count==2,"Expected two Offset edge uses");
 FinspFormationAttempted=true;
 int rc=nx12_finsp_formation(Convert.ToUInt32(body.Tag),Convert.ToUInt32(edges["xmin"].Tag),
  Convert.ToUInt32(edges["xmax"].Tag),Path.Combine(logs,"reverse_brep-native.log"));
 if(rc!=0)return Json(new{ok=false,rc=rc,quarantine=true,save_permitted=false});
 int count=FinspFormationWitnesses(uf,offset,planes,edges,logs);
 bool unchanged=hash==IvBox2609Hash(path)&&inventory.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=unchanged,pid=System.Diagnostics.Process.GetCurrentProcess().Id,rc=rc,sha256=hash,
  objects_before=inventory.Length,objects_after=IvBox2609Inventory(_part).Length,modified=0,witnesses=count,
  offset=offset.Tag.ToString(),plane_xmin=planes["xmin"].Tag.ToString(),plane_xmax=planes["xmax"].Tag.ToString(),
  edge_xmin=edges["xmin"].Tag.ToString(),edge_xmax=edges["xmax"].Tag.ToString()});
}

private static int FinspFormationWitnesses(NXOpen.UF.UFSession uf,Face offset,Dictionary<string,Face> planes,
 Dictionary<string,Edge> edges,string logs) {
 var inputs=File.ReadAllLines(Path.Combine(logs,"physical-requests.tsv"));
 IvBox2609Need(inputs.Length>=20&&inputs.Length<=600,"Bounded physical witness set required");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<string,IntPtr>();
 try {
  foreach(var edge in edges) {
   IntPtr h;uf.Eval.Initialize(edge.Value.Tag,out h);handles.Add(edge.Key,h);
  }
  using(var file=new FileStream(Path.Combine(logs,"physical-native.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
  using(var writer=new StreamWriter(file)) {
   foreach(string line in inputs) {
    var row=line.Split('\t');
    IvBox2609Need(row.Length==10&&(row[1]=="F"||row[1]=="P")&&(row[3]=="plane"||row[3]=="offset"),"Malformed witness");
    string role=row[2];var face=row[3]=="offset"?offset:planes[role];
    var numbers=row.Skip(4).Select(x=>Double.Parse(x,culture)).ToArray();
    var h=handles[role];var limit=new double[2];uf.Eval.AskLimits(h,limit);
    double t=numbers[0];
    IvBox2609Need(t>=limit[0]-1e-12&&t<=limit[1]+1e-12,"Edge parameter out of range");
    var anchor=new double[3];var anchorD=new double[3];
    uf.Eval.Evaluate(h,1,t,anchor,anchorD);
    var uv=new[]{numbers[1],numbers[2]};var inputPoint=numbers.Skip(3).ToArray();
    var nativePoint=new double[3];var du=new double[3];var dv=new double[3];var normal=new double[3];
    if(row[1]=="P")uf.Modl.AskFaceParm2(face.Tag,inputPoint,uv,nativePoint);
    else uf.Modl.AskFaceProps(face.Tag,uv,nativePoint,du,dv,new double[3],new double[3],normal,new double[2]);
    var edgeUv=new double[2];var support=new double[3];
    uf.Modl.AskFaceParm2(face.Tag,anchor,edgeUv,support);
    var closest=new double[3];double closestT;
    uf.Eval.EvaluateClosestPoint(h,nativePoint,out closestT,closest);
    int containment;uf.Modl.AskPointContainment(nativePoint,face.Tag,out containment);
    writer.WriteLine(Json(new{id=row[0],kind=row[1],role=role,face=face.Tag.ToString(),edge=edges[role].Tag.ToString(),
     anchor_t=t,anchor=anchor,anchor_d=anchorD,uv=uv,point=nativePoint,du=du,dv=dv,normal=normal,
     edge_uv=edgeUv,edge_support_point=support,nearest_edge_t=closestT,nearest_edge_point=closest,containment=containment}));
   }
   writer.Flush();file.Flush(true);
  }
 } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
 return inputs.Length;
}

private static bool TrimOffsetAttempted;
private static string FinspTrimOffsetAudit(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_FINSP_TRIM_OFFSET_TRIAL");
 IvBox2609Need(args.Length==1&&!TrimOffsetAttempted&&(mode=="offset_ask2d"||mode=="filter"),"Isolated trim witness process required");
 string folder=LabPath(@"models\phase4_finsp_trim_offset_20261112");
 string logs=LabPath(@"logs\phase5\finsp-trim-offset-20261112");
 string fixture=Path.Combine(folder,mode+".prt");
 for(var directory=new DirectoryInfo(folder);directory!=null;directory=directory.Parent)
  IvBox2609Need((directory.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,fixture,StringComparison.OrdinalIgnoreCase)&&File.Exists(fixture)&&
  (File.GetAttributes(fixture)&FileAttributes.ReparsePoint)==0,"Exact millimeter fixture required");
 var s=Session.GetSession();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(fixture);
 int dirty=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(s.Parts.Cast<BasePart>().Count()==1&&s.Parts.Work==_part&&s.Parts.Display==_part&&dirty==0&&
  hash=="85e82314c0d86f75cae625b153029f599b71383d87d00aad73c54152c35b3f05","Unchanged disposable fixture required");
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody&&body.GetFaces().Length==4&&body.GetEdges().Length==4,"Unexpected fixture geometry");
 var face=body.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Offset");
 var edges=new Dictionary<string,Edge>();
 var planes=new Dictionary<string,Face>();
 foreach(var e in face.GetEdges()) {
  var plane=e.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Planar");
  var box=new double[6];uf.Modl.AskBoundingBox(plane.Tag,box);
  double x=(box[0]+box[3])/2;
  IvBox2609Need(Math.Abs(x)<.001||Math.Abs(x-180)<.001,"Unexpected offset end plane");
  string role=Math.Abs(x)<.001?"xmin":"xmax";
  edges.Add(role,e);planes.Add(role,plane);
 }
 IvBox2609Need(edges.Count==2&&planes.Count==2,"Expected two Offset Edge uses");
 var before=IvBox2609Inventory(_part);
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 var bounds=new double[4];uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
 int u,v;double up,vp;uf.Modl.AskFacePeriodicity(face.Tag,out u,out up,out v,out vp);
 TrimOffsetAttempted=true;
 int count=0;
 if(mode=="offset_ask2d") {
  string raw=Path.Combine(logs,"offset-ask2d-native.log");
  IvBox2609Need(!File.Exists(raw),"Native Ask2d receipt already exists");
  using(var f=new FileStream(raw+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
  using(var w=new StreamWriter(f)) {
   w.WriteLine(Json(new{pid=pid,path=fixture,sha256=hash,face=face.Tag.ToString(),kind="Offset",
    dirty=dirty,object_count=before.Length,uv=bounds,u_status=u,u_period=up,v_status=v,v_period=vp}));
   w.Flush();f.Flush(true);
  }
  int rc=nx12_ask2d_sheet_once(Convert.ToUInt32(face.Tag),raw);
  if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false,post_nx_queries_permitted=false});
 } else {
  count=FinspTrimWitnesses(uf,face,planes,edges,logs);
 }
 var after=IvBox2609Inventory(_part);
 string afterHash=IvBox2609Hash(fixture);
 int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 return Json(new{ok=before.SequenceEqual(after)&&hash==afterHash&&(mode=="offset_ask2d"||modified==0),
  rc=0,pid=pid,mode=mode,face=face.Tag.ToString(),edge_xmin=edges["xmin"].Tag.ToString(),edge_xmax=edges["xmax"].Tag.ToString(),
  object_count_before=before.Length,object_count_after=after.Length,modified_before=dirty,modified_after=modified,
  sha256_before=hash,sha256_after=afterHash,uv=bounds,u_period=up,v_period=vp,witnesses=count,save_permitted=false});
}

private static int FinspTrimWitnesses(NXOpen.UF.UFSession uf,Face offset,Dictionary<string,Face> planes,
 Dictionary<string,Edge> edges,string logs) {
 var requests=File.ReadAllLines(Path.Combine(logs,"trim-requests.tsv"));
 IvBox2609Need(requests.Length>0&&requests.Length<=1800,"Bounded trim candidate set required");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<string,IntPtr>();
 try {
  foreach(var e in edges) {
   IntPtr h;uf.Eval.Initialize(e.Value.Tag,out h);handles.Add(e.Key,h);
  }
  using(var f=new FileStream(Path.Combine(logs,"trim-native.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
  using(var w=new StreamWriter(f)) {
   foreach(string line in requests) {
    string[] a=line.Split('\t');
    IvBox2609Need(a.Length==6&&(a[2]=="offset"||a[2]=="plane"),"Malformed trim witness");
    var face=a[2]=="offset"?offset:planes[a[1]];
    var edge=edges[a[1]];
    var xyz=a.Skip(3).Select(x=>Double.Parse(x,culture)).ToArray();
    var uv=new double[2];var support=new double[3];
    uf.Modl.AskFaceParm2(face.Tag,xyz,uv,support);
    double u=uv[0],v=uv[1];var uvState=new int[1];
    uf.Modl.AskUvPointsContainment(1,ref u,ref v,face.Tag,uvState);
    int rawState,supportState,edgeState;
    uf.Modl.AskPointContainment(xyz,face.Tag,out rawState);
    uf.Modl.AskPointContainment(support,face.Tag,out supportState);
    uf.Modl.AskPointContainment(xyz,edge.Tag,out edgeState);
    double t;var edgePoint=new double[3];var d=new double[3];var h=handles[a[1]];
    uf.Eval.EvaluateClosestPoint(h,xyz,out t,edgePoint);uf.Eval.Evaluate(h,1,t,edgePoint,d);
    w.WriteLine(Json(new{id=a[0],role=a[1],face_role=a[2],face=face.Tag.ToString(),edge=edge.Tag.ToString(),
      xyz=xyz,uv=uv,support_point=support,raw_face_status=rawState,support_face_status=supportState,uv_status=uvState[0],
      direct_edge_status=edgeState,nearest_edge_t=t,nearest_edge_xyz=edgePoint,nearest_edge_d=d}));
   }
   w.Flush();f.Flush(true);
  }
 } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
 return requests.Length;
}

private static bool FinspEdgeCoverageAttempted;
private static string FinspEdgeCoverage(string[] args)
{
    string mode=Environment.GetEnvironmentVariable("NX_FINSP_EDGE_COVERAGE_TRIAL");
    IvBox2609Need(args.Length==1&&!FinspEdgeCoverageAttempted&&(mode=="anchor"||mode=="inverse"),
        "Isolated native edge coverage experiment required");
    string root=LabPath(@"models\phase4_finsp_edge_coverage_20261113");
    string logs=LabPath(@"logs\phase5\finsp-edge-coverage-20261113");
    string path=Path.Combine(root,mode+".prt");
    for(var d=new DirectoryInfo(root);d!=null;d=d.Parent)
        IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
    IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
        String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)&&
        (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Exact copied fixture required");
    var session=Session.GetSession();
    var uf=NXOpen.UF.UFSession.GetUFSession();
    string hash=IvBox2609Hash(path);
    IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
        Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&
        hash=="85e82314c0d86f75cae625b153029f599b71383d87d00aad73c54152c35b3f05","Unchanged isolated fixture required");
    var body=_part.Bodies.ToArray().Single();
    IvBox2609Need(body.IsSolidBody&&body.GetFaces().Length==4&&body.GetEdges().Length==4,"Unexpected fixture");
    var offset=body.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Offset");
    var edges=new Dictionary<string,Edge>();
    var planes=new Dictionary<string,Face>();
    foreach(var edge in offset.GetEdges())
    {
        var plane=edge.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Planar");
        var box=new double[6];
        uf.Modl.AskBoundingBox(plane.Tag,box);
        double x=(box[0]+box[3])/2;
        IvBox2609Need(Math.Abs(x)<.001||Math.Abs(x-180)<.001,"Unknown adjacent plane");
        string role=Math.Abs(x)<.001?"xmin":"xmax";
        edges.Add(role,edge);
        planes.Add(role,plane);
    }
    IvBox2609Need(edges.Count==2,"Two intersection edge uses required");
    var before=IvBox2609Inventory(_part);
    FinspEdgeCoverageAttempted=true;
    int count=0;
    var handles=new Dictionary<string,IntPtr>();
    using(var f=new FileStream(Path.Combine(logs,mode+"-native.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
    using(var w=new StreamWriter(f))
    {
        try
        {
            foreach(var item in edges)
            {
                IntPtr handle;
                uf.Eval.Initialize(item.Value.Tag,out handle);
                handles.Add(item.Key,handle);
                if(mode!="anchor")continue;
                var limits=new double[2];
                uf.Eval.AskLimits(handle,limits);
                int size;
                double[] knots;
                uf.Eval.AskSplineKnots(handle,out size,out knots);
                IvBox2609Need(size==knots.Length&&size>=4&&size<=4096,"Bounded native Edge knots required");
                var cuts=knots.Where(k=>k>limits[0]&&k<limits[1]).Concat(limits).Distinct().OrderBy(k=>k).ToArray();
                double tolerance;
                uf.Modl.AskEdgeTolerance(item.Value.Tag,out tolerance);
                var charts=new List<object>();
                foreach(var face in new[]{planes[item.Key],offset})
                {
                    var bounds=new double[4];
                    uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
                    int us,vs;double up,vp;
                    uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
                    charts.Add(new{tag=face.Tag.ToString(),bounds=bounds,u_period=up,v_period=vp,u_status=us,v_status=vs});
                }
                w.WriteLine(Json(new{kind="edge",role=item.Key,tag=item.Value.Tag.ToString(),
                    charts=charts,limits=limits,knots=knots,cuts=cuts,edge_tolerance_mm=tolerance}));
                var parameters=new SortedSet<double>(cuts);
                for(int i=1;i<cuts.Length;++i)
                    foreach(double q in new[]{.25,.5,.75})parameters.Add(cuts[i-1]+(cuts[i]-cuts[i-1])*q);
                int index=0;
                foreach(double t in parameters)
                {
                    var p=new double[3];var deriv=new double[6];
                    uf.Eval.Evaluate(handle,2,t,p,deriv);
                    string id=item.Key+":"+index++;
                    foreach(string faceRole in new[]{"plane","offset"})
                    {
                        var face=faceRole=="offset"?offset:planes[item.Key];
                        try {w.WriteLine(Json(FinspCoveragePoint(uf,handle,item.Value,face,faceRole,item.Key,id,p,t,deriv)));}
                        catch(NXException e) {w.WriteLine(Json(new{id=id,role=item.Key,face_role=faceRole,t=t,xyz=p,edge_derivatives=deriv,rc=e.ErrorCode}));}
                        ++count;
                    }
                }
            }
            if(mode=="inverse")
            {
                var requests=File.ReadAllLines(Path.Combine(logs,"inverse-requests.tsv"));
                IvBox2609Need(requests.Length>0&&requests.Length<=4500,"Bounded independent candidate set required");
                var culture=System.Globalization.CultureInfo.InvariantCulture;
                foreach(string line in requests)
                {
                    string[] a=line.Split('\t');
                    IvBox2609Need(a.Length==6&&(a[2]=="offset"||a[2]=="plane"),"Malformed inverse request");
                    var xyz=a.Skip(3).Select(x=>Double.Parse(x,culture)).ToArray();
                    var face=a[2]=="offset"?offset:planes[a[1]];
                    try {w.WriteLine(Json(FinspCoveragePoint(uf,handles[a[1]],edges[a[1]],face,a[2],a[1],a[0],xyz,null,null)));}
                    catch(NXException e) {w.WriteLine(Json(new{id=a[0],role=a[1],face_role=a[2],xyz=xyz,rc=e.ErrorCode}));}
                    ++count;
                }
            }
        }
        finally
        {
            foreach(var h in handles)
            {
                uf.Eval.Free(h.Value);
                w.WriteLine(Json(new{kind="release",role=h.Key,operation="UF_EVAL_free",managed_return="success"}));
            }
            w.Flush();f.Flush(true);
        }
    }
    var after=IvBox2609Inventory(_part);
    string afterHash=IvBox2609Hash(path);
    int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
    return Json(new{ok=before.SequenceEqual(after)&&hash==afterHash&&modified==0,rc=0,
        pid=System.Diagnostics.Process.GetCurrentProcess().Id,mode=mode,points=count,
        object_count_before=before.Length,object_count_after=after.Length,modified_before=0,modified_after=modified,
        sha256_before=hash,sha256_after=afterHash,save_permitted=false});
}

private static object FinspCoveragePoint(NXOpen.UF.UFSession uf,IntPtr handle,Edge edge,Face face,
    string faceRole,string role,string id,double[] xyz,double? t,double[] derivatives)
{
    var uv=new double[2];var support=new double[3];
    uf.Modl.AskFaceParm2(face.Tag,xyz,uv,support);
    double u=uv[0],v=uv[1];
    var uvStatus=new int[1];
    uf.Modl.AskUvPointsContainment(1,ref u,ref v,face.Tag,uvStatus);
    int rawStatus,supportStatus,edgeStatus;
    uf.Modl.AskPointContainment(xyz,face.Tag,out rawStatus);
    uf.Modl.AskPointContainment(support,face.Tag,out supportStatus);
    uf.Modl.AskPointContainment(xyz,edge.Tag,out edgeStatus);
    var point=new double[3];var du=new double[3];var dv=new double[3];var normal=new double[3];
    uf.Modl.AskFaceProps(face.Tag,uv,point,du,dv,new double[3],new double[3],normal,new double[2]);
    double closestT;
    var closest=new double[3];var edgeD=new double[3];
    uf.Eval.EvaluateClosestPoint(handle,xyz,out closestT,closest);
    uf.Eval.Evaluate(handle,1,closestT,closest,edgeD);
    return new{kind="sample",id=id,role=role,face_role=faceRole,face=face.Tag.ToString(),edge=edge.Tag.ToString(),
        t=t,xyz=xyz,edge_derivatives=derivatives,native_uv=uv,support_xyz=support,support_eval_xyz=point,
        support_du=du,support_dv=dv,support_normal=normal,raw_face_status=rawStatus,support_face_status=supportStatus,
        uv_status=uvStatus[0],direct_edge_status=edgeStatus,nearest_edge_t=closestT,nearest_edge_xyz=closest,
        nearest_edge_derivative=edgeD,rc=0};
}

private const string ComplexStepRoot=@"@NX_LAB_ROOT@\models\phase4_complex_step_20261110";
private const string ComplexStepLogs=@"@NX_LAB_ROOT@\logs\phase5\complex-step-20261110";
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\complex-step-20261110\offset-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_offset_fin_geometry(uint face,int refined,string path);
private static bool ComplexStepAttempted;
private static string ComplexStepCapture(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_COMPLEX_STEP_TRIAL");
 IvBox2609Need(args.Length==1&&!ComplexStepAttempted&&(mode=="offset"||mode=="offset_cold"||
     mode=="original"||mode=="step"||mode=="original_compare"||mode=="step_compare"||mode=="original_brep"||mode=="step_brep"),"Dedicated one-shot complex probe required");
 string file=Path.Combine(ComplexStepRoot,mode+".prt");
 bool step=mode.StartsWith("step",StringComparison.Ordinal);
 string source=LabPath(@"models\phase4_phase5_staged_20261108_212227\"+(step?"complex_offline_attempt3_readback.prt":"complex_probe_copy.prt"));
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
    String.Equals(_part.FullPath,file,StringComparison.OrdinalIgnoreCase)&&File.Exists(file),"Exact independent saved copy required");
 for(DirectoryInfo d=new DirectoryInfo(ComplexStepRoot);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse ancestor");
 IvBox2609Need((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display required");
 string hash=IvBox2609Hash(file);
 IvBox2609Need(hash==IvBox2609Hash(source)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Dirty part or hash mismatch");
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"Solid required");
 var before=IvBox2609Inventory(_part);
 var faces=body.GetFaces();
 var edges=body.GetEdges();
 IvBox2609Need(faces.Length==4&&edges.Length==4,"Unexpected complex fixture");
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 ComplexStepAttempted=true;
 if(mode.EndsWith("_brep",StringComparison.Ordinal)) {
  var output=Path.Combine(ComplexStepLogs,mode+"-native.log");
  var edgeTolerances=new List<object>();
  foreach(var edge in edges) {
   double tolerance;uf.Modl.AskEdgeTolerance(edge.Tag,out tolerance);
   edgeTolerances.Add(new{tag=edge.Tag.ToString(),tolerance_mm=tolerance});
  }
  int rc=nx12_offset_fin_geometry(Convert.ToUInt32(body.Tag),1,output);
  if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false});
  bool clean=before.SequenceEqual(IvBox2609Inventory(_part))&&hash==IvBox2609Hash(file)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
  return Json(new{ok=clean,pid=pid,sha256=hash,objects=before.Length,modified=0,edge_tolerances=edgeTolerances,requested_brep_tolerance_mm=.00001});
 }
 if(mode.StartsWith("offset",StringComparison.Ordinal)) {
  var f=faces.Single(x=>x.SolidFaceType.ToString()=="Offset");
  uint tag=Convert.ToUInt32(f.Tag);
  var bounds=new double[4];int u,v;double pu,pv;
  uf.Modl.AskFaceUvMinmax(f.Tag,bounds);uf.Modl.AskFacePeriodicity(f.Tag,out u,out pu,out v,out pv);
  var output=Path.Combine(ComplexStepLogs,mode+"-native.log");
  int rc=nx12_offset_fin_geometry(tag,0,output);
  if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,face=tag,quarantine=true,save_permitted=false});
  bool clean=before.SequenceEqual(IvBox2609Inventory(_part))&&hash==IvBox2609Hash(file)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
  return Json(new{ok=clean,pid=pid,face=tag,kind="Offset",edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),
    uv=bounds,u_status=u,u_period=pu,v_status=v,v_period=pv,sha256=hash,objects=before.Length,modified=0});
 }
 string dataFile=Path.Combine(ComplexStepLogs,mode+"-native.jsonl");
 using(var stream=new FileStream(dataFile,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(stream)) {
  Action<object> emit=x=>writer.WriteLine(Json(x));
  if(mode.EndsWith("_compare",StringComparison.Ordinal))ComplexStepCompare(uf,faces,edges,mode,emit);
  else {
   foreach(var f in faces) {
    double[] uv=new double[4];int us,vs;double up,vp;
    uf.Modl.AskFaceUvMinmax(f.Tag,uv);uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
    int type,sense;double rad,rad2;var point=new double[3];var axis=new double[3];var box=new double[6];
    uf.Modl.AskFaceData(f.Tag,out type,point,axis,box,out rad,out rad2,out sense);
    emit(new{kind="face",tag=f.Tag.ToString(),type=f.SolidFaceType.ToString(),uv=uv,u_status=us,u_period=up,v_status=vs,v_period=vp,sense=sense,edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray()});
    for(int i=0;i<16;++i)for(int j=0;j<10;++j) {
     double u=uv[0]+(uv[1]-uv[0])*(i+.5)/16.0,v=uv[2]+(uv[3]-uv[2])*(j+.5)/10.0;
     var state=new int[1];uf.Modl.AskUvPointsContainment(1,ref u,ref v,f.Tag,state);
     if(state[0]!=1)continue;
     var p=new double[3];var n=new double[3];
     uf.Modl.AskFaceProps(f.Tag,new[]{u,v},p,new double[3],new double[3],new double[3],new double[3],n,new double[2]);
     emit(new{kind="face_sample",tag=f.Tag.ToString(),uv=new[]{u,v},point=p,normal=n});
    }
   }
   foreach(var e in edges) {
    IntPtr h=IntPtr.Zero;
    try {
     uf.Eval.Initialize(e.Tag,out h);var lim=new double[2];uf.Eval.AskLimits(h,lim);
     bool spline;uf.Eval.IsSpline(h,out spline);object splineData=null;
     if(spline) {
      NXOpen.UF.UFEval.Spline sd;uf.Eval.AskSpline(h,out sd);
      int nk,np;double[] knots,poles;
      uf.Eval.AskSplineKnots(h,out nk,out knots);uf.Eval.AskSplineControlPts(h,out np,out poles);
      IvBox2609Need(knots.Length==nk&&poles.Length==np*4&&nk==sd.num_knots&&np==sd.num_control,"Edge spline arrays");
      splineData=new{order=sd.order,knots=knots,poles=poles,rational=sd.is_rational,periodic=sd.is_periodic};
     }
     emit(new{kind="edge",tag=e.Tag.ToString(),type=e.SolidEdgeType.ToString(),limits=lim,spline=splineData,faces=e.GetFaces().Select(f=>f.Tag.ToString()).ToArray()});
     for(int i=0;i<=128;++i) {
      double t=lim[0]+(lim[1]-lim[0])*i/128.0;var p=new double[3];var d=new double[3];
      uf.Eval.Evaluate(h,1,t,p,d);
      emit(new{kind="edge_sample",tag=e.Tag.ToString(),i=i,t=t,point=p,tangent=d});
     }
    } finally {if(h!=IntPtr.Zero)uf.Eval.Free(h);}
   }
  }
  writer.Flush();stream.Flush(true);
 }
 bool unchanged=hash==IvBox2609Hash(file)&&before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=unchanged,pid=pid,sha256=hash,faces=faces.Length,edges=edges.Length,objects=before.Length,modified=0,data=dataFile});
}
private static void ComplexStepCompare(NXOpen.UF.UFSession uf,Face[] faces,Edge[] edges,string mode,Action<object> emit) {
 var input=Path.Combine(ComplexStepLogs,mode+"-points.tsv");
 var lines=File.ReadAllLines(input);
 IvBox2609Need(lines.Length>0&&lines.Length<=1500,"Comparison sample bounds");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<Tag,IntPtr>();
 try {
  foreach(var e in edges){IntPtr h;uf.Eval.Initialize(e.Tag,out h);handles.Add(e.Tag,h);}
  foreach(string line in lines) {
   var a=line.Split('\t');
   IvBox2609Need(a.Length==10&&(a[0]=="face"||a[0]=="edge"),"Malformed comparison input");
   var p=new double[]{Double.Parse(a[3],culture),Double.Parse(a[4],culture),Double.Parse(a[5],culture)};
   if(a[0]=="edge") {
    foreach(var e in edges) {
     var h=handles[e.Tag];double t;var q=new double[3];var d=new double[3];
     uf.Eval.EvaluateClosestPoint(h,p,out t,q);
     uf.Eval.Evaluate(h,1,t,q,d);
     emit(new{kind="edge",source=a[1],sample=a[2],target=e.Tag.ToString(),parameter=t,point=q,tangent=d});
    }
   } else {
    foreach(var f in faces) {
     var uv=new double[2];var q=new double[3];var n=new double[3];var bounds=new double[4];
     uf.Modl.AskFaceParm2(f.Tag,p,uv,q);uf.Modl.AskFaceUvMinmax(f.Tag,bounds);
     int status;
     uf.Modl.AskPointContainment(q,f.Tag,out status);
     bool domain=uv[0]>=bounds[0]-1e-10&&uv[0]<=bounds[1]+1e-10&&uv[1]>=bounds[2]-1e-10&&uv[1]<=bounds[3]+1e-10;
     if(domain) {
      uv[0]=Math.Min(bounds[1],Math.Max(bounds[0],uv[0]));uv[1]=Math.Min(bounds[3],Math.Max(bounds[2],uv[1]));
      uf.Modl.AskFaceProps(f.Tag,uv,q,new double[3],new double[3],new double[3],new double[3],n,new double[2]);
     }
     emit(new{kind="face",source=a[1],sample=a[2],target=f.Tag.ToString(),uv=uv,point=q,normal=n,trim_status=status,in_param_range=domain});
    }
   }
  }
 } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
}

private const string PeriodicHoleRoot=@"@NX_LAB_ROOT@\models\phase4_periodic_hole_20261110";
private const string PeriodicHoleLogs=@"@NX_LAB_ROOT@\logs\phase5\periodic-hole-20261110";
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\periodic-hole-20261110\periodic-hole-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_periodic_hole_prepare(uint part,int count,string path);
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\periodic-hole-20261110\periodic-hole-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_periodic_hole_surface(uint part,string path);
private static int PeriodicHolePhase;
private static void PeriodicHoleGuard(string mode,string model,bool exists) {
 IvBox2609Need(Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL")==mode&&File.Exists(model)==exists&&
    _part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
    String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact isolated periodic fixture required");
 for(DirectoryInfo d=new DirectoryInfo(PeriodicHoleRoot);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 if(exists)IvBox2609Need((File.GetAttributes(model)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display part required");
}
private static string PeriodicHoleBuild(string[] args) {
 bool prepare=args[0]=="nx_periodic_hole_prepare";
 IvBox2609Need(args.Length==1&&PeriodicHolePhase==(prepare?0:1),"Staged one-shot fixture required");
 bool two=Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL")=="double";
 string stem=two?"periodic_double":"periodic_split";
 string model=Path.Combine(PeriodicHoleRoot,stem+".prt");
 string output=Path.Combine(PeriodicHoleLogs,(two?"double":"split")+(prepare?"-prepare.log":"-surface.log"));
 PeriodicHoleGuard(two?"double":"split",model,false);
 IvBox2609Need(!File.Exists(output)&&!_part.Bodies.ToArray().Any()&&
     _part.Curves.ToArray().Length==(prepare?0:two?16:12),"Expected fixture inventory before native call");
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 uint part=Convert.ToUInt32(_part.Tag);
 PeriodicHolePhase=-1;
 int rc=prepare?nx12_periodic_hole_prepare(part,two?16:12,output):nx12_periodic_hole_surface(part,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false,native_log=output});
 if(prepare) {
  PeriodicHolePhase=1;
  return Json(new{ok=true,rc=rc,pid=pid,native_log=output,save_permitted=false});
 }
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 int faults;int[] types;Tag[] bad;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out types,out bad);
 int us,vs;double up,vp;double[] bounds=new double[4];
 uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
 uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
 var witness=PeriodicHoleWitness(uf,face,two);
 bool ok=!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&faults==0&&
     us==1&&Math.Abs(up-1)<1e-10&&vs==0&&_part.Curves.ToArray().Length==0&&witness.Item2;
 PeriodicHolePhase=2;
 return Json(new{ok=ok,rc=rc,pid=pid,native_log=output,save_permitted=ok,face=face.Tag.ToString(),
    body_consistency_faults=faults,u_status=us,u_period=up,v_status=vs,v_period=vp,uv_bounds=bounds,
    edges=face.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),witness=witness.Item1});
}
private static Tuple<List<object>,bool> PeriodicHoleWitness(NXOpen.UF.UFSession uf,Face face,bool two) {
 var sample=new[]{new[]{.04,.50,2.0},new[]{.96,.50,2.0},new[]{.5,.50,two?2.0:1.0},new[]{.28,.50,1.0},
    new[]{.04,.25,1.0},new[]{.96,.75,1.0},new[]{.5,0.0,3.0},new[]{.5,1.0,3.0}};
 bool ok=true;var rows=new List<object>();
 foreach(var row in sample) {
  double u=row[0],v=row[1];var state=new int[1];
  uf.Modl.AskUvPointsContainment(1,ref u,ref v,face.Tag,state);
  rows.Add(new{u=u,v=v,status=state[0],expected=(int)row[2]});
  ok=ok&&state[0]==(int)row[2];
 }
 return Tuple.Create(rows,ok);
}


private static bool PeriodicReadAttempted;
private static string PeriodicHoleCold(string[] args) {
 IvBox2609Need(args.Length==1&&!PeriodicReadAttempted,"Fresh isolated read required");
 bool two=Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL")=="double-read";
 string original=Path.Combine(PeriodicHoleRoot,two?"periodic_double.prt":"periodic_split.prt");
 string model=Path.Combine(PeriodicHoleRoot,two?"periodic_double_read.prt":"periodic_split_read.prt");
 string output=Path.Combine(PeriodicHoleLogs,two?"double-cold-ask2d.log":"split-cold-ask2d.log");
 PeriodicHoleGuard(two?"double-read":"read",model,true);
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".brep"),"Prior runtime evidence exists");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(model);
 int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(hash==IvBox2609Hash(original)&&modified==0,"Unmodified byte-identical fixture required");
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();var edges=face.GetEdges();
 IvBox2609Need(!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&edges.Length>2&&edges.Length<=32,"Unexpected cold sheet topology");
 int us,vs;double up,vp;double[] bounds=new double[4];
 uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
 uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
 var witness=PeriodicHoleWitness(uf,face,two);
 IvBox2609Need(us==1&&Math.Abs(up-1)<1e-10&&vs==0&&witness.Item2,"Cold periodicity or witness mismatch");
 var before=IvBox2609Inventory(_part);
 var geometry=new List<object>();
 foreach(var edge in edges) {
  IntPtr h=IntPtr.Zero;
  try {
   uf.Eval.Initialize(edge.Tag,out h);var lim=new double[2];uf.Eval.AskLimits(h,lim);
   var points=new List<object>();
   for(int i=0;i<=16;++i) {
    double t=lim[0]+(lim[1]-lim[0])*i/16.0;var p=new double[3];var d=new double[3];
    uf.Eval.Evaluate(h,1,t,p,d);points.Add(new{parameter=t,point=p,tangent=d});
   }
   bool spline;uf.Eval.IsSpline(h,out spline);
   object data=null;
   if(spline) {
    NXOpen.UF.UFEval.Spline d;uf.Eval.AskSpline(h,out d);
    int nk,np;double[] knots,poles;
    uf.Eval.AskSplineKnots(h,out nk,out knots);uf.Eval.AskSplineControlPts(h,out np,out poles);
    IvBox2609Need(knots.Length==nk&&poles.Length==np*4&&nk==d.num_knots&&np==d.num_control,"Bad spline buffer");
    data=new{knots=knots,poles_flat=poles,order=d.order,rational=d.is_rational};
   }
   geometry.Add(new{tag=edge.Tag.ToString(),limits=lim,type=edge.SolidEdgeType.ToString(),spline=data,faces=edge.GetFaces().Select(f=>f.Tag.ToString()).ToArray(),samples=points});
  } finally {if(h!=IntPtr.Zero)uf.Eval.Free(h);}
 }
 int faults;int[] types;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out types,out faulty);
 IvBox2609Need(faults==0,"Cold fixture consistency failure");
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 uint tag=Convert.ToUInt32(face.Tag);
 using(var file=new FileStream(output+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  writer.WriteLine("pid="+pid+" nx=12.0.2.9 part="+model+" face="+tag+" objects="+before.Length+" modified="+modified+" sha256="+hash+" tolerance_mm=0.001");
  writer.WriteLine(Json(new{face=tag,edges=geometry}));
  writer.Flush();file.Flush(true);
 }
 PeriodicReadAttempted=true;
 int brep=nx12_brep_topology_once(tag,output+".brep");
 if(brep!=0)return Json(new{ok=false,rc=brep,pid=pid,quarantine=true,save_permitted=false});
 IvBox2609Need(before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"UF_BREP changed cold fixture");
 int rc=nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&hash==IvBox2609Hash(model),pid=pid,rc=rc,face=face.Tag.ToString(),
  u_period=up,u_status=us,v_status=vs,uv_bounds=bounds,witness=witness.Item1,edges=geometry,objects_before=before.Length,objects_after=after.Length,modified_before=modified,
  modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),sha256=hash,body_consistency_faults=faults,save_permitted=false});
}
private static bool PeriodicComplexAttempted;
private static string PeriodicComplexRead(string[] args) {
 IvBox2609Need(args.Length==1&&!PeriodicComplexAttempted,"Fresh complex probe process required");
 var mode=Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL");
 var match=System.Text.RegularExpressions.Regex.Match(mode??"","^cx_(original|step)_(inventory|[0-7])$");
 IvBox2609Need(match.Success,"Exact complex copy mode required");
 string model=Path.Combine(PeriodicHoleRoot,mode+".prt");
 string source=LabPath(@"models\phase4_phase5_staged_20261108_212227\"+
    (match.Groups[1].Value=="original"?"complex_probe_copy.prt":"complex_offline_attempt3_readback.prt"));
 PeriodicHoleGuard(mode,model,true);
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(model);
 int dirty=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(dirty==0&&hash==IvBox2609Hash(source),"Unmodified byte-identical complex copy required");
 var before=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"One native solid required");
 int faults;int[] codes;Tag[] faultTags;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out codes,out faultTags);
 IvBox2609Need(faults==0,"Complex model consistency failure");
 var faces=body.GetFaces().OrderBy(f=>Convert.ToUInt32(f.Tag)).ToArray();
 var faceRows=new List<object>();
 var edgeRows=new List<object>();
 var edges=body.GetEdges();
 foreach(var face in faces) {
  int us,vs;double up,vp;var bounds=new double[4];
  uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
  uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
  faceRows.Add(new{tag=face.Tag.ToString(),type=face.SolidFaceType.ToString(),uv=bounds,u_period=up,
      u_status=us,v_period=vp,v_status=vs,edges=face.GetEdges().Select(e=>e.Tag.ToString()).ToArray()});
 }
 foreach(var edge in edges)edgeRows.Add(new{tag=edge.Tag.ToString(),type=edge.SolidEdgeType.ToString(),
      faces=edge.GetFaces().Select(f=>f.Tag.ToString()).ToArray()});
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 if(match.Groups[2].Value=="inventory") {
  bool clean=before.SequenceEqual(IvBox2609Inventory(_part))&&hash==IvBox2609Hash(model)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
  return Json(new{ok=clean,pid=pid,faces=faceRows,edges=edgeRows,sha256=hash,body_consistency_faults=faults});
 }
 int index=Int32.Parse(match.Groups[2].Value);
 IvBox2609Need(index<faces.Length,"Face index outside native inventory");
 var selected=faces[index];string kind=selected.SolidFaceType.ToString();
 string strategy;
 if(kind=="Planar") {
  IvBox2609Need(selected.GetEdges().Length>0,"Planar trim edges required");
  strategy="planar trim; raw returned chart and rings, no periodic closure assumption";
 } else if(kind=="Parametric") {
  int u,v;double up,vp;
  uf.Modl.AskFacePeriodicity(selected.Tag,out u,out up,out v,out vp);
  IvBox2609Need(u==0||u==1,"Unsupported parametric U periodicity class");
  IvBox2609Need(v==0||v==1,"Unsupported parametric V periodicity class");
  strategy="parametric trim; record period, raw splines and native graph; no inferred domain";
 } else {
  return Json(new{ok=false,pid=pid,face=selected.Tag.ToString(),kind=kind,unsupported=true,save_permitted=false});
 }
 string output=Path.Combine(PeriodicHoleLogs,mode+"-ask2d.log");
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".brep"),"Prior native evidence exists");
 var tag=Convert.ToUInt32(selected.Tag);
 using(var file=new FileStream(output+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  writer.WriteLine(Json(new{pid=pid,model=model,sha256=hash,face=tag,type=kind,objects=before.Length,dirty=dirty,strategy=strategy,faces=faceRows,edges=edgeRows}));
  writer.Flush();file.Flush(true);
 }
 PeriodicComplexAttempted=true;
 int b=nx12_brep_topology_once(tag,output+".brep");
 if(b!=0)return Json(new{ok=false,pid=pid,rc=b,face=tag,kind=kind,quarantine=true,save_permitted=false});
 IvBox2609Need(before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"BRep changed model");
 int rc=nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,pid=pid,rc=rc,face=tag,kind=kind,quarantine=true,save_permitted=false});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&hash==IvBox2609Hash(model),pid=pid,rc=rc,face=tag,kind=kind,strategy=strategy,
    objects_before=before.Length,objects_after=after.Length,modified_before=dirty,modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),
    sha256=hash,native_log=output,save_permitted=false});
}

// Phase5 isolated spline lifecycle probe. No surface creation or save from this command.
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110\nurbs-spline-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_nurbs_spline_probe(uint part,int mode,string path);
private static bool NurbsSplineProbeAttempted;
private static string NurbsSplineProbe(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_nurbs_fatal_recovery_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_NURBS_RECOVERY");
 IvBox2609Need(args.Length==1&&!NurbsSplineProbeAttempted&&(mode=="1"||mode=="2"||mode=="4"),"Fresh dedicated recovery process required");
 string model=Path.Combine(root,"spline_probe_"+mode+".prt");
 string output=Path.Combine(logs,"spline-"+mode+"-native.log");
 IvBox2609Need(!File.Exists(model)&&!File.Exists(output)&&_part!=null&&_part.IsFullyLoaded&&
   _part.PartUnits==BasePart.Units.Millimeters&&String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact unsaved blank fixture required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
    !_part.Bodies.ToArray().Any()&&!_part.Curves.ToArray().Any(),"Expected sole blank part");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 int dirty=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 var before=IvBox2609Inventory(_part);
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 using(var file=new FileStream(output+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  writer.WriteLine("pid="+pid+" nx=12.0.2.9 part="+model+" part_tag="+_part.Tag+" objects="+before.Length+" modified="+dirty+" persisted=0");
  writer.Flush();file.Flush(true);
 }
 NurbsSplineProbeAttempted=true;
 int rc=nx12_nurbs_spline_probe(Convert.ToUInt32(_part.Tag),Int32.Parse(mode),output);
 if(rc!=0)return Json(new{ok=false,pid=pid,rc=rc,quarantine=true,save_permitted=false,native_log=output});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&!File.Exists(model),pid=pid,rc=rc,mode=mode,
   objects_before=before.Length,objects_after=after.Length,modified_before=dirty,
   modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),prt_exists=File.Exists(model),
   save_permitted=false,native_log=output});
}
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110\nurbs-spline-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_prepare_trim_fixture(uint part,uint holes,string path);
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110\nurbs-spline-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_finish_trim_fixture(uint part,string path);
private static int TrimRecoveryState;
private static string TrimRecovery(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_nurbs_fatal_recovery_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_NURBS_RECOVERY");
 bool prepare=args[0]=="nx_nurbs_trim_recovery_prepare";
 IvBox2609Need(args.Length==1&&(mode=="trim0"||mode=="trim1"||mode=="trim2")&&
    (prepare?TrimRecoveryState==0:TrimRecoveryState==1),"Dedicated staged fixture process required");
 int holes=mode[4]-'0';
 string model=Path.Combine(root,mode+".prt");
 string output=Path.Combine(logs,mode+(prepare?"-prepare.log":"-surface.log"));
 IvBox2609Need(!File.Exists(model)&&!File.Exists(output)&&_part!=null&&_part.IsFullyLoaded&&
   _part.PartUnits==BasePart.Units.Millimeters&&String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact new unsaved fixture required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
  !_part.Bodies.ToArray().Any(),"Expected sole new work/display part");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 uint part=Convert.ToUInt32(_part.Tag);
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 TrimRecoveryState=-1;
 if(prepare) {
  IvBox2609Need(!_part.Curves.ToArray().Any(),"Empty part required");
  int rc=nx12_prepare_trim_fixture(part,(uint)holes,output);
  if(rc==0)TrimRecoveryState=1;
  return Json(new{ok=rc==0,rc=rc,pid=pid,holes=holes,quarantine=rc!=0,save_permitted=false,native_log=output});
 }
 int result=nx12_finish_trim_fixture(part,output);
 if(result!=0)return Json(new{ok=false,rc=result,pid=pid,quarantine=true,save_permitted=false,native_log=output});
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();
 var edges=face.GetEdges();
 IvBox2609Need(!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&edges.Length==4*(1+holes)&&
     !_part.Curves.ToArray().Any(),"Unexpected sheet, edge, or temporary curve count");
 int faults;int[] types;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out types,out faulty);
 IvBox2609Need(faults==0,"Fixture consistency failure");
 var witness=new List<object>();
 var witnessParameters=new[]{new[]{.12,.12},new[]{.29,.35},new[]{.685,.645},new[]{.01,.01}};
 int[] expect={1,holes>=1?2:1,holes>=2?2:1,2};
 for(int i=0;i<expect.Length;++i) {
  double u=witnessParameters[i][0],v=witnessParameters[i][1];var state=new int[1];
  uf.Modl.AskUvPointsContainment(1,ref u,ref v,face.Tag,state);
  witness.Add(new{u=u,v=v,status=state[0],expected=expect[i]});
  IvBox2609Need(state[0]==expect[i],"Native UV containment disagrees with fixture construction");
 }
 TrimRecoveryState=2;
 return Json(new{ok=true,rc=result,pid=pid,holes=holes,face=face.Tag.ToString(),
    edge_count=edges.Length,witness=witness,body_consistency_faults=faults,save_permitted=true,native_log=output});
}
private static bool TrimReadAttempted;
private static string TrimRecoveryRead(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_nurbs_fatal_recovery_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_NURBS_RECOVERY");
 IvBox2609Need(args.Length==1&&!TrimReadAttempted&&(mode=="read0"||mode=="read1"||mode=="read2"),"Fresh read process required");
 int holes=mode[4]-'0';
 string original=Path.Combine(root,"trim"+holes+".prt");
 string model=Path.Combine(root,mode+".prt");
 string output=Path.Combine(logs,mode+"-ask2d.log");
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".brep")&&_part!=null&&_part.IsFullyLoaded&&
   _part.PartUnits==BasePart.Units.Millimeters&&String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact independent copy required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(model)&FileAttributes.ReparsePoint)==0&&(File.GetAttributes(original)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Expected sole work/display part");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(model);
 int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(hash==IvBox2609Hash(original)&&modified==0,"Unmodified byte-identical fixture required");
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();var edges=face.GetEdges();
 IvBox2609Need(!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&edges.Length==4*(1+holes),"Unexpected cold sheet topology");
 var before=IvBox2609Inventory(_part);
 var geometry=new List<object>();
 foreach(var edge in edges) {
  IntPtr h=IntPtr.Zero;
  try {
   uf.Eval.Initialize(edge.Tag,out h);var lim=new double[2];uf.Eval.AskLimits(h,lim);
   var points=new List<object>();
   for(int i=0;i<=16;++i) {
    double t=lim[0]+(lim[1]-lim[0])*i/16.0;var p=new double[3];var d=new double[3];
    uf.Eval.Evaluate(h,1,t,p,d);points.Add(new{parameter=t,point=p,tangent=d});
   }
   bool spline;uf.Eval.IsSpline(h,out spline);
   object data=null;
   if(spline) {
    NXOpen.UF.UFEval.Spline d;uf.Eval.AskSpline(h,out d);
    int nk,np;double[] knots,poles;
    uf.Eval.AskSplineKnots(h,out nk,out knots);uf.Eval.AskSplineControlPts(h,out np,out poles);
    IvBox2609Need(knots.Length==nk&&poles.Length==np*4&&nk==d.num_knots&&np==d.num_control,"Bad spline buffer");
    data=new{knots=knots,poles_flat=poles,order=d.order,rational=d.is_rational};
   }
   geometry.Add(new{tag=edge.Tag.ToString(),limits=lim,type=edge.SolidEdgeType.ToString(),spline=data,faces=edge.GetFaces().Select(f=>f.Tag.ToString()).ToArray(),samples=points});
  } finally {if(h!=IntPtr.Zero)uf.Eval.Free(h);}
 }
 int faults;int[] types;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out types,out faulty);
 IvBox2609Need(faults==0,"Cold fixture consistency failure");
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 uint tag=Convert.ToUInt32(face.Tag);
 using(var file=new FileStream(output+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  writer.WriteLine("pid="+pid+" nx=12.0.2.9 part="+model+" face="+tag+" objects="+before.Length+" modified="+modified+" sha256="+hash+" tolerance_mm=0.001");
  writer.WriteLine(Json(new{face=tag,edges=geometry}));
  writer.Flush();file.Flush(true);
 }
 TrimReadAttempted=true;
 int brep=nx12_brep_topology_once(tag,output+".brep");
 if(brep!=0)return Json(new{ok=false,rc=brep,pid=pid,quarantine=true,save_permitted=false});
 IvBox2609Need(before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"UF_BREP changed cold fixture");
 int rc=nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&hash==IvBox2609Hash(model),pid=pid,rc=rc,face=face.Tag.ToString(),
  holes=holes,edges=geometry,objects_before=before.Length,objects_after=after.Length,modified_before=modified,
  modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),sha256=hash,body_consistency_faults=faults,save_permitted=false});
}

// New read-only capture; consumes no Ask2dtrim or UF_BREP call.
private static string NurbsPcurveEdgeMappingEvidence(string[] args) {
 const string directory=@"@NX_LAB_ROOT@\models\phase4_nurbs_topology_rebuild_20261110";
 var mode=Environment.GetEnvironmentVariable("NX_PCURVE_CAPTURE");
 IvBox2609Need(mode=="periodic-v2"||mode=="periodic-cold","Isolated capture mode required");
 string path=Path.Combine(directory,mode=="periodic-v2"?"periodic_mapping_v2.prt":"periodic_mapping_cold_repair.prt");
 const string expected="12d26f73c58eb28dd664ac74b8566ac11ec611a3515313704fdfc7f6b522e748";
 IvBox2609Need(args.Length==1&&
  _part!=null&&_part.IsFullyLoaded&&String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&
  _part.PartUnits==BasePart.Units.Millimeters,"Isolated periodic mapping fixture required");
 for(DirectoryInfo d=new FileInfo(path).Directory;d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display part required");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&IvBox2609Hash(path)==expected,"Fixture provenance or saved state");
 var before=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 var face=body.GetFaces().Single();
 IvBox2609Need(!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&face.GetEdges().Length==2,"Expected periodic sheet");
 int us,vs;double up,vp;var uv=new double[4];
 uf.Modl.AskFaceUvMinmax(face.Tag,uv);
 uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
 var edges=new List<object>();
 foreach(var edge in face.GetEdges()) {
  IntPtr h=IntPtr.Zero;
  try {
   uf.Eval.Initialize(edge.Tag,out h);
   var limits=new double[2];uf.Eval.AskLimits(h,limits);
   bool isSpline;uf.Eval.IsSpline(h,out isSpline);
   IvBox2609Need(isSpline,"Expected spline evaluator");
   NXOpen.UF.UFEval.Spline spline;uf.Eval.AskSpline(h,out spline);
   int nk,np;double[] knots,poles;
   uf.Eval.AskSplineKnots(h,out nk,out knots);
   uf.Eval.AskSplineControlPts(h,out np,out poles);
   IvBox2609Need(nk==spline.num_knots&&np==spline.num_control&&knots.Length==nk&&poles.Length==4*np,"Invalid native spline counts");
   var samples=new List<object>();
   for(int i=0;i<=32;++i) {
    double t=limits[0]+(limits[1]-limits[0])*i/32.0;
    var p=new double[3];var d=new double[3];uf.Eval.Evaluate(h,1,t,p,d);
    samples.Add(new{parameter=t,point=p,tangent=d});
   }
   edges.Add(new{tag=edge.Tag.ToString(),faces=edge.GetFaces().Select(f=>f.Tag.ToString()).ToArray(),
    native_type=edge.SolidEdgeType.ToString(),limits=limits,knots=knots,poles_flat=poles,order=spline.order,
    rational=spline.is_rational,periodic=spline.is_periodic,samples=samples});
  } finally {if(h!=IntPtr.Zero)uf.Eval.Free(h);}
 }
 var seamSamples=new List<object>();
 var interiorSamples=new List<object>();
 if(mode=="periodic-cold") {
  foreach(double v in new[]{0.0,0.25,0.5,0.75,1.0}) {
   var xyz0=new double[3];var xyz1=new double[3];
   var du0=new double[3];var du1=new double[3];var dv0=new double[3];var dv1=new double[3];
   uf.Modl.AskFaceProps(face.Tag,new[]{uv[0],v},xyz0,du0,dv0,new double[3],new double[3],new double[3],new double[2]);
   uf.Modl.AskFaceProps(face.Tag,new[]{uv[1],v},xyz1,du1,dv1,new double[3],new double[3],new double[3],new double[2]);
   seamSamples.Add(new{v=v,xyz_min=xyz0,xyz_max=xyz1,du_min=du0,du_max=du1,dv_min=dv0,dv_max=dv1});
  }
  foreach(double u in new[]{0.125,0.375,0.625,0.875})foreach(double v in new[]{0.125,0.5,0.875}) {
   var xyz=new double[3];
   uf.Modl.AskFaceProps(face.Tag,new[]{u,v},xyz,new double[3],new double[3],new double[3],new double[3],new double[3],new double[2]);
   int state;uf.Modl.AskPointContainment(xyz,face.Tag,out state);
   interiorSamples.Add(new{u=u,v=v,xyz=xyz,native_state=state});
  }
 }
 var after=IvBox2609Inventory(_part);
 int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 string hash=IvBox2609Hash(path);
 return Json(new{ok=hash==expected&&modified==0&&before.SequenceEqual(after),read_only_query=true,
  face=face.Tag.ToString(),path=path,sha256=hash,modified=modified,objects_before=before.Length,objects_after=after.Length,
  pid=System.Diagnostics.Process.GetCurrentProcess().Id,uv=uv,u_status=us,u_period=up,v_status=vs,v_period=vp,
  edges=edges,seam_samples=seamSamples,interior_samples=interiorSamples,scope="3D Edge geometry only; combines with archived pcurves and BREP after independent correspondence review"});
}

// Single-use experimental native lifecycle trial; process and file guards precede the call.
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\ask2dtrim-20261110\ask2d-native-trial-v2.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_ask2dtrim_once(uint face,string path);
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\ask2dtrim-20261110\ask2d-native-sheet-trial.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_ask2d_sheet_once(uint face,string path);
private static bool IvAsk2610Attempted;
private static string IvAsk2610Trial(string[] a) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\phase4_ask2dtrim_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\ask2dtrim-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_ASK2D_TRIAL");
 IvBox2609Need(a.Length==1&&!IvAsk2610Attempted&&(mode=="planar"||mode=="sheet")&&a[0]=="nx_ask2d_"+mode+"_trial_once","Independent single-use trial process required");
 string fixture=Path.Combine(root,"phase4_ask2d_"+mode+(mode=="planar"?"_once_v2.prt":"_once_v1.prt"));string output=Path.Combine(logs,"ask2d-"+mode+(mode=="planar"?"-native-v2.log":"-native.log"));
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".preflight.txt"),"Prior trial evidence exists; no replay");
 IvBox2609Need(_part!=null&&String.Equals(_part.FullPath,fixture,StringComparison.OrdinalIgnoreCase)&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters,"Exact saved trial copy required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(fixture)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var s=Session.GetSession();int count=0;foreach(BasePart p in s.Parts){count++;IvBox2609Need(p.Tag==_part.Tag,"Other part loaded");}
 IvBox2609Need(count==1&&s.Parts.Work==_part&&s.Parts.Display==_part,"Sole work/display part required");
 var uf=NXOpen.UF.UFSession.GetUFSession();int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));string hash=IvBox2609Hash(fixture);
 string expected=mode=="planar"?"2a7669bf7c12f8bf70196ea23e1956cdc885f331a0b87f6639f244704a852a94":"12d26f73c58eb28dd664ac74b8566ac11ec611a3515313704fdfc7f6b522e748";
 IvBox2609Need(modified==0&&hash==expected,"Saved fixture provenance mismatch");
 var body=_part.Bodies.ToArray().Single();Face face;
 if(mode=="planar") {
  IvBox2609Need(body.IsSolidBody,"Control must be solid");
  var caps=body.GetFaces().Select(IvAn2609Data).Where(f=>f.type==22&&f.f.GetEdges().Length==5&&Math.Abs(f.dir[2])>0.999999).OrderByDescending(f=>f.point[2]).ToArray();
  IvBox2609Need(caps.Length==2&&caps[0].point[2]-caps[1].point[2]>1,"Two distinct holed caps required");face=caps[0].f;
  IvBox2609Need(face.GetEdges().Count(e=>e.SolidEdgeType.ToString()=="Circular")==1&&face.GetEdges().Count(e=>e.SolidEdgeType.ToString()=="Linear")==4,"Known cap border mismatch");
 } else {
  IvBox2609Need(!body.IsSolidBody,"Sheet required");face=body.GetFaces().Single();IvBox2609Need(face.SolidFaceType.ToString()=="Parametric"&&face.GetEdges().Length==2,"Expected parametric sheet");
 }
 var before=IvBox2609Inventory(_part);int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 using(var f=new FileStream(output+".preflight.txt",FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var w=new StreamWriter(f)) {w.WriteLine("pid="+pid+" nx=12.0.2.9 part="+fixture+" face="+face.Tag+" tolerance_mm=0.001 sha256="+hash+" objects="+before.Length+" modified="+modified);w.Flush();f.Flush(true);}
 IvAsk2610Attempted=true;uint tag=Convert.ToUInt32(face.Tag);int rc=mode=="planar"?nx12_ask2dtrim_once(tag,output):nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,mode=mode,face=face.Tag.ToString(),native_log=output,quarantine=true,post_nx_queries_permitted=false});
 var after=IvBox2609Inventory(_part);int afterModified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));string afterHash=IvBox2609Hash(fixture);bool clean=hash==afterHash&&before.SequenceEqual(after);
 return Json(new{ok=clean,rc=rc,pid=pid,mode=mode,face=face.Tag.ToString(),native_type=face.SolidFaceType.ToString(),object_count_before=before.Length,object_count_after=after.Length,modified_before=modified,modified_after=afterModified,sha256_before=hash,sha256_after=afterHash,inventory_unchanged=clean,native_log=output,save_permitted=false});
}

    // P13 v1: independent v2 queue. This does not certify the NX thread contract.
    private const int WmReadonlyContext = 0x8000 + 2;
    private static readonly NxAiLab.Diagnostics.ContextStartupGate ContextStartup = new NxAiLab.Diagnostics.ContextStartupGate();
    private static readonly NxAiLab.Diagnostics.ContextRegistry ContextRequests = new NxAiLab.Diagnostics.ContextRegistry();
    private static readonly ConcurrentQueue<NxAiLab.Diagnostics.ContextRequest> ContextQueue = new ConcurrentQueue<NxAiLab.Diagnostics.ContextRequest>();

    private static byte[] ReadContextAwareLineBytes(Stream stream)
    {
        return NxAiLab.Diagnostics.ContextProtocol.ReadFrame(stream);
    }

    private static void HandleReadonlyContextFrame(Stream stream, byte[] frame)
    {
        NxAiLab.Diagnostics.ContextRequest request;
        try
        {
            string text = new UTF8Encoding(false, true).GetString(frame);
            request = ContextRequests.Accept(text, 30000);
        }
        catch (Exception e)
        {
            // No validated owned UUID: no response fabricated and no dispatch.
            Log("context v2 frame rejected: " + e.GetType().FullName + ": " + e.Message);
            return;
        }
        ContextQueue.Enqueue(request);
        try
        {
            // Existing dispatcher path retained; no claim that thread legality is proven.
            // This synchronous call has no hard execution timeout. The slot rejects late results.
            SendMessage(_timerWindow.Handle, WmReadonlyContext, IntPtr.Zero, IntPtr.Zero);
            string response = request.TakeResponse();
            if (response == null)
            {
                request.Expire();
                response = NxAiLab.Diagnostics.ContextProtocol.ErrorResponse(request.RequestUuid, "UNKNOWN_EXECUTION_OUTCOME", "No timely owned completion; no retry or cancellation");
            }
            byte[] wire = Encoding.UTF8.GetBytes(response + "\n");
            stream.Write(wire, 0, wire.Length);
            stream.Flush();
            Log("context v2 response sent: request_uuid=" + request.RequestUuid);
        }
        catch (Exception e)
        {
            request.Expire();
            Log("context v2 delivery UNKNOWN: request_uuid=" + request.RequestUuid + " " + e.GetType().FullName + ": " + e.Message);
            // Never retry, dequeue v1 response, reset UUID, or execute again.
        }
    }

    private static void DrainReadonlyContextQueue()
    {
        NxAiLab.Diagnostics.ContextRequest request;
        while (ContextQueue.TryDequeue(out request))
        {
            if (!request.TryBegin()) continue;
            try
            {
                var capture = NxAiLab.Diagnostics.ContextCapture.Capture(new ReadonlyContextBackend());
                bool complete = (bool)capture["capture_complete"];
                bool accepted = request.Complete(request.RequestUuid, capture, complete ? "CAPTURE_COMPLETED" : "CAPTURE_PARTIAL", complete);
                if (!accepted) Log("context completion isolated: request_uuid=" + request.RequestUuid + " reason=" + request.LastRejectedCompletion);
            }
            catch (Exception e)
            {
                bool accepted = request.CompleteFailure(request.RequestUuid, e);
                if (!accepted) Log("context failure isolated: request_uuid=" + request.RequestUuid + " reason=" + request.LastRejectedCompletion);
            }
        }
    }

    private sealed class ReadonlyContextBackend : NxAiLab.Diagnostics.IContextBackend
    {
        public int GetProcessId() { using (var process = System.Diagnostics.Process.GetCurrentProcess()) { return process.Id; } }
        public string GetAssemblyLocation() { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        public string GetAssemblyMvid() { return System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("D"); }
        public string GetAssemblyVersion() { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        public string GetAssemblyDiskSha256()
        {
            string path = GetAssemblyLocation();
            if (String.IsNullOrEmpty(path)) throw new IOException("Executing assembly has no disk location");
            var before = new FileInfo(path); long size = before.Length; DateTime written = before.LastWriteTimeUtc;
            byte[] digest;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (var file = File.OpenRead(path)) { digest = hash.ComputeHash(file); }
            var after = new FileInfo(path);
            if (after.Length != size || after.LastWriteTimeUtc != written) throw new IOException("Assembly disk file changed during hash observation");
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }
        public object GetCachedSession() { return _session; } // Read only; never initialize this field.
        public object GetFreshSession() { return Session.GetSession(); }
        public object GetWorkPart(object session) { return ((Session)session).Parts.Work; }
        public object GetDisplayPart(object session) { return ((Session)session).Parts.Display; }
        public object[] GetLoadedParts(object session) { return ((Session)session).Parts.ToArray(); }
        public string GetPartPath(object part) { return ((BasePart)part).FullPath; }
        public string GetPartTag(object part) { return ContextTag(((BasePart)part).Tag); }
        public bool GetPartModified(object part) { return NXOpen.UF.UFSession.GetUFSession().Part.IsModified(((BasePart)part).Tag); }
        public bool GetPartReadOnly(object part) { return ((BasePart)part).IsReadOnly; }
        public bool GetPartFullyLoaded(object part) { return ((BasePart)part).IsFullyLoaded; }
        public int GetUfLoadedCount() { return NXOpen.UF.UFSession.GetUFSession().Part.AskNumParts(); }
        public string GetUfNthTag(int index) { return ContextTag(NXOpen.UF.UFSession.GetUFSession().Part.AskNthPart(index)); }
        public string GetUfWorkTag() { return ContextTag(NXOpen.UF.UFSession.GetUFSession().Assem.AskWorkPart()); }
        public string GetUfDisplayTag() { return ContextTag(NXOpen.UF.UFSession.GetUFSession().Part.AskDisplayPart()); }
        private static string ContextTag(Tag tag) { return tag == Tag.Null ? null : ((int)tag).ToString(CultureInfo.InvariantCulture); }
    }

}

// Portable capture policy. NX getters are supplied by the narrow Loader adapter.
// This file performs no NX, process, pipe, or filesystem operations itself.
namespace NxAiLab.Diagnostics
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    public interface IContextBackend
    {
        int GetProcessId();
        string GetAssemblyLocation();
        string GetAssemblyMvid();
        string GetAssemblyVersion();
        string GetAssemblyDiskSha256();
        object GetCachedSession();
        object GetFreshSession();
        object GetWorkPart(object session);
        object GetDisplayPart(object session);
        object[] GetLoadedParts(object session);
        string GetPartPath(object part);
        string GetPartTag(object part);
        bool GetPartModified(object part);
        bool GetPartReadOnly(object part);
        bool GetPartFullyLoaded(object part);
        int GetUfLoadedCount();
        string GetUfNthTag(int index);
        string GetUfWorkTag();
        string GetUfDisplayTag();
    }

    public static class ContextCapture
    {
        public const int MaximumParts = 10000;

        private sealed class State
        {
            public bool Complete = true;
        }

        private static string Now()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        public static Dictionary<string, object> Receipt(object value, string source, string status, Exception error)
        {
            object detail = error == null ? null : (object)new Dictionary<string, object> {
                { "type", error.GetType().FullName }, { "message", error.Message }
            };
            return new Dictionary<string, object> {
                { "value", value }, { "source", source }, { "status", status },
                { "error", detail }, { "capture_timestamp", Now() }
            };
        }

        private static Dictionary<string, object> Read(State state, string source, Func<object> getter)
        {
            try { return Receipt(getter(), source, "CAPTURED", null); }
            catch (Exception ex) { state.Complete = false; return Receipt(null, source, "ERROR", ex); }
        }

        private static bool Good(Dictionary<string, object> field)
        {
            return (string)field["status"] == "CAPTURED";
        }

        private static string RequiredText(string value, string label)
        {
            if (String.IsNullOrEmpty(value)) throw new InvalidDataException(label + " returned no value");
            return value;
        }

        private static Dictionary<string, object> Part(State state, IContextBackend b, object part)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            var path = Read(state, "BasePart.FullPath", delegate { return b.GetPartPath(part); });
            if (Good(path) && String.IsNullOrEmpty(path["value"] as string))
            {
                path["status"] = "EMPTY_PATH_RETURNED";
                state.Complete = false;
            }
            row["path"] = path;
            row["tag"] = Read(state, "BasePart inherited TaggedObject.Tag", delegate { return RequiredText(b.GetPartTag(part), "Part.Tag"); });
            row["uf_modified"] = Read(state, "UF_PART_is_modified(part.Tag): raw boolean", delegate { return b.GetPartModified(part); });
            row["is_read_only"] = Read(state, "BasePart.IsReadOnly", delegate { return b.GetPartReadOnly(part); });
            row["is_fully_loaded"] = Read(state, "BasePart.IsFullyLoaded", delegate { return b.GetPartFullyLoaded(part); });
            return row;
        }

        private static Dictionary<string, object> Role(State state, IContextBackend b, object fresh, bool isWork)
        {
            string role = isWork ? "Work" : "Display";
            if (fresh == null) return Receipt(null, "fresh Session.Parts." + role, "NOT_ATTEMPTED_NO_FRESH_SESSION", null);
            var getter = Read(state, "fresh Session.Parts." + role, delegate { return isWork ? b.GetWorkPart(fresh) : b.GetDisplayPart(fresh); });
            if (!Good(getter)) return getter;
            object part = getter["value"];
            if (part == null) return Receipt(null, "fresh Session.Parts." + role, isWork ? "NO_ACTIVE_WORK_PART" : "NO_ACTIVE_DISPLAY_PART", null);
            // Each property is read once and independently. In particular, error handling
            // never reads FullPath again and never discards another successful property.
            return Receipt(Part(state, b, part), "fresh Session.Parts." + role, "CAPTURED", null);
        }

        private static Dictionary<string, object> UfTag(State state, string source, Func<string> getter)
        {
            var result = Read(state, source, delegate { return getter(); });
            if (Good(result) && result["value"] == null) result["status"] = "NULL_TAG_RETURNED";
            else if (Good(result) && (string)result["value"] == "")
            {
                state.Complete = false;
                result["status"] = "INVALID_EMPTY_TAG";
            }
            return result;
        }

        private static Dictionary<string, object> ModifiedSummary(List<Dictionary<string, object>> rows, bool complete)
        {
            if (complete && rows.Count == 0)
                return Receipt(null, "aggregate of captured loaded member UF flags", "NOT_APPLICABLE_EMPTY_COLLECTION", null);
            bool known = complete;
            bool any = false;
            foreach (var row in rows)
            {
                var tag = (Dictionary<string, object>)row["tag"];
                var modified = (Dictionary<string, object>)row["uf_modified"];
                if (!Good(tag) || !Good(modified)) known = false;
                else any |= (bool)modified["value"];
            }
            if (!known)
                return Receipt(null, "aggregate of captured loaded member UF flags", "UNKNOWN_PARTIAL_COLLECTION_OR_FLAGS", null);
            return Receipt(new Dictionary<string, object> {
                { "all_captured_flags_known", true }, { "any_captured_modified", any },
                { "scope", "enumerated members and flags only; not an atomic session snapshot" }
            }, "aggregate of captured loaded member UF flags", "CAPTURED", null);
        }

        public static Dictionary<string, object> Capture(IContextBackend b)
        {
            if (b == null) throw new ArgumentNullException("b");
            string started = Now();
            var state = new State();
            var fields = new Dictionary<string, object>();
            fields["process.pid"] = Read(state, "Process.GetCurrentProcess().Id", delegate {
                int pid = b.GetProcessId(); if (pid <= 0) throw new InvalidDataException("PID must be positive"); return pid;
            });
            fields["assembly.location"] = Read(state, "Assembly.GetExecutingAssembly().Location", delegate { return RequiredText(b.GetAssemblyLocation(), "Assembly.Location"); });
            fields["assembly.mvid"] = Read(state, "Assembly.ManifestModule.ModuleVersionId", delegate { return RequiredText(b.GetAssemblyMvid(), "MVID"); });
            fields["assembly.version"] = Read(state, "Assembly.GetName().Version", delegate { return RequiredText(b.GetAssemblyVersion(), "Assembly.Version"); });
            fields["assembly.disk_file_sha256"] = Read(state, "SHA256 of disk file at executing Assembly.Location; NOT memory bytes", delegate {
                string h = b.GetAssemblyDiskSha256();
                if (h == null || !System.Text.RegularExpressions.Regex.IsMatch(h, "^[0-9a-f]{64}$")) throw new InvalidDataException("Disk SHA256 must be lowercase 64 hex");
                return h;
            });
            fields["thread_contract"] = Receipt(null, "Selected local NX12 contract review; thread equality cannot certify legality", "THREAD_CONTRACT_UNKNOWN", null);

            var cachedRead = Read(state, "Loader cached _session read without assignment", delegate { return b.GetCachedSession(); });
            var freshRead = Read(state, "Session.GetSession() fresh getter", delegate { return b.GetFreshSession(); });
            object cached = Good(cachedRead) ? cachedRead["value"] : null;
            object fresh = Good(freshRead) ? freshRead["value"] : null;
            fields["session.cached_present"] = Good(cachedRead)
                ? Receipt(cached != null, "Loader cached _session read without assignment", cached == null ? "CACHED_SESSION_NOT_INITIALIZED" : "CAPTURED", null)
                : cachedRead;
            if (Good(freshRead) && fresh == null)
            {
                state.Complete = false;
                fields["session.fresh_available"] = Receipt(null, "Session.GetSession()", "FRESH_SESSION_UNAVAILABLE", null);
            }
            else fields["session.fresh_available"] = Good(freshRead) ? Receipt(true, "Session.GetSession()", "CAPTURED", null) : freshRead;
            fields["session.managed_reference_equals"] = cached != null && fresh != null
                ? Receipt(Object.ReferenceEquals(cached, fresh), "Object.ReferenceEquals: managed references ONLY, not NX kernel session identity", "CAPTURED", null)
                : Receipt(null, "Object.ReferenceEquals: managed references ONLY", "NOT_APPLICABLE_MISSING_REFERENCE", null);
            fields["work_part"] = Role(state, b, fresh, true);
            fields["display_part"] = Role(state, b, fresh, false);

            var rows = new List<Dictionary<string, object>>();
            bool loadedComplete = false;
            if (fresh == null)
            {
                fields["loaded_parts"] = Receipt(null, "fresh Session.Parts.ToArray(): BasePart[]", "NOT_ATTEMPTED_NO_FRESH_SESSION", null);
            }
            else
            {
                var arrayRead = Read(state, "fresh Session.Parts.ToArray(): BasePart[]", delegate {
                    object[] parts = b.GetLoadedParts(fresh); if (parts == null) throw new InvalidDataException("Loaded array was null"); return parts;
                });
                if (Good(arrayRead))
                {
                    object[] parts = (object[])arrayRead["value"];
                    loadedComplete = parts.Length <= MaximumParts;
                    if (!loadedComplete) state.Complete = false;
                    for (int i = 0; i < Math.Min(parts.Length, MaximumParts); i++)
                    {
                        if (parts[i] == null)
                        {
                            state.Complete = false; loadedComplete = false;
                            var bad = new Dictionary<string, object>();
                            foreach (string property in new [] { "path", "tag", "uf_modified", "is_read_only", "is_fully_loaded" })
                                bad[property] = Receipt(null, "loaded array item " + i, "NULL_PART_ENTRY", null);
                            rows.Add(bad);
                        }
                        else rows.Add(Part(state, b, parts[i]));
                    }
                    fields["loaded_parts"] = Receipt(rows, "fresh Session.Parts.ToArray(): BasePart[]", !loadedComplete ? "PARTIAL" : rows.Count == 0 ? "EMPTY_LOADED_COLLECTION" : "CAPTURED", null);
                }
                else fields["loaded_parts"] = arrayRead;
            }
            fields["loaded_modified_summary"] = ModifiedSummary(rows, loadedComplete);

            var ufCount = Read(state, "UF_PART_ask_num_parts: direct count, NOT return code", delegate {
                int count = b.GetUfLoadedCount(); if (count < 0) throw new InvalidDataException("Negative UF loaded count"); return count;
            });
            fields["uf.loaded_count"] = ufCount;
            var ufRows = new List<Dictionary<string, object>>();
            bool ufComplete = Good(ufCount);
            if (ufComplete)
            {
                int count = (int)ufCount["value"];
                if (count > MaximumParts) { state.Complete = false; ufComplete = false; }
                for (int i = 0; i < Math.Min(count, MaximumParts); i++)
                {
                    int index = i;
                    var tag = UfTag(state, "UF_PART_ask_nth_part(index): direct tag, NOT return code", delegate { return b.GetUfNthTag(index); });
                    if (!Good(tag)) { ufComplete = false; state.Complete = false; }
                    ufRows.Add(new Dictionary<string, object> {
                        { "index", Receipt(i, "requested zero-based UF part index", "CAPTURED", null) }, { "tag", tag }
                    });
                }
                fields["uf.loaded_tags"] = Receipt(ufRows, "UF_PART_ask_nth_part for each index 0..count-1", !ufComplete ? "PARTIAL" : count == 0 ? "EMPTY_LOADED_COLLECTION" : "CAPTURED", null);
            }
            else fields["uf.loaded_tags"] = Receipt(null, "UF_PART_ask_nth_part", "NOT_ATTEMPTED_UNKNOWN_COUNT", null);
            fields["uf.work_tag"] = UfTag(state, "UF_ASSEM_ask_work_part: direct tag", delegate { return b.GetUfWorkTag(); });
            fields["uf.display_tag"] = UfTag(state, "UF_PART_ask_display_part: direct tag", delegate { return b.GetUfDisplayTag(); });

            var managedTags = new List<string>();
            foreach (var row in rows)
            {
                var tag = (Dictionary<string, object>)row["tag"];
                if (!Good(tag)) loadedComplete = false; else managedTags.Add((string)tag["value"]);
            }
            var ufTags = new List<string>();
            foreach (var row in ufRows)
            {
                var tag = (Dictionary<string, object>)row["tag"];
                if (Good(tag)) ufTags.Add((string)tag["value"]);
            }
            if (!loadedComplete || !ufComplete)
                fields["crosscheck.loaded_tags"] = Receipt(null, "managed and UF tag observations", "UNKNOWN_PARTIAL_OBSERVATIONS", null);
            else
            {
                var m = new HashSet<string>(managedTags, StringComparer.Ordinal);
                var u = new HashSet<string>(ufTags, StringComparer.Ordinal);
                bool equal = m.SetEquals(u) && managedTags.Count == ufTags.Count && m.Count == managedTags.Count && u.Count == ufTags.Count;
                if (!equal) state.Complete = false;
                fields["crosscheck.loaded_tags"] = Receipt(new Dictionary<string, object> {
                    { "managed_tags", managedTags }, { "uf_tags", ufTags }, { "same_unique_tag_set", equal }
                }, "sequential managed/UF tag observations; not atomic", equal ? "CONSISTENT_FINITE_OBSERVATION" : "INCONSISTENT", null);
            }
            return new Dictionary<string, object> {
                { "schema_version", "p13-minimal-context-v1" }, { "fields", fields },
                { "capture_window", new Dictionary<string, object> { { "started_at", started }, { "ended_at", Now() }, { "atomic", false } } },
                { "capture_complete", state.Complete }, { "geometry_acceptance", false }
            };
        }
    }
}

// Portable v2 protocol core. No NXOpen, Pipe, process observer, or v1 queue access.
// This file is appended outside NX_MCP_Loader by the checked generator.
namespace NxAiLab.Diagnostics
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Web.Script.Serialization;

    public static class ContextProtocol
    {
        public const string Command = "nx_readonly_context";
        public const string SchemaVersion = "p13-context-v1";
        public const int MaxRequestBytes = 4096;
        public const int MaxResponseBytes = 1048576;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private const string UuidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

        // Same byte/LF framing as v1. Only a detected JSON context frame gains
        // the v2 byte limit. This does not close the caller-owned stream or retry.
        public static byte[] ReadFrame(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            using (MemoryStream bytes = new MemoryStream())
            {
                bool decided = false, context = false;
                int b;
                while ((b = stream.ReadByte()) >= 0)
                {
                    if (b == (byte)'\n') break;
                    bytes.WriteByte((byte)b);
                    if (!decided && b != 32 && b != 9 && b != 13)
                    {
                        decided = true;
                        context = b == (byte)'{';
                    }
                    if (context && bytes.Length > MaxRequestBytes)
                        throw new InvalidDataException("Context request exceeds 4096 bytes");
                }
                return bytes.ToArray();
            }
        }

        public static bool IsCanonicalUuid(string value)
        {
            Guid id;
            return value != null && Regex.IsMatch(value, "\\A" + UuidPattern + "\\z") &&
                Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && id.ToString("D") == value;
        }

        // Deliberately accepts only a tiny JSON grammar. Duplicate keys are rejected
        // before JavaScriptSerializer could collapse them. JSON escapes are not needed
        // for any accepted key/value and are rejected rather than canonicalized.
        public static string ParseRequestUuid(string frame)
        {
            if (frame == null) throw new FormatException("NULL_CONTEXT_FRAME");
            if (StrictUtf8.GetByteCount(frame) > MaxRequestBytes) throw new FormatException("CONTEXT_REQUEST_LIMIT_EXCEEDED");
            Match outer = Regex.Match(frame, "\\A[ \\t\\r\\n]*\\{(?<body>[^{}]*)\\}[ \\t\\r\\n]*\\z");
            if (!outer.Success) throw new FormatException("INVALID_CONTEXT_FRAME");
            string[] members = outer.Groups["body"].Value.Split(',');
            if (members.Length != 3) throw new FormatException("CONTEXT_REQUIRES_EXACTLY_THREE_FIELDS");
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            string pattern = "\\A[ \\t\\r\\n]*\"(?<key>wire_version|command|request_uuid)\"[ \\t\\r\\n]*:[ \\t\\r\\n]*(?<value>2|\"nx_readonly_context\"|\"" + UuidPattern + "\")[ \\t\\r\\n]*\\z";
            foreach (string member in members)
            {
                Match m = Regex.Match(member, pattern);
                if (!m.Success) throw new FormatException("INVALID_CONTEXT_FIELD");
                string key = m.Groups["key"].Value;
                if (values.ContainsKey(key)) throw new FormatException("DUPLICATE_CONTEXT_FIELD");
                values.Add(key, m.Groups["value"].Value);
            }
            if (!values.ContainsKey("wire_version") || values["wire_version"] != "2" ||
                !values.ContainsKey("command") || values["command"] != "\"" + Command + "\"" ||
                !values.ContainsKey("request_uuid")) throw new FormatException("INVALID_CONTEXT_CONTRACT");
            string raw = values["request_uuid"];
            if (raw.Length != 38 || raw[0] != '"' || raw[37] != '"') throw new FormatException("INVALID_CONTEXT_UUID");
            string uuid = raw.Substring(1, 36);
            if (!IsCanonicalUuid(uuid)) throw new FormatException("INVALID_CONTEXT_UUID");
            return uuid;
        }

        private static Dictionary<string, object> Envelope(string uuid, object context, string outcome, bool complete, object error)
        {
            if (!IsCanonicalUuid(uuid)) throw new ArgumentException("Response requires its own canonical UUID", "uuid");
            return new Dictionary<string, object> {
                {"wire_version", 2}, {"schema_version", SchemaVersion}, {"command", Command},
                {"request_uuid", uuid}, {"outcome", outcome}, {"capture_complete", complete},
                {"geometry_acceptance", false}, {"snapshot_atomic", false}, {"context", context},
                {"error", error}, {"response_created_at", DateTimeOffset.UtcNow.ToString("o")},
                {"request_uuid_bound_to_completion_slot", true},
                {"binding_semantics", "SERVER_ASSERTION_CLIENT_MUST_INDEPENDENTLY_VERIFY_UUID_VERSION_COMMAND"}
            };
        }

        private static string SerializeRaw(object value)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxResponseBytes;
            serializer.RecursionLimit = 64;
            return serializer.Serialize(value);
        }

        public static string ErrorResponse(string uuid, string outcome, string message)
        {
            return TypedErrorResponse(uuid, outcome, "ContextProtocolError", message);
        }

        internal static string TypedErrorResponse(string uuid, string outcome, string type, string message)
        {
            if (message == null) message = "No additional detail";
            // Bound exception detail without silently truncating a successful context.
            bool truncated = message.Length > 2048;
            if (truncated) message = message.Substring(0, 2048);
            return SerializeRaw(Envelope(uuid, null, outcome, false,
                new Dictionary<string, object> { {"type", type}, {"message", message}, {"message_truncated", truncated} }));
        }

        internal static string SerializeResponse(string uuid, IDictionary<string, object> context, string outcome, bool complete)
        {
            if (outcome != "CAPTURE_COMPLETED" && outcome != "CAPTURE_PARTIAL" && outcome != "CAPTURE_FAILED" &&
                outcome != "THREAD_CONTRACT_UNKNOWN") throw new ArgumentException("Invalid capture outcome", "outcome");
            if (context == null) return ErrorResponse(uuid, "CAPTURE_FAILED", "NULL_CONTEXT_PAYLOAD");
            object contextComplete;
            complete = complete && outcome == "CAPTURE_COMPLETED" && context.Count > 0 &&
                context.TryGetValue("capture_complete", out contextComplete) && contextComplete is bool && (bool)contextComplete;
            try
            {
                string result = SerializeRaw(Envelope(uuid, context, outcome, complete, null));
                if (StrictUtf8.GetByteCount(result) + 1 > MaxResponseBytes)
                    return ErrorResponse(uuid, "LIMIT_EXCEEDED", "CONTEXT_RESPONSE_BYTE_LIMIT_EXCEEDED");
                return result;
            }
            catch (InvalidOperationException)
            {
                return ErrorResponse(uuid, "LIMIT_EXCEEDED", "CONTEXT_SERIALIZATION_LIMIT_EXCEEDED");
            }
            catch (ArgumentException e)
            {
                return ErrorResponse(uuid, "CAPTURE_FAILED", "CONTEXT_SERIALIZATION_FAILED: " + e.GetType().FullName);
            }
        }

        // Client-side/offline validation only. A server assertion is not client proof.
        private static bool HasDuplicateRootKeys(string json, JavaScriptSerializer serializer)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            int depth = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    int start = i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i += 2; continue; }
                        if (json[i] == '"') break;
                        i++;
                    }
                    if (i >= json.Length) return true; // Invalid string: fail closed.
                    int after = i + 1;
                    while (after < json.Length && Char.IsWhiteSpace(json[after])) after++;
                    if (depth == 1 && after < json.Length && json[after] == ':')
                    {
                        string key = serializer.Deserialize<string>(json.Substring(start, i - start + 1));
                        if (!keys.Add(key)) return true;
                    }
                }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return false;
        }

        public static bool ResponseMatchesRequest(string response, string expectedUuid)
        {
            if (!IsCanonicalUuid(expectedUuid) || response == null) return false;
            try
            {
                if (StrictUtf8.GetByteCount(response) + 1 > MaxResponseBytes) return false;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaxResponseBytes;
                serializer.RecursionLimit = 64;
                if (HasDuplicateRootKeys(response, serializer)) return false;
                Dictionary<string, object> value = serializer.DeserializeObject(response) as Dictionary<string, object>;
                if (value == null) return false;
                object version, command, uuid;
                return value.TryGetValue("wire_version", out version) && version is int && (int)version == 2 &&
                    value.TryGetValue("command", out command) && command is string && (string)command == Command &&
                    value.TryGetValue("request_uuid", out uuid) && uuid is string && (string)uuid == expectedUuid;
            }
            catch (Exception) { return false; }
        }
    }

    public sealed class ContextRegistry
    {
        public const int MaxLifetimeRequests = 10000;
        private readonly object sync = new object();
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Func<long> clock;
        private readonly int capacity;
        private static readonly Stopwatch LifetimeClock = Stopwatch.StartNew();
        public ContextRegistry() : this(delegate { return LifetimeClock.ElapsedMilliseconds; }, MaxLifetimeRequests) { }
        public ContextRegistry(Func<long> monotonicMilliseconds) : this(monotonicMilliseconds, MaxLifetimeRequests) { }
        public ContextRegistry(Func<long> monotonicMilliseconds, int maxLifetimeRequests)
        {
            if (monotonicMilliseconds == null) throw new ArgumentNullException("monotonicMilliseconds");
            if (maxLifetimeRequests <= 0 || maxLifetimeRequests > MaxLifetimeRequests) throw new ArgumentOutOfRangeException("maxLifetimeRequests");
            clock = monotonicMilliseconds; capacity = maxLifetimeRequests;
        }
        public ContextRequest Accept(string frame, int timeoutMilliseconds)
        {
            string uuid = ContextProtocol.ParseRequestUuid(frame);
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            lock (sync)
            {
                if (consumed.Contains(uuid)) throw new InvalidOperationException("DUPLICATE_CONTEXT_UUID_NO_REPLAY");
                if (consumed.Count >= capacity) throw new InvalidOperationException("CONTEXT_REGISTRY_FULL_FAIL_CLOSED");
                long now = clock();
                if (now < 0 || now > long.MaxValue - timeoutMilliseconds) throw new InvalidOperationException("INVALID_MONOTONIC_CLOCK");
                ContextRequest request = new ContextRequest(uuid, now, now + timeoutMilliseconds, clock);
                consumed.Add(uuid); // Lifecycle tombstone; never removed, including on timeout.
                return request;
            }
        }
    }

    public sealed class ContextRequest
    {
        private readonly object sync = new object();
        private readonly string uuid;
        private readonly Func<long> clock;
        private readonly long deadline;
        private long lastNow;
        private string execution = "QUEUED";
        private string delivery = "OPEN";
        private string response;
        private string rejected;
        private int rejectedCount;
        internal ContextRequest(string requestUuid, long acceptedAt, long deadlineMilliseconds, Func<long> monotonicMilliseconds)
        { uuid = requestUuid; lastNow = acceptedAt; deadline = deadlineMilliseconds; clock = monotonicMilliseconds; }
        public string RequestUuid { get { return uuid; } }
        public string ExecutionState { get { lock (sync) { return execution; } } }
        public string DeliveryState { get { lock (sync) { return delivery; } } }
        public string LastRejectedCompletion { get { lock (sync) { return rejected; } } }
        public int RejectedCompletionCount { get { lock (sync) { return rejectedCount; } } }
        private bool TimedOut()
        {
            long now = clock();
            if (now < lastNow) { delivery = "UNKNOWN_CLOCK"; throw new InvalidOperationException("MONOTONIC_CLOCK_MOVED_BACKWARDS"); }
            lastNow = now;
            return now >= deadline;
        }
        private void Reject(string reason) { rejected = reason; rejectedCount++; }
        public bool TryBegin()
        {
            lock (sync)
            {
                if (execution != "QUEUED" || delivery != "OPEN") return false;
                if (TimedOut()) { execution = "SKIPPED_EXPIRED"; delivery = "EXPIRED"; return false; }
                execution = "EXECUTING"; return true;
            }
        }
        public bool Complete(string responseUuid, IDictionary<string, object> context, string outcome, bool captureComplete)
        {
            return CompleteRendered(responseUuid, delegate { return ContextProtocol.SerializeResponse(uuid, context, outcome, captureComplete); });
        }
        public bool CompleteFailure(string responseUuid, Exception error)
        {
            if (error == null) throw new ArgumentNullException("error");
            return CompleteRendered(responseUuid, delegate { return ContextProtocol.TypedErrorResponse(uuid, "CAPTURE_FAILED", error.GetType().FullName, error.Message); });
        }
        private bool CompleteRendered(string responseUuid, Func<string> render)
        {
            lock (sync)
            {
                if (!String.Equals(responseUuid, uuid, StringComparison.Ordinal)) { Reject("MISMATCHED_OR_MISSING_UUID_ISOLATED"); return false; }
                if (execution != "EXECUTING") { Reject("DUPLICATE_OR_UNSTARTED_COMPLETION_ISOLATED"); return false; }
                execution = "COMPLETED";
                if (delivery != "OPEN" || TimedOut()) { delivery = "EXPIRED"; Reject("STALE_COMPLETION_ISOLATED"); return false; }
                response = render();
                return true;
            }
        }
        public string TakeResponse()
        {
            lock (sync)
            {
                if (delivery != "OPEN" || response == null) return null;
                if (TimedOut()) { delivery = "EXPIRED"; response = null; Reject("STALE_RESPONSE_ISOLATED_BEFORE_DELIVERY"); return null; }
                string result = response; response = null; delivery = "RESPONSE_CLAIMED"; return result;
            }
        }
        public void Expire()
        {
            lock (sync)
            {
                if (delivery != "OPEN") return;
                delivery = "EXPIRED"; response = null;
                if (execution == "QUEUED") execution = "SKIPPED_EXPIRED";
                // EXECUTING is intentionally not cancelled or aborted.
            }
        }
    }

    public sealed class ContextStartupGate
    {
        private int state; // 0 never, 1 initializing, 2 published, 3 failed/unknown.
        public bool TryEnter() { return Interlocked.CompareExchange(ref state, 1, 0) == 0; }
        public void Publish()
        {
            if (Interlocked.CompareExchange(ref state, 2, 1) != 1) throw new InvalidOperationException("STARTUP_NOT_OWNED_INITIALIZING");
        }
        public bool Fail() { return Interlocked.CompareExchange(ref state, 3, 1) == 1; }
        public string State
        {
            get
            {
                int value = Thread.VolatileRead(ref state);
                return value == 0 ? "NEVER_STARTED" : value == 1 ? "INITIALIZING" : value == 2 ? "DISPATCH_CONTEXT_PUBLISHED" : "FAILED_OR_UNKNOWN";
            }
        }
    }
}
