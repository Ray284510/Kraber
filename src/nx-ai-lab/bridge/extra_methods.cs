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
        Sketch sk=GetSketch(args[1]); NXObject[] geom=sk.GetAllGeometry();
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
