// Source-only NX12 reader. Compile as an additional source file with the loader.
// Dispatch: case "nx_independent_nurbs_topology": return Json(IvNurbs2610.Read(parts, _part));
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;

public static class IvNurbs2610
{
    const string Root = @"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\independent_volume_20261109\phase4_nurbs_topology_20261110";
    const string Original = "phase4_iv_nurbs_original_20261110.prt";
    const string Step = "phase4_iv_nurbs_step_20261110.prt";
    const string OriginalHash = "85e82314c0d86f75cae625b153029f599b71383d87d00aad73c54152c35b3f05";
    const string StepHash = "da04fa962760c9e097251462bd93cc87b2e04db7f0a34a3e7f3e7d0597455742";
    static void Need(bool v, string why) { if (!v) throw new InvalidOperationException(why); }
    static string Hash(string path)
    {
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var h = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }
    static string[] Inventory(Part p, UFSession uf)
    {
        var seen = new HashSet<Tag>(); var rows = new List<string>(); Tag t = Tag.Null;
        while ((t = uf.Obj.CycleAll(p.Tag, t)) != Tag.Null)
        {
            Need(seen.Add(t) && seen.Count <= 100000, "Invalid/excessive object cycle");
            int type, sub; uf.Obj.AskTypeAndSubtype(t, out type, out sub);
            rows.Add(t.ToString() + ":" + type + ":" + sub);
        }
        rows.Sort(StringComparer.Ordinal); return rows.ToArray();
    }
    static string Guard(Part p, UFSession uf)
    {
        Need(p != null && !String.IsNullOrEmpty(p.FullPath), "Saved part required");
        string path = Path.GetFullPath(p.FullPath), name = Path.GetFileName(path);
        Need(String.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase)
            && (name == Original || name == Step), "Exact isolated copy path required");
        Need(File.Exists(path) && p.IsFullyLoaded && p.PartUnits == BasePart.Units.Millimeters,
            "Saved fully loaded millimeter part required");
        Need((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Reparse file");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(path)); d != null; d = d.Parent)
            Need((d.Attributes & FileAttributes.ReparsePoint) == 0, "Reparse ancestor");
        var s = Session.GetSession(); int count = 0;
        foreach (BasePart q in s.Parts) { count++; Need(q.Tag == p.Tag, "Other loaded part"); }
        Need(count == 1 && s.Parts.Work == p && s.Parts.Display == p, "Sole work/display required");
        Need(Convert.ToInt32(uf.Part.IsModified(p.Tag)) == 0, "Dirty part refused");
        Need(Hash(path) == (name == Original ? OriginalHash : StepHash), "Copy provenance SHA mismatch");
        return path;
    }
    public static object Read(string[] args, Part p)
    {
        var uf = UFSession.GetUFSession();
        var facesOut = new List<object>(); var edgesOut = new List<object>();
        var errors = new List<string>(); var unsupported = new List<object>();
        string path = null, before = null, after = null; string[] inv = null, post = null;
        bool unchanged = false, reciprocal = true, complete = false;
        try
        {
            Need(args != null && args.Length == 1, "No query arguments");
            path = Guard(p, uf); before = Hash(path); inv = Inventory(p, uf);
            var bodies = p.Bodies.ToArray(); Need(bodies.Length == 1 && bodies[0].IsSolidBody, "Sole solid required");
            var body = bodies[0]; var faces = body.GetFaces(); var edges = body.GetEdges();
            var fd = faces.ToDictionary(f => f.Tag); var ed = edges.ToDictionary(e => e.Tag);
            var fe = new Dictionary<Tag, Tag[]>(); var ef = new Dictionary<Tag, Tag[]>();
            foreach (var f in faces) fe.Add(f.Tag, f.GetEdges().Select(e => e.Tag).ToArray());
            foreach (var e in edges) ef.Add(e.Tag, e.GetFaces().Select(f => f.Tag).ToArray());
            // Preserve repeated entries; unique adjacency does not establish fin multiplicity at seams.
            foreach (var f in faces)
            {
                string tag = f.Tag.ToString();
                foreach (var e in fe[f.Tag])
                    if (!ed.ContainsKey(e) || !ef[e].Contains(f.Tag)) reciprocal = false;
                var row = new Dictionary<string, object>();
                row["tag"] = tag; row["native_type"] = f.SolidFaceType.ToString();
                row["edges_unordered"] = fe[f.Tag].Select(t => t.ToString()).ToArray();
                try
                {
                    int type, sense, us, vs; double radius, radData, up, vp;
                    var point = new double[3]; var axis = new double[3]; var box = new double[6]; var uv = new double[4];
                    uf.Modl.AskFaceData(f.Tag, out type, point, axis, box, out radius, out radData, out sense);
                    uf.Modl.AskFaceUvMinmax(f.Tag, uv);
                    uf.Modl.AskFacePeriodicity(f.Tag, out us, out up, out vs, out vp);
                    row["uf_type"] = type; row["norm_dir"] = sense;
                    row["point"] = point; row["axis"] = axis; row["radius"] = radius; row["rad_data"] = radData;
                    row["uv_bounds_not_trim_domain"] = uv;
                    row["periodicity"] = new {u_status=us,u_period=up,v_status=vs,v_period=vp};
                    string native = f.SolidFaceType.ToString();
                    if (native == "Parametric" || native == "Offset")
                    {
                        row["bsurface_status"] = "disabled_after_native_fatal";
                        unsupported.Add(new {face=tag,native_type=native,reason="Bsurface extraction disabled pending isolated crash attribution"});
                    }
                    else { row["bsurface_status"] = "unsupported_native_type";
                        unsupported.Add(new {face=tag,native_type=native,reason="AskBsurf supports Parametric/Offset only"}); }
                }
                catch (Exception ex) { row["query_error"] = ex.ToString(); errors.Add("Face " + tag + ": " + ex.Message); }
                facesOut.Add(row);
            }
            foreach (var e in edges)
            {
                foreach (var f in ef[e.Tag])
                    if (!fd.ContainsKey(f) || !fe[f].Contains(e.Tag)) reciprocal = false;
                edgesOut.Add(new {tag=e.Tag.ToString(),native_type=e.SolidEdgeType.ToString(),
                    faces_unordered=ef[e.Tag].Select(t=>t.ToString()).ToArray(),
                    adjacency_count=ef[e.Tag].Length,fin_multiplicity_known=false});
            }
            int n; int[] faults; Tag[] bad;
            uf.Modl.AskBodyConsistency(body.Tag, out n, out faults, out bad);
            if (n != 0) errors.Add("Body consistency fault count: " + n);
            if (!reciprocal) errors.Add("Nonreciprocal or foreign face-edge incidence");
            complete = facesOut.Count == faces.Length && edgesOut.Count == edges.Length;
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        finally
        {
            if (before != null && inv != null)
                try
                {
                    after = Hash(path); post = Inventory(p, uf);
                    Guard(p, uf);
                    unchanged = before == after && inv.SequenceEqual(post);
                    if (!unchanged) errors.Add("Post-query SHA/inventory changed");
                }
                catch (Exception ex) { errors.Add("Post-audit: " + ex.ToString()); }
        }
        return new {ok=errors.Count==0 && unchanged && complete, read_only_query=true,
            part=path, sha256_before=before,sha256_after=after, inventory_before=inv, inventory_after=post,
            unchanged=unchanged, faces=facesOut, edges=edgesOut,
            incidence_enumeration_complete=complete,reciprocal_adjacency=complete && reciprocal,
            unsupported=unsupported,errors=errors,
            trim_pcurves_available=false,ordered_loops_available=false,closure_proven=false,
            independent_integrator_accepted=false,
            scope="Surface data and unordered incidence only. No UV rectangle assumption, sampled boundary, integral, convergence, or 0.1 mm3 claim."};
    }
}
