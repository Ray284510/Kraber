// P04 bounded Face trim transition diagnostic. No inference of native FinUse intervals.
// Reuses P03 sampling and accepted P01 strict selector/metric. At most 36 queries in one external NX process.
#define main geometry_core_p02_unused_main
#include "geometry-core-p01.cpp"
#undef main
#include <fstream>
#include <sstream>
struct Request03{int id;tag_t edge,face;int native_chart;double t,uv[2],xyz[3],derivative[3],duv[2];};
int sample03(const Request03& r,Eval& ref){
 double parameter=0,closest[3],q[3],dq[6];
 check(UF_EVAL_evaluate_closest_point(ref.p,const_cast<double*>(r.xyz),&parameter,closest),"reference_closest");
 check(UF_EVAL_evaluate(ref.p,2,parameter,q,dq),"reference_evaluate");
 int sf=0,se=0,tf=0,te=0;
 check(UF_MODL_ask_point_containment(const_cast<double*>(r.xyz),r.face,&sf),"source_face_classification");
 check(UF_MODL_ask_point_containment(const_cast<double*>(r.xyz),r.edge,&se),"source_edge_classification");
 check(UF_MODL_ask_point_containment(q,r.face,&tf),"target_face_classification");
 check(UF_MODL_ask_point_containment(q,r.edge,&te),"target_edge_classification");
 double source_inverse_uv[2],source_projected[3],target_uv[2],target_face[3];
 check(UF_MODL_ask_face_parm_2(r.face,const_cast<double*>(r.xyz),source_inverse_uv,source_projected),"source_face_inverse");
 check(UF_MODL_ask_face_parm_2(r.face,q,target_uv,target_face),"target_face_inverse");
 double fp[3]{},fu[3]{},fv[3]{},fuu[3]{},fvv[3]{},normal[3]{},radii[2]{},chain[3]{};bool evaluated=false;
 if(r.native_chart){
  double box[4];check(UF_MODL_ask_face_uv_minmax(r.face,box),"face_uv_limits");
  if(r.uv[0]>=box[0]&&r.uv[0]<=box[1]&&r.uv[1]>=box[2]&&r.uv[1]<=box[3]){
   check(UF_MODL_ask_face_props(r.face,const_cast<double*>(r.uv),fp,fu,fv,fuu,fvv,normal,radii),"finsp_own_native_face_evaluate");
   for(int i=0;i<3;i++)chain[i]=fu[i]*r.duv[0]+fv[i]*r.duv[1];evaluated=true;
  }
 }
 cout<<"{\"kind\":\"fin_reference\",\"id\":"<<r.id<<",\"edge\":"<<r.edge<<",\"face\":"<<r.face<<",\"topology_incidence\":true,\"source_t\":"<<r.t<<",\"source_uv\":";arr(r.uv,2);
 cout<<",\"source_xyz\":";arr(r.xyz,3);cout<<",\"source_derivative\":";arr(r.derivative,3);cout<<",\"target_t\":"<<parameter<<",\"closest_target_xyz\":";arr(closest,3);cout<<",\"target_xyz\":";arr(q,3);cout<<",\"target_derivatives\":";arr(dq,6);
 cout<<",\"source_face_status\":"<<sf<<",\"source_edge_status\":"<<se<<",\"target_face_status\":"<<tf<<",\"target_edge_status\":"<<te<<",\"source_inverse_uv\":";arr(source_inverse_uv,2);cout<<",\"source_face_projection_xyz\":";arr(source_projected,3);cout<<",\"target_face_uv\":";arr(target_uv,2);cout<<",\"target_face_projection_xyz\":";arr(target_face,3);
 cout<<",\"distance_mm\":"<<::euclidean_distance_mm(r.xyz,q)<<",\"closest_evaluate_residual_mm\":"<<::euclidean_distance_mm(closest,q)<<",\"native_face_evaluated\":"<<(evaluated?"true":"false")<<",\"native_face_lift_xyz\":";
 if(evaluated)arr(fp,3);else cout<<"null";
 cout<<",\"native_face_chain_derivative\":";if(evaluated)arr(chain,3);else cout<<"null";
 cout<<",\"native_face_lift_residual_mm\":";if(evaluated)cout<<::euclidean_distance_mm(fp,r.xyz);else cout<<"null";
 cout<<",\"source_origin\":\"P04_current_payload_or_explicit_non_FINSP_control\",\"closest_status\":\"single_native_candidate_uniqueness_unproved\"}\n";
 unchanged("after_fin_reference");return sf;
}
int main(int argc,char** argv){
 cout<<setprecision(17)<<unitbuf;if(argc!=4)return 80;
 cout<<"{\"kind\":\"start\",\"pid\":"<<GetCurrentProcessId()<<"}\n";
 try{
 vector<Request03> requests;ifstream in(argv[2]);Request03 r{};
 while(in>>r.id>>r.edge>>r.face>>r.native_chart>>r.t>>r.uv[0]>>r.uv[1]>>r.xyz[0]>>r.xyz[1]>>r.xyz[2]>>r.derivative[0]>>r.derivative[1]>>r.derivative[2]>>r.duv[0]>>r.duv[1])requests.push_back(r);
 if(!in.eof()||requests.size()!=264)throw runtime_error("request file count or format");
 for(size_t i=0;i<requests.size();i++)if(requests[i].id!=int(i))throw runtime_error("lookup identity");
 check(UF_initialize(),"initialize");char module[MAX_PATH]{};if(!GetModuleFileNameA(GetModuleHandleA("libufun.dll"),module,MAX_PATH))throw runtime_error("module identity");string mp(module);for(auto& c:mp)if(c=='\\')c='/';cout<<"{\"kind\":\"loaded_module\",\"path\":\""<<mp<<"\"}\n";
 UF_PART_load_status_t load{};check(UF_PART_open(argv[1],&part,&load),"open");check(UF_PART_free_load_status(&load),"free_load");auto before=inventory();emit_inventory(before,"before");unchanged("before");
 tag_t body=0;for(tag_t t:before){int ty=0,st=0;check(UF_OBJ_ask_type_and_subtype(t,&ty,&st),"object_type");if(ty==UF_solid_type&&st==0){if(body)throw runtime_error("multiple bodies");body=t;}}if(!body)throw runtime_error("body");
 int option=UF_BREP_FINSP_OPTION,count=0;double value=1;UF_BREP_options_t opts{1,&option,&value};UF_BREP_topology_t* graph=nullptr;UF_BREP_state_t* states=nullptr;
 check(UF_BREP_ask_topology(body,&opts,&graph,&count,&states),"topology");if(states)UF_free(states);cout<<"{\"kind\":\"topology_states\",\"count\":"<<count<<",\"success_output_released\":true}\n";if(count)throw runtime_error("topology states");
 walk(graph,{},-1,0);auto selected=select_edges();set<int> nodes;
 for(auto e:selected)for(const auto& u:uses[e]){nodes.insert(u.path[2].id);nodes.insert(u.path[4].id);}
 if(nodes.size()!=7)throw runtime_error("four fins three faces");
 // Verify every request incidence BEFORE any evaluator initialization.
 for(const auto& request:requests){bool found=false;for(auto e:selected)if(e==request.edge)for(const auto& u:uses[e])if(u.face==request.face)found=true;if(!found)throw runtime_error("request incidence");}
 // No ask_geometry calls: P03 exact payloads are reused; topology is queried only for strict identity/incidence.
 check(UF_BREP_release_topology(graph,nullptr),"release_topology");unchanged("after_brep");
 map<tag_t,Eval> handles;
 try{
  for(auto edge:selected){Eval e;check(UF_EVAL_initialize_2(edge,&e.p),"initialize_2");handles.emplace(edge,e);auto& h=handles.at(edge);check(UF_EVAL_ask_limits(h.p,h.limits),"reference_limits");check(UF_EVAL_is_periodic(h.p,&h.periodic),"reference_periodic");logical spline=false;check(UF_EVAL_is_spline(h.p,&spline),"reference_is_spline");if(spline||!h.periodic)throw runtime_error("procedural fixture identity");cout<<"{\"kind\":\"reference_metadata\",\"edge\":"<<edge<<",\"is_spline\":"<<spline<<",\"periodic\":"<<h.periodic<<",\"evaluator_domain\":";arr(h.limits,2);cout<<",\"spline_active_domain\":null}\n";}
  
  for(int group=0;group<4;group++){
   int left=group*65,right=left+64;
   int ls=sample03(requests[left],handles.at(requests[left].edge));
   int rs=sample03(requests[right],handles.at(requests[right].edge));
   if(ls!=2||rs!=3)throw runtime_error("historical transition endpoints not reproduced; stop without replay");
   int steps=0;bool interrupted=false;
   for(;steps<6;steps++){
    int mid=(left+right)/2;
    int ms=sample03(requests[mid],handles.at(requests[mid].edge));
    cout<<"{\"kind\":\"transition_step\",\"group\":"<<group<<",\"step\":"<<steps<<",\"left_id\":"<<left<<",\"right_id\":"<<right<<",\"mid_id\":"<<mid<<",\"mid_face_status\":"<<ms<<"}\n";
    if(ms==2)left=mid;else if(ms==3)right=mid;else{interrupted=true;steps++;break;}
   }
   cout<<"{\"kind\":\"sampled_transition_bracket\",\"group\":"<<group<<",\"left_id\":"<<left<<",\"right_id\":"<<right<<",\"left_t\":"<<requests[left].t<<",\"right_t\":"<<requests[right].t<<",\"steps\":"<<steps<<",\"unexpected_intermediate_status\":"<<(interrupted?"true":"false")<<",\"native_fin_use_interval\":null,\"monotonicity\":\"unproved\"}\n";
  }
  for(int i=260;i<264;i++)sample03(requests[i],handles.at(requests[i].edge));
  for(auto& h:handles){check(UF_EVAL_free(h.second.p),"free_reference");h.second.p=nullptr;}
 }catch(...){for(auto& h:handles)if(h.second.p)cout<<"{\"kind\":\"cleanup_reference\",\"rc\":"<<UF_EVAL_free(h.second.p)<<"}\n";throw;}
 auto after=inventory();emit_inventory(after,"after");unchanged("after");if(before!=after)throw runtime_error("inventory mismatch");check(UF_PART_close(part,0,2),"close_no_save");check(UF_terminate(),"terminate");cout<<"{\"kind\":\"terminal\",\"status\":\"SUCCESS_NO_SAVE\"}\n";return 0;
 }catch(const exception& e){cout<<"{\"kind\":\"terminal\",\"status\":\"FAILED_NO_REPLAY\",\"reason\":\""<<e.what()<<"\"}\n";return 1;}
}
