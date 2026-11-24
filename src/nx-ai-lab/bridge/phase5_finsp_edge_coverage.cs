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
