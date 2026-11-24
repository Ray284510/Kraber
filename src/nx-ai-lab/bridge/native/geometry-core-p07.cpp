// P07: one isolated process, 10 Edge positions / 20 owner-Face queries.
// Wait for bounded offline candidates, then evaluate reverse closest witnesses.
#define main geometry_core_p01_unused_main
#include "geometry-core-p01.cpp"
#undef main
#include <fstream>
#include <sstream>
int main(int argc,char** argv){
 cout<<setprecision(17)<<unitbuf;if(argc!=3)return 80;
 map<tag_t,Eval> handles;UF_BREP_topology_t* graph=nullptr;bool initialized=false,opened=false;
 cout<<"{\"kind\":\"start\",\"pid\":"<<GetCurrentProcessId()<<"}\n";
 try{
  check(UF_initialize(),"initialize");initialized=true;
  char mp[MAX_PATH]{};if(!GetModuleFileNameA(GetModuleHandleA("libufun.dll"),mp,MAX_PATH))throw runtime_error("NX module");string module(mp);for(auto& c:module)if(c=='\\')c='/';cout<<"{\"kind\":\"module\",\"path\":\""<<module<<"\"}\n";
  UF_PART_load_status_t load{};int open_rc=UF_PART_open(argv[1],&part,&load);check(UF_PART_free_load_status(&load),"free_load_status");check(open_rc,"open");opened=true;
  auto before=inventory();emit_inventory(before,"before");unchanged("before");tag_t body=0;
  for(auto t:before){int ty=0,st=0;check(UF_OBJ_ask_type_and_subtype(t,&ty,&st),"object_type");if(ty==UF_solid_type&&st==0){if(body)throw runtime_error("multiple bodies");body=t;}}
  if(!body)throw runtime_error("no body");int option=UF_BREP_FINSP_OPTION,count=0;double value=1;UF_BREP_options_t opts{1,&option,&value};UF_BREP_state_t* states=nullptr;UF_BREP_topology_t* output=nullptr;
  int tr=UF_BREP_ask_topology(body,&opts,&output,&count,&states);check(tr,"topology");graph=output;
  if(states)UF_free(states);cout<<"{\"kind\":\"topology_states\",\"count\":"<<count<<",\"release\":\"UF_free_returned\"}\n";if(count)throw runtime_error("topology states");
  walk(graph,{},-1,0);auto selected=select_edges();
  for(auto edge:selected){set<tag_t> faces;for(auto& u:uses[edge])faces.insert(u.face);set<tag_t> expected{26691u,edge==26695?26693u:26692u};if(faces!=expected)throw runtime_error("owner Face mismatch");}
  check(UF_BREP_release_topology(graph,nullptr),"release_topology");graph=nullptr;unchanged("after_topology");
  for(auto edge:selected){Eval e;check(UF_EVAL_initialize_2(edge,&e.p),"initialize_2");handles.emplace(edge,e);auto& h=handles.at(edge);
   check(UF_EVAL_ask_limits(h.p,h.limits),"limits");check(UF_EVAL_is_periodic(h.p,&h.periodic),"periodic");logical spline=false;check(UF_EVAL_is_spline(h.p,&spline),"is_spline");if(spline||!h.periodic)throw runtime_error("reference identity");
   cout<<"{\"kind\":\"reference_metadata\",\"edge\":"<<edge<<",\"domain\":";arr(h.limits,2);cout<<",\"periodic\":true,\"is_spline\":false,\"spline_active_domain\":null}\n";
  }
  int qid=0;
  for(auto edge:selected){auto& h=handles.at(edge);
   for(int pos=0;pos<5;pos++){
    double t=h.limits[0]+(h.limits[1]-h.limits[0])*pos/4.;double xyz[3],der[6];check(UF_EVAL_evaluate(h.p,2,t,xyz,der),"edge_evaluate");
    for(auto& u:uses[edge]){
     double uv[2]{},fp[3]{},box[4]{},period[2]{};int periods[2]{};int raw=0,support=0,uvstate=0,edgestate=0;
     int rc=UF_MODL_ask_face_parm_2(u.face,xyz,uv,fp);cout<<"{\"kind\":\"api\",\"api\":\"ask_face_parm_2\",\"query_id\":"<<qid<<",\"rc\":"<<rc<<"}\n";
     if(rc){cout<<"{\"kind\":\"edge_face\",\"id\":"<<qid++<<",\"edge\":"<<edge<<",\"face\":"<<u.face<<",\"rc\":"<<rc<<",\"status\":\"UNRESOLVED_API_FAILURE\"}\n";continue;}
     check(UF_MODL_ask_face_uv_minmax(u.face,box),"face_uv_box");check(UF_MODL_ask_face_periodicity(u.face,&periods[0],&period[0],&periods[1],&period[1]),"face_periodicity");
     check(UF_MODL_ask_point_containment(xyz,u.face,&raw),"raw_face_classification");check(UF_MODL_ask_point_containment(fp,u.face,&support),"support_face_classification");
     check(UF_MODL_ask_uv_points_containment(1,&uv[0],&uv[1],u.face,&uvstate),"uv_classification");check(UF_MODL_ask_point_containment(xyz,edge,&edgestate),"edge_classification");
     bool in_box=uv[0]>=box[0]&&uv[0]<=box[1]&&uv[1]>=box[2]&&uv[1]<=box[3];double lift[3]{},du[3]{},dv[3]{},duu[3]{},dvv[3]{},normal[3]{},radii[2]{};
     if(in_box)check(UF_MODL_ask_face_props(u.face,uv,lift,du,dv,duu,dvv,normal,radii),"face_uv_forward");
     cout<<"{\"kind\":\"edge_face\",\"id\":"<<qid++<<",\"edge\":"<<edge<<",\"face\":"<<u.face<<",\"sample_index\":"<<pos<<",\"winding_endpoint\":"<<(pos==4?1:0)<<",\"edge_t\":"<<t<<",\"edge_xyz\":";arr(xyz,3);cout<<",\"edge_derivatives\":";arr(der,6);cout<<",\"uv\":";arr(uv,2);cout<<",\"face_projected_xyz\":";arr(fp,3);
     cout<<",\"face_box\":";arr(box,4);cout<<",\"periods\":";arr(period,2);cout<<",\"period_status\":["<<periods[0]<<','<<periods[1]<<"],\"raw_face_status\":"<<raw<<",\"support_face_status\":"<<support<<",\"uv_status\":"<<uvstate<<",\"edge_status\":"<<edgestate<<",\"projection_residual_mm\":"<<::euclidean_distance_mm(xyz,fp)<<",\"rc\":0,\"topology_path\":";path_json(u.path);
     cout<<",\"face_lift_xyz\":";if(in_box)arr(lift,3);else cout<<"null";cout<<",\"face_lift_residual_mm\":";if(in_box)cout<<::euclidean_distance_mm(fp,lift);else cout<<"null";
     cout<<",\"support_query_semantics\":\"returned_underlying_surface_candidate_not_proven_unique_or_trim_inside\"}\n";unchanged("after_edge_face");
    }
   }
  }
  if(qid!=20)throw runtime_error("query count");cout<<"{\"kind\":\"offline_ready\",\"queries\":20}\n";
  const ULONGLONG started=GetTickCount64();while(GetFileAttributesA(argv[2])==INVALID_FILE_ATTRIBUTES){if(GetTickCount64()-started>1200000)throw runtime_error("offline candidate timeout; no replay");Sleep(100);}
  ifstream input(argv[2]);int n=0;if(!(input>>n)||n<0||n>6000)throw runtime_error("reverse candidate limit");
  for(int i=0;i<n;i++){
   int id=0,source_query=0;tag_t edge=0;double fin_t=0,xyz[3];if(!(input>>id>>source_query>>edge>>fin_t>>xyz[0]>>xyz[1]>>xyz[2])||id!=i||!handles.count(edge))throw runtime_error("reverse format");
   double parameter=0,closest[3],evaluated[3],d[6];auto& h=handles.at(edge);
   check(UF_EVAL_evaluate_closest_point(h.p,xyz,&parameter,closest),"reverse_closest");check(UF_EVAL_evaluate(h.p,2,parameter,evaluated,d),"reverse_evaluate");
   cout<<"{\"kind\":\"reverse\",\"id\":"<<id<<",\"source_query\":"<<source_query<<",\"edge\":"<<edge<<",\"fin_t\":"<<fin_t<<",\"source_xyz\":";arr(xyz,3);cout<<",\"target_t\":"<<parameter<<",\"closest_target_xyz\":";arr(closest,3);cout<<",\"target_xyz\":";arr(evaluated,3);cout<<",\"target_derivatives\":";arr(d,6);cout<<",\"distance_mm\":"<<::euclidean_distance_mm(xyz,evaluated)<<",\"closest_residual_mm\":"<<::euclidean_distance_mm(closest,evaluated)<<",\"uniqueness\":\"UNKNOWN\",\"rc\":0}\n";
  }
  string extra;if(input>>extra)throw runtime_error("extra reverse input");unchanged("after_reverse");
  for(auto& h:handles){check(UF_EVAL_free(h.second.p),"free_reference");h.second.p=nullptr;}
  auto after=inventory();emit_inventory(after,"after");unchanged("after");if(before!=after)throw runtime_error("object inventory mismatch");
  check(UF_PART_close(part,0,2),"close_no_save");opened=false;check(UF_terminate(),"terminate");initialized=false;
  cout<<"{\"kind\":\"terminal\",\"status\":\"SUCCESS_NO_SAVE\",\"edge_face_queries\":20,\"reverse_queries\":"<<n<<"}\n";return 0;
 }catch(const exception& e){
  for(auto& h:handles)if(h.second.p)cout<<"{\"kind\":\"cleanup\",\"resource\":\"evaluator\",\"rc\":"<<UF_EVAL_free(h.second.p)<<"}\n";
  if(graph)cout<<"{\"kind\":\"cleanup\",\"resource\":\"known_successful_topology\",\"rc\":"<<UF_BREP_release_topology(graph,nullptr)<<"}\n";
  if(opened)cout<<"{\"kind\":\"cleanup\",\"resource\":\"close_no_save\",\"rc\":"<<UF_PART_close(part,0,2)<<"}\n";
  if(initialized)cout<<"{\"kind\":\"cleanup\",\"resource\":\"terminate\",\"rc\":"<<UF_terminate()<<"}\n";
  cout<<"{\"kind\":\"terminal\",\"status\":\"FAILED_NO_REPLAY\",\"reason\":\""<<e.what()<<"\"}\n";return 1;
 }
}
