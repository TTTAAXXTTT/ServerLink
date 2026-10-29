using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ServerLink
{
    public sealed class DesktopWorker : IDisposable
    {
        readonly BlockingCollection<Action> queue=new BlockingCollection<Action>(16);readonly Thread thread;
        readonly string adapterMode;readonly OperationLedger ledger;IChatAdapter adapter;Exception initializationError;
        public DesktopWorker(string directory)
        {
            var config=ServerConfiguration.Load();adapterMode=config.Adapter=="content"?"content":"desktop";
            string path=Path.Combine(directory,"operation-ledger-v1.dat");
            ledger=new OperationLedger(File.Exists(path)?Secrets.Load<List<OperationEntry>>(path):null,items=>Secrets.Save(path,items));
            thread=new Thread(()=>{try{adapter=adapterMode=="content"?(IChatAdapter)new CdpAdapter(directory,config.ContentPort):new DesktopAdapter(directory);}catch(Exception ex){initializationError=ex;}foreach(var action in queue.GetConsumingEnumerable())action();});
            thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
        }
        static bool IsWrite(string op){return op=="create-chat"||op=="send"||op=="open"||op=="stop"||op=="recover-draft";}
        WireReply ReceiptReply(string id,OperationReceipt value)
        {
            if(value==null)return new WireReply {Id=id,Ok=false,ErrorCode="operation_not_found",Error="未找到操作记录；不会自动重试。"};
            return new WireReply {Id=id,Ok=value.Phase=="confirmed",Operation=value,ErrorCode=value.Phase=="not_submitted"?"not_submitted":value.Phase=="confirmed"?null:"operation_pending",Error=value.Error??"操作仍在执行或待核对。"};
        }
        void Decorate(ChatState state)
        {
            var desktop=adapter as DesktopAdapter;ledger.Observe(state,desktop==null?"":desktop.ProcessKey);
            state.Operations=ledger.Receipts();state.AgentEpoch=ledger.Epoch;state.Capabilities.Add("operation-status-v1");
        }
        public WireReply Handle(WireRequest request)
        {
            if(request.Op=="operation")return ReceiptReply(request.Id,ledger.Query(request.Handle));
            if(request.Op=="health")return new WireReply {Id=request.Id,Ok=true,State=new ChatState {AgentEpoch=ledger.Epoch,Operations=ledger.Receipts(),Runtime=SessionRuntime.Capture(adapterMode),Error=initializationError==null?"":"接入组件初始化失败。"}};
            bool write=IsWrite(request.Op);OperationReceipt known;
            if(write&&((request.Text??"").Length>32000||(request.SessionId??"").Length>100||(request.ContextId??"").Length>100||(request.Handle??"").Length>100))return new WireReply {Id=request.Id,Ok=false,ErrorCode="invalid_request",Error="请求内容过长，本次未执行。"};
            try{if(write&&!ledger.Begin(request,out known))return ReceiptReply(request.Id,known);}
            catch(Exception ex){return new WireReply {Id=request.Id,Ok=false,ErrorCode=ex is BridgeFault?((BridgeFault)ex).Code:"not_submitted",Error=ex is BridgeFault?ex.Message:"操作记录无法保存，本次未提交。"};}
            var timer=Stopwatch.StartNew();var completion=new TaskCompletionSource<WireReply>();
            Action action=()=>{
                INavigationAdapter tracked=null;
                try
                {
                    long queued=timer.ElapsedMilliseconds;
                    if(write&&!ledger.Claim(request.Id,queued>=20000)){completion.TrySetResult(ReceiptReply(request.Id,ledger.Query(request.Id)));return;}
                    if(!write&&queued>=20000){completion.TrySetResult(new WireReply {Id=request.Id,Ok=false,ErrorCode="queue_expired",Error="同步排队已过期，请刷新。"});return;}
                    timer.Restart();if(initializationError!=null)throw new BridgeFault("adapter_unavailable","桥接初始化失败，请检查数据目录和 Claude。");
                    tracked=adapter as INavigationAdapter;
                    if(write)
                    {
                        if(tracked==null)throw new BridgeFault("unsupported_adapter","当前接入方式不支持受保护的写操作，请使用桌面接入。");
                        var before=adapter.Read();
                        if(!String.IsNullOrEmpty(request.AgentEpoch)&&request.AgentEpoch!=ledger.Epoch)throw new BridgeFault("stale_context","桥接已重启，请刷新后重试。");
                        if(!String.IsNullOrEmpty(request.ContextId)&&request.ContextId!=before.ContextId)throw new BridgeFault("stale_context","服务器当前页面已变化，本次未执行。");
                        if(!String.IsNullOrEmpty(request.SessionId)&&request.SessionId!=before.SessionId)throw new BridgeFault("stale_context","服务器当前会话已变化，本次未执行。");
                        ledger.Prepare(request.Id,before,((DesktopAdapter)adapter).ProcessKey);tracked.BeforeInvoke=()=>ledger.Invoking(request.Id);
                    }
                    ChatState state;
                    switch(request.Op)
                    {
                        case "status":state=adapter.Read();break;
                        case "create-chat":state=tracked.Create(request.ContextId);break;
                        case "open":state=adapter.Open(request.Handle);break;
                        case "send":state=adapter.Send(request.Id,request.SessionId,request.Text);break;
                        case "stop":state=adapter.Stop(request.SessionId);break;
                        case "recover-draft":var recovery=adapter as IDraftRecoveryAdapter;if(recovery==null)throw new BridgeFault("unsupported_operation","当前方式不支持取回草稿。");state=recovery.RecoverDraft(request.SessionId,request.Text);break;
                        default:throw new BridgeFault("unsupported_operation","不支持的操作。");
                    }
                    if(write)ledger.Complete(request.Id,state);Decorate(state);
                    state.Runtime=SessionRuntime.Capture(adapterMode);state.Runtime.QueueMilliseconds=queued;state.Runtime.OperationMilliseconds=timer.ElapsedMilliseconds;
                    completion.TrySetResult(new WireReply {Id=request.Id,Ok=true,State=state,Operation=write?ledger.Query(request.Id):null});
                }
                catch(Exception ex)
                {
                    try{if(write)ledger.Fail(request.Id,ex);var receipt=write?ledger.Query(request.Id):null;completion.TrySetResult(new WireReply {Id=request.Id,Ok=false,Operation=receipt,ErrorCode=receipt!=null?(receipt.Phase=="not_submitted"?"not_submitted":"outcome_unknown"):ex is BridgeFault?((BridgeFault)ex).Code:"read_failed",Error=receipt!=null&&!receipt.Terminal?"操作可能已执行，请刷新核对；不会自动重试。":ex.Message});}
                    catch{completion.TrySetResult(new WireReply {Id=request.Id,Ok=false,ErrorCode="storage_error",Error="操作记录写入失败，请保持当前状态并核对原应用。"});}
                }
                finally{if(tracked!=null)tracked.BeforeInvoke=null;}
            };
            if(!queue.TryAdd(action))
            {
                if(write)ledger.Claim(request.Id,true);
                return new WireReply {Id=request.Id,Ok=false,Operation=write?ledger.Query(request.Id):null,ErrorCode="not_submitted",Error="服务器队列已满，本次未执行。"};
            }
            if(!completion.Task.Wait(26000))
            {
                if(write){ledger.Claim(request.Id,true);return ReceiptReply(request.Id,ledger.Query(request.Id));}
                return new WireReply {Id=request.Id,Ok=false,ErrorCode="read_timeout",Error="界面同步超时，请稍后刷新。"};
            }
            return completion.Task.Result;
        }
        public void Dispose(){queue.CompleteAdding();}
    }
}
