    private static string MotionAudit()
    {
        var uf=NXOpen.UF.UFSession.GetUFSession();object solverParameters;
        try {NXOpen.UF.UFMotion.SolverParameters p;uf.Motion.AskSolverParameters(out p);solverParameters=p;}catch(NXException e){solverParameters=new {error=e.Message,code=e.ErrorCode};}
        var joints=new List<object>();var drivers=new List<object>();var solutions=new List<object>();
        foreach(var j in _part.MotionManager.Joints.ToArray()) {var b=_part.MotionManager.Joints.CreateJointBuilder(j);try {var d=b.JointDefine;joints.Add(new {name=j.Name,type=d.JointType.ToString(),first=d.FirstLink.Name,second=d.SecondLink.Name,origin1=d.FirstOrigin.Coordinates,origin2=d.SecondOrigin.Coordinates,axis1=d.FirstVector.Vector,axis2=d.SecondVector.Vector,lower_defined=d.LowerLimitDefined,upper_defined=d.UpperLimitDefined,lower_degrees=d.LowerLimitAngleExpression.Value,upper_degrees=d.UpperLimitAngleExpression.Value});}finally{b.Destroy();}}
        foreach(var j in _part.MotionManager.JointDrivers.ToArray()) {var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(j);try {var d=b.DriverMultiOperations.MotionEulerAngle1;drivers.Add(new {name=j.Name,joint=((NXObject)b.Joint.Value).Name,type=d.TypeOption.ToString(),integration=d.IntegrationTypeOption.ToString(),displacement=d.DisplacementExpression.RightHandSide,velocity=d.VelocityExpression.RightHandSide,acceleration=d.AccelerationExpression.RightHandSide,jerk=d.JerkExpression.RightHandSide,unit=d.VelocityExpression.Units.Name});}finally{b.Destroy();}}
        foreach(var s in _part.MotionManager.MotionSolutions.ToArray()){var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(s);try{solutions.Add(new {name=s.GetName(),analysis_type=b.AnalysisType.ToString(),solution_type=b.SolutionType.ToString(),gruebler_dof=s.CalculateGrueblerCount(),time=b.TimeExpression.Value,steps=b.Step,gravity=b.GravityValueExpression.Value});}finally{b.Destroy();}}
        return Json(new {ok=true,part=_part.FullPath,solver_parameters=solverParameters,joints=joints,drivers=drivers,solutions=solutions,body_contact_count=_part.MotionManager.BodyContacts.ToArray().Length,analysis_enum_documentation="NX12 Dynamic means Dynamic or Kinematic analysis; use solver parameters, DOF and native solver output together"});
    }
    private static string MotionTransforms(string[] a)
    {
        int frame=int.Parse(a[1]);var rows=new List<object>();var uf=NXOpen.UF.UFSession.GetUFSession();
        foreach(var link in _part.MotionManager.Links.ToArray()){var mat=new double[16];uf.Motion.AskLinkTransfromForGivenFrame(link.Tag,frame,mat);rows.Add(new {link=link.Name,matrix=mat});}
        return Json(new {ok=true,frame=frame,links=rows,source="NX12 UF_MOTION_ask_link_transfrom_for_given_frame native solver result"});
    }
    private static string MotionMarkerCreate(string[] a)
    {
        Phase2Part();var b=_part.Markers.CreateMarkerBuilder(null);var p=new Point3d(D(a[3]),D(a[4]),D(a[5]));var m=new Matrix3x3();m.Xx=1;m.Yy=1;m.Zz=1;
        try{b.Name=a[1];b.SelectLink.Value=MotionLinkByName(a[2]);b.OriginPoint=_part.Points.CreatePoint(p);b.Csys=_part.CoordinateSystems.CreateCoordinateSystem(p,m,false);var obj=b.Commit();return Json(new {ok=true,marker=obj.Name,origin=p});}finally{b.Destroy();}
    }
    private static string MotionMarkerResults(string[] a)
    {
        var uf=NXOpen.UF.UFSession.GetUFSession();var rows=new List<object>();
        foreach(var marker in _part.Markers.ToArray()) {
            if(!marker.Name.StartsWith(a[1],StringComparison.Ordinal))continue;var xyz=new List<object>();
            foreach(var component in new NXOpen.UF.UFMotion.VectorComponent[]{NXOpen.UF.UFMotion.VectorComponent.XComponent,NXOpen.UF.UFMotion.VectorComponent.YComponent,NXOpen.UF.UFMotion.VectorComponent.ZComponent}) {
                int count;double[] values;uf.Motion.AskTransDisplacementResults(marker.Tag,component,NXOpen.UF.UFMotion.ReferenceFrame.Absolute,out count,out values);xyz.Add(new {component=component.ToString(),count=count,values=values});
            }rows.Add(new {marker=marker.Name,coordinates=xyz});
        }return Json(new {ok=true,markers=rows,source="UF_MOTION_ask_trans_displacement_results native result series"});
    }
    private static string MotionJointResults()
    {
        var uf=NXOpen.UF.UFSession.GetUFSession();var rows=new List<object>();
        foreach(var joint in _part.MotionManager.Joints.ToArray()) {
            var angles=new List<object>();foreach(var angle in new NXOpen.UF.UFMotion.DispAngle[]{NXOpen.UF.UFMotion.DispAngle.EulerOne,NXOpen.UF.UFMotion.DispAngle.EulerTwo,NXOpen.UF.UFMotion.DispAngle.EulerThree}) {
                int count;double[] values;uf.Motion.AskRotDisplacementResults(joint.Tag,angle,NXOpen.UF.UFMotion.ReferenceFrame.SecondLink,out count,out values);angles.Add(new {angle=angle.ToString(),count=count,radians=values});
            }rows.Add(new {joint=joint.Name,angles=angles});
        }return Json(new {ok=true,joints=rows,source="UF_MOTION_ask_rot_displacement_results, SecondLink reference, 3-1-3 Euler radians"});
    }
    private static Vector3d P3Unit(Vector3d v) {double n=Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);if(n<1e-9)throw new Exception("Zero axis");return new Vector3d(v.X/n,v.Y/n,v.Z/n);}
    private static Vector3d P3Cross(Vector3d a,Vector3d b){return new Vector3d(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);}
    private static Point3d P3Point(Point3d o,Vector3d u,Vector3d v,double x,double y){return new Point3d(o.X+u.X*x+v.X*y,o.Y+u.Y*x+v.Y*y,o.Z+u.Z*x+v.Z*y);}
    private static string SpatialShape(string[] a)
    {
        if(_part==null || !(_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase3_",StringComparison.OrdinalIgnoreCase) || _part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_",StringComparison.OrdinalIgnoreCase)))throw new Exception("Spatial modeling requires phase3_ or phase4_ part");
        string name=a[1],kind=a[2],mode=a[12];double w=D(a[9]),h=D(a[10]),depth=D(a[11]);if(w<=0 || h<=0 || depth<=0)throw new Exception("Positive sizes required");
        var o=new Point3d(D(a[3]),D(a[4]),D(a[5]));var n=P3Unit(new Vector3d(D(a[6]),D(a[7]),D(a[8])));
        var u=P3Unit(P3Cross(Math.Abs(n.Z)<0.9?new Vector3d(0,0,1):new Vector3d(0,1,0),n));var v=P3Cross(n,u);
        var m=new Matrix3x3();m.Xx=u.X;m.Xy=u.Y;m.Xz=u.Z;m.Yx=v.X;m.Yy=v.Y;m.Yz=v.Z;m.Zx=n.X;m.Zy=n.Y;m.Zz=n.Z;
        Body target=a.Length>13 && a[13]!="-"?GetBody(a[13]):null;
        var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Spatial feature "+name);
        try {
            var sb=_part.Sketches.CreateSketchInPlaceBuilder2(null);Sketch sk;
            try {sb.PlaneReference=_part.Planes.CreateFixedTypePlane(o,m,SmartObject.UpdateOption.WithinModeling);sb.AxisReference=_part.Directions.CreateDirection(o,u,SmartObject.UpdateOption.WithinModeling);sb.SketchOrigin=_part.Points.CreatePoint(o);sb.PlaneOption=Sketch.PlaneOption.ExistingPlane;sb.OriginOption=OriginMethod.SpecifyPoint;sk=(Sketch)sb.Commit();}finally{sb.Destroy();}
            sk.SetName(name+"_profile");sk.Activate(Sketch.ViewReorient.True);var curves=new List<Curve>();
            if(kind=="cylinder")curves.Add(_part.Curves.CreateArc(o,u,v,w/2,0,2*Math.PI));
            else if(kind=="box") {double[] xs={-w/2,w/2,w/2,-w/2};double[] ys={-h/2,-h/2,h/2,h/2};for(int i=0;i<4;i++)curves.Add(_part.Curves.CreateLine(P3Point(o,u,v,xs[i],ys[i]),P3Point(o,u,v,xs[(i+1)%4],ys[(i+1)%4])));}
            else throw new Exception("Unknown profile");
            foreach(var curve in curves){sk.AddGeometry(curve,Sketch.InferConstraintsOption.InferNoConstraints);var g=new Sketch.ConstraintGeometry();g.Geometry=curve;g.PointType=Sketch.ConstraintPointType.None;sk.CreateFixedConstraint(g);}
            sk.Update();sk.Deactivate(Sketch.ViewReorient.True,Sketch.UpdateLevel.Model);
            var section=_part.Sections.CreateSection();section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveFeature(new Feature[]{sk.Feature})},null,null,null,o,Section.Mode.Create,false);
            var eb=_part.Features.CreateExtrudeBuilder(null);NXOpen.Features.Extrude f;
            try {eb.Section=section;eb.Direction=_part.Directions.CreateDirection(o,n,SmartObject.UpdateOption.WithinModeling);eb.Limits.StartExtend.Value.RightHandSide="0";eb.Limits.EndExtend.Value.RightHandSide=depth.ToString(CultureInfo.InvariantCulture);eb.BooleanOperation.Type=mode=="subtract"?BooleanOperation.BooleanType.Subtract:target==null?BooleanOperation.BooleanType.Create:BooleanOperation.BooleanType.Unite;if(target!=null)eb.BooleanOperation.SetTargetBodies(new Body[]{target});f=(NXOpen.Features.Extrude)eb.CommitFeature();f.SetName(name);}finally{eb.Destroy();}
            Body body=f.GetBodies()[0];string id="BODY_"+(++_bodyCounter);_bodies[id]=body;string sid="SKETCH_"+(++_sketchCounter);_sketches[sid]=sk;
            return Json(new {ok=true,body_id=id,sketch_id=sid,feature=f.GetFeatureName(),origin=o,axis=n});
        }catch(Exception e){Session.GetSession().UndoToMark(mark,null);return Json(new {ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
    }


