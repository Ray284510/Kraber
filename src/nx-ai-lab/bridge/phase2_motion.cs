    private static string MotionLegacyCreate()
    {
        Phase2Part();string masterPath=_part.FullPath;SavePart();string created=Session.GetSession().MotionSession.CreateSimulation(_part);_part=Session.GetSession().Parts.Work;_partPath=_part.FullPath;return Json(new {ok=true,master=masterPath,created=created,part=_part.FullPath,api="NX12-supported deprecated CreateSimulation"});
    }
    private static NXOpen.Assemblies.Component MotionComponent(string name,NXOpen.Assemblies.Component parent)
    {
        foreach(var c in parent.GetChildren()) {if(c.Name==name)return c;var found=MotionComponent(name,c);if(found!=null)return found;}return null;
    }
    private static NXOpen.Motion.Link MotionLinkByName(string name) {foreach(var x in _part.MotionManager.Links.ToArray())if(x.Name==name)return x;throw new Exception("Unknown link "+name);}
    private static NXOpen.Motion.Joint MotionJointByName(string name) {foreach(var x in _part.MotionManager.Joints.ToArray())if(x.Name==name)return x;throw new Exception("Unknown joint "+name);}
    private static NXOpen.Motion.MotionSolution MotionSolutionByName(string name) {foreach(var x in _part.MotionManager.MotionSolutions.ToArray())if(x.Name==name || x.GetName()==name)return x;throw new Exception("Unknown solution "+name);}
    private static string MotionSetup()
    {
        Phase2Part();Session.GetSession().MotionSession.InitializeSimulation(_part);
        var env=Session.GetSession().MotionSession.Environments;env.SetSolver(NXOpen.Motion.MotionEnvironment.Solver.Simcenter);env.SetAnalysisType(NXOpen.Motion.MotionEnvironment.Analysis.Kinematics);env.SetComponentBasedMechanism(true);
        return Json(new {ok=true,solver=env.GetSolver().ToString(),part=_part.FullPath});
    }
    private static string MotionLinkCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.Links.CreateLinkBuilder(null);
        try {b.Name=a[1];b.Fixed=a[2]=="1";for(int i=3;i<a.Length;i++) {var c=MotionComponent(a[i],_part.ComponentAssembly.RootComponent);if(c==null)throw new Exception("Component missing "+a[i]);var p=(Part)c.Prototype;if(!p.IsFullyLoaded){var s=p.LoadFully();s.Dispose();}b.Geometries.Add(c);}var obj=b.Commit();return Json(new {ok=true,link=obj.Name,tag=obj.Tag.ToString(),fixed_link=b.Fixed});}finally{b.Destroy();}
    }
    private static string MotionJointCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.Joints.CreateJointBuilder(null);
        try {var d=b.JointDefine;d.Name=a[1];d.JointType=(NXOpen.Motion.JointDefine.Type)Enum.Parse(typeof(NXOpen.Motion.JointDefine.Type),a[2],true);d.FirstLink=MotionLinkByName(a[3]);d.SecondLink=MotionLinkByName(a[4]);
            var p=new Point3d(double.Parse(a[5]),double.Parse(a[6]),double.Parse(a[7]));var v=new Vector3d(double.Parse(a[8]),double.Parse(a[9]),double.Parse(a[10]));
            d.FirstOrigin=_part.Points.CreatePoint(p);d.SecondOrigin=_part.Points.CreatePoint(p);d.FirstOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;d.SecondOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;
            d.FirstVector=_part.Directions.CreateDirection(p,v,SmartObject.UpdateOption.WithinModeling);d.SecondVector=_part.Directions.CreateDirection(p,v,SmartObject.UpdateOption.WithinModeling);d.SnapLinks=false;
            if(a.Length>12){d.LowerLimitDefined=true;d.UpperLimitDefined=true;if(d.JointType==NXOpen.Motion.JointDefine.Type.Revolute){d.LowerLimitAngleExpression.RightHandSide=a[11];d.UpperLimitAngleExpression.RightHandSide=a[12];}else{d.LowerLimitExpression.RightHandSide=a[11];d.UpperLimitExpression.RightHandSide=a[12];}}
            var obj=b.Commit();return Json(new {ok=true,joint=obj.Name,tag=obj.Tag.ToString(),joint_type=d.JointType.ToString()});
        }finally{b.Destroy();}
    }
    private static string MotionState()
    {
        var links=new List<object>();var joints=new List<object>();var drivers=new List<object>();var solutions=new List<object>();
        foreach(var x in _part.MotionManager.Links.ToArray()) {var b=_part.MotionManager.Links.CreateLinkBuilder(x);try{links.Add(new {name=x.Name,tag=x.Tag.ToString(),fixed_link=b.Fixed,geometry_count=b.Geometries.Size});}finally{b.Destroy();}}
        foreach(var x in _part.MotionManager.Joints.ToArray()){var b=_part.MotionManager.Joints.CreateJointBuilder(x);try{var d=b.JointDefine;joints.Add(new {name=x.Name,type=d.JointType.ToString(),first_link=d.FirstLink==null?null:d.FirstLink.Name,second_link=d.SecondLink==null?null:d.SecondLink.Name});}finally{b.Destroy();}}
        foreach(var x in _part.MotionManager.JointDrivers.ToArray())drivers.Add(new {name=x.Name,tag=x.Tag.ToString()});
        foreach(var x in _part.MotionManager.MotionSolutions.ToArray())solutions.Add(new {name=x.GetName(),gruebler_dof=x.CalculateGrueblerCount()});
        return Json(new {ok=true,part=_part.FullPath,solver=Session.GetSession().MotionSession.Environments.GetSolver().ToString(),links=links,joints=joints,drivers=drivers,solutions=solutions});
    }
    private static string MotionSolutionCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(null);
        try {b.Name=a[1];b.SolutionType=a[2]=="articulation"?NXOpen.Motion.MotionSolutionBuilder.SolutionTypes.Articulation:NXOpen.Motion.MotionSolutionBuilder.SolutionTypes.Normal;b.AnalysisType=NXOpen.Motion.MotionSolutionBuilder.AnalysisTypes.Dynamic;b.Step=int.Parse(a[4]);b.TimeExpression.RightHandSide=a[3];b.GravityValueExpression.RightHandSide="0";var obj=(NXOpen.Motion.MotionSolution)b.Commit();return Json(new {ok=true,solution=obj.GetName(),gruebler_dof=obj.CalculateGrueblerCount()});}finally{b.Destroy();}
    }
    private static string MotionSolve(string[] a) {Phase2Part();var s=MotionSolutionByName(a[1]);_part.MotionManager.MotionSolutions.SetActiveSolution(s);s.SolveNormalRunSolution();return Json(new {ok=true,solution=s.GetName(),solver_returned=true});}
    private static string MotionDriverCreate(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(null);
        try {b.Name=a[1];b.Joint.Value=MotionJointByName(a[2]);var d=a[3]=="slider"?b.DriverMultiOperations.MotionTranslationZ:b.DriverMultiOperations.MotionEulerAngle1;d.TypeOption=NXOpen.Motion.DriverOperation.Type.Polynomial;d.IntegrationTypeOption=NXOpen.Motion.DriverOperation.IntegrationType.Displacement;d.DisplacementExpression.RightHandSide="0";d.VelocityExpression.RightHandSide=a[4];d.AccelerationExpression.RightHandSide="0";d.JerkExpression.RightHandSide="0";var obj=b.Commit();return Json(new {ok=true,driver=obj.Name,velocity=d.VelocityExpression.RightHandSide,velocity_unit=d.VelocityExpression.Units==null?null:d.VelocityExpression.Units.Name});}finally{b.Destroy();}
    }
    private static string MotionJointAxis(string[] a)
    {
        Phase2Part();var b=_part.MotionManager.Joints.CreateJointBuilder(MotionJointByName(a[1]));try{var d=b.JointDefine;var v=new Vector3d(double.Parse(a[2]),double.Parse(a[3]),double.Parse(a[4]));d.FirstVector=_part.Directions.CreateDirection(d.FirstOrigin.Coordinates,v,SmartObject.UpdateOption.WithinModeling);d.SecondVector=_part.Directions.CreateDirection(d.SecondOrigin.Coordinates,v,SmartObject.UpdateOption.WithinModeling);b.Commit();return Json(new{ok=true,joint=a[1],axis=v});}finally{b.Destroy();}
    }
    private static string MotionAngle(string[] a)
    {
        Phase2Part();var uf=NXOpen.UF.UFSession.GetUFSession();var m=new NXOpen.UF.UFMotion.Measurement();uf.Motion.InitMeasurementStruct(out m);m.type=(NXOpen.UF.UFMotion.MeasurementType)2;m.object_1=P2Geometry(P2Component(a[1]),a[2]).Tag;m.object_2=P2Geometry(P2Component(a[3]),a[4]).Tag;Tag tag;uf.Motion.CreateMeasurement(ref m,out tag);try{double value;uf.Motion.Measure(tag,int.Parse(a[5]),out value);return Json(new{ok=true,step=int.Parse(a[5]),angle_native=value,source="UF_MOTION_measure angle between actual linked linear edges"});}finally{uf.Motion.DeleteMeasurement(tag);}
    }
    private static string Phase2Color(string[] a) {Phase2Part();int color=int.Parse(a[1]);if(color<1||color>216)throw new Exception("NX color index 1..216 required");
        if(_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_",StringComparison.OrdinalIgnoreCase)){var d=Session.GetSession().DisplayManager.NewDisplayModification();try{d.ApplyToAllFaces=true;d.NewColor=color;var bodies=new List<DisplayableObject>();foreach(Body b in _part.Bodies)bodies.Add(b);d.Apply(bodies.ToArray());}finally{d.Dispose();}}
        else foreach(var b in _part.Bodies.ToArray())b.Color=color;return Json(new{ok=true,color=color});}
    private static string Phase2View(string[] a) {Phase2Part();var v=_part.ModelingViews.WorkView;v.Orient(a[1]=="top"?NXOpen.View.Canned.Top:NXOpen.View.Canned.Isometric,NXOpen.View.ScaleAdjustment.Fit);v.SetScale(v.Scale*double.Parse(a[2]));return Json(new {ok=true,view=a[1],scale=v.Scale});}
    private static string Phase2Application(string[] a) {string app=a[1]=="motion"?"UG_APP_MECHANISMS":"UG_APP_MODELING";Session.GetSession().ApplicationSwitchImmediate(app);return Json(new {ok=true,application=app});}
    private static NXOpen.Motion.AnimationControl _motionAnimation;
    private static string _motionAnimationPart,_motionAnimationSolution;
    private static string MotionFrame(string[] a)
    {
        Phase2Part();bool reacquired=false;
        if(_motionAnimation!=null) {try {var alive=_motionAnimation.Tag;}catch(NXException e){Log("Animation controller expired; reacquire native results: "+e.ErrorCode+" "+e.Message);_motionAnimation=null;reacquired=true;}}
        if(_motionAnimation!=null && (_motionAnimationPart!=_part.FullPath || _motionAnimationSolution!=a[1])) {_motionAnimation.Finish();_motionAnimation=null;}
        if(_motionAnimation==null){var s=MotionSolutionByName(a[1]);_part.MotionManager.MotionSolutions.SetActiveSolution(s);s.LoadResult();_motionAnimation=s.GetAnimationControl();_motionAnimationPart=_part.FullPath;_motionAnimationSolution=a[1];}
        if(a[2]=="finish"){_motionAnimation.StepToDesignPosition();_motionAnimation.Finish();_motionAnimation=null;return Json(new{ok=true,finished=true});}
        _motionAnimation.StepTo(int.Parse(a[2]));Session.GetSession().ListingWindow.CloseWindow();return Json(new{ok=true,solution=a[1],active_solution=_part.MotionManager.MotionSolutions.GetActiveSolution().GetName(),frame=_motionAnimation.CurrentStep,number_steps=_motionAnimation.NumberSteps,controller_reacquired=reacquired});
    }
    private class MotionPair {public string a,b;public Tag tag;}
    private static List<MotionPair> _motionPairs=new List<MotionPair>();
    private static string _motionCheckPart;
    private static Tag _motionCheckPartTag;
    private static string PairKey(Tag a,Tag b) {string x=a.ToString(),y=b.ToString();return String.CompareOrdinal(x,y)<0?x+":"+y:y+":"+x;}
    private static string MotionCollisionSetup()
    {
        Phase2Part();_motionPairs.Clear();_motionCheckPart=null;
        var uf=NXOpen.UF.UFSession.GetUFSession();var bodies=new List<Tuple<string,string,Tag>>();
        foreach(var link in _part.MotionManager.Links.ToArray()) {var builder=_part.MotionManager.Links.CreateLinkBuilder(link);try {
            foreach(var geo in builder.Geometries.GetArray()) {var c=geo as NXOpen.Assemblies.Component;if(c==null)throw new Exception("Collision tool requires component-based links");var p=(Part)c.Prototype;
                if(!p.IsFullyLoaded){var ls=p.LoadFully();ls.Dispose();}
                foreach(var body in p.Bodies.ToArray()) {var occ=c.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Body occurrence unavailable");bodies.Add(Tuple.Create(link.Name,c.Name,occ.Tag));}
            }
        }finally{builder.Destroy();}}
        // NX12 persists interference definitions in SIM. Reuse them after reopen;
        // creating duplicates can raise a native SEHException on this version.
        var existing=new Dictionary<string,Tag>();Tag cursor=Tag.Null;int found=0,reused=0,created=0;
        do {uf.Obj.CycleObjsInPart(_part.Tag,211,ref cursor);if(cursor!=Tag.Null){NXOpen.UF.UFMotion.InterferenceStruct data;uf.Motion.AskInterference(cursor,out data);string key=PairKey(data.solid_1,data.solid_2);if(!existing.ContainsKey(key))existing.Add(key,cursor);found++;}}while(cursor!=Tag.Null);
        var complete=new List<MotionPair>();
        for(int i=0;i<bodies.Count;i++)for(int j=i+1;j<bodies.Count;j++)if(bodies[i].Item1!=bodies[j].Item1){
            string key=PairKey(bodies[i].Item3,bodies[j].Item3);Tag tag;
            if(existing.TryGetValue(key,out tag))reused++;
            else {var data=new NXOpen.UF.UFMotion.InterferenceStruct();uf.Motion.InitInterferenceStruct(out data);data.solid_1=bodies[i].Item3;data.solid_2=bodies[j].Item3;uf.Motion.CreateInterference(ref data,out tag);created++;}
            complete.Add(new MotionPair{a=bodies[i].Item2,b=bodies[j].Item2,tag=tag});
        }
        _motionPairs=complete;_motionCheckPart=_part.FullPath;_motionCheckPartTag=_part.Tag;
        return Json(new{ok=true,pair_count=_motionPairs.Count,body_count=bodies.Count,persisted_definitions=found,reused_definitions=reused,created_definitions=created,source="NX UF_MOTION real component body occurrences; persistent definitions reused by actual body tags"});
    }
    private static string MotionCollisionCheck(string[] a)
    {
        Phase2Part();if(_motionCheckPart!=_part.FullPath || _motionCheckPartTag!=_part.Tag || _motionPairs.Count==0)throw new Exception("Call nx_motion_collision_setup first");int step=int.Parse(a[1]);var uf=NXOpen.UF.UFSession.GetUFSession();int count=0;var pairs=new List<object>();
        foreach(var pair in _motionPairs){int result;uf.Motion.Interference(pair.tag,step,out result);if(result!=0)count++;pairs.Add(new {a=pair.a,b=pair.b,interferes=result!=0,nx_result=result});}
        return Json(new{ok=true,step=step,pairs=pairs,interference_count=count,source="UF_MOTION_interference evaluated at native solver result step; discrete sampling"});
    }
