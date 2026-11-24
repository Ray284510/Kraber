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
