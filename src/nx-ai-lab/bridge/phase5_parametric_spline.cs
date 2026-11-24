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
