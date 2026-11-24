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
