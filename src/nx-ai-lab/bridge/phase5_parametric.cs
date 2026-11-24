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
