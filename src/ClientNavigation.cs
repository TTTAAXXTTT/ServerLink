using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerLink
{
    public sealed class SavedClientDraft
    {
        public string Server {get;set;} public string Session {get;set;} public string Text {get;set;}
    }
    public sealed class PendingClientAction
    {
        public string Id {get;set;} public string Kind {get;set;} public string SourceSession {get;set;} public DateTime StartedUtc {get;set;}
    }
    public sealed partial class ClientForm
    {
        readonly Button newChat=new Button {Text="＋ 新增会话",Dock=DockStyle.Fill,Enabled=false};
        readonly List<SavedClientDraft> drafts=new List<SavedClientDraft>();
        readonly string controllerId=Guid.NewGuid().ToString("N");
        PendingClientAction pendingAction;string activeDraftSession="";bool draftStorageBlocked,switchingDraft,navigationConfirmed;
        string CurrentServer {get{return pair==null?"isolated":ServerKey(pair);}}
        void InitializeNavigation(TableLayoutPanel side)
        {
            side.Controls.Remove(connect);var row=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=Padding.Empty};
            row.RowStyles.Add(new RowStyle(SizeType.Percent,50));row.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            connect.Dock=DockStyle.Fill;newChat.Dock=DockStyle.Fill;Style(newChat,true);row.Controls.Add(connect,0,0);row.Controls.Add(newChat,0,1);side.Controls.Add(row,0,1);
            newChat.Click+=async(s,e)=>await CreateChat();
            FormClosing+=(s,e)=>{if(persistDraft&&!SaveLocalDraft()&&MessageBox.Show(this,"草稿尚未保存。是否仍要退出？请先复制需要的文字。","草稿未保存",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)e.Cancel=true;};
            UpdateControls();
        }
        static bool IsDefiniteRejection(string code)
        {
            return new[]{"not_submitted","stale_context","controller_busy","operation_pending","invalid_request","request_conflict","journal_full","queue_expired","unsupported_operation","unsupported_adapter"}.Contains(code??"");
        }
        void UpdateNavigationControls()
        {
            bool capable=state!=null&&state.Capabilities!=null&&state.Capabilities.Contains("create-chat-v1");
            Enable(newChat,connection!=null&&!working&&!uncertain&&pendingMessage==null&&pendingAction==null&&recoveringDraft==null&&!draftStorageBlocked&&interfaceReady&&capable&&state.CanSend);
            statusTip.SetToolTip(newChat,connection==null?"请先连接服务器。":pendingAction!=null||pendingMessage!=null?"请先核对上一条操作结果。":!capable?"请同时更新服务端，并确认 Claude 处于可识别的普通聊天页面。":"在服务器 Claude 创建普通聊天。");
            string label=pendingAction!=null?(DateTime.UtcNow-pendingAction.StartedUtc>TimeSpan.FromSeconds(30)?"结果待核对 · 请刷新":"正在核对会话…"):working?"请稍候…":"＋ 新增会话";if(newChat.Text!=label)newChat.Text=label;
        }
        async Task CreateChat()
        {
            if(!newChat.Enabled)return;
            if(state==null||!state.Capabilities.Contains("create-chat-v1")){status.Text="服务器尚不支持新增会话，请同时更新服务端。";return;}
            if(!String.IsNullOrWhiteSpace(state.DraftText)){status.Text="服务器有未发送草稿，请先取回处理，再新增会话。";return;}
            if(state.PageKind=="blank"){status.Text="当前已是新会话，输入首条消息即可。";return;}
            status.Text="正在打开服务器新会话…";await Command("create-chat",null,null);
            if(pendingAction==null&&!uncertain&&state!=null&&state.PageKind=="blank"){status.Text="新会话已就绪，输入首条消息开始聊天。";composer.Focus();}
        }
        void CaptureDraft()
        {
            if(switchingDraft)return;
            string session=!String.IsNullOrEmpty(activeDraftSession)?activeDraftSession:draftSession;
            if(String.IsNullOrEmpty(session))return;
            var value=drafts.FirstOrDefault(x=>x.Server==CurrentServer&&x.Session==session);
            if(value==null){value=new SavedClientDraft {Server=CurrentServer,Session=session};drafts.Add(value);}value.Text=composer.Text;
        }
        void ChangeDraftSession(string session)
        {
            if(String.IsNullOrEmpty(session)||activeDraftSession==session)return;
            CaptureDraft();if(!SaveLocalDraft()){draftStorageBlocked=true;return;}
            if(String.IsNullOrEmpty(activeDraftSession)&&!String.IsNullOrEmpty(composer.Text))
            {
                // Old saved drafts with a known binding remain on that binding.
                if(!String.IsNullOrEmpty(draftSession)&&draftSession!=session)return;
            }
            var next=drafts.FirstOrDefault(x=>x.Server==CurrentServer&&x.Session==session);
            bool first=String.IsNullOrEmpty(activeDraftSession);activeDraftSession=session;switchingDraft=true;
            try{if(!first||String.IsNullOrEmpty(composer.Text))composer.Text=next==null?"":next.Text??"";draftSession=String.IsNullOrEmpty(composer.Text)?"":session;}
            finally{switchingDraft=false;}SaveLocalDraft();
        }
        void ReconcileNavigation(ChatState value)
        {
            var records=value.Operations??new List<OperationReceipt>();
            if(pendingAction!=null)
            {
                var action=records.FirstOrDefault(x=>x.Id==pendingAction.Id);
                if(action!=null&&action.Terminal){if(action.Phase=="not_submitted")actionError=action.Error;else if(action.ContextId==value.ContextId)navigationConfirmed=true;else actionError="操作已完成，但服务器已切换到其他会话，请核对草稿。";pendingAction=null;uncertain=pendingMessage!=null;SaveLocalDraft();}
                else if(DateTime.UtcNow-pendingAction.StartedUtc>TimeSpan.FromSeconds(30))status.Text="会话操作尚未确认，请刷新核对；程序不会重复新建。";
            }
            if(pendingMessage!=null&&!String.IsNullOrEmpty(pendingMessage.Id))
            {
                var receipt=records.FirstOrDefault(x=>x.Id==pendingMessage.Id);
                if(receipt!=null&&receipt.Phase=="not_submitted"){actionError=receipt.Error;pendingMessage=null;uncertain=pendingAction!=null;SaveLocalDraft();}
                else if(receipt!=null&&receipt.Phase=="confirmed"&&(pendingMessage.SessionId??"").StartsWith("new:",StringComparison.Ordinal)&&receipt.ContextId==pendingMessage.ContextId&&receipt.SessionId==value.SessionId&&value.PageKind=="chat")
                {
                    string old=pendingMessage.SessionId;pendingMessage.SessionId=value.SessionId;
                    foreach(var draft in drafts.Where(x=>x.Server==CurrentServer&&x.Session==old))draft.Session=value.SessionId;
                    if(activeDraftSession==old)activeDraftSession=value.SessionId;if(draftSession==old)draftSession=value.SessionId;SaveLocalDraft();
                }
            }
            if(pendingMessage==null&&pendingAction==null&&(value.PageKind=="chat"||value.PageKind=="blank"||String.IsNullOrEmpty(value.PageKind)))
            {
                if(navigationConfirmed||state==null||String.IsNullOrEmpty(composer.Text)||activeDraftSession==value.SessionId)ChangeDraftSession(value.SessionId);
                else if(activeDraftSession!=value.SessionId)actionError="服务器当前会话已变化，请核对草稿后重新输入；消息尚未发送。";
            }
            navigationConfirmed=false;
        }
        async Task QueryMissingOperation(IBridgeConnection current)
        {
            if(state==null||!state.Capabilities.Contains("operation-status-v1"))return;
            string id=pendingAction!=null?pendingAction.Id:pendingMessage!=null?pendingMessage.Id:null;
            if(String.IsNullOrEmpty(id)||state.Operations.Any(x=>x.Id==id))return;
            var request=new WireRequest {Id=Guid.NewGuid().ToString("N"),Op="operation",Handle=id};var reply=await Task.Run(()=>current.Call(request));
            if(closing||connection!=current)return;
            if(reply.Operation!=null){state.Operations.Add(reply.Operation);Render(state);}
            else if(reply.ErrorCode=="operation_not_found")status.Text="服务器未找到原操作记录，结果仍待核对；程序不会自动重试。";
        }
    }
}
