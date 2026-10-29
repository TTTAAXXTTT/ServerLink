using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace ServerLink
{
    public sealed class NavigationRecord
    {
        public string Process {get;set;} public string Route {get;set;} public string Context {get;set;} public string Session {get;set;} public bool Promotion {get;set;}
    }
    public sealed partial class DesktopAdapter
    {
        string navigationPath;NavigationRecord navigation;
        public Action BeforeInvoke {get;set;}
        public string ProcessKey {get{return snapshot==null?"":snapshot.ProcessEpoch;}}
        void MarkInvoke(){if(BeforeInvoke!=null)BeforeInvoke();}
        void LoadNavigation(string directory){navigationPath=Path.Combine(directory,"navigation-v1.dat");navigation=File.Exists(navigationPath)?Secrets.Load<NavigationRecord>(navigationPath):new NavigationRecord();}
        void SaveNavigation(){Secrets.Save(navigationPath,navigation);}
        public static bool IsNewChatNode(DesktopNode node,DesktopSnapshot capture)
        {
            if(node.Type!="ControlType.Button"||!node.CanInvoke||node.Disabled||!Regex.IsMatch(node.Name??"",@"^(?:新增|新建(?:聊天|对话|會話)?|新對話|新聊天|開始新對話|New chat)(?:\s*(?:\(.+\)|Ctrl\+N))?$",RegexOptions.IgnoreCase))return false;
            int parent=node.Parent;
            for(int depth=0;depth<12;depth++)
            {
                var ancestor=capture.Nodes.FirstOrDefault(n=>n.Id==parent);if(ancestor==null)break;
                if(Regex.IsMatch(ancestor.Name??"",@"^(側邊欄|侧边栏|Sidebar)$",RegexOptions.IgnoreCase))return true;
                parent=ancestor.Parent;
            }
            return false;
        }
        void DecorateNavigation()
        {
            if(state.PageKind!="chat"&&state.PageKind!="blank")
            {if(state.PageKind=="other"&&navigation.Promotion){navigation.Promotion=false;SaveNavigation();}return;}
            if(String.IsNullOrEmpty(snapshot.ProcessEpoch)||!String.IsNullOrEmpty(state.Error)||snapshot.Truncated)return;
            string route=state.PageKind=="chat"?state.SessionId:"blank";
            bool changed=navigation.Process!=snapshot.ProcessEpoch||navigation.Route!=route||String.IsNullOrEmpty(navigation.Context);
            if(changed)
            {
                bool promotion=navigation.Process==snapshot.ProcessEpoch&&navigation.Route=="blank"&&navigation.Promotion&&state.PageKind=="chat";
                if(!promotion)navigation.Context=Guid.NewGuid().ToString("N");
                navigation.Process=snapshot.ProcessEpoch;navigation.Route=route;navigation.Promotion=false;
                navigation.Session=state.PageKind=="blank"?"new:"+navigation.Context:state.SessionId;SaveNavigation();
            }
            state.ContextId=navigation.Context;if(state.PageKind=="blank"){state.SessionId=navigation.Session;state.Title="新会话";}
            if(snapshot.Nodes.Count(n=>IsNewChatNode(n,snapshot))==1&&state.CanSend)state.Capabilities.Add("create-chat-v1");
        }
        public ChatState Create(string expectedContext)
        {
            Read();if(String.IsNullOrEmpty(expectedContext)||state.ContextId!=expectedContext)throw new BridgeFault("stale_context","服务器页面已变化，请刷新后新建。");
            if(state.Generating)throw new BridgeFault("generating","Claude 正在回复，请等待结束后新建。");
            if(!state.CanSend)throw new BridgeFault("page_unavailable","当前页面不可安全操作，请刷新。");
            if(!String.IsNullOrWhiteSpace(state.DraftText))throw new BridgeFault("server_draft","服务器存在草稿，请先取回处理。");
            if(state.PageKind=="blank")return state;
            var targets=snapshot.Nodes.Where(n=>IsNewChatNode(n,snapshot)).ToList();
            if(targets.Count!=1)throw new BridgeFault("unsupported_page","未识别到唯一的新聊天入口；本次未执行。");
            string previous=state.ContextId;MarkInvoke();((InvokePattern)reader.Elements[targets[0].Id].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var limit=DateTime.UtcNow.AddSeconds(8);
            do{Thread.Sleep(180);Read();if(state.PageKind=="blank"&&state.CanSend&&state.ContextId!=previous&&String.IsNullOrWhiteSpace(state.DraftText))return state;}while(DateTime.UtcNow<limit);
            throw new BridgeFault("outcome_unknown","已调用新聊天入口，尚未确认页面，请核对结果。",false);
        }
        void PreparePromotion(){if(state.PageKind=="blank"){navigation.Promotion=true;SaveNavigation();}}
    }
}
