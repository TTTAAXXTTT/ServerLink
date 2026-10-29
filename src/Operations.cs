using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ServerLink
{
    public interface INavigationAdapter
    {
        Action BeforeInvoke {get;set;}
        ChatState Create(string expectedContext);
    }
    public sealed class OperationEntry
    {
        public OperationReceipt Receipt {get;set;}
        public string Fingerprint {get;set;} public string Owner {get;set;}
        public string SourceSession {get;set;} public string SourceContext {get;set;}
        public string ProcessEpoch {get;set;} public int AfterIndex {get;set;}
        public string Text {get;set;} public string CreatedUtc {get;set;}
    }
    public sealed class OperationLedger
    {
        readonly object gate=new object();readonly List<OperationEntry> entries;
        readonly Action<List<OperationEntry>> save;
        string owner="";long ownerUntil;readonly Stopwatch clock=Stopwatch.StartNew();
        public readonly string Epoch=Guid.NewGuid().ToString("N");
        public OperationLedger(List<OperationEntry> loaded,Action<List<OperationEntry>> save)
        {
            entries=loaded??new List<OperationEntry>();this.save=save;
            // A crash before an Invoke boundary is recoverable; after it is unknown.
            foreach(var e in entries.Where(e=>e.Receipt.Phase=="queued"||e.Receipt.Phase=="running"))
            {e.Receipt.Phase="not_submitted";e.Receipt.ErrorCode="not_submitted";e.Receipt.Error="桥接已重启，排队操作未执行。";}
            Persist();
        }
        static string Fingerprint(WireRequest r){return Secrets.Digest(Encoding.UTF8.GetBytes(Json.Write(new {r.Op,r.SessionId,r.ContextId,r.Text,r.Handle,r.AgentEpoch})));}
        void Persist(){save(entries);}
        public OperationReceipt Query(string id){lock(gate){var e=entries.FirstOrDefault(x=>x.Receipt.Id==id);return e==null?null:Copy(e.Receipt);}}
        public List<OperationReceipt> Receipts(){lock(gate)return entries.Skip(Math.Max(0,entries.Count-40)).Select(x=>Copy(x.Receipt)).ToList();}
        static OperationReceipt Copy(OperationReceipt r){return Json.Read<OperationReceipt>(Json.Write(r));}
        public bool Begin(WireRequest r,out OperationReceipt existing)
        {
            lock(gate)
            {
                Guid id;if(!Guid.TryParseExact(r.Id,"N",out id))throw new BridgeFault("invalid_request","操作编号无效。");
                var found=entries.FirstOrDefault(x=>x.Receipt.Id==r.Id);
                if(found!=null){if(found.Fingerprint!=Fingerprint(r))throw new BridgeFault("request_conflict","操作编号已用于另一项内容。");existing=Copy(found.Receipt);return false;}
                var unresolved=entries.FirstOrDefault(x=>!x.Receipt.Terminal);
                if(unresolved!=null)throw new BridgeFault("operation_pending","服务器还有操作待核对，请先核对结果。",true);
                string requester=r.Owner??"legacy";
                if(owner!=requester&&clock.ElapsedMilliseconds<ownerUntil)throw new BridgeFault("controller_busy","另一客户端正在控制服务器，请稍后重试。");
                if(!String.IsNullOrEmpty(r.AgentEpoch)&&r.AgentEpoch!=Epoch)throw new BridgeFault("stale_context","桥接已重启，请刷新会话后重试。");
                // Keep tombstones: never forget an old ID and execute it again.
                if(entries.Count>=20000)throw new BridgeFault("journal_full","操作记录已满，请先维护备份；本次未执行。");
                var e=new OperationEntry {Receipt=new OperationReceipt {Id=r.Id,Kind=r.Op,Phase="queued",ContextId=r.ContextId,SessionId=r.SessionId},Fingerprint=Fingerprint(r),Owner=requester,SourceContext=r.ContextId,SourceSession=r.SessionId,Text=r.Text,CreatedUtc=DateTime.UtcNow.ToString("o")};
                entries.Add(e);try{Persist();}catch{entries.Remove(e);throw;}
                owner=requester;ownerUntil=clock.ElapsedMilliseconds+60000;existing=null;return true;
            }
        }
        public bool Claim(string id,bool expired)
        {
            lock(gate){var e=entries.Single(x=>x.Receipt.Id==id);if(e.Receipt.Phase!="queued")return false;if(expired){RejectCore(e,"queue_expired","操作在排队时已过期，未执行。");return false;}e.Receipt.Phase="running";Persist();return true;}
        }
        public void Prepare(string id,ChatState state,string processEpoch)
        {
            lock(gate){var e=entries.Single(x=>x.Receipt.Id==id);e.SourceSession=state.SessionId;e.SourceContext=state.ContextId;e.ProcessEpoch=processEpoch;e.AfterIndex=Math.Max(state.TotalMessages,state.Messages.Count==0?0:state.Messages.Max(x=>x.Index));Persist();}
        }
        public void Invoking(string id)
        {
            lock(gate){var e=entries.Single(x=>x.Receipt.Id==id);if(e.Receipt.Phase!="running")throw new InvalidOperationException("操作状态已变化，未调用原应用。");e.Receipt.Phase="invoking";Persist();}
        }
        public void Fail(string id,Exception error)
        {
            lock(gate){var e=entries.Single(x=>x.Receipt.Id==id);if(e.Receipt.Terminal)return;
                if(e.Receipt.Phase=="queued"||e.Receipt.Phase=="running")RejectCore(e,error is BridgeFault?((BridgeFault)error).Code:"not_submitted",error.Message);
                else{e.Receipt.Phase="unknown";e.Receipt.ErrorCode="outcome_unknown";e.Receipt.Error="操作可能已执行，请核对结果。";Persist();}}
        }
        void RejectCore(OperationEntry e,string code,string message){e.Receipt.Phase="not_submitted";e.Receipt.ErrorCode=code;e.Receipt.Error=message;Persist();}
        public void Complete(string id,ChatState state)
        {
            lock(gate){var e=entries.Single(x=>x.Receipt.Id==id);if(e.Receipt.Terminal)return;
                if(e.Receipt.Kind=="send"){e.Receipt.Phase="submitted";Persist();return;}
                e.Receipt.Phase="confirmed";e.Receipt.SessionId=state.SessionId;e.Receipt.ContextId=state.ContextId;e.Text=null;Persist();}
        }
        public void Observe(ChatState state,string processEpoch)
        {
            lock(gate)
            {
                if(!String.IsNullOrEmpty(state.Error)||!state.CanSend&&!state.Generating)return;
                foreach(var e in entries.Where(x=>!x.Receipt.Terminal&&x.Receipt.Kind=="create-chat"&&(x.Receipt.Phase=="invoking"||x.Receipt.Phase=="unknown")))
                {
                    if(state.PageKind!="blank"||!state.CanSend||!String.IsNullOrWhiteSpace(state.DraftText)||e.ProcessEpoch!=processEpoch||String.IsNullOrEmpty(state.ContextId)||state.ContextId==e.SourceContext)continue;
                    e.Receipt.Phase="confirmed";e.Receipt.SessionId=state.SessionId;e.Receipt.ContextId=state.ContextId;e.Receipt.Error="";e.Receipt.ErrorCode="";Persist();
                }
                foreach(var e in entries.Where(x=>!x.Receipt.Terminal&&x.Receipt.Kind=="send"&&(x.Receipt.Phase=="submitted"||x.Receipt.Phase=="unknown"||x.Receipt.Phase=="invoking")))
                {
                    bool first=(e.SourceSession??"").StartsWith("new:",StringComparison.Ordinal);
                    bool same=state.SessionId==e.SourceSession&&state.ContextId==e.SourceContext;
                    bool promoted=first&&state.PageKind=="chat"&&e.ProcessEpoch==processEpoch&&state.ContextId==e.SourceContext&&e.AfterIndex==0;
                    if(!(same||promoted))continue;
                    var users=state.Messages.Where(x=>x.Role=="user"&&x.Index>e.AfterIndex).ToList();
                    if(first&&(users.Count!=1||users[0].Index!=1))continue;
                    if(!users.Any(x=>MessageProof.Matches(x.Text,e.Text)))continue;
                    e.Receipt.Phase="confirmed";e.Receipt.SessionId=state.SessionId;e.Receipt.ContextId=state.ContextId;e.Text=null;e.Receipt.Error="";e.Receipt.ErrorCode="";Persist();
                }
            }
        }
    }
}
