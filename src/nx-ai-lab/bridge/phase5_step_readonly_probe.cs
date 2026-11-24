using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using NXOpen;
using NXOpen.UF;

public class Phase5StepReadonlyProbe
{
    const string Root = @"@NX_LAB_ROOT@\logs\phase5\staged_20261108_212227";
    static string Q(string s)
    {
        if (s == null) return "null";
        var b = new StringBuilder("\"");
        foreach (char c in s) {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < 32 || c > 126) b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }
    static string N(double x) { return double.IsNaN(x) || double.IsInfinity(x) ? Q(x.ToString(CultureInfo.InvariantCulture)) : x.ToString("R", CultureInfo.InvariantCulture); }
    static string A(double[] a) { return "[" + string.Join(",", Array.ConvertAll(a, N)) + "]"; }
    static bool Modified(UFSession uf, Tag tag)
    {
        // Resolve only the installed UF.Part.IsModified signature; unknown forms fail closed.
        foreach (MethodInfo m in uf.Part.GetType().GetMethods()) {
            if (m.Name != "IsModified") continue;
            ParameterInfo[] p = m.GetParameters();
            if (p.Length == 1 && p[0].ParameterType == typeof(Tag) && m.ReturnType == typeof(bool))
                return (bool)m.Invoke(uf.Part, new object[] { tag });
            if (p.Length != 2 || p[0].ParameterType != typeof(Tag) || !p[1].IsOut) continue;
            if (p[1].ParameterType != typeof(bool).MakeByRefType() && p[1].ParameterType != typeof(int).MakeByRefType()) continue;
            object[] a = { tag, null }; object rc = m.Invoke(uf.Part, a);
            if (rc != null && (!(rc is int) || (int)rc != 0)) throw new Exception("IsModified query failed");
            return Convert.ToBoolean(a[1], CultureInfo.InvariantCulture);
        }
        throw new Exception("Unsupported IsModified signature; refusing probe");
    }
    static void RequireSaved(Session s, UFSession uf)
    {
        bool dirty = false;
        foreach (BasePart p in s.Parts) dirty |= Modified(uf, p.Tag);
        if (dirty) throw new Exception("Unsaved part: refused; nothing saved or closed");
    }
    public static void Main(string[] args)
    {
        var s = Session.GetSession(); var uf = UFSession.GetUFSession(); RequireSaved(s, uf);
        if (args == null || args.Length != 1 || !Path.IsPathRooted(args[0])) throw new ArgumentException("One absolute new JSON path required");
        string output = Path.GetFullPath(args[0]);
        if (!string.Equals(Path.GetDirectoryName(output), Root, StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(output), @"\A[A-Za-z0-9][A-Za-z0-9_-]*\.json\z")) throw new IOException("Output outside allowed directory or invalid filename");
        for (DirectoryInfo d = new DirectoryInfo(Root); d != null; d = d.Parent)
            if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Missing directory or reparse point refused");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Overwrite refused");
        var rows = new List<string>();
        foreach (BasePart bp in s.Parts) {
            Part p = bp as Part; var bodies = new List<string>();
            if (p != null) foreach (Body b in p.Bodies) {
                bool solid = b.IsSolidBody; string result = "\"mass\":null,\"statistics\":null,\"note\":\"non-solid skipped\"";
                if (solid) {
                    double[] acc = new double[11], mass = new double[47], stats = new double[13]; acc[0] = 0.9999999999;
                    uf.Modl.AskMassProps3d(new Tag[] { b.Tag }, 1, 1, 4, 1.0, 1, acc, mass, stats);
                    result = "\"type\":1,\"units\":4,\"density\":1.0,\"accuracy\":1,\"acc_value_input\":[0.9999999999,0,0,0,0,0,0,0,0,0,0],\"mass\":" + A(mass) + ",\"statistics\":" + A(stats);
                }
                bodies.Add("{\"tag\":" + Q(b.Tag.ToString()) + ",\"solid\":" + (solid ? "true" : "false") + "," + result + "}");
            }
            rows.Add("{\"path\":" + Q(bp.FullPath) + ",\"modified\":false,\"bodies\":[" + string.Join(",", bodies.ToArray()) + "]}");
        }
        string prefix = Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output));
        string genericDef = prefix + ".generic.def", specificDef = prefix + ".specific.def";
        foreach (string path in new string[] { genericDef, specificDef })
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Settings overwrite refused: " + path);
        Func<string, Action<string>, string> serialize = (path, save) => {
            RequireSaved(s, uf);
            for (DirectoryInfo d = new DirectoryInfo(Root); d != null; d = d.Parent)
                if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Missing directory or reparse point refused");
            if (!string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Settings outside log directory");
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Settings overwrite refused: " + path);
            save(path); // SaveSettings only; never Commit.
            RequireSaved(s, uf);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Missing or reparse settings file");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) throw new IOException("Empty serialized settings: " + path);
            string sha;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            return "{\"path\":" + Q(path) + ",\"bytes\":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":" + Q(sha) + "}";
        };
        string genericSerialized, specificSerialized;
        string defaults, genericDefaults, genericRoundtrip, specificRoundtrip;
        var generic = s.DexManager.CreateStepCreator();
        try {
            Func<string> snapshot = () => "{\"ExportFrom\":" + Q(generic.ExportFrom.ToString()) + ",\"SettingsFile\":" + Q(generic.SettingsFile) + ",\"BsplineTol\":" + N(generic.BsplineTol) + ",\"ObjectTypes\":{\"Solids\":" + (generic.ObjectTypes.Solids ? "true" : "false") + "}}";
            genericDefaults = snapshot();
            generic.BsplineTol = 0.00001;
            double actualTol = generic.BsplineTol;
            generic.ObjectTypes.Solids = true;
            bool actualSolids = generic.ObjectTypes.Solids;
            genericSerialized = serialize(genericDef, path => { generic.SaveSettings(path); });
            genericRoundtrip = "{\"requested\":{\"BsplineTol\":0.00001,\"ObjectTypes\":{\"Solids\":true}},\"actual_immediate\":{\"BsplineTol\":" + N(actualTol) + ",\"ObjectTypes\":{\"Solids\":" + (actualSolids ? "true" : "false") + "}},\"actual_final\":" + snapshot() + "}";
        }
        finally { generic.Destroy(); }
        var creator = s.DexManager.CreateStep214Creator();
        try {
            Func<string> snapshot = () => "{\"ExportFrom\":" + Q(creator.ExportFrom.ToString()) + ",\"SettingsFile\":" + Q(creator.SettingsFile) + ",\"BsplineTol\":" + N(creator.BsplineTol) + ",\"ObjectTypes\":{\"Solids\":" + (creator.ObjectTypes.Solids ? "true" : "false") + "}}";
            defaults = snapshot();
            creator.BsplineTol = 0.00001;
            double actualTol = creator.BsplineTol;
            creator.ObjectTypes.Solids = true;
            bool actualSolids = creator.ObjectTypes.Solids;
            specificSerialized = serialize(specificDef, path => { creator.SaveSettings(path); });
            specificRoundtrip = "{\"requested\":{\"BsplineTol\":0.00001,\"ObjectTypes\":{\"Solids\":true}},\"actual_immediate\":{\"BsplineTol\":" + N(actualTol) + ",\"ObjectTypes\":{\"Solids\":" + (actualSolids ? "true" : "false") + "}},\"actual_final\":" + snapshot() + "}";
        }
        finally { creator.Destroy(); }
        string json = "{\"scope\":\"uncommitted builder SaveSettings probe; serialized settings only, not actual STEP precision; no export or acceptance claim\",\"work\":" + Q(s.Parts.Work == null ? null : s.Parts.Work.FullPath) + ",\"display\":" + Q(s.Parts.Display == null ? null : s.Parts.Display.FullPath) + ",\"parts\":[" + string.Join(",", rows.ToArray()) + "],\"Step214Creator_defaults\":" + defaults + ",\"StepCreator_defaults\":" + genericDefaults + ",\"StepCreator_roundtrip\":" + genericRoundtrip + ",\"Step214Creator_roundtrip\":" + specificRoundtrip + "}";
        json = json.Substring(0, json.Length - 1) + ",\"serialized_settings\":{\"scope\":\"Settings serialization only; not actual STEP precision\",\"StepCreator\":" + genericSerialized + ",\"Step214Creator\":" + specificSerialized + "}}";
        RequireSaved(s, uf);
        using (var f = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(f, new UTF8Encoding(false))) w.Write(json);
    }
