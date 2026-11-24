    // Read-only converged measurement; comparison thresholds are not changed.
    private static string AccurateVolume(string[] a)
    {
        if (_part==null) throw new Exception("No active part");
        double tol=D(a[1]); if(tol<0.9 || tol>1.0)throw new Exception("Integration accuracy must be 0.9..1.0");
        var rows=new List<object>();double total=0;
        foreach(Body b in _part.Bodies) {
            double[] acc=new double[11], mass=new double[47],stats=new double[13];acc[0]=tol;
            NXOpen.UF.UFSession.GetUFSession().Modl.AskMassProps3d(new Tag[]{b.Tag},1,1,4,1.0,1,acc,mass,stats);
            double volume=mass[1]*1e9; total+=volume;rows.Add(new {tag=b.Tag.ToString(),volume_mm3=volume,volume_range_error_mm3=stats[1]*1e9,statistics=stats});
        }
        return Json(new {ok=true,part=_part.FullPath,integration_accuracy=tol,volume_mm3=total,bodies=rows});
    }
    private static void MechanismPart() {Phase2Part();if(!_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_",StringComparison.OrdinalIgnoreCase))throw new Exception("New mechanism operations require phase4 model");}
    private static string ProfileSolid(string[] a)
    {
        MechanismPart();string file=Path.GetFullPath(a[2]);SafePath(file);if(!file.StartsWith(Path.GetDirectoryName(_part.FullPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Profile data must be beside current part");
        double z=D(a[3]),depth=D(a[4]);if(depth<=0)throw new Exception("Positive depth required");
        var rows=File.ReadAllLines(file);bool boolean=a[0]=="nx_profile_boolean";Body target=boolean?GetBody(a[6]):null;bool polygon=boolean?(a.Length>7 && a[7]=="polyline"):(a.Length>5 && a[5]=="polyline");if(rows.Length<(polygon?3:8) || rows.Length>4096)throw new Exception("Invalid profile point count");
        var points=new NXOpen.UF.UFCurve.PtSlopeCrvatr[rows.Length];
        for(int i=0;i<rows.Length;i++){var xy=rows[i].Split(',');if(xy.Length!=2)throw new Exception("Expected X,Y per row");points[i].point=new double[]{D(xy[0]),D(xy[1]),z};points[i].slope_type=-1;points[i].crvatr_type=-1;points[i].slope=new double[3];points[i].crvatr=new double[3];foreach(double v in points[i].point)if(double.IsNaN(v)||double.IsInfinity(v)||Math.Abs(v)>10000)throw new Exception("Invalid profile point");}
        var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Mechanism spline "+a[1]);
        try {
            var o=new Point3d(0,0,z);var m=new Matrix3x3();m.Xx=1;m.Yy=1;m.Zz=1;
            // UF spline defining data is the native profile; it is not presented as a constrained sketch.
            var curves=new List<Curve>();bool self=false;
            if(polygon){for(int i=0;i<points.Length;i++){var p=points[i].point;var q=points[(i+1)%points.Length].point;curves.Add(_part.Curves.CreateLine(new Point3d(p[0],p[1],p[2]),new Point3d(q[0],q[1],q[2])));}}
            else {Tag tag;var uf=NXOpen.UF.UFSession.GetUFSession();uf.Curve.CreateSplineThruPts(3,1,points.Length,points,null,1,out tag);uf.Curve.IsSplineSelfInt(tag,out self);if(self)throw new Exception("NX spline self-intersects");curves.Add((Curve)NXOpen.Utilities.NXObjectManager.Get(tag));}
            curves[0].SetName(a[1]+"_working_curve");var section=_part.Sections.CreateSection();section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(curves.ToArray())},curves[0],null,null,new Point3d(points[0].point[0],points[0].point[1],z),Section.Mode.Create,false);
            var eb=_part.Features.CreateExtrudeBuilder(null);NXOpen.Features.Extrude f;
            if(!boolean)eb.BooleanOperation.Type=BooleanOperation.BooleanType.Create;
            try{eb.Section=section;eb.Direction=_part.Directions.CreateDirection(o,new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);eb.Limits.StartExtend.Value.RightHandSide="0";eb.Limits.EndExtend.Value.RightHandSide=a[4];if(boolean){eb.BooleanOperation.Type=a[5]=="subtract"?BooleanOperation.BooleanType.Subtract:BooleanOperation.BooleanType.Unite;eb.BooleanOperation.SetTargetBodies(new Body[]{target});}f=(NXOpen.Features.Extrude)eb.CommitFeature();f.SetName(a[1]);}finally{eb.Destroy();}
            string id="BODY_"+(++_bodyCounter);_bodies[id]=f.GetBodies()[0];return Json(new{ok=true,body_id=id,feature=f.GetFeatureName(),profile_points=points.Length,profile_kind=polygon?"polyline":"periodic_cubic_spline",self_intersecting=self});
        }catch(Exception e){Session.GetSession().UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,stack=e.StackTrace,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
    }
    private static string CurvePairCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.CurveOnCurves.CreateCurveOnCurveBuilder(null);
        try{b.Name=a[1];b.SetICurve(new NXObject[]{P2Geometry(MotionComponent(a[2],_part.ComponentAssembly.RootComponent),a[3])});b.SetJCurve(new NXObject[]{P2Geometry(MotionComponent(a[4],_part.ComponentAssembly.RootComponent),a[5])});b.LockSlip=a[6]=="1";var obj=b.Commit();return Json(new{ok=true,name=obj.Name,lock_slip=b.LockSlip,first_curves=b.GetICurve().Length,second_curves=b.GetJCurve().Length});}finally{b.Destroy();}
    }
    private static string HigherPairState()
    {
        var rows=new List<object>();foreach(var x in _part.MotionManager.CurveOnCurves.ToArray()){var b=_part.MotionManager.CurveOnCurves.CreateCurveOnCurveBuilder(x);try{rows.Add(new{name=x.Name,type="CurveOnCurve",lock_slip=b.LockSlip,first_curve_count=b.GetICurve().Length,second_curve_count=b.GetJCurve().Length});}finally{b.Destroy();}}
        foreach(var x in _part.MotionManager.PointOnCurves.ToArray()){var b=_part.MotionManager.PointOnCurves.CreatePointOnCurveBuilder(x);try{rows.Add(new{name=x.Name,type="PointOnCurve",link=b.Link.Value.Name,curve_count=b.GetCurves().Length,point=b.Point.Coordinates,parameterization=b.CurveParameterizedType.ToString(),spacing=b.SpacingExpression.Value,spacing_unit=b.SpacingExpression.Units.TypeName});}finally{b.Destroy();}}
        return Json(new{ok=true,pairs=rows});
    }
    private static string CollisionVolumes(string[] a)
    {
        MechanismPart();if(_motionCheckPart!=_part.FullPath || _motionCheckPartTag!=_part.Tag || _motionPairs.Count==0)throw new Exception("Call collision setup");int frame=int.Parse(a[1]);var rows=new List<object>();var uf=NXOpen.UF.UFSession.GetUFSession();
        foreach(var pair in _motionPairs){int hit;uf.Motion.Interference(pair.tag,frame,out hit);if(hit==0)continue;
            var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Invisible,"Temporary native intersection inspection");
            try{int count;Tag[] tags;uf.Motion.CreateInterferenceBody(pair.tag,frame,NXOpen.UF.UFMotion.ReferenceFrame.Absolute,out count,out tags);double volume=0;var bodies=new List<object>();
                foreach(Tag tag in tags){var body=NXOpen.Utilities.NXObjectManager.Get(tag) as Body;if(body==null)throw new Exception("Intersection is not body");double[] mass=new double[47],stats=new double[13],acc=new double[11];acc[0]=1;uf.Modl.AskMassProps3d(new Tag[]{tag},1,1,4,1,1,acc,mass,stats);volume+=mass[1]*1e9;bodies.Add(new{solid=body.IsSolidBody,volume_mm3=mass[1]*1e9});}
                rows.Add(new{a=pair.a,b=pair.b,nx_interference=hit,body_count=count,volume_mm3=volume,bodies=bodies});
            }catch(NXException e){rows.Add(new{a=pair.a,b=pair.b,nx_interference=hit,error=e.Message,nx_error_code=e.ErrorCode});}
            finally{Session.GetSession().UndoToMark(mark,null);Session.GetSession().DeleteUndoMark(mark,null);}
        }
        return Json(new{ok=true,frame=frame,intersections=rows,source="Native UF_MOTION_create_interference_body; temporary bodies undone, original hits retained"});
    }
    private static string TightenSolution(string[] a)
    {
        MechanismPart();double value=D(a[2]);if(value<1e-12 || value>1e-6)throw new Exception("Only tighter kinematic accuracy 1e-12..1e-6 supported");
        var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(MotionSolutionByName(a[1]));
        try{var p=b.LmsSolverProperty;double oldA=p.AssemblyTolerance.Value,oldK=p.KinematicSolutionTolerance.Value;if(value>oldA || value>oldK)throw new Exception("Refusing to loosen solver accuracy");p.AssemblyTolerance.RightHandSide=a[2];p.KinematicSolutionTolerance.RightHandSide=a[2];b.Commit();return Json(new{ok=true,solution=a[1],before_assembly=oldA,before_kinematic=oldK,after_assembly=p.AssemblyTolerance.Value,after_kinematic=p.KinematicSolutionTolerance.Value});}finally{b.Destroy();}
    }

    private static string IntegratorRefine(string[] a)
    {
        MechanismPart();double tol=D(a[2]),step=D(a[3]);if(tol<1e-12 || tol>1e-6 || step<1e-6 || step>0.01)throw new Exception("Bounded integration refinement required");
        var b=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(MotionSolutionByName(a[1]));
        try{var p=b.LmsSolverProperty;double oldTol=p.DynamicIntegrationTolerance.Value,oldSol=p.DynamicSolutionTolerance.Value,oldStep=p.DynamicMaxIntegrationStep.Value;if(tol>oldTol || tol>oldSol || step>oldStep)throw new Exception("Refusing coarser integration");p.DynamicIntegrationTolerance.RightHandSide=a[2];p.DynamicSolutionTolerance.RightHandSide=a[2];p.DynamicMaxIntegrationStep.RightHandSide=a[3];b.Commit();return Json(new{ok=true,solution=a[1],before_integration_tolerance=oldTol,before_solution_tolerance=oldSol,before_max_step=oldStep,after_integration_tolerance=p.DynamicIntegrationTolerance.Value,after_solution_tolerance=p.DynamicSolutionTolerance.Value,after_max_step=p.DynamicMaxIntegrationStep.Value,environment_analysis_query="NX12 exposes SetAnalysisType without a matching getter; inspect generated native input"});}finally{b.Destroy();}
    }
    private static string PointCurveSampling(string[] a)
    {
        MechanismPart();NXOpen.Motion.PointOnCurve obj=null;foreach(NXOpen.Motion.PointOnCurve x in _part.MotionManager.PointOnCurves.ToArray())if(x.Name==a[1])obj=x;if(obj==null)throw new Exception("PointOnCurve not found");
        double spacing=D(a[2]);if(spacing<0.001 || spacing>1)throw new Exception("Spacing refinement bounded to 0.001..1 mm");
        var b=_part.MotionManager.PointOnCurves.CreatePointOnCurveBuilder(obj);
        try{double old=b.SpacingExpression.Value;string mode=b.CurveParameterizedType.ToString();if(b.CurveParameterizedType==NXOpen.Motion.CurveParameterizedTypes.Spacing && spacing>=old)throw new Exception("New spacing must be finer than current spacing");b.CurveParameterizedType=NXOpen.Motion.CurveParameterizedTypes.Spacing;b.SpacingExpression.RightHandSide=a[2];b.Commit();return Json(new{ok=true,name=obj.Name,before_parameterization=mode,before_spacing=old,after_spacing=b.SpacingExpression.Value,unit=b.SpacingExpression.Units.TypeName});}finally{b.Destroy();}
    }
    private static string PointCurveCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.PointOnCurves.CreatePointOnCurveBuilder(null);
        try{b.Name=a[1];b.Link.Value=MotionLinkByName(a[2]);b.Point=_part.Points.CreatePoint(new Point3d(D(a[3]),D(a[4]),D(a[5])));b.SetCurves(new NXObject[]{P2Geometry(MotionComponent(a[6],_part.ComponentAssembly.RootComponent),a[7])});var obj=b.Commit();return Json(new{ok=true,name=obj.Name,link=b.Link.Value.Name,curve_count=b.GetCurves().Length,point=b.Point.Coordinates});}finally{b.Destroy();}
    }

    private static string ProfileCurve(string[] a)
    {
        MechanismPart();SafePath(a[2]);string file=Path.GetFullPath(a[2]);if(!file.StartsWith(Path.GetDirectoryName(_part.FullPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Curve data must be beside part");
        var rows=File.ReadAllLines(file);if(rows.Length<8 || rows.Length>4096)throw new Exception("Invalid curve count");var points=new NXOpen.UF.UFCurve.PtSlopeCrvatr[rows.Length];
        for(int i=0;i<rows.Length;i++){var xy=rows[i].Split(',');points[i].point=new double[]{D(xy[0]),D(xy[1]),D(a[3])};points[i].slope_type=-1;points[i].crvatr_type=-1;points[i].slope=new double[3];points[i].crvatr=new double[3];}
        Tag tag;NXOpen.UF.UFSession.GetUFSession().Curve.CreateSplineThruPts(3,1,points.Length,points,null,1,out tag);var curve=(Curve)NXOpen.Utilities.NXObjectManager.Get(tag);curve.SetName(a[1]);return Json(new{ok=true,name=curve.Name,tag=tag.ToString(),point_count=points.Length});
    }
    private static string MechanismLine(string[] a)
    {
        MechanismPart();var p=new Point3d(D(a[2]),D(a[3]),D(a[4]));var q=new Point3d(D(a[5]),D(a[6]),D(a[7]));
        foreach(double v in new double[]{p.X,p.Y,p.Z,q.X,q.Y,q.Z})if(double.IsNaN(v)||double.IsInfinity(v)||Math.Abs(v)>10000)throw new Exception("Finite bounded line required");
        if(Math.Abs(p.X-q.X)+Math.Abs(p.Y-q.Y)+Math.Abs(p.Z-q.Z)<1e-8)throw new Exception("Distinct endpoints required");
        var curve=_part.Curves.CreateLine(p,q);curve.SetName(a[1]);return Json(new{ok=true,name=curve.Name,tag=curve.Tag.ToString(),start=p,end=q,source="Actual NX12 CurveCollection.CreateLine"});
    }
    private static string DriverVelocity(string[] a)
    {
        MechanismPart();foreach(var x in _part.MotionManager.JointDrivers.ToArray())if(x.Name==a[1]){var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(x);try{var d=b.DriverMultiOperations.MotionEulerAngle1;string before=d.VelocityExpression.RightHandSide;d.VelocityExpression.RightHandSide=a[2];b.Commit();return Json(new{ok=true,name=x.Name,before=before,after=d.VelocityExpression.RightHandSide});}finally{b.Destroy();}}throw new Exception("Driver not found");
    }
    private static string GearPairCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.Couplers.CreateCouplerGearBuilder(null);
        try{b.Name=a[1];b.FirstJoint.Value=MotionJointByName(a[2]);b.SecondJoint.Value=MotionJointByName(a[3]);b.FirstRadiusExpression.RightHandSide=a[4];b.SecondRadiusExpression.RightHandSide=a[5];b.ContactPoint=_part.Points.CreatePoint(new Point3d(D(a[6]),D(a[7]),D(a[8])));var obj=b.Commit();return Json(new{ok=true,name=obj.Name,first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_radius=b.FirstRadiusExpression.Value,second_radius=b.SecondRadiusExpression.Value,ratio=b.RatioExpression.Value});}finally{b.Destroy();}
    }
    private static string MotionAnalysisSelect(string[] a)
    {
        MechanismPart();if(a[1]!="kinematics" && a[1]!="dynamics")throw new Exception("Explicit kinematics or dynamics required");
        try{Session.GetSession().MotionSession.Environments.SetAnalysisType(a[1]=="dynamics"?NXOpen.Motion.MotionEnvironment.Analysis.Dynamics:NXOpen.Motion.MotionEnvironment.Analysis.Kinematics);return Json(new{ok=true,requested=a[1],setter_returned=true,scope="Setter result only; solver completion and physical data must be verified separately"});}catch(NXException e){return Json(new{ok=false,requested=a[1],nx_error_code=e.ErrorCode,error=e.Message});}
    }
    private static string SessionPartSafetyState()
    {
        var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
        var rows=new List<object>();bool anyModified=false,allKnown=true;
        foreach(var part in session.Parts.ToArray())
        {
            try{bool modified=uf.Part.IsModified(part.Tag);anyModified|=modified;
                rows.Add(new{path=part.FullPath,tag=part.Tag.ToString(),is_modified=modified,is_read_only=part.IsReadOnly,is_fully_loaded=part.IsFullyLoaded});}
            catch(Exception e){allKnown=false;rows.Add(new{path=part.FullPath,error=e.Message});}
        }
        return Json(new{ok=true,read_only_query=true,nx=NxVersion(),process_id=System.Diagnostics.Process.GetCurrentProcess().Id,bridge_location=System.Reflection.Assembly.GetExecutingAssembly().Location,process_temp_directory=Path.GetTempPath(),all_modified_flags_known=allKnown,any_modified=anyModified,parts=rows,scope="Actual loaded-part paths and UF_PART_is_modified; no saving, closing or relocation performed"});
    }
    private static string JointTorqueCreate(string[] a)
    {
        MechanismPart();double moment=D(a[3]);if(double.IsNaN(moment)||double.IsInfinity(moment)||Math.Abs(moment)>100000)throw new Exception("Bounded torque in N mm required");
        var b=_part.MotionManager.ScalarTorques.CreateScalarTorqueBuilder(null);
        try{b.Name=a[1];b.TorqueType=NXOpen.Motion.ScalarTorqueBuilder.ScalarTorqueType.Joint;b.Joint.Value=MotionJointByName(a[2]);b.ValueType=NXOpen.Motion.ForceValueTypes.Constant;b.ApplicationType=NXOpen.Motion.ScalarTorqueBuilder.AppType.ActionReaction;b.Direction=NXOpen.Motion.ScalarTorqueBuilder.DirectionType.Z;
            string unit=b.ValueExpression.Units.TypeName;double value;if(unit=="NewtonMilliMeter" || unit=="NewtonMillimeter")value=moment;else if(unit=="NewtonMeter")value=moment/1000;else throw new Exception("Unverified native torque unit "+unit+"; builder not committed");
            b.ValueExpression.RightHandSide=value.ToString(System.Globalization.CultureInfo.InvariantCulture);var obj=b.Commit();return Json(new{ok=true,name=obj.Name,joint=b.Joint.Value.Name,requested_N_mm=moment,native_value=b.ValueExpression.GetValueUsingUnits(NXOpen.Expression.UnitsOption.Expression),native_base_value=b.ValueExpression.Value,native_rhs=b.ValueExpression.RightHandSide,native_unit=unit,application=b.ApplicationType.ToString(),scope="Experimental constant joint preload; not a material spring or stress validation"});
        }finally{b.Destroy();}
    }
    private static string MotionContactApi()
    {
        MechanismPart();var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(null);
        try{var rows=new List<object>();foreach(var p in b.GetType().GetProperties())if(p.Name=="FirstContactGeometry" || p.Name=="SecondContactGeometry" || p.Name=="LmsParameters") {var methods=new List<string>();foreach(var method in p.PropertyType.GetMethods())methods.Add(method.ToString());rows.Add(new{name=p.Name,type=p.PropertyType.FullName,methods=methods});}return Json(new{ok=true,builder=b.GetType().FullName,properties=rows,committed=false,scope="Actual NX12 builder availability; no contact simulation accepted"});}finally{b.Destroy();}
    }
    private static object ContactExpression(NXOpen.Expression e) {return new{expression=e.RightHandSide,value_in_expression_units=e.GetValueUsingUnits(NXOpen.Expression.UnitsOption.Expression),expression_unit=e.Units==null?null:e.Units.TypeName,value_in_base_units=e.Value};}
    private static string BodyContactState(string[] a)
    {
        MechanismPart();var rows=new List<object>();foreach(var x in _part.MotionManager.BodyContacts.ToArray()){var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(x);try{var p=b.LmsParameters;rows.Add(new{name=x.Name,force_model=p.ForceModel.ToString(),stiffness=ContactExpression(p.Stiffness),damping=ContactExpression(p.MaterialDamping),exponent=ContactExpression(p.ForceExponent),max_penetration=ContactExpression(p.MaxPenetrationDepth)});}finally{b.Destroy();}}return Json(new{ok=true,contacts=rows,scope="NX Expression.Value is base-part units; expression units are read explicitly with GetValueUsingUnits"});
    }
    private static string BodyContactModelEdit(string[] a)
    {
        MechanismPart();foreach(var x in _part.MotionManager.BodyContacts.ToArray())if(x.Name==a[1]){var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(x);try{string before=b.LmsParameters.ForceModel.ToString();if(a[2]=="midplane")b.LmsParameters.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.Midplane;else if(a[2]=="projectedvertex")b.LmsParameters.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.ProjectedVertex;else throw new Exception("Explicit supported contact model required");b.Commit();return Json(new{ok=true,name=x.Name,before=before,after=b.LmsParameters.ForceModel.ToString()});}finally{b.Destroy();}}throw new Exception("Contact not found");
    }
    private static string MotionMassState()
    {
        MechanismPart();var rows=new List<object>();foreach(var link in _part.MotionManager.Links.ToArray()){var b=_part.MotionManager.Links.CreateLinkBuilder(link);try{var p=b.MassProperty;rows.Add(new{name=link.Name,fixed_link=b.Fixed,mass_type=p.MassType.ToString(),mass=p.MassExpression.Value,mass_unit=p.MassExpression.Units==null?null:p.MassExpression.Units.TypeName,ixx=p.IxxExpression.Value,iyy=p.IyyExpression.Value,izz=p.IzzExpression.Value,inertia_unit=p.IxxExpression.Units==null?null:p.IxxExpression.Units.TypeName,center=p.MassCenter==null?(object)null:p.MassCenter.Coordinates});}finally{b.Destroy();}}return Json(new{ok=true,links=rows});
    }
    private static string PreciseMotionMass(string[] a)
    {
        MechanismPart();if(a.Length!=3 || (a[2]!="0" && a[2]!="1"))throw new Exception("link apply_0_or_1 required");
        var wcs=_part.WCS.CoordinateSystem;var wm=wcs.Orientation.Element;var wo=wcs.Origin;
        if(Math.Abs(wo.X)+Math.Abs(wo.Y)+Math.Abs(wo.Z)+Math.Abs(wm.Xx-1)+Math.Abs(wm.Yy-1)+Math.Abs(wm.Zz-1)+Math.Abs(wm.Xy)+Math.Abs(wm.Xz)+Math.Abs(wm.Yx)+Math.Abs(wm.Yz)+Math.Abs(wm.Zx)+Math.Abs(wm.Zy)>1e-10)throw new Exception("Verified identity analysis WCS required");
        var b=_part.MotionManager.Links.CreateLinkBuilder(MotionLinkByName(a[1]));
        try{
            if(b.Fixed)throw new Exception("Moving link required");var tags=new List<Tag>();
            foreach(var item in b.Geometries.GetArray()){var c=item as NXOpen.Assemblies.Component;if(c==null)throw new Exception("Component geometry required");foreach(Body body in ((Part)c.Prototype).Bodies){var occ=c.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Body occurrence unavailable");tags.Add(occ.Tag);}}
            if(tags.Count==0)throw new Exception("No bodies");double[] lower=new double[47],higher=new double[47],sl=new double[13],sh=new double[13],acc=new double[11];
            var uf=NXOpen.UF.UFSession.GetUFSession();acc[0]=.99999;uf.Modl.AskMassProps3d(tags.ToArray(),tags.Count,1,4,1,1,acc,lower,sl);acc[0]=1;uf.Modl.AskMassProps3d(tags.ToArray(),tags.Count,1,4,1,1,acc,higher,sh);
            foreach(int i in new int[]{1,2,31,32,33})if(!(higher[i]>0) || Math.Abs(lower[i]-higher[i])>Math.Max(1e-14,Math.Abs(higher[i])*1e-7))throw new Exception("Mass/inertia integration has not converged");
            var p=b.MassProperty;var before=new{type=p.MassType.ToString(),mass=ContactExpression(p.MassExpression),ixx=ContactExpression(p.IxxExpression),iyy=ContactExpression(p.IyyExpression),izz=ContactExpression(p.IzzExpression)};
            if(a[2]=="1"){
                if(p.MassExpression.Units.TypeName!="Kilogram" || p.IxxExpression.Units.TypeName!="KilogramMilliMeterSquared")throw new Exception("Unverified native mass/inertia units");
                var x=P3Unit(new Vector3d(higher[22],higher[23],higher[24]));var y=P3Unit(new Vector3d(higher[25],higher[26],higher[27]));var z=P3Unit(P3Cross(x,y));
                if(Math.Abs(x.X*y.X+x.Y*y.Y+x.Z*y.Z)>1e-8 || Math.Abs(z.X*higher[28]+z.Y*higher[29]+z.Z*higher[30])<.99999999)throw new Exception("Invalid principal basis");
                var matrix=new Matrix3x3();matrix.Xx=x.X;matrix.Xy=x.Y;matrix.Xz=x.Z;matrix.Yx=y.X;matrix.Yy=y.Y;matrix.Yz=y.Z;matrix.Zx=z.X;matrix.Zy=z.Y;matrix.Zz=z.Z;
                var center=new Point3d(higher[3]*1000,higher[4]*1000,higher[5]*1000);p.MassType=NXOpen.Motion.LinkMassProperty.MassPropertyType.UserDefined;p.MassCenter=_part.Points.CreatePoint(center);p.InertiaCsys=_part.CoordinateSystems.CreateCoordinateSystem(center,_part.NXMatrices.Create(matrix),false);
                p.MassExpression.RightHandSide=higher[2].ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IxxExpression.RightHandSide=(higher[31]*1e6).ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IyyExpression.RightHandSide=(higher[32]*1e6).ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IzzExpression.RightHandSide=(higher[33]*1e6).ToString("R",System.Globalization.CultureInfo.InvariantCulture);p.IxyExpression.RightHandSide="0";p.IxzExpression.RightHandSide="0";p.IyzExpression.RightHandSide="0";b.Commit();
            }
            return Json(new{ok=true,link=a[1],applied=a[2]=="1",before=before,lower=lower,higher=higher,lower_statistics=sl,higher_statistics=sh,uf_units="kg,m; volume[1],mass[2],COM[3:5],principal_axes[22:30],principal_moments[31:33]",scope="Measured from real body occurrences with original body density; user-defined Motion values use converged native principal mass properties, not arbitrary calibration"});
        }finally{b.Destroy();}
    }
    private static string Phase4Density(string[] a)
    {
        MechanismPart();double rho=D(a[1]);if(rho<=0 || rho>30000)throw new Exception("Density kg/m^3 outside bounded material range");var rows=new List<object>();foreach(Body body in _part.Bodies){double before=body.Density;body.Density=rho;rows.Add(new{tag=body.Tag.ToString(),before_kg_m3=before,after_kg_m3=body.Density});}if(rows.Count==0)throw new Exception("No work-part bodies; do not modify assembly prototypes implicitly");return Json(new{ok=true,bodies=rows,source="NX12 Body.Density documented kg/m^3; explicit experimental material assumption"});
    }
    private static string AnalyticalSphereExtrusion(string[] a)
    {
        MechanismPart();if((a.Length!=14 && a.Length!=15) || !_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_finalization_",StringComparison.OrdinalIgnoreCase))throw new Exception("Analytical contact experiment requires finalization model and explicit geometry");
        if(a.Length==15 && a[14]!="0" && a[14]!="1")throw new Exception("Material side must be 0 or 1");
        double radius=D(a[6]),angle=D(a[10]);if(radius<=0 || radius>1000 || angle<0 || angle>180)throw new Exception("Invalid radius or partial-sphere angle");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Analytical contact experiment");
        string stage="create_property";var cleanupErrors=new List<string>();NXOpen.Motion.AnalyticalContactPropertyBuilder pb=null;NXOpen.Motion.AnalyticalContactBuilder b=null;
        try{
            pb=_part.MotionManager.AnalyticalContactPropertys.CreateAnalyticalContactPropertyBuilder(null);pb.Name=a[1]+"Material";
            foreach(var material in new[]{pb.FirstCustomizedMaterial,pb.SecondCustomizedMaterial}){material.IsCustomizedMaterial=true;material.Density.Value=7.8e-6;material.YoungModulus.Value=2e8;material.PoissonRatio.Value=.3;}
            pb.RestitutionCoefficient.RightHandSide="0.1";pb.FrictionCoefficientValue.RightHandSide="0";
            var property=(NXOpen.Motion.AnalyticalContactProperty)pb.Commit();
            stage="create_contact_builder";b=_part.MotionManager.AnalyticalContacts.CreateAnalyticalContactBuilder(null);b.Name=a[1];b.ContactType=NXOpen.Motion.AnalyticalContactBuilder.ContactTypeOption.SphereToExtrusion;b.ContactProperty.Value=property;
            b.SphereLink.Value=MotionLinkByName(a[2]);b.SpherePoint=_part.Points.CreatePoint(new Point3d(D(a[3]),D(a[4]),D(a[5])));b.SphereRadius.RightHandSide=a[6];b.ExtrusionLink.Value=MotionLinkByName(a[7]);
            var component=P2Component(a[8]);object sheetEvidence=null;
            if(a[9].StartsWith("sheet:",StringComparison.Ordinal)){
                var face=P2Geometry(component,a[9].Substring(6)) as Face;if(face==null)throw new Exception("Actual planar extrusion end face required");var edges=face.GetEdges();if(edges.Length<3)throw new Exception("Closed physical profile required");
                stage="sheet_copy_native_edge_curves";var curves=new List<Curve>();foreach(var edge in edges){Tag curveTag;NXOpen.UF.UFSession.GetUFSession().Modl.CreateCurveFromEdge(edge.Tag,out curveTag);curves.Add((Curve)NXOpen.Utilities.NXObjectManager.Get(curveTag));}Point3d helpA,helpB;edges[0].GetVertices(out helpA,out helpB);stage="sheet_section_from_native_curves";var section=_part.Sections.CreateSection();section.AddToSection(new SelectionIntentRule[]{_part.ScRuleFactory.CreateRuleCurveDumb(curves.ToArray())},curves[0],null,null,helpA,Section.Mode.Create,false);
                stage="sheet_extrude_builder";var extrude=_part.Features.CreateExtrudeBuilder(null);Body sheet;
                try{extrude.Section=section;extrude.FeatureOptions.BodyType=NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Sheet;extrude.Direction=_part.Directions.CreateDirection(new Point3d(0,0,27.6),new Vector3d(0,0,-1),SmartObject.UpdateOption.WithinModeling);extrude.Limits.StartExtend.Value.RightHandSide="0";extrude.Limits.EndExtend.Value.RightHandSide="7.2";extrude.BooleanOperation.Type=BooleanOperation.BooleanType.Create;stage="sheet_commit";var feature=(NXOpen.Features.BodyFeature)extrude.CommitFeature();feature.SetName(a[1]+"PhysicalProfileSheet");var bodies=feature.GetBodies();if(bodies.Length!=1 || bodies[0].IsSolidBody)throw new Exception("Expected one native sheet contact body");sheet=bodies[0];sheetEvidence=new{feature=feature.GetFeatureName(),source_face=a[9].Substring(6),source_edge_count=edges.Length,sheet_faces=sheet.GetFaces().Length,solid=sheet.IsSolidBody,depth_mm=7.2,scope="Native sheet extruded directly from actual wheel upper-face edges; original solid unchanged"};}finally{try{extrude.Destroy();}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}}
                stage="select_sheet";b.ExtrusionBodies.Add(sheet);
            }else if(a[9]=="body"){var bodies=((Part)component.Prototype).Bodies.ToArray();if(bodies.Length!=1)throw new Exception("Exactly one extrusion body required");b.ExtrusionBodies.Add((Body)component.FindOccurrence(bodies[0]));}else b.ExtrusionBodies.Add(P2Geometry(component,a[9]));
            b.ExtrusionGeometryData=false;b.InfiniteExtrusion=false;b.FindAllRoots=true;b.EffectiveContactDepth.RightHandSide="7.2";b.MaxTesselationLength.RightHandSide="0.1";if(a.Length==15)b.ExtrusionMaterialSide=a[14]=="1";
            b.PartialSphereOption=angle>0;if(angle>0){b.PartialSphereAngle.RightHandSide=a[10];b.PartialSphereVector=_part.Directions.CreateDirection(new Point3d(D(a[3]),D(a[4]),D(a[5])),new Vector3d(D(a[11]),D(a[12]),D(a[13])),SmartObject.UpdateOption.WithinModeling);}
            stage="contact_validate";bool valid=b.Validate();if(!valid)throw new Exception("Native analytical contact validation rejected supplied geometry");stage="contact_commit";var obj=b.Commit();
            return Json(new{ok=true,name=obj.Name,type=obj.GetType().Name,validated=valid,sphere_link=b.SphereLink.Value.Name,extrusion_link=b.ExtrusionLink.Value.Name,sheet_geometry=sheetEvidence,radius=ContactExpression(b.SphereRadius),partial_sphere=b.PartialSphereOption,partial_angle=ContactExpression(b.PartialSphereAngle),extrusion_material_side=b.ExtrusionMaterialSide,max_penetration_depth=ContactExpression(b.EffectiveContactDepth),young_modulus=ContactExpression(pb.FirstCustomizedMaterial.YoungModulus),density=ContactExpression(pb.FirstCustomizedMaterial.Density),poisson_ratio=pb.FirstCustomizedMaterial.PoissonRatio.Value,restitution=pb.RestitutionCoefficient.Value,scope="Experimental native sphere-to-extrusion analytical contact; spherical proxy and material assumptions must be declared separately from actual-body collision acceptance. Not a measured force or stress validation."});
        }catch(Exception e){
            if(b!=null){try{b.Destroy();}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}b=null;}
            if(pb!=null){try{pb.Destroy();}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}pb=null;}
            bool rolledBack=false;try{session.UndoToMark(mark,null);rolledBack=true;}catch(Exception cleanup){cleanupErrors.Add(cleanup.Message);}
            return Json(new{ok=false,error=e.Message,stage=stage,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=rolledBack,cleanup_errors=cleanupErrors});}
        finally{if(b!=null){try{b.Destroy();}catch{}}if(pb!=null){try{pb.Destroy();}catch{}}}
    }
    private static string MotionExportInput(string[] a)
    {
        MechanismPart();if(a.Length!=2 || !_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_finalization_",StringComparison.OrdinalIgnoreCase))throw new Exception("Export diagnostics require a fresh finalization model and solution name");
        string dir=Path.GetDirectoryName(_part.FullPath),stem=Path.GetFileNameWithoutExtension(_part.FullPath)+"-";
        foreach(string file in Directory.GetFiles(dir))if(Path.GetFileName(file).StartsWith(stem,StringComparison.OrdinalIgnoreCase))throw new Exception("Refusing to replace existing native solver diagnostics");
        var solution=MotionSolutionByName(a[1]);_part.MotionManager.MotionSolutions.SetActiveSolution(solution);solution.ExportSolverInputFile();
        var rows=new List<object>();foreach(string file in Directory.GetFiles(dir)){var info=new FileInfo(file);if(info.Name.StartsWith(stem,StringComparison.OrdinalIgnoreCase))rows.Add(new{path=info.FullName,bytes=info.Length});}
        return Json(new{ok=true,solution=solution.GetName(),files=rows,scope="Actual NX12 solver input export only; no solve or acceptance implied"});
    }
    private static string BodyContactCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(null);
        try{b.Name=a[1];b.ContactType=NXOpen.Motion.BodyContactBuilder.ContactTypeOption.CADToCADContact;
            var c1=MotionComponent(a[2],_part.ComponentAssembly.RootComponent);var c2=MotionComponent(a[3],_part.ComponentAssembly.RootComponent);if(c1==null || c2==null || c1==c2)throw new Exception("Two distinct actual components required");
            foreach(Body body in ((Part)c1.Prototype).Bodies){var occ=c1.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Contact body occurrence unavailable");b.FirstContactGeometry.Add(occ);}
            foreach(Body body in ((Part)c2.Prototype).Bodies){var occ=c2.FindOccurrence(body) as Body;if(occ==null)throw new Exception("Contact body occurrence unavailable");b.SecondContactGeometry.Add(occ);}
            var p=b.LmsParameters;p.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.ProjectedVertex;p.CoulombFrictionOption=NXOpen.Motion.BodyContactLms.CoulombFriction.Off;p.ForceExponent.RightHandSide="1";p.Stiffness.RightHandSide=a[4];p.MaterialDamping.RightHandSide=a[5];p.MaxPenetrationDepth.RightHandSide=a[6];p.ActionDistanceTolerance.RightHandSide="0.001";p.BaseDistanceTolerance.RightHandSide="0.001";p.ActionAngleTolerance.RightHandSide="1";p.BaseAngleTolerance.RightHandSide="1";
            var obj=b.Commit();return Json(new{ok=true,name=obj.Name,type=obj.GetType().Name,first_component=c1.Name,second_component=c2.Name,force_model=p.ForceModel.ToString(),exponent=p.ForceExponent.Value,stiffness=ContactExpression(p.Stiffness),damping=ContactExpression(p.MaterialDamping),max_penetration=ContactExpression(p.MaxPenetrationDepth),friction=p.CoulombFrictionOption.ToString(),scope="Penalty contact; creation is not contact-force or interference acceptance"});
        }finally{b.Destroy();}
    }
    private static string BodyFaceContactCreate(string[] a)
    {
        MechanismPart();if(a.Length!=9)throw new Exception("name component1 face_refs component2 face_refs stiffness damping depth required");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Experimental face contact "+a[1]);
        var b=_part.MotionManager.BodyContacts.CreateBodyContactBuilder(null);
        try{
            b.Name=a[1];b.ContactType=NXOpen.Motion.BodyContactBuilder.ContactTypeOption.CADToCADContact;
            var c1=P2Component(a[2]);var c2=P2Component(a[4]);if(c1==c2)throw new Exception("Distinct components required");
            var first=a[3].Split(',');var second=a[5].Split(',');
            foreach(string reference in first){var face=P2Geometry(c1,reference) as Face;if(face==null)throw new Exception("Actual face occurrence required");b.FirstContactGeometry.Add(face);}
            foreach(string reference in second){var face=P2Geometry(c2,reference) as Face;if(face==null)throw new Exception("Actual face occurrence required");b.SecondContactGeometry.Add(face);}
            var p=b.LmsParameters;p.ForceModel=NXOpen.Motion.BodyContactLms.ForceModelType.Midplane;p.CoulombFrictionOption=NXOpen.Motion.BodyContactLms.CoulombFriction.Off;
            p.ForceExponent.RightHandSide="1";p.Stiffness.RightHandSide=a[6];p.MaterialDamping.RightHandSide=a[7];p.MaxPenetrationDepth.RightHandSide=a[8];
            p.ActionDistanceTolerance.RightHandSide="0.001";p.BaseDistanceTolerance.RightHandSide="0.001";p.ActionAngleTolerance.RightHandSide="1";p.BaseAngleTolerance.RightHandSide="1";
            var obj=b.Commit();return Json(new{ok=true,name=obj.Name,first_component=c1.Name,second_component=c2.Name,first_face_references=first,second_face_references=second,force_model=p.ForceModel.ToString(),scope="Experimental actual Face selections; native solver support and full-body collision coverage must be independently verified"});
        }catch(Exception e){session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{b.Destroy();}
    }
    private static string RackPinionCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.Couplers.CreateCouplerRckpnBuilder(null);
        try{b.Name=a[1];b.FirstJoint.Value=MotionJointByName(a[3]);b.SecondJoint.Value=MotionJointByName(a[2]);b.FirstRadiusExpression.RightHandSide=a[4];b.SecondRadiusExpression.RightHandSide=a[4];b.RatioExpression.RightHandSide=(-D(a[4])).ToString(System.Globalization.CultureInfo.InvariantCulture);b.ContactPoint=_part.Points.CreatePoint(new Point3d(D(a[5]),D(a[6]),D(a[7])));var obj=b.Commit();return Json(new{ok=true,name=obj.Name,type=obj.GetType().Name,first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_radius=b.FirstRadiusExpression.Value,second_radius=b.SecondRadiusExpression.Value,ratio=b.RatioExpression.Value,contact=b.ContactPoint.Coordinates});}finally{b.Destroy();}
    }
    private static string MechanismCouplerState()
    {
        MechanismPart();var active=_part.MotionManager.MotionSolutions.GetActiveSolution();var rows=new List<object>();
        foreach(var obj in _part.MotionManager.Couplers.ToArray()){
            var rack=obj as NXOpen.Motion.CouplerRckpn;
            if(rack!=null){var rb=_part.MotionManager.Couplers.CreateCouplerRckpnBuilder(rack);try{rows.Add(new{name=obj.Name,type="RackPinion",first_joint=rb.FirstJoint.Value.Name,second_joint=rb.SecondJoint.Value.Name,ratio=rb.RatioExpression.Value,first_radius=rb.FirstRadiusExpression.Value,second_radius=rb.SecondRadiusExpression.Value,contact_point=rb.ContactPoint.Coordinates,suppressed=active.GetIsSuppressed(obj)});}finally{rb.Destroy();}continue;}
            var gear=obj as NXOpen.Motion.CouplerGear;if(gear==null){rows.Add(new{name=obj.Name,type=obj.GetType().Name,suppressed=active.GetIsSuppressed(obj)});continue;}
            var b=_part.MotionManager.Couplers.CreateCouplerGearBuilder(gear);try{rows.Add(new{name=obj.Name,type="Gear",first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_radius=b.FirstRadiusExpression.Value,second_radius=b.SecondRadiusExpression.Value,contact_point=b.ContactPoint.Coordinates,suppressed=active.GetIsSuppressed(obj)});}finally{b.Destroy();}
        }
        foreach(var obj in _part.MotionManager.GeneralCouplers.ToArray()){var b=_part.MotionManager.GeneralCouplers.CreateGeneralCouplerBuilder(obj);try{rows.Add(new{name=obj.Name,type="General",attachment=b.Attachment.ToString(),first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_type=b.FirstType.ToString(),second_type=b.SecondType.ToString(),first_scale=b.FirstScaleExpression.Value,second_scale=b.SecondScaleExpression.Value,suppressed=active.GetIsSuppressed(obj)});}finally{b.Destroy();}}
        var sb=_part.MotionManager.MotionSolutions.CreateSolutionBuilder(active);try{return Json(new{ok=true,active_solution=active.GetName(),couplers=rows,assembly_tolerance=sb.LmsSolverProperty.AssemblyTolerance.Value,kinematic_tolerance=sb.LmsSolverProperty.KinematicSolutionTolerance.Value,gruebler_dof=active.CalculateGrueblerCount()});}finally{sb.Destroy();}
    }
    private static string GeneralCouplerCreate(string[] a)
    {
        MechanismPart();var b=_part.MotionManager.GeneralCouplers.CreateGeneralCouplerBuilder(null);
        try{b.Name=a[1];b.Attachment=NXOpen.Motion.GeneralCouplerBuilder.AttachmentType.Two;b.FirstJoint.Value=MotionJointByName(a[2]);b.SecondJoint.Value=MotionJointByName(a[3]);b.FirstType=NXOpen.Motion.GcouplerMovementTypes.Rotation;b.SecondType=NXOpen.Motion.GcouplerMovementTypes.Rotation;b.FirstScaleExpression.RightHandSide=a[4];b.SecondScaleExpression.RightHandSide=a[5];var obj=b.Commit();return Json(new{ok=true,name=obj.Name,first_joint=b.FirstJoint.Value.Name,second_joint=b.SecondJoint.Value.Name,first_scale=b.FirstScaleExpression.Value,second_scale=b.SecondScaleExpression.Value});}finally{b.Destroy();}
    }
    private static string SphereSolid(string[] a)
    {
        MechanismPart();double diameter=D(a[5]);if(diameter<=0 || diameter>10000)throw new Exception("Positive bounded diameter required");var mark=Session.GetSession().SetUndoMark(Session.MarkVisibility.Visible,"Mechanism sphere");
        try{var b=_part.Features.CreateSphereBuilder(null);try{b.Type=NXOpen.Features.SphereBuilder.Types.CenterPointAndDiameter;b.CenterPoint=_part.Points.CreatePoint(new Point3d(D(a[2]),D(a[3]),D(a[4])));b.Diameter.RightHandSide=a[5];if(a.Length>7 && a[7]!="-"){b.BooleanOption.Type=a[6]=="subtract"?BooleanOperation.BooleanType.Subtract:BooleanOperation.BooleanType.Unite;b.BooleanOption.SetTargetBodies(new Body[]{GetBody(a[7])});}var feature=(NXOpen.Features.BodyFeature)b.CommitFeature();feature.SetName(a[1]);string id="BODY_"+(++_bodyCounter);_bodies[id]=feature.GetBodies()[0];return Json(new{ok=true,body_id=id,feature=feature.GetFeatureName(),diameter=diameter});}finally{b.Destroy();}}
        catch(Exception e){Session.GetSession().UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
    }
    private static string JointReference(string[] a)
    {
        MechanismPart();var p=new Point3d(D(a[2]),D(a[3]),D(a[4]));var axis=P3Unit(new Vector3d(D(a[5]),D(a[6]),D(a[7])));var point=_part.Points.CreatePoint(p);point.SetName(a[1]+"_P");point.SetVisibility(SmartObject.VisibilityOption.Visible);var line=_part.Curves.CreateLine(p,new Point3d(p.X+10*axis.X,p.Y+10*axis.Y,p.Z+10*axis.Z));line.SetName(a[1]+"_A");return Json(new{ok=true,point_name=point.Name,curve_name=line.Name,origin=p,axis=axis});
    }
    private static string ComponentDatums(string[] a)
    {
        var component=P2Component(a[1]);var part=(Part)component.Prototype;var points=new List<object>();var curves=new List<object>();foreach(Point p in part.Points)if(!String.IsNullOrEmpty(p.Name))points.Add(new{name=p.Name,coordinates=p.Coordinates,occurrence_available=component.FindOccurrence(p)!=null});foreach(Curve c in part.Curves)if(!String.IsNullOrEmpty(c.Name))curves.Add(new{name=c.Name,type=c.GetType().Name,occurrence_available=component.FindOccurrence(c)!=null});return Json(new{ok=true,component=component.Name,prototype_points=points,prototype_curves=curves});
    }
    private static string UniversalJointCreate(string[] a)
    {
        MechanismPart();var p=new Point3d(D(a[4]),D(a[5]),D(a[6]));var v=P3Unit(new Vector3d(D(a[7]),D(a[8]),D(a[9])));var w=P3Unit(new Vector3d(D(a[10]),D(a[11]),D(a[12])));if(Math.Abs(v.X*w.X+v.Y*w.Y+v.Z*w.Z)>1e-8)throw new Exception("Universal cross-pin axes must be orthogonal");var b=_part.MotionManager.Joints.CreateJointBuilder(null);
        try{var d=b.JointDefine;d.Name=a[1];d.JointType=NXOpen.Motion.JointDefine.Type.Universal;d.AxisType=NXOpen.Motion.JointDefine.UniversalJointAxisType.CrossPin;d.FirstLink=MotionLinkByName(a[2]);d.SecondLink=MotionLinkByName(a[3]);d.FirstOrigin=_part.Points.CreatePoint(p);d.SecondOrigin=_part.Points.CreatePoint(p);d.FirstOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;d.SecondOrientationType=NXOpen.Motion.JointDefine.OrientationType.Vector;d.FirstVector=_part.Directions.CreateDirection(p,v,SmartObject.UpdateOption.WithinModeling);d.SecondVector=_part.Directions.CreateDirection(p,w,SmartObject.UpdateOption.WithinModeling);d.SnapLinks=false;var obj=b.Commit();return Json(new{ok=true,name=obj.Name,type=d.JointType.ToString(),axis_type=d.AxisType.ToString(),first_axis=d.FirstVector.Vector,second_axis=d.SecondVector.Vector,origin=p});}finally{b.Destroy();}
    }
    private static object DriverOperationInfo(string coordinate,NXOpen.Motion.DriverOperation d)
    {
        if(d.TypeOption==NXOpen.Motion.DriverOperation.Type.Undefined)return new{coordinate=coordinate,type="Undefined"};
        return new {coordinate=coordinate,type=d.TypeOption.ToString(),integration=d.IntegrationTypeOption.ToString(),displacement=d.DisplacementExpression.RightHandSide,velocity=d.VelocityExpression.RightHandSide,velocity_unit=d.VelocityExpression.Units==null?null:d.VelocityExpression.Units.Name,harmonic_offset=d.HarmonicDisplacementExpression.RightHandSide,amplitude=d.AmplitudeExpression.RightHandSide,amplitude_unit=d.AmplitudeExpression.Units==null?null:d.AmplitudeExpression.Units.Name,frequency=d.FrequencyExpression.RightHandSide,frequency_unit=d.FrequencyExpression.Units==null?null:d.FrequencyExpression.Units.Name,phase=d.PhaseAngleExpression.RightHandSide,phase_unit=d.PhaseAngleExpression.Units==null?null:d.PhaseAngleExpression.Units.Name};
    }
    private static string HarmonicDriverCreate(string[] a)
    {
        MechanismPart();if(a.Length!=8 || (a[3]!="revolute" && a[3]!="slider") || !(D(a[4])>0) || !(D(a[5])>0))throw new Exception("name joint revolute|slider positive_amplitude positive_frequency phase offset required");
        var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(null);
        try{b.Name=a[1];b.Joint.Value=MotionJointByName(a[2]);var d=a[3]=="slider"?b.DriverMultiOperations.MotionTranslationZ:b.DriverMultiOperations.MotionEulerAngle1;d.TypeOption=NXOpen.Motion.DriverOperation.Type.Harmonic;d.IntegrationTypeOption=NXOpen.Motion.DriverOperation.IntegrationType.Displacement;d.AmplitudeExpression.RightHandSide=a[4];d.FrequencyExpression.RightHandSide=a[5];d.PhaseAngleExpression.RightHandSide=a[6];d.HarmonicDisplacementExpression.RightHandSide=a[7];var obj=b.Commit();return Json(new{ok=true,name=obj.Name,operation=DriverOperationInfo(a[3],d),note="Native harmonic input; validate expression units and actual result frequency before acceptance"});}finally{b.Destroy();}
    }
    private static string ResearchProfileDriver(string[] a)
    {
        MechanismPart();
        if((a.Length!=4 && a.Length!=5) || !_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_research_",StringComparison.OrdinalIgnoreCase))throw new Exception("Research profile driver requires name, joint, local CSV and optional linear|akima in a research model");
        if(a.Length==5 && a[4]!="linear" && a[4]!="akima")throw new Exception("Only native linear or Akima interpolation supported by this experiment");
        string file=Path.GetFullPath(a[3]);if(Path.GetDirectoryName(file)!=Path.GetDirectoryName(_part.FullPath) || Path.GetExtension(file)!=".csv")throw new Exception("Profile CSV must be beside the current simulation");
        var values=new List<double>();double last=-1;
        foreach(string line in File.ReadAllLines(file)){if(String.IsNullOrWhiteSpace(line))continue;var cells=line.Split(',');if(cells.Length!=2)throw new Exception("time,angle_deg rows required");double t=D(cells[0]),q=D(cells[1]);if(Double.IsNaN(t)||Double.IsInfinity(t)||Double.IsNaN(q)||Double.IsInfinity(q)||t<=last||t<0||Math.Abs(q)>10000)throw new Exception("Invalid bounded profile");last=t;values.Add(t);values.Add(q);}
        if(values.Count<6 || values.Count>200002 || values[0]!=0)throw new Exception("3..100001 samples starting at zero required");
        var session=Session.GetSession();var mark=session.SetUndoMark(Session.MarkVisibility.Visible,"Research ideal profile");NXOpen.Motion.JointDriverBuilder builder=null;
        try{
            var timeUnit=_part.UnitCollection.FindObject("Second");var angleUnit=_part.UnitCollection.FindObject("Degrees");
            var table=_part.FieldManager.CreateFieldTableFromData(a[1]+"_Table",timeUnit,angleUnit,NXOpen.Fields.FieldVariable.ValueType.Real,values.ToArray());table.InterpolationMethod=a.Length==5 && a[4]=="akima"?NXOpen.Fields.FieldEvaluator.InterpolationEnum.Akima1d:NXOpen.Fields.FieldEvaluator.InterpolationEnum.Linear1d;
            var field=_part.MotionManager.FieldDatas.CreateFieldData(table);
            builder=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(null);builder.Name=a[1];builder.Joint.Value=MotionJointByName(a[2]);var driver=builder.DriverMultiOperations.MotionEulerAngle1;
            driver.TypeOption=NXOpen.Motion.DriverOperation.Type.Profile;driver.IntegrationTypeOption=NXOpen.Motion.DriverOperation.IntegrationType.Displacement;driver.Profile.Value=field;
            bool valid=builder.Validate();if(!valid)throw new Exception("Native profile builder rejected input");var obj=builder.Commit();
            return Json(new{ok=true,name=obj.Name,validated=valid,type=driver.TypeOption.ToString(),joint=a[2],samples=values.Count/2,time_unit=timeUnit.TypeName,angle_unit=angleUnit.TypeName,interpolation=table.InterpolationMethod.ToString(),profile_type=driver.Profile.Value.GetType().FullName,scope="Prescribed ideal kinematic profile. No unilateral contact or force validation. Must cross-check actual native result independently."});
        }catch(Exception e){if(builder!=null){try{builder.Destroy();}catch{}builder=null;}session.UndoToMark(mark,null);return Json(new{ok=false,error=e.Message,nx_error_code=e is NXException?((NXException)e).ErrorCode:0,rolled_back=true});}
        finally{if(builder!=null)builder.Destroy();}
    }
    private static string ResearchFaceNormal(string[] a)
    {
        MechanismPart();if(a.Length!=6 || !_part.FullPath.StartsWith(@"@NX_LAB_ROOT@\models\phase4_research_",StringComparison.OrdinalIgnoreCase))throw new Exception("Read-only research face normal: component faceRef x y z");
        var face=P2Geometry(P2Component(a[1]),a[2]) as Face;if(face==null)throw new Exception("Face required");
        var uf=NXOpen.UF.UFSession.GetUFSession();double[] given={D(a[3]),D(a[4]),D(a[5])},uv=new double[2],nearest=new double[3],point=new double[3],u1=new double[3],v1=new double[3],u2=new double[3],v2=new double[3],normal=new double[3],radii=new double[2];
        uf.Modl.AskFaceParm(face.Tag,given,uv,nearest);uf.Modl.AskFaceProps(face.Tag,uv,point,u1,v1,u2,v2,normal,radii);
        double[] plus=new double[3],minus=new double[3];for(int i=0;i<3;i++){plus[i]=point[i]+0.001*normal[i];minus[i]=point[i]-0.001*normal[i];}
        int on,positive,negative;Tag body=face.GetBody().Tag;uf.Modl.AskPointContainment(point,body,out on);uf.Modl.AskPointContainment(plus,body,out positive);uf.Modl.AskPointContainment(minus,body,out negative);
        return Json(new{ok=true,read_only=true,component=a[1],reference=a[2],requested_point=given,surface_point=point,unit_face_normal=normal,u_derivative=u1,v_derivative=v1,point_on_body=on,plus_normal_point=plus,plus_normal_containment=positive,minus_normal_point=minus,minus_normal_containment=negative,containment_codes="1 inside; 2 outside; 3 on body",source="NX12 UF_MODL_ask_face_props and UF_MODL_ask_point_containment; no model mutation"});
    }
    private static string ResearchAnalyticalState()
    {
        MechanismPart();var rows=new List<object>();
        foreach(var contact in _part.MotionManager.AnalyticalContacts.ToArray()){
            var b=_part.MotionManager.AnalyticalContacts.CreateAnalyticalContactBuilder(contact);
            try{rows.Add(new{name=contact.Name,type=b.ContactType.ToString(),sphere_link=b.SphereLink.Value.Name,extrusion_link=b.ExtrusionLink.Value.Name,sphere_point=b.SpherePoint.Coordinates,sphere_radius=ContactExpression(b.SphereRadius),material_side=b.ExtrusionMaterialSide,partial_sphere=b.PartialSphereOption,partial_angle=ContactExpression(b.PartialSphereAngle),find_all_roots=b.FindAllRoots,max_penetration_depth=ContactExpression(b.EffectiveContactDepth),infinite_extrusion=b.InfiniteExtrusion,geometry_from_file=b.ExtrusionGeometryData,geometry_object_count=b.ExtrusionBodies.GetArray().Length});}
            finally{b.Destroy();}
        }
        return Json(new{ok=true,contacts=rows,scope="Read actual native analytical contact definitions; not force/normal convergence or acceptance"});
    }
    private static string MotionDriverDetails()
    {
        MechanismPart();var rows=new List<object>();foreach(var driver in _part.MotionManager.JointDrivers.ToArray()){var b=_part.MotionManager.JointDrivers.CreateJointDriverBuilder(driver);try{rows.Add(new{name=driver.Name,joint=((NXObject)b.Joint.Value).Name,rotational=DriverOperationInfo("rotation",b.DriverMultiOperations.MotionEulerAngle1),translational=DriverOperationInfo("translation",b.DriverMultiOperations.MotionTranslationZ)});}finally{b.Destroy();}}return Json(new{ok=true,drivers=rows});
    }
    private static string MotionSuppression(string[] a)
    {
        MechanismPart();NXObject obj=null;foreach(var x in _part.MotionManager.Couplers.ToArray())if(x.Name==a[2])obj=x;foreach(var x in _part.MotionManager.JointDrivers.ToArray())if(x.Name==a[2])obj=x;if(obj==null)throw new Exception("Named coupler or driver not found");
        var solution=MotionSolutionByName(a[1]);if(a[3]=="1")solution.AddSuppressedObject(obj);else solution.RemoveSuppressedObject(obj);
        return Json(new{ok=true,solution=solution.GetName(),object_name=obj.Name,suppressed=solution.GetIsSuppressed(obj),gruebler_dof=solution.CalculateGrueblerCount()});
    }
