    private static bool ModernLabPart() {return _part!=null && (_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase2_",StringComparison.OrdinalIgnoreCase) || _part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase3_",StringComparison.OrdinalIgnoreCase) || _part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_",StringComparison.OrdinalIgnoreCase));}
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