public static string ExactBounds()
{
    Session s = Session.GetSession();
    UFSession uf = UFSession.GetUFSession();
    RequireSaved(s, uf);
    Part p = s.Parts.Work;
    if (p == null) throw new Exception("No work part; nothing changed");
    if (s.Parts.Display != p)
        throw new Exception("Require work = display part for this standalone probe");
    var rows = new List<string>();
    foreach (Body body in p.Bodies)
    {
        double[] fast = new double[6], corner = new double[3];
        double[] lengths = new double[3];
        double[,] dirs = new double[3, 3];
        uf.Modl.AskBoundingBox(body.Tag, fast);
        // NX12 NULL_TAG means Work CSYS, NOT absolute CSYS.
        uf.Modl.AskBoundingBoxExact(body.Tag, Tag.Null, corner, dirs, lengths);
        double[] box = { double.PositiveInfinity, double.PositiveInfinity,
            double.PositiveInfinity, double.NegativeInfinity,
            double.NegativeInfinity, double.NegativeInfinity };
        for (int mask = 0; mask < 8; ++mask)
            for (int k = 0; k < 3; ++k)
            {
                double v = corner[k];
                for (int j = 0; j < 3; ++j)
                    if ((mask & (1 << j)) != 0) v += dirs[j, k] * lengths[j];
                box[k] = Math.Min(box[k], v);
                box[k + 3] = Math.Max(box[k + 3], v);
            }
        var axes = new List<string>();
        for (int j = 0; j < 3; ++j)
            axes.Add(A(new double[] { dirs[j, 0], dirs[j, 1], dirs[j, 2] }));
        rows.Add("{\"tag\":" + Q(body.Tag.ToString())
            + ",\"solid\":" + (body.IsSolidBody ? "true" : "false")
            + ",\"fast_absolute_bounds\":" + A(fast)
            + ",\"exact_wcs_box\":{\"min_corner_absolute\":" + A(corner)
            + ",\"directions_absolute_rows\":[" + string.Join(",", axes.ToArray())
            + "],\"distances\":" + A(lengths) + "}"
            + ",\"absolute_envelope_of_exact_wcs_box\":" + A(box) + "}");
    }
    RequireSaved(s, uf);
    return "{\"ok\":true,\"read_only_query\":true,\"part\":" + Q(p.FullPath)
        + ",\"partunits\":" + Q(p.PartUnits.ToString())
        + ",\"alignment\":\"current WCS; never changed\",\"bodies\":["
        + string.Join(",", rows.ToArray()) + "]}";
}
static void DevFinite(double[] values)
{
    foreach (double x in values)
        if (double.IsNaN(x) || double.IsInfinity(x))
            throw new InvalidOperationException("Nonfinite deviation value");
}
static bool DevTrim(UFSession uf, Tag face, double[] point,
    string sample, TextWriter log)
{
    DevFinite(point);
    int status;
    uf.Modl.AskPointContainment(point, face, out status);
    log.WriteLine("{\"kind\":\"source_trim\",\"sample\":" + Q(sample)
        + ",\"face\":" + Q(face.ToString()) + ",\"point\":" + A(point)
        + ",\"status\":" + status.ToString(CultureInfo.InvariantCulture) + "}");
    log.Flush();
    if (status == 2) return false;
    if (status != 1 && status != 3)
        throw new InvalidOperationException("Unknown source trim status");
    return true;
}
static double DevDistance(UFSession uf, Tag[] targetFaces,
    double[] point, string sample, TextWriter log, bool boundedSheetWitnesses = false)
{
    if (point == null || point.Length != 3 || log == null || string.IsNullOrEmpty(sample))
        throw new ArgumentException("Missing distance input/log");
    DevFinite(point);
    if (targetFaces == null || targetFaces.Length == 0)
        throw new InvalidOperationException("No target faces");
    var json = new System.Web.Script.Serialization.JavaScriptSerializer();
    Action<object> emit = row => { log.WriteLine(json.Serialize(row)); log.Flush(); };
    Func<double[], double[], double> norm = (a, b) => {
        DevFinite(a); DevFinite(b);
        double x = a[0]-b[0], y = a[1]-b[1], z = a[2]-b[2];
        double d = Math.Sqrt(x*x+y*y+z*z); DevFinite(new double[] { d }); return d;
    };
    double best = double.PositiveInfinity;
    double[] bestPoint = null;
    string bestFace = null, bestEdge = null;
    foreach (Tag face in targetFaces)
    {
        if (face == Tag.Null) throw new InvalidOperationException("Null face");
        double distance, accuracy;
        double[] p1 = new double[3], p2 = new double[3];
        uf.Modl.AskMinimumDist3(2, Tag.Null, face, 1, point, 0,
            new double[3], out distance, p1, p2, out accuracy);
        emit(new { kind = "native_distance", sample = sample, target_face = face.ToString(),
            input = point, point1 = p1, point2 = p2, distance_mm = distance, accuracy = accuracy,
            role = "diagnostic_only_NOT_acceptance_or_error_bound" });
        DevFinite(p1); DevFinite(p2); DevFinite(new double[] { distance, accuracy });
        if (distance < 0 || accuracy < 0) throw new InvalidOperationException("Negative native result");
        double[] uv = new double[2], projected = new double[3], evaluated = new double[3];
        uf.Modl.AskFaceParm2(face, point, uv, projected);
        DevFinite(uv); DevFinite(projected);
        uf.Modl.AskFaceProps(face, uv, evaluated, new double[3], new double[3],
            new double[3], new double[3], new double[3], new double[2]);
        DevFinite(evaluated);
        double evaluatedDistance = norm(point, evaluated), residual = norm(projected, evaluated);
        int status;
        uf.Modl.AskPointContainment(evaluated, face, out status);
        emit(new { kind = "evaluated_face_witness", sample = sample, face = face.ToString(),
            input = point, uv = uv, projected = projected, evaluated = evaluated,
            distance_mm = evaluatedDistance, projection_residual_mm = residual, trim_status = status,
            native_witness_to_evaluated_mm = norm(p2, evaluated),
            native_input_residual_mm = norm(point, p1),
            native_scalar_vs_witness_norm_mm = Math.Abs(distance-norm(p1,p2)) });
        if (status != 1 && status != 2 && status != 3)
            throw new InvalidOperationException("Unknown evaluated face trim status");
        double candidate = double.PositiveInfinity;
        double[] witness = null;
        string witnessEdge = null;
        if (status == 1 || status == 3) { candidate = evaluatedDistance; witness = evaluated; }
        else
        {
            Face targetFace = NXOpen.Utilities.NXObjectManager.Get(face) as Face;
            if (targetFace == null || targetFace.Tag != face)
                throw new InvalidOperationException("Cannot resolve target face");
            Edge[] edges = targetFace.GetEdges();
            if (edges == null || edges.Length == 0)
                throw new InvalidOperationException("Outside projection without boundary");
            foreach (Edge edge in edges)
            {
                if (edge == null || edge.Tag == Tag.Null)
                    throw new InvalidOperationException("Invalid boundary edge");
                IntPtr ev = IntPtr.Zero; Exception primary = null;
                try
                {
                    uf.Eval.Initialize2(edge.Tag, out ev);
                    if (ev == IntPtr.Zero) throw new InvalidOperationException("Null edge evaluator");
                    double[] limits = new double[2], closest = new double[3], ep = new double[3];
                    uf.Eval.AskLimits(ev, limits); DevFinite(limits);
                    if (limits[1] < limits[0]) throw new InvalidOperationException("Reversed edge limits");
                    double t;
                    uf.Eval.EvaluateClosestPoint(ev, point, out t, closest);
                    DevFinite(new double[] { t }); DevFinite(closest);
                    bool inRange = t >= limits[0] && t <= limits[1];
                    emit(new { kind = "boundary_parameter", sample = sample, face = face.ToString(),
                        edge = edge.Tag.ToString(), input = point, closest = closest,
                        parameter = t, limits = limits, in_range = inRange, membership = "Face.GetEdges" });
                    if (boundedSheetWitnesses)
                    {
                        // The unconstrained closest result is diagnostic only when outside
                        // the actual edge domain. Never clamp t or reuse closest as an endpoint.
                        if (!inRange)
                            emit(new { kind = "boundary_candidate_rejected", sample = sample,
                                face = face.ToString(), edge = edge.Tag.ToString(),
                                parameter = t, limits = limits, projected = closest,
                                reason = "closest_parameter_outside_actual_edge_limits",
                                interpretation = "notcertifiednearest" });
                        var parameters = new List<double>();
                        var roles = new List<string>();
                        if (inRange) { parameters.Add(t); roles.Add("in_domain_closest_parameter"); }
                        // Exact native limits, independently evaluated, including degenerate edges.
                        if (!parameters.Contains(limits[0])) {
                            parameters.Add(limits[0]); roles.Add("native_lower_endpoint");
                        }
                        if (!parameters.Contains(limits[1])) {
                            parameters.Add(limits[1]); roles.Add("native_upper_endpoint");
                        }
                        for (int ci = 0; ci < parameters.Count; ++ci)
                        {
                            double ct = parameters[ci];
                            DevFinite(new double[] { ct });
                            if (ct < limits[0] || ct > limits[1])
                                throw new InvalidOperationException("Candidate outside actual edge limits");
                            double[] cp = new double[3];
                            uf.Eval.Evaluate(ev, 0, ct, cp, new double[0]);
                            DevFinite(cp);
                            double cd = norm(point, cp);
                            int edgeStatus, faceStatus;
                            uf.Modl.AskPointContainment(cp, edge.Tag, out edgeStatus);
                            uf.Modl.AskPointContainment(cp, face, out faceStatus);
                            bool knownEdge = edgeStatus == 1 || edgeStatus == 2 || edgeStatus == 3;
                            bool knownFace = faceStatus == 1 || faceStatus == 2 || faceStatus == 3;
                            bool accepted = knownEdge && knownFace &&
                                edgeStatus != 2 && faceStatus != 2;
                            // Compare with closest only for the original in-domain candidate.
                            // Endpoint coordinates always come from their own UF Eval call.
                            double? closestResidual = inRange && ct == t ?
                                (double?)norm(closest, cp) : null;
                            emit(new { kind = "bounded_boundary_candidate", sample = sample,
                                face = face.ToString(), edge = edge.Tag.ToString(),
                                membership = "Face.GetEdges", input = point,
                                candidate_role = roles[ci], parameter = ct, limits = limits,
                                in_range = true, evaluated = cp, distance_mm = cd,
                                closest_projection_residual_mm = closestResidual,
                                edge_trim_status = edgeStatus, face_trim_status = faceStatus,
                                accepted = accepted,
                                rejection_reason = accepted ? null :
                                    (!knownEdge || !knownFace ? "unknown_trim_status" : "outside_trim"),
                                interpretation = "notcertifiednearest",
                                distance_role = "valid_witness_upper_bound_at_this_sample_only_when_accepted" });
                            if (!knownEdge || !knownFace)
                                throw new InvalidOperationException("Unknown bounded witness trim status");
                            if (!accepted) continue;
                            if (cd < candidate) {
                                candidate = cd; witness = cp; witnessEdge = edge.Tag.ToString();
                            }
                        }
                        // Executes the enclosing finally/Free, then considers the next edge.
                        // No accepted candidates across the face leaves infinity/null:
                        // existing finite/witness guards below stop the query.
                        continue;
                    }
                    if (!inRange) throw new InvalidOperationException("Closest edge parameter outside limits");
                    uf.Eval.Evaluate(ev, 0, t, ep, new double[0]); DevFinite(ep);
                    double ed = norm(point, ep), er = norm(closest, ep);
                    int es; uf.Modl.AskPointContainment(ep, edge.Tag, out es);
                    emit(new { kind = "evaluated_boundary_witness", sample = sample, face = face.ToString(),
                        edge = edge.Tag.ToString(), membership = "Face.GetEdges", input = point,
                        parameter = t, limits = limits, projected = closest, evaluated = ep,
                        distance_mm = ed, projection_residual_mm = er, trim_status = es });
                    if (es != 1 && es != 3)
                        throw new InvalidOperationException("Outside or unknown evaluated edge trim status");
                    if (ed < candidate) { candidate = ed; witness = ep; witnessEdge = edge.Tag.ToString(); }
                }
                catch (Exception ex) { primary = ex; throw; }
                finally
                {
                    if (ev != IntPtr.Zero) try { uf.Eval.Free(ev); }
                    catch (Exception cleanup) {
                        if (primary != null) throw new AggregateException("Edge evaluation and Free failed", primary, cleanup);
                        throw;
                    }
                }
            }
        }
        DevFinite(new double[] { candidate });
        if (witness == null) throw new InvalidOperationException("No evaluated target witness");
        emit(new { kind = "face_constrained_distance", sample = sample, face = face.ToString(),
            edge = witnessEdge, input = point, evaluated = witness, distance_mm = candidate,
            method = boundedSheetWitnesses ? "finite_valid_witness_notcertifiednearest" : "evaluated_point_witness_NOT_exact_constrained_minimum",
            projection_residual_mm = residual });
        if (candidate < best) {
            best = candidate; bestPoint = witness; bestFace = face.ToString(); bestEdge = witnessEdge;
        }
    }
    DevFinite(new double[] { best });
    if (bestPoint == null) throw new InvalidOperationException("No final evaluated witness");
    emit(new { kind = "verified_sample_witness", sample = sample, input = point,
        target_face = bestFace, target_edge = bestEdge, evaluated = bestPoint, distance_mm = best,
        interpretation = boundedSheetWitnesses ? "finite_valid_witness_upper_bound_at_this_sample_notcertifiednearest_NOT_global_Hausdorff_bound" : "finite_evaluated_witness_NOT_global_Hausdorff_bound" });
    return best;
}
sealed class DevSampleStats
{
    public const double ThresholdMm = 0.0001;
    public long Faces, UniqueEdges, FaceCandidates, FaceAccepted, FaceRejected, EdgeAccepted, DistanceQueries;
    public double SampledMaxMm = 0.0;
    public string MaxSample = null;
}
static DevSampleStats DevSampleDirection(UFSession uf, Body source, Tag[] targetFaces, string direction, TextWriter log, bool boundedSheetWitnesses = false)
{
    if (source == null || targetFaces == null || targetFaces.Length == 0 || string.IsNullOrEmpty(direction) || log == null)
        throw new ArgumentException("Missing sampler input");
    var r = new DevSampleStats(); string context = direction;
    Action<string> emit = row => { log.WriteLine(row); log.Flush(); };
    Action<double[], string> measure = (p, id) => {
        double d = DevDistance(uf, targetFaces, p, id, log, boundedSheetWitnesses); DevFinite(new double[] { d });
        if (d < 0) throw new InvalidOperationException("Negative sampled distance");
        r.DistanceQueries += targetFaces.Length;
        if (r.MaxSample == null || d > r.SampledMaxMm) { r.SampledMaxMm = d; r.MaxSample = id; }
        emit("{\"kind\":\"sample_minimum\",\"sample\":" + Q(id) + ",\"distance_mm\":" + N(d) + "}");
    };
    Face[] faces = source.GetFaces();
    Array.Sort(faces, (a, b) => StringComparer.Ordinal.Compare(a.JournalIdentifier, b.JournalIdentifier));
    if (faces.Length == 0) throw new InvalidOperationException("No source faces");
    var adjacency = new Dictionary<Tag, List<string>>();
    foreach (Face f in faces) foreach (Edge e in f.GetEdges()) {
        if (!adjacency.ContainsKey(e.Tag)) adjacency.Add(e.Tag, new List<string>());
        if (!adjacency[e.Tag].Contains(f.JournalIdentifier)) adjacency[e.Tag].Add(f.JournalIdentifier);
    }
    foreach (Face f in faces) {
        context = direction + "/face/" + f.JournalIdentifier; long accepted = 0;
        emit("{\"kind\":\"face_begin\",\"id\":" + Q(context) + ",\"tag\":" + Q(f.Tag.ToString()) + "}");
        double[] lim = new double[4]; uf.Modl.AskFaceUvMinmax(f.Tag, lim); DevFinite(lim);
        emit("{\"kind\":\"face_limits\",\"id\":" + Q(context) + ",\"uv_limits\":" + A(lim) + ",\"degenerate\":" + ((lim[0] == lim[1] || lim[2] == lim[3]) ? "true" : "false") + "}");
        if (lim[1] < lim[0] || lim[3] < lim[2]) throw new InvalidOperationException("Reversed UV limits: " + context);
        for (int i = 0; i <= 16; ++i) for (int j = 0; j <= 16; ++j) {
            string id = context + "/" + i.ToString("D2", CultureInfo.InvariantCulture) + "/" + j.ToString("D2", CultureInfo.InvariantCulture);
            double[] uv = { (1-i/16.0)*lim[0]+(i/16.0)*lim[1], (1-j/16.0)*lim[2]+(j/16.0)*lim[3] }, p = new double[3]; DevFinite(uv);
            emit("{\"kind\":\"face_sample_begin\",\"sample\":" + Q(id) + ",\"uv\":" + A(uv) + "}");
            uf.Modl.AskFaceProps(f.Tag, uv, p, new double[3], new double[3], new double[3], new double[3], new double[3], new double[2]);
            emit("{\"kind\":\"face_sample\",\"sample\":" + Q(id) + ",\"uv\":" + A(uv) + ",\"point\":" + A(p) + "}"); DevFinite(p); ++r.FaceCandidates;
            if (!DevTrim(uf, f.Tag, p, id, log)) { ++r.FaceRejected; continue; }
            measure(p, id); ++accepted; ++r.FaceAccepted;
        }
        emit("{\"kind\":\"face_coverage\",\"id\":" + Q(context) + ",\"accepted\":" + accepted.ToString(CultureInfo.InvariantCulture) + ",\"rejected\":" + (289-accepted).ToString(CultureInfo.InvariantCulture) + "}");
        if (accepted == 0) throw new InvalidOperationException("Zero accepted UV coverage: " + context);
        ++r.Faces;
    }
    Edge[] edges = source.GetEdges(); var seen = new HashSet<Tag>();
    Array.Sort(edges, (a, b) => StringComparer.Ordinal.Compare(a.JournalIdentifier, b.JournalIdentifier));
    foreach (Edge e in edges) {
        if (!seen.Add(e.Tag)) continue;
        context = direction + "/edge/" + e.JournalIdentifier;
        if (!adjacency.ContainsKey(e.Tag)) throw new InvalidOperationException("Edge lacks face adjacency: " + context);
        emit("{\"kind\":\"edge_begin\",\"id\":" + Q(context) + ",\"tag\":" + Q(e.Tag.ToString()) + ",\"adjacent_faces\":[" + string.Join(",", adjacency[e.Tag].ConvertAll(Q).ToArray()) + "]}");
        IntPtr ev = IntPtr.Zero; Exception primary = null;
        try {
            uf.Eval.Initialize2(e.Tag, out ev);
            if (ev == IntPtr.Zero) throw new InvalidOperationException("Null evaluator: " + context);
            double[] lim = new double[2]; uf.Eval.AskLimits(ev, lim); DevFinite(lim);
            emit("{\"kind\":\"edge_limits\",\"id\":" + Q(context) + ",\"limits\":" + A(lim) + ",\"degenerate\":" + (lim[0] == lim[1] ? "true" : "false") + "}");
            if (lim[1] < lim[0]) throw new InvalidOperationException("Reversed edge limits: " + context);
            for (int k = 0; k <= 32; ++k) {
                string id = context + "/" + k.ToString("D2", CultureInfo.InvariantCulture);
                double t = (1-k/32.0)*lim[0]+(k/32.0)*lim[1]; DevFinite(new double[] { t }); double[] p = new double[3];
                emit("{\"kind\":\"edge_sample_begin\",\"sample\":" + Q(id) + ",\"t\":" + N(t) + "}");
                uf.Eval.Evaluate(ev, 0, t, p, new double[0]);
                emit("{\"kind\":\"edge_sample\",\"sample\":" + Q(id) + ",\"t\":" + N(t) + ",\"point\":" + A(p) + "}"); DevFinite(p);
                if (!DevTrim(uf, e.Tag, p, id, log)) throw new InvalidOperationException("Point outside source edge: " + id);
                measure(p, id); ++r.EdgeAccepted;
            }
        }
        catch (Exception ex) { primary = ex; throw; }
        finally {
            if (ev != IntPtr.Zero) try { uf.Eval.Free(ev); }
                catch (Exception cleanup) { if (primary != null) throw new AggregateException("Edge evaluation and Free failed: " + context, primary, cleanup); throw; }
        }
        ++r.UniqueEdges;
    }
    foreach (Tag edge in adjacency.Keys) if (!seen.Contains(edge)) throw new InvalidOperationException("Face edge absent from body edges");
    if (r.FaceAccepted == 0) throw new InvalidOperationException("No measured samples");
    return r;
}
public static string RelativeMass(string toleranceText)
{
    double tol;
    if (!double.TryParse(toleranceText, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out tol) ||
        double.IsNaN(tol) || double.IsInfinity(tol) || tol < 1e-12 || tol > 1e-4)
        throw new ArgumentException("Tolerance must be finite, invariant, in [1e-12,1e-4].");
    Session s = Session.GetSession();
    UFSession uf = UFSession.GetUFSession();
    RequireSaved(s, uf);
    try
    {
        Part p = s.Parts.Work;
        Part d = s.Parts.Display;
        if (p == null || d == null || p.Tag != d.Tag ||
            string.IsNullOrEmpty(p.FullPath) || p.PartUnits != BasePart.Units.Millimeters)
            throw new InvalidOperationException("Require saved millimeter work part equal to display part.");
        List<Body> bodies = new List<Body>();
        foreach (Body b in p.Bodies)
        {
            if (!b.IsSolidBody) throw new InvalidOperationException("Sheet/non-solid bodies are not accepted.");
            bodies.Add(b);
        }
        if (bodies.Count == 0) throw new InvalidOperationException("At least one solid is required.");
        Func<double, bool> finite = x => !double.IsNaN(x) && !double.IsInfinity(x);
        List<string> rows = new List<string>();
        double total = 0.0, sumEstimates = 0.0;
        bool allAchieved = true;
        foreach (Body b in bodies)
        {
            double[] acc = new double[11], mass = new double[47], stats = new double[13];
            acc[0] = tol; acc[1] = tol; // Area/volume tested; remaining zero tests intentionally disabled.
            uf.Modl.AskMassProps3d(new Tag[] { b.Tag }, 1, 1, 4, 1.0, 2, acc, mass, stats);
            foreach (double x in mass) if (!finite(x)) throw new InvalidOperationException("Nonfinite raw mass property.");
            foreach (double x in stats) if (!finite(x)) throw new InvalidOperationException("Nonfinite raw statistic.");
            double volume = mass[1] * 1e9, relative = stats[1];
            double estimate = Math.Abs(volume) * Math.Abs(relative);
            if (mass[0] < 0 || mass[2] < 0 || mass[46] < 0 || !finite(volume) || volume < 0 ||
                !finite(estimate) || estimate < 0)
                throw new InvalidOperationException("Invalid nonnegative mass/volume/error estimate; raw_mass47=" + A(mass) + "; raw_statistics13=" + A(stats));
            bool achieved = Math.Abs(relative) <= tol;
            bool areaAchieved = Math.Abs(stats[0]) <= tol;
            allAchieved = allAchieved && achieved && areaAchieved;
            total += volume; sumEstimates += estimate;
            if (!finite(total) || total < 0 || !finite(sumEstimates) || sumEstimates < 0)
                throw new InvalidOperationException("Invalid aggregate volume/error estimate.");
            rows.Add("{\"body_tag\":" + Q(b.Tag.ToString()) + ",\"mass47\":" + A(mass) +
                ",\"statistics13\":" + A(stats) + ",\"volume_mm3\":" + N(volume) +
                ",\"statistics1_relative_estimate\":" + N(relative) + ",\"requested_tolerance\":" + N(tol) +
                ",\"statistics1_relative_estimate_magnitude\":" + N(Math.Abs(relative)) +
                ",\"area_achieved_relative_estimate_le_requested_tolerance\":" + (areaAchieved ? "true" : "false") +
                ",\"achieved_relative_estimate_le_requested_tolerance\":" + (achieved ? "true" : "false") +
                ",\"estimated_volume_error_mm3\":" + N(estimate) + ",\"error_interpretation\":" + Q("estimate_NOT_bound") + "}");
        }
        return "{\"ok\":true,\"scope\":" + Q("read-only query success only; NOT STEP acceptance") +
            ",\"part\":" + Q(p.FullPath) + ",\"accuracy\":2,\"units\":4,\"requested_tolerance\":" + N(tol) +
            ",\"bodies\":[" + string.Join(",", rows.ToArray()) + "],\"aggregate_volume_mm3\":" + N(total) +
            ",\"sum_estimated_volume_error_mm3\":" + N(sumEstimates) +
            ",\"error_interpretation\":" + Q("sum of per-body estimates; NOT a bound or global bound") +
            ",\"all_bodies_achieved_relative_estimate_le_requested_tolerance\":" + (allAchieved ? "true" : "false") + "}";
    }
    finally { RequireSaved(s, uf); }
}
public static string RelativeMassTargeted(string mode,string toleranceText)
{
    if(mode!="volume_only" && mode!="area_only")throw new ArgumentException("Only diagnostic volume_only or area_only accepted");
    double tol;
    if (!double.TryParse(toleranceText, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out tol) ||
        double.IsNaN(tol) || double.IsInfinity(tol) || tol < 1e-12 || tol > 1e-4)
        throw new ArgumentException("Tolerance must be finite, invariant, in [1e-12,1e-4].");
    Session s = Session.GetSession();
    UFSession uf = UFSession.GetUFSession();
    RequireSaved(s, uf);
    try
    {
        Part p = s.Parts.Work;
        Part d = s.Parts.Display;
        if (p == null || d == null || p.Tag != d.Tag ||
            string.IsNullOrEmpty(p.FullPath) || p.PartUnits != BasePart.Units.Millimeters)
            throw new InvalidOperationException("Require saved millimeter work part equal to display part.");
        List<Body> bodies = new List<Body>();
        foreach (Body b in p.Bodies)
        {
            if (!b.IsSolidBody) throw new InvalidOperationException("Sheet/non-solid bodies are not accepted.");
            bodies.Add(b);
        }
        if (bodies.Count == 0) throw new InvalidOperationException("At least one solid is required.");
        Func<double, bool> finite = x => !double.IsNaN(x) && !double.IsInfinity(x);
        List<string> rows = new List<string>();
        double total = 0.0, sumEstimates = 0.0;
        bool allAchieved = true;
        foreach (Body b in bodies)
        {
            double[] acc = new double[11], mass = new double[47], stats = new double[13];
            acc[0] = mode=="area_only"?tol:0; acc[1] = mode=="volume_only"?tol:0; // Area/volume tested; remaining zero tests intentionally disabled.
            uf.Modl.AskMassProps3d(new Tag[] { b.Tag }, 1, 1, 4, 1.0, 2, acc, mass, stats);
            foreach (double x in mass) if (!finite(x)) throw new InvalidOperationException("Nonfinite raw mass property.");
            foreach (double x in stats) if (!finite(x)) throw new InvalidOperationException("Nonfinite raw statistic.");
            double volume = mass[1] * 1e9, relative = stats[1];
            double estimate = Math.Abs(volume) * Math.Abs(relative);
            if (mass[0] < 0 || mass[2] < 0 || mass[46] < 0 || !finite(volume) || volume < 0 ||
                !finite(estimate) || estimate < 0)
                throw new InvalidOperationException("Invalid nonnegative mass/volume/error estimate; raw_mass47=" + A(mass) + "; raw_statistics13=" + A(stats));
            bool achieved = Math.Abs(relative) <= tol;
            bool areaAchieved = Math.Abs(stats[0]) <= tol;
            allAchieved = allAchieved && (mode=="volume_only"?achieved:areaAchieved);
            total += volume; sumEstimates += estimate;
            if (!finite(total) || total < 0 || !finite(sumEstimates) || sumEstimates < 0)
                throw new InvalidOperationException("Invalid aggregate volume/error estimate.");
            rows.Add("{\"body_tag\":" + Q(b.Tag.ToString()) + ",\"mass47\":" + A(mass) +
                ",\"statistics13\":" + A(stats) + ",\"volume_mm3\":" + N(volume) +
                ",\"statistics1_relative_estimate\":" + N(relative) + ",\"requested_tolerance\":" + N(tol) +
                ",\"statistics1_relative_estimate_magnitude\":" + N(Math.Abs(relative)) +
                ",\"area_achieved_relative_estimate_le_requested_tolerance\":" + (areaAchieved ? "true" : "false") +
                ",\"achieved_relative_estimate_le_requested_tolerance\":" + (achieved ? "true" : "false") +
                ",\"estimated_volume_error_mm3\":" + N(estimate) + ",\"error_interpretation\":" + Q("estimate_NOT_bound") + "}");
        }
        return "{\"ok\":true,\"scope\":" + Q("read-only isolated-target diagnostic; NOT STEP acceptance") + ",\"target_mode\":" + Q(mode) + ",\"active_area\":" + (mode=="area_only"?"true":"false") + ",\"active_volume\":" + (mode=="volume_only"?"true":"false") +
            ",\"part\":" + Q(p.FullPath) + ",\"accuracy\":2,\"units\":4,\"requested_tolerance\":" + N(tol) +
            ",\"bodies\":[" + string.Join(",", rows.ToArray()) + "],\"aggregate_volume_mm3\":" + N(total) +
            ",\"sum_estimated_volume_error_mm3\":" + N(sumEstimates) +
            ",\"error_interpretation\":" + Q("sum of per-body estimates; NOT a bound or global bound") +
            ",\"all_bodies_active_target_achieved\":" + (allAchieved ? "true" : "false") + "}";
    }
    finally { RequireSaved(s, uf); }
}
public static string RunDeviation(string sourcePath, string targetPath, string output)
{
    Session s = Session.GetSession(); UFSession uf = UFSession.GetUFSession();
    Part work = s.Parts.Work, display = s.Parts.Display;
    Tag wt = work == null ? Tag.Null : work.Tag, dt = display == null ? Tag.Null : display.Tag;
    var json = new System.Web.Script.Serialization.JavaScriptSerializer();
    StreamWriter writer = null; DevSampleStats forward = null, reverse = null;
    const string models = @"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227";
    Func<string, string> absolute = path => {
        if (string.IsNullOrEmpty(path) || !System.Text.RegularExpressions.Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]")) throw new ArgumentException("Absolute drive path required");
        return Path.GetFullPath(path);
    };
    Action<string> ancestors = dir => {
        for (DirectoryInfo d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Missing/reparse ancestor refused");
    };
    Func<object, string, object> get = (obj, name) => {
        if (obj == null) throw new InvalidOperationException("Missing WCS object; caller must record alignment; query refused");
        var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p == null || !p.CanRead || p.GetIndexParameters().Length != 0) throw new NotSupportedException("NX WCS getter unavailable: " + name + "; caller must record alignment; query refused");
        return p.GetValue(obj, null);
    };
    Action alignment = () => {
        object cs = get(get(work, "WCS"), "CoordinateSystem");
        Point3d o = (Point3d)get(cs, "Origin"); Matrix3x3 m = (Matrix3x3)get(get(cs, "Orientation"), "Element");
        if (!(o.X == 0 && o.Y == 0 && o.Z == 0 && m.Xx == 1 && m.Yy == 1 && m.Zz == 1 && m.Xy == 0 && m.Xz == 0 && m.Yx == 0 && m.Yz == 0 && m.Zx == 0 && m.Zy == 0)) throw new InvalidOperationException("Current WCS must equal absolute CSYS exactly");
    };
    Func<string, Body> loadedBody = path => {
        if (!string.Equals(Path.GetDirectoryName(path), models, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(path), ".prt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("PRT must be directly under exact task model root");
        ancestors(Path.GetDirectoryName(path));
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Missing/reparse PRT refused");
        Part found = null;
        foreach (BasePart bp in s.Parts) if (string.Equals(bp.FullPath, path, StringComparison.OrdinalIgnoreCase)) { if (found != null) throw new InvalidOperationException("Ambiguous loaded path"); found = bp as Part; }
        if (found == null || found.PartUnits != BasePart.Units.Millimeters || found.ComponentAssembly.RootComponent != null) throw new InvalidOperationException("Require already-loaded standalone millimeter part");
        Body body = null;
        foreach (Body b in found.Bodies) { if (!b.IsSolidBody || body != null) throw new InvalidOperationException("Exactly one solid and no sheets required"); body = b; }
        if (body == null) throw new InvalidOperationException("No solid"); return body;
    };
    try {
        try {
            RequireSaved(s, uf);
            output = absolute(output);
            if (!string.Equals(Path.GetDirectoryName(output), Root, StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(output), @"\A[A-Za-z0-9][A-Za-z0-9_-]*\.jsonl\z")) throw new IOException("Require ASCII basename .jsonl in exact task log directory");
            ancestors(Root);
            if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Existing output refused");
            writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)); writer.AutoFlush = true;
            sourcePath = absolute(sourcePath); targetPath = absolute(targetPath);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Distinct PRT paths required");
            Body source = loadedBody(sourcePath), target = loadedBody(targetPath);
            if (source.Tag == target.Tag) throw new InvalidOperationException("Distinct loaded bodies required");
            alignment();
            writer.WriteLine(json.Serialize(new { kind = "begin", source = sourcePath, target = targetPath, alignment = "current_WCS_origin_zero_orientation_identity; caller responsible for shared absolute model alignment; no registration or WCS change", threshold_mm = DevSampleStats.ThresholdMm }));
            Tag[] targetFaceTags = Array.ConvertAll(target.GetFaces(), f => f.Tag), sourceFaceTags = Array.ConvertAll(source.GetFaces(), f => f.Tag);
            forward = DevSampleDirection(uf, source, targetFaceTags, "source_to_target", writer);
            writer.WriteLine(json.Serialize(new { kind = "direction_completed", direction = "source_to_target", coverage_counts = forward }));
            reverse = DevSampleDirection(uf, target, sourceFaceTags, "target_to_source", writer);
            writer.WriteLine(json.Serialize(new { kind = "direction_completed", direction = "target_to_source", coverage_counts = reverse }));
            DevFinite(new double[] { forward.SampledMaxMm, reverse.SampledMaxMm }); alignment();
        }
        finally {
            try { RequireSaved(s, uf); }
            finally { if ((s.Parts.Work == null ? Tag.Null : s.Parts.Work.Tag) != wt || (s.Parts.Display == null ? Tag.Null : s.Parts.Display.Tag) != dt) throw new InvalidOperationException("Work/display identity changed; nothing restored, saved or closed"); }
        }
        string result = json.Serialize(new { kind = "success", ok = true, scope = "query_completed_NOT_universal_acceptance", interpretation = "finite_samples_NOT_global_Hausdorff_bound", threshold_mm = DevSampleStats.ThresholdMm, sampled_pass = forward.SampledMaxMm <= DevSampleStats.ThresholdMm && reverse.SampledMaxMm <= DevSampleStats.ThresholdMm, coverage_counts = new { source_to_target = forward, target_to_source = reverse }, output = output });
        writer.WriteLine(result); return result;
    }
    catch (Exception ex) {
        string failure = json.Serialize(new { kind = "failure", ok = false, error = ex.ToString(), interpretation = "finite_samples_NOT_global_Hausdorff_bound", coverage_counts = new { source_to_target = forward, target_to_source = reverse }, partial_log_retained = writer != null });
        try { if (writer != null) writer.WriteLine(failure); }
        catch (Exception logError) { return json.Serialize(new { ok = false, error = ex.ToString(), log_error = logError.ToString(), partial_log_retained = true }); }
        return failure;
    }
    finally { if (writer != null) writer.Dispose(); }
}
    public static string RunSheetDeviation(string sourcePath, string sourceTag,
    string targetPath, string targetTag, string newLogPath)
{
    Session s = Session.GetSession(); UFSession uf = UFSession.GetUFSession();
    Part work = s.Parts.Work, display = s.Parts.Display;
    Tag wt = work == null ? Tag.Null : work.Tag, dt = display == null ? Tag.Null : display.Tag;
    var json = new System.Web.Script.Serialization.JavaScriptSerializer();
    StreamWriter writer = null; DevSampleStats forward = null, reverse = null;
    Body source = null, target = null;
    const string sheetModels = @"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\phase4_surface_trim_extend";
    var loaded = new Dictionary<Tag, string>();
    foreach (BasePart bp in s.Parts) loaded.Add(bp.Tag, bp.FullPath);
    Func<string, string> absolute = path => {
        if (string.IsNullOrEmpty(path) ||
            !System.Text.RegularExpressions.Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]"))
            throw new ArgumentException("Absolute drive path required");
        return Path.GetFullPath(path);
    };
    Action<string> ancestors = dir => {
        for (DirectoryInfo d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Missing/reparse ancestor refused");
    };
    Func<object, string, object> get = (obj, name) => {
        if (obj == null) throw new InvalidOperationException("Missing WCS object");
        PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p == null || !p.CanRead || p.GetIndexParameters().Length != 0)
            throw new NotSupportedException("NX getter unavailable: " + name);
        return p.GetValue(obj, null);
    };
    Action<Part> identityWcs = part => {
        object cs = get(get(part, "WCS"), "CoordinateSystem");
        Point3d o = (Point3d)get(cs, "Origin");
        Matrix3x3 m = (Matrix3x3)get(get(cs, "Orientation"), "Element");
        if (!(o.X == 0 && o.Y == 0 && o.Z == 0 &&
              m.Xx == 1 && m.Yy == 1 && m.Zz == 1 &&
              m.Xy == 0 && m.Xz == 0 && m.Yx == 0 &&
              m.Yz == 0 && m.Zx == 0 && m.Zy == 0))
            throw new InvalidOperationException("Require identity absolute WCS; nothing changed");
    };
    Func<string, string, Body> selectedSheet = (path, tagText) => {
        if (!string.Equals(Path.GetDirectoryName(path), sheetModels, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".prt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PRT must be directly in the exact sheet task directory");
        ancestors(sheetModels);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Missing/reparse PRT refused");
        if (string.IsNullOrEmpty(tagText) ||
            !System.Text.RegularExpressions.Regex.IsMatch(tagText, @"\A[1-9][0-9]*\z"))
            throw new ArgumentException("Canonical nonzero decimal current body tag required");
        Part found = null;
        foreach (BasePart bp in s.Parts)
            if (string.Equals(bp.FullPath, path, StringComparison.OrdinalIgnoreCase)) {
                if (found != null) throw new InvalidOperationException("Ambiguous loaded part");
                found = bp as Part;
                if (found == null) throw new InvalidOperationException("Loaded object is not Part");
            }
        if (found == null || !found.IsFullyLoaded ||
            found.PartUnits != BasePart.Units.Millimeters ||
            found.ComponentAssembly.RootComponent != null)
            throw new InvalidOperationException("Require already fully loaded standalone millimeter part");
        // NX12 exposes WCS only for the displayed part. Geometry below is
        // evaluated in owning-part absolute coordinates, with no registration.
        Body selected = null;
        foreach (Body b in found.Bodies) {
            // No ordinal, name, registry handle, nearest-body or bbox fallback.
            if (!string.Equals(b.Tag.ToString(), tagText, StringComparison.Ordinal)) continue;
            if (selected != null) throw new InvalidOperationException("Ambiguous body tag");
            if (b.Tag == Tag.Null || b.IsSolidBody || !b.IsSheetBody ||
                b.OwningPart == null || b.OwningPart.Tag != found.Tag)
                throw new InvalidOperationException("Exact tag must belong to a sheet in the specified part");
            selected = b;
        }
        if (selected == null) throw new InvalidOperationException("Current body tag absent from specified part");
        if (selected.GetFaces().Length == 0) throw new InvalidOperationException("Selected sheet has no faces");
        return selected;
    };
    Action invariant = () => {
        RequireSaved(s, uf);
        if ((s.Parts.Work == null ? Tag.Null : s.Parts.Work.Tag) != wt ||
            (s.Parts.Display == null ? Tag.Null : s.Parts.Display.Tag) != dt)
            throw new InvalidOperationException("Work/display changed; nothing restored, saved or closed");
        int count = 0;
        foreach (BasePart bp in s.Parts) {
            string prior;
            if (!loaded.TryGetValue(bp.Tag, out prior) ||
                !string.Equals(prior, bp.FullPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Loaded part identity/path changed");
            ++count;
        }
        if (count != loaded.Count) throw new InvalidOperationException("Loaded part set changed");
        if (work == null || display == null || wt != dt) throw new InvalidOperationException("Require same work/display part");
        identityWcs(display);
        if (source != null && selectedSheet(sourcePath, sourceTag).Tag != source.Tag)
            throw new InvalidOperationException("Source selection changed");
        if (target != null && selectedSheet(targetPath, targetTag).Tag != target.Tag)
            throw new InvalidOperationException("Target selection changed");
    };
    Func<Body, Tag[]> faceTags = body => {
        Face[] faces = body.GetFaces();
        if (faces.Length == 0) throw new InvalidOperationException("No selected sheet faces");
        var tags = new HashSet<Tag>();
        foreach (Face face in faces)
            if (face == null || face.Tag == Tag.Null || face.GetBody().Tag != body.Tag ||
                !tags.Add(face.Tag))
                throw new InvalidOperationException("Invalid selected-body face membership");
        return Array.ConvertAll(faces, f => f.Tag);
    };
    try {
        try {
            invariant();
            sourcePath = absolute(sourcePath); targetPath = absolute(targetPath);
            newLogPath = absolute(newLogPath);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Distinct part paths required");
            source = selectedSheet(sourcePath, sourceTag);
            target = selectedSheet(targetPath, targetTag);
            if (source.Tag == target.Tag) throw new InvalidOperationException("Distinct selected sheets required");
            Tag[] sourceFaces = faceTags(source), targetFaces = faceTags(target);
            if (!string.Equals(Path.GetDirectoryName(newLogPath), Root, StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(newLogPath),
                    @"\A[A-Za-z0-9][A-Za-z0-9_-]*\.jsonl\z"))
                throw new IOException("Require new ASCII basename .jsonl in exact task log directory");
            ancestors(Root);
            if (File.Exists(newLogPath) || Directory.Exists(newLogPath))
                throw new IOException("Existing log refused");
            invariant();
            writer = new StreamWriter(new FileStream(newLogPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None), new UTF8Encoding(false));
            writer.AutoFlush = true;
            writer.WriteLine(json.Serialize(new {
                kind = "begin", query = "RunSheetDeviation",
                source = sourcePath, source_body_tag = sourceTag,
                target = targetPath, target_body_tag = targetTag,
                source_face_tags = Array.ConvertAll(sourceFaces, t => t.ToString()),
                target_face_tags = Array.ConvertAll(targetFaces, t => t.ToString()),
                selection = "exact_current_tag_in_specified_part; all_other_bodies_excluded",
                alignment = "current_display_WCS_identity_checked; owning_part_absolute_coordinates; shared_absolute_alignment_required; no_registration",
                threshold_mm = DevSampleStats.ThresholdMm,
                face_grid = "17x17", samples_per_unique_edge = 33,
                interpretation = "finite_valid_witnesses_notcertifiednearest_NOT_global_Hausdorff_bound"
            }));
            forward = DevSampleDirection(uf, source, targetFaces, "source_to_target", writer, true);
            writer.WriteLine(json.Serialize(new { kind = "direction_completed",
                direction = "source_to_target", coverage_counts = forward }));
            invariant();
            reverse = DevSampleDirection(uf, target, sourceFaces, "target_to_source", writer, true);
            writer.WriteLine(json.Serialize(new { kind = "direction_completed",
                direction = "target_to_source", coverage_counts = reverse }));
            DevFinite(new double[] { forward.SampledMaxMm, reverse.SampledMaxMm });
        }
        finally { invariant(); }
        string result = json.Serialize(new {
            kind = "success", ok = true, read_only_query = true,
            scope = "selected_sheet_query_completed_NOT_universal_acceptance",
            source = sourcePath, source_body_tag = sourceTag,
            target = targetPath, target_body_tag = targetTag,
            interpretation = "finite_valid_witnesses_notcertifiednearest_NOT_global_Hausdorff_bound",
            threshold_mm = DevSampleStats.ThresholdMm,
            sampled_pass = forward.SampledMaxMm <= DevSampleStats.ThresholdMm &&
                reverse.SampledMaxMm <= DevSampleStats.ThresholdMm,
            sampled_pass_scope = "valid_witness_threshold_only; false_means_NOT_certified_by_these_witnesses_NOT_proven_true_distance_failure; notcertifiednearest_NOT_global_fidelity",
            coverage_counts = new { source_to_target = forward, target_to_source = reverse },
            output = newLogPath
        });
        writer.WriteLine(result); return result;
    }
    catch (Exception ex) {
        string failure = json.Serialize(new { kind = "failure", ok = false,
            error = ex.ToString(), source = sourcePath, source_body_tag = sourceTag,
            target = targetPath, target_body_tag = targetTag,
            interpretation = "incomplete_query_NOT_precision_acceptance",
            coverage_counts = new { source_to_target = forward, target_to_source = reverse },
            partial_log_retained = writer != null, output = newLogPath });
        try { if (writer != null) writer.WriteLine(failure); }
        catch (Exception logError) {
            return json.Serialize(new { ok = false, error = ex.ToString(),
                log_error = logError.ToString(), partial_log_retained = true, output = newLogPath });
        }
        return failure;
    }
    finally { if (writer != null) writer.Dispose(); }
}

public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Immediately; }
}
