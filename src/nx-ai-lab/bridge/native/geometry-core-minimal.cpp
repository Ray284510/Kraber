
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <set>
#include <cmath>
#include <stdexcept>
#include <uf.h>
#include <uf_part.h>
#include <uf_obj.h>
#include <uf_object_types.h>
#include <uf_modl.h>
#include <uf_eval.h>
#include <uf_brep.h>
void check(int r,const char* a){printf("api=%s rc=%d\n",a,r);if(r)throw std::runtime_error(a);}
std::set<tag_t> edges;
void walk(UF_BREP_topology_t* n,int parent,int orientation,int depth,bool offset=false){
 if(!n||depth>8||n->num_children>32)throw std::runtime_error("topology bounds");
 int id;UF_BREP_ask_identifier(n,&id);
 printf("node=%d parent=%d type=%d tag=%u orientation=%d children=%d\n",id,parent,int(n->type),n->tag,orientation,n->num_children);
 if(n->type==UF_BREP_FACE_TOPO){int kind=0;check(UF_MODL_ask_face_type(n->tag,&kind),"face_type");offset=(kind==UF_MODL_OFFSET_FACE);}
 if(n->type==UF_BREP_EDGE_TOPO&&offset)edges.insert(n->tag);
 for(int i=0;i<n->num_children;i++)walk(n->children[i].child,id,n->children[i].orientation,depth+1,offset);
}
int inventory(tag_t p){int n=0;tag_t t=0;while((t=UF_OBJ_cycle_all(p,t))!=0){if(++n>100000)throw std::runtime_error("inventory");}return n;}
void examine(tag_t edge){
 double v[3]{},z[3]{};int vertices=0;
 check(UF_MODL_ask_edge_verts(edge,v,z,&vertices),"vertices");
 printf("edge=%u vertices=%d first=%.17g,%.17g,%.17g second=%.17g,%.17g,%.17g use_domain=unknown phase=unknown geometry_sense=unknown\n",edge,vertices,v[0],v[1],v[2],z[0],z[1],z[2]);
 UF_EVAL_p_t a=nullptr,b=nullptr;
 check(UF_EVAL_initialize(edge,&a),"initialize_approximate");
 try{
 check(UF_EVAL_initialize_2(edge,&b),"initialize_2");
 double da[2],db[2];check(UF_EVAL_ask_limits(a,da),"limits_old");check(UF_EVAL_ask_limits(b,db),"limits_native");
 UF_EVAL_spline_t info{};check(UF_EVAL_ask_spline(a,&info),"spline");
 int nk=0;double* knots=nullptr;check(UF_EVAL_ask_spline_knots(a,&nk,&knots),"knots");
 double first=da[0];for(int i=0;i<nk;i++)if(knots[i]>da[0]&&knots[i]<da[1]){first=knots[i];break;}
 printf("edge=%u evaluator_old=%.17g,%.17g evaluator_native=%.17g,%.17g spline_active=%.17g,%.17g periodic=%d\n",edge,da[0],da[1],db[0],db[1],knots[info.order-1],knots[info.num_control],info.is_periodic);
 UF_free(knots);printf("free_knots=success\n");
 double parameters[]={da[0],first,(da[0]+first)/2};
 for(double t:parameters){
 double p[3],q[3],d[3],nt=0;int ps=0,qs=0;
 check(UF_EVAL_evaluate(a,1,t,p,d),"evaluate_old");
 check(UF_EVAL_evaluate_closest_point(b,p,&nt,q),"project_native");
 check(UF_EVAL_evaluate(b,1,nt,q,d),"evaluate_native");
 check(UF_MODL_ask_point_containment(p,edge,&ps),"containment_old");
 check(UF_MODL_ask_point_containment(q,edge,&qs),"containment_native");
 double gap=0;for(int i=0;i<3;i++)gap+=(p[i]-q[i])*(p[i]-q[i]);
 printf("sample edge=%u old_t=%.17g native_t=%.17g old_status=%d native_status=%d distance=%.17g old_xyz=%.17g,%.17g,%.17g native_xyz=%.17g,%.17g,%.17g\n",edge,t,nt,ps,qs,std::sqrt(gap),p[0],p[1],p[2],q[0],q[1],q[2]);
 }
 check(UF_EVAL_free(b),"free_native");b=nullptr;
 check(UF_EVAL_free(a),"free_old");a=nullptr;
 }catch(...){if(b)UF_EVAL_free(b);if(a)UF_EVAL_free(a);throw;}
}
int main(int argc,char** argv){
 if(argc!=2)return 80;setvbuf(stdout,nullptr,_IONBF,0);
 printf("pid=%lu fixture=%s\n",GetCurrentProcessId(),argv[1]);
 try{
 check(UF_initialize(),"initialize");tag_t part=0;UF_PART_load_status_t status{};
 check(UF_PART_open(argv[1],&part,&status),"open");check(UF_PART_free_load_status(&status),"free_load");
 int before=inventory(part),modified=UF_PART_is_modified(part);
 printf("objects_before=%d modified_before=%d\n",before,modified);
 tag_t body=0,t=0;
 while((t=UF_OBJ_cycle_all(part,t))!=0){int ty,st;check(UF_OBJ_ask_type_and_subtype(t,&ty,&st),"type");if(ty==UF_solid_type&&st==0){body=t;break;}}
 if(!body)throw std::runtime_error("body");
 int opt=UF_BREP_FINSP_OPTION,count=0;double val=1;
 UF_BREP_options_t options{1,&opt,&val};UF_BREP_topology_t* graph=nullptr;UF_BREP_state_t* states=nullptr;
 check(UF_BREP_ask_topology(body,&options,&graph,&count,&states),"topology");if(states)UF_free(states);
 try{walk(graph,0,0,0);}catch(...){UF_BREP_release_topology(graph,nullptr);throw;}
 check(UF_BREP_release_topology(graph,nullptr),"release_topology");
 if(edges.size()!=2)throw std::runtime_error("two Offset incident edges required before evaluation");
 int selected=0;
 for(tag_t e:edges){double box[6];check(UF_MODL_ask_bounding_box(e,box),"box");
 if(std::abs(box[3]-box[0])<1e-5&&(std::abs(box[0])<1e-5||std::abs(box[0]-180)<1e-5)){selected++;examine(e);}}
 int after=inventory(part),changed=UF_PART_is_modified(part);
 printf("selected=%d objects_after=%d modified_after=%d\n",selected,after,changed);
 if(selected!=2||before!=after||modified||changed)throw std::runtime_error("postcondition");
 check(UF_PART_close(part,0,2),"close_no_save");check(UF_terminate(),"terminate");
 printf("terminal=SUCCESS_NO_SAVE\n");return 0;
 }catch(const std::exception& e){printf("terminal=FAILED reason=%s\n",e.what());return 1;}
}
