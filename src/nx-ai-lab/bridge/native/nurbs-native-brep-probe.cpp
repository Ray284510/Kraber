// One read-only UF_BREP graph traversal. Compiled against the installed NX 12 headers.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <uf.h>
#include <uf_brep.h>
#include <uf_obj.h>
static_assert(sizeof(tag_t)==4 && sizeof(void*)==8 && sizeof(UF_BREP_topology_t)==40, "NX12 x64 layout mismatch");
namespace {
struct Log {
    HANDLE file;
    explicit Log(const char* path):file(CreateFileA(path,GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_WRITE_THROUGH,nullptr)){}
    ~Log(){if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);}
    bool write(const char* fmt,...) {
        if(file==INVALID_HANDLE_VALUE)return false;
        char buf[512];va_list ap;va_start(ap,fmt);int n=vsnprintf(buf,sizeof(buf),fmt,ap);va_end(ap);
        DWORD written=0;
        return n>0&&n<sizeof(buf)&&WriteFile(file,buf,static_cast<DWORD>(n),&written,nullptr)&&written==static_cast<DWORD>(n)&&FlushFileBuffers(file);
    }
};
bool walk(const UF_BREP_topology_t* node, int depth, int& visits, Log& log) {
    if(!node||depth>12||++visits>256||node->num_children<0||node->num_children>64||
       (node->num_children&&!node->children))return false;
    int id=0;UF_BREP_ask_identifier(node,&id);
    if(!log.write("node depth=%d id=%d type=%d tag=%u child_count=%d\n",depth,id,static_cast<int>(node->type),static_cast<unsigned>(node->tag),node->num_children))return false;
    for(int i=0;i<node->num_children;++i) {
        const auto& c=node->children[i];
        if(!log.write("use parent=%d index=%d orientation=%d\n",id,i,c.orientation)||!walk(c.child,depth+1,visits,log))return false;
    }
    return true;
}
}
extern "C" __declspec(dllexport) int __cdecl nx12_brep_topology_once(unsigned face,const char* path) {
    Log log(path);if(!log.write("before pid=%lu face=%u option=FINSP value=1\n",GetCurrentProcessId(),face))return 100;
    if(!UF_is_initialized()){log.write("UF not initialized; no topology call\n");return 101;}
    int opt=UF_BREP_FINSP_OPTION;double value=1.;UF_BREP_options_t options{1,&opt,&value};
    UF_BREP_topology_t* graph=nullptr;
    // State diagnostics deliberately omitted: installed header permits null output pointer.
    int states=0;int rc=UF_BREP_ask_topology(face,&options,&graph,&states,nullptr);
    bool logged=log.write("after ask_topology rc=%d graph_present=%d states=%d\n",rc,graph!=nullptr,states);
    if(rc||!graph){log.write("quarantine: no traversal or retry\n");return 102;}
    int count=0;bool traversed=logged&&walk(graph,0,count,log);
    log.write("before release visited=%d traversal_ok=%d\n",count,traversed);
    int freed=UF_BREP_release_topology(graph,nullptr);
    bool released=log.write("after release rc=%d\n",freed);
    return traversed&&!freed&&released?0:103;
}
