// One-shot external NX 12 read-only diagnostic. No geometry edits or saves.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <uf.h>
#include <uf_part.h>
#include <uf_obj.h>
#include <uf_object_types.h>
#include <uf_modl.h>
#include <uf_eval.h>
#include <uf_brep.h>
#include <iostream>
#include <iomanip>
#include <vector>
#include <map>
#include <set>
#include <array>
#include <cmath>
#include <stdexcept>
#include <string>
using namespace std;
void check(int r,const char* api){cout<<"{\"kind\":\"api\",\"api\":\""<<api<<"\",\"rc\":"<<r<<"}\n";if(r)throw runtime_error(api);}
void arr(const double* p,int n){cout<<'[';for(int i=0;i<n;i++){if(i)cout<<',';cout<<p[i];}cout<<']';}
double euclidean_distance_mm(const double* a,const double* b){double d=0;for(int i=0;i<3;i++)d+=(a[i]-b[i])*(a[i]-b[i]);return sqrt(d);}
tag_t part=0;
void unchanged(const char* stage){int m=UF_PART_is_modified(part);cout<<"{\"kind\":\"modified\",\"stage\":\""<<stage<<"\",\"value\":"<<m<<"}\n";if(m)throw runtime_error("part modified");}
set<tag_t> inventory(){set<tag_t> s;tag_t t=0;while((t=UF_OBJ_cycle_all(part,t))!=0){if(!s.insert(t).second||s.size()>100000)throw runtime_error("inventory cycle");}return s;}
void emit_inventory(const set<tag_t>& s,const char* stage){cout<<"{\"kind\":\"inventory\",\"stage\":\""<<stage<<"\",\"tags\":[";bool comma=false;for(auto t:s){if(comma)cout<<',';cout<<t;comma=true;}cout<<"]}\n";}
struct Step{int id,type,index,orientation;tag_t tag;};
struct Use{vector<Step> path;tag_t face;int face_type;};
map<tag_t,vector<Use>> uses;
void path_json(const vector<Step>& p){cout<<'[';for(size_t i=0;i<p.size();i++){if(i)cout<<',';auto s=p[i];cout<<"{\"id\":"<<s.id<<",\"type\":"<<s.type<<",\"tag\":"<<s.tag<<",\"child_index\":"<<s.index<<",\"orientation\":"<<s.orientation<<'}';}cout<<']';}
void walk(UF_BREP_topology_t* n,vector<Step> path,int index,int orientation,tag_t face=0,int face_type=-1){
 if(!n||path.size()>8||n->num_children<0||n->num_children>32)throw runtime_error("topology bounds");
 int id=0;UF_BREP_ask_identifier(n,&id);path.push_back({id,int(n->type),index,orientation,n->tag});
 if(n->type==UF_BREP_FACE_TOPO){face=n->tag;check(UF_MODL_ask_face_type(face,&face_type),"face_type");}
 cout<<"{\"kind\":\"topology_use\",\"path\":";path_json(path);cout<<",\"face\":"<<face<<",\"face_type\":"<<face_type<<",\"children\":"<<n->num_children<<"}\n";
 if(n->type==UF_BREP_EDGE_TOPO)uses[n->tag].push_back({path,face,face_type});
 for(int i=0;i<n->num_children;i++)walk(n->children[i].child,path,i,n->children[i].orientation,face,face_type);
}
vector<tag_t> select_edges(){
 vector<tag_t> selected;set<string> roles;set<tag_t> offset_faces;
 // Finish ALL identity checks before initializing either evaluator.
 for(auto& pair:uses){
  tag_t e=pair.first;int offsets=0,planes=0;set<tag_t> faces;
  for(const auto& u:pair.second){faces.insert(u.face);if(u.face_type==UF_MODL_OFFSET_FACE){offsets++;offset_faces.insert(u.face);}if(u.face_type==UF_MODL_PLANAR_FACE)planes++;}
  double box[6];check(UF_MODL_ask_bounding_box(e,box),"edge_bbox");
  string role="other";if(abs(box[3]-box[0])<1e-5){if(abs(box[0])<1e-5)role="xmin";else if(abs(box[0]-180)<1e-5)role="xmax";}
  bool candidate=offsets>0;
  cout<<"{\"kind\":\"candidate\",\"edge\":"<<e<<",\"role\":\""<<role<<"\",\"offset_uses\":"<<offsets<<",\"plane_uses\":"<<planes<<",\"bbox\":";arr(box,6);
  cout<<",\"uses\":[";for(size_t i=0;i<pair.second.size();i++){if(i)cout<<',';path_json(pair.second[i].path);}cout<<"],\"offset_candidate\":"<<(candidate?"true":"false")<<"}\n";
  if(!candidate)continue;
  if(offsets!=1||planes!=1||faces.size()!=2||pair.second.size()!=2||role=="other")throw runtime_error("adjacency identity mismatch");
  // Frozen identical-hash fixture: require old topology use paths, not only tags.
  const int plane_node=role=="xmin"?137:140,loop_node=role=="xmin"?182:181,fin_node=role=="xmin"?132:180;
  bool offset_path=false,plane_path=false;
  for(const auto& u:pair.second){const auto& p=u.path;if(p.size()!=6||p[0].id!=114||p[1].id!=126||p[2].type!=5||p[3].type!=6||p[4].type!=7||p[5].type!=8)throw runtime_error("use hierarchy mismatch");
   if(u.face_type==65)offset_path=p[2].id==142&&p[3].id==loop_node&&p[4].id==fin_node&&p[4].orientation==1&&p[5].orientation==1;
   if(u.face_type==22)plane_path=p[2].id==plane_node&&p[4].orientation==-1&&p[5].orientation==-1;
  }
  if(!offset_path||!plane_path||e!=(role=="xmin"?26698u:26695u)||!roles.insert(role).second)throw runtime_error("historical identity mismatch");
  selected.push_back(e);
  cout<<"{\"kind\":\"identity\",\"edge\":"<<e<<",\"historical_edge\":"<<(role=="xmin"?44490:44487)<<",\"role\":\""<<role<<"\",\"proof\":\"same fixture hash enforced by runner; complete topology path; Offset and plane adjacency; bounding box role\",\"edge_use_domain\":null,\"fin_use_domain\":null,\"periodic_phase\":null,\"geometric_sense\":null}\n";
 }
 if(selected.size()!=2||roles.size()!=2||offset_faces.size()!=1)throw runtime_error("exact two identities required");
 cout<<"{\"kind\":\"selector_gate\",\"pass\":true,\"count\":2}\n";return selected;
}
struct Eval{UF_EVAL_p_t p=nullptr;double limits[2]{};vector<double> knots;int order=0,controls=0;logical periodic=false;};
void describe(Eval& e,tag_t tag,const char* method){
 check(UF_EVAL_ask_limits(e.p,e.limits),"limits");logical periodic=false,spline=false;
 check(UF_EVAL_is_periodic(e.p,&periodic),"periodic");check(UF_EVAL_is_spline(e.p,&spline),"is_spline");
 e.periodic=periodic;
 UF_EVAL_spline_t s{};int rc=UF_EVAL_ask_spline(e.p,&s);
 cout<<"{\"kind\":\"evaluator\",\"edge\":"<<tag<<",\"method\":\""<<method<<"\",\"limits\":";arr(e.limits,2);
 cout<<",\"is_periodic\":"<<periodic<<",\"is_spline\":"<<spline<<",\"ask_spline_rc\":"<<rc<<"}\n";
 if(!spline){if(rc==0)throw runtime_error("inconsistent spline query");cout<<"{\"kind\":\"spline_unavailable\",\"edge\":"<<tag<<",\"method\":\""<<method<<"\",\"knots\":null,\"active_domain\":null,\"reason\":\"native evaluator is not spline; knot API requires spline; no other evaluator data substituted\"}\n";return;}
 check(rc,"ask_spline");e.order=s.order;e.controls=s.num_control;
 int n=0;double* k=nullptr;int kr=UF_EVAL_ask_spline_knots(e.p,&n,&k);
 if(kr){cout<<"{\"kind\":\"unknown_output_ownership\",\"api\":\"ask_spline_knots\",\"rc\":"<<kr<<"}\n";throw runtime_error("knots ownership isolated");}
 if(!k||n!=s.num_knots||s.order<1||s.order>n||s.num_control>=n){if(k)UF_free(k);throw runtime_error("invalid spline metadata");}
 try{e.knots.assign(k,k+n);}catch(...){UF_free(k);throw;}
 UF_free(k);cout<<"{\"kind\":\"release\",\"resource\":\"knots\",\"edge\":"<<tag<<",\"method\":\""<<method<<"\",\"status\":\"UF_free_returned\"}\n";
 cout<<"{\"kind\":\"spline\",\"edge\":"<<tag<<",\"method\":\""<<method<<"\",\"order\":"<<s.order<<",\"controls\":"<<s.num_control<<",\"rational\":"<<s.is_rational<<",\"periodic\":"<<s.is_periodic<<",\"active_domain\":["<<e.knots[s.order-1]<<','<<e.knots[s.num_control]<<"],\"knots\":";arr(e.knots.data(),n);cout<<"}\n";
}
void witness(tag_t edge,Eval& from,Eval& to,const char* direction,const char* label,double t){
 double p[3],dp[6],q[3],dq[6],projected[3],back[3],rt=0,u=0;int ps=0,qs=0;
 check(UF_EVAL_evaluate(from.p,2,t,p,dp),"evaluate_source");
 check(UF_EVAL_evaluate_closest_point(to.p,p,&u,projected),"closest_target");
 check(UF_EVAL_evaluate(to.p,2,u,q,dq),"evaluate_target");
 check(UF_EVAL_evaluate_closest_point(from.p,q,&rt,back),"closest_return");
 check(UF_MODL_ask_point_containment(p,edge,&ps),"containment_source");check(UF_MODL_ask_point_containment(q,edge,&qs),"containment_target");
 const double parameter_delta=rt-t,period=from.limits[1]-from.limits[0];
 if(!from.periodic||!std::isfinite(period)||period<=0)throw runtime_error("periodic fixture invariant");
 const double parameter_folded=parameter_delta-std::round(parameter_delta/period)*period;
 cout<<"{\"kind\":\"witness\",\"edge\":"<<edge<<",\"direction\":\""<<direction<<"\",\"label\":\""<<label<<"\",\"source_t\":"<<t<<",\"target_t\":"<<u<<",\"return_t\":"<<rt<<",\"source_xyz\":";arr(p,3);cout<<",\"target_xyz\":";arr(q,3);cout<<",\"return_xyz\":";arr(back,3);cout<<",\"source_derivatives\":";arr(dp,6);cout<<",\"target_derivatives\":";arr(dq,6);
 cout<<",\"closest_target_xyz\":";arr(projected,3);cout<<",\"source_containment\":"<<ps<<",\"target_containment\":"<<qs<<",\"forward_distance_mm\":"<<::euclidean_distance_mm(p,q)<<",\"reverse_distance_mm\":"<<::euclidean_distance_mm(q,back)<<",\"roundtrip_spatial_mm\":"<<::euclidean_distance_mm(p,back)<<",\"closest_evaluate_residual_mm\":"<<::euclidean_distance_mm(projected,q)<<",\"parameter_roundtrip_raw\":"<<parameter_delta<<",\"source_evaluator_period\":"<<period<<",\"parameter_roundtrip_periodic\":"<<parameter_folded<<",\"periodic_fold_method\":\"delta-round(delta/period)*period; ties away from zero\",\"multi_solution_status\":\"unknown_single_native_candidate\",\"parameter_map\":\"local_native_projection_not_assumed_linear\"}\n";
 unchanged("after_witness");
}
void examine(tag_t edge){
 double v[3]{},w[3]{};int n=0;check(UF_MODL_ask_edge_verts(edge,v,w,&n),"vertices");cout<<"{\"kind\":\"vertices\",\"edge\":"<<edge<<",\"count\":"<<n<<",\"first\":";if(n>0)arr(v,3);else cout<<"null";cout<<",\"second\":";if(n>1)arr(w,3);else cout<<"null";cout<<"}\n";
 Eval a,b;check(UF_EVAL_initialize(edge,&a.p),"initialize_approximate");
 try{
 check(UF_EVAL_initialize_2(edge,&b.p),"initialize_2");describe(a,edge,"initialize");describe(b,edge,"initialize_2");
 vector<double> internal;for(double k:a.knots)if(k>a.limits[0]&&k<a.limits[1]&&(internal.empty()||k!=internal.back()))internal.push_back(k);
 if(internal.size()<2)throw runtime_error("two old knots required");
 vector<pair<string,double>> old={{"start",a.limits[0]},{"end",a.limits[1]},{"knot1",internal[0]},{"knot2",internal[1]},{"first_span_midpoint",(a.limits[0]+internal[0])/2}};
 vector<pair<string,double>> native={{"start",b.limits[0]},{"end",b.limits[1]},{"domain_midpoint",(b.limits[0]+b.limits[1])/2}};
 for(auto s:old)witness(edge,a,b,"old_to_native",s.first.c_str(),s.second);
 for(auto s:native)witness(edge,b,a,"native_to_old",s.first.c_str(),s.second);
 check(UF_EVAL_free(b.p),"free_native");b.p=nullptr;check(UF_EVAL_free(a.p),"free_old");a.p=nullptr;
 }catch(...){if(b.p)cout<<"{\"kind\":\"cleanup_native\",\"rc\":"<<UF_EVAL_free(b.p)<<"}\n";if(a.p)cout<<"{\"kind\":\"cleanup_old\",\"rc\":"<<UF_EVAL_free(a.p)<<"}\n";throw;}
}
int main(int argc,char** argv){
 cout<<setprecision(17)<<unitbuf;if(argc!=2)return 80;
 cout<<"{\"kind\":\"start\",\"pid\":"<<GetCurrentProcessId()<<"}\n";
 try{
 check(UF_initialize(),"initialize");
 char module[MAX_PATH]{};if(!GetModuleFileNameA(GetModuleHandleA("libufun.dll"),module,MAX_PATH))throw runtime_error("loaded module identity");
 string module_path(module);for(char& c:module_path)if(c=='\\')c='/';
 cout<<"{\"kind\":\"loaded_module\",\"path\":\""<<module_path<<"\"}\n";
 UF_PART_load_status_t load{};check(UF_PART_open(argv[1],&part,&load),"open");check(UF_PART_free_load_status(&load),"free_load");
 auto before=inventory();emit_inventory(before,"before");unchanged("before");tag_t body=0;
 for(auto t:before){int ty=0,st=0;int rc=UF_OBJ_ask_type_and_subtype(t,&ty,&st);if(rc)check(rc,"object_type");if(ty==UF_solid_type&&st==0){if(body)throw runtime_error("multiple bodies");body=t;}}
 if(!body)throw runtime_error("missing body");
 int opt=UF_BREP_FINSP_OPTION,count=0;double val=1;UF_BREP_options_t options{1,&opt,&val};UF_BREP_topology_t* graph=nullptr;UF_BREP_state_t* states=nullptr;
 check(UF_BREP_ask_topology(body,&options,&graph,&count,&states),"topology");
 for(int i=0;i<count;i++)cout<<"{\"kind\":\"topology_state\",\"code\":"<<states[i].state_code<<",\"flag\":"<<states[i].flag<<",\"tag\":"<<states[i].ug_tag<<"}\n";
 if(states)UF_free(states);cout<<"{\"kind\":\"release\",\"resource\":\"topology_states\",\"count\":"<<count<<",\"status\":\"known_success_output_released_or_null\"}\n";
 try{walk(graph,{},-1,0);}catch(...){cout<<"{\"kind\":\"cleanup_topology\",\"rc\":"<<UF_BREP_release_topology(graph,nullptr)<<"}\n";throw;}
 check(UF_BREP_release_topology(graph,nullptr),"release_topology");
 auto selected=select_edges();unchanged("selector_gate");for(auto e:selected)examine(e);
 auto after=inventory();emit_inventory(after,"after");unchanged("after");if(before!=after)throw runtime_error("inventory mismatch");
 check(UF_PART_close(part,0,2),"close_no_save");check(UF_terminate(),"terminate");cout<<"{\"kind\":\"terminal\",\"status\":\"SUCCESS_NO_SAVE\"}\n";return 0;
 }catch(const exception& e){cout<<"{\"kind\":\"terminal\",\"status\":\"FAILED_NO_REPLAY\",\"reason\":\""<<e.what()<<"\"}\n";return 1;}
}
