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
