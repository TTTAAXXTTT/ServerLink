using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerLink
{
    sealed class LoseOneReply : IBridgeConnection
    {
        readonly IBridgeConnection inner;public bool Dropped;public int Sends;
        public LoseOneReply(IBridgeConnection inner){this.inner=inner;}
        public void Connect(Pairing pair){inner.Connect(pair);}
        public WireReply Call(WireRequest r){if(r.Op=="send")Sends++;var reply=inner.Call(r);if(r.Op=="send"&&!Dropped&&reply.Ok){Dropped=true;throw new IOException("Controlled test: discard one completed send reply.");}return reply;}
        public void Dispose(){inner.Dispose();}
    }
    sealed class NewChatLiveRunner : Form
    {
        readonly string[] args;readonly List<string> lines=new List<string>();
        public NewChatLiveRunner(string[] args){this.args=args;ShowInTaskbar=false;WindowState=FormWindowState.Minimized;Shown+=async(s,e)=>await Run();}
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);lines.Add("PASS "+message);}
        async Task WaitFor(Func<bool> test,Func<Task> poll,int seconds=40)
        {var end=DateTime.UtcNow.AddSeconds(seconds);while(!test()){if(DateTime.UtcNow>end)throw new TimeoutException("Verification window elapsed; no automatic resend performed.");await Task.Delay(1100);await poll();}}
        async Task Run()
        {
            int result=0;
            try
            {
                var pair=Secrets.Load<Pairing>(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkClient","connection.dat"));
                using(var connection=new BridgeConnection())
                {
                    await Task.Run(()=>connection.Connect(pair));var reply=await Task.Run(()=>connection.Call(new WireRequest {Id=Guid.NewGuid().ToString("N"),Op="status"}));Check(reply.Ok&&reply.State!=null,"Existing saved pairing TLS connection and server read");var state=reply.State;
                    lines.Add("INFO Bridge="+(state.Runtime==null?"unknown":state.Runtime.BridgeVersion)+" Claude="+state.Version+" PageKind="+state.PageKind+" ContextPresent="+!String.IsNullOrEmpty(state.ContextId)+" CanSend="+state.CanSend+" Generating="+state.Generating+" Create="+state.Capabilities.Contains("create-chat-v1")+" RDP="+(state.Runtime==null?-1:state.Runtime.ActiveRdpSessions)+" Error="+state.Error);
                    lines.Add("INFO BridgeBuild="+(state.Runtime==null?"unknown":state.Runtime.BridgeBuild));
                    string expected=args.FirstOrDefault(x=>x.StartsWith("--build="));if(expected!=null)Check(state.Runtime!=null&&String.Equals(state.Runtime.BridgeBuild,expected.Substring(8),StringComparison.OrdinalIgnoreCase),"Running server binary hash matches the exact packaged build");
                    if(args.Contains("--exercise"))
                    {
                        Check(state.Capabilities.Contains("create-chat-v1"),"Server reports new-chat capability");Check(String.IsNullOrWhiteSpace(state.DraftText)&&!state.Generating,"No original server draft or active reply");
                        if(args.Contains("--require-no-rdp"))Check(state.Runtime!=null&&state.Runtime.ActiveRdpSessions==0,"Server has zero active RDP sessions before exercise");
                        using(var client=new ClientForm(false){ShowInTaskbar=false,WindowState=FormWindowState.Minimized})
                        {
                            var lossy=args.Contains("--lose-send-reply")?new LoseOneReply(connection):null;
                            client.Show();client.TestAttach(lossy==null?(IBridgeConnection)connection:lossy,state);
                            var test=state.History.SingleOrDefault(h=>h.Title=="远程聊天测试");Check(test!=null,"Unique dedicated original test chat found");await client.TestOpen(test.Handle);await WaitFor(()=>client.TestState.Title=="远程聊天测试",()=>client.TestPoll());
                            string original=client.TestState.SessionId;client.TestDraft("SL029-local-only-draft");await client.TestCreate();Check(!client.TestActionPending&&client.TestState.PageKind=="blank"&&client.TestDraftText=="","Client opens a real blank chat and isolates the old local draft");
                            string marker="SL029-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
                            client.TestDraft("请只回复“"+marker+" 成功”。不要调用工具或修改文件。\r\n\r\n");await client.TestSend();
                            if(lossy!=null)
                            {
                                Check(lossy.Dropped&&client.TestPending&&client.TestDraftText!="","A deliberately lost send reply preserves the unknown operation and original draft");
                                await Task.Run(()=>connection.Connect(pair));var resumed=await Task.Run(()=>connection.Call(new WireRequest {Id=Guid.NewGuid().ToString("N"),Op="status"}));Check(resumed.Ok&&resumed.State!=null,"Paired connection reconnects after losing the send reply");client.TestAttach(connection,resumed.State);
                                Check(lossy.Sends==1,"Recovery did not send the original request a second time");
                            }
                            await WaitFor(()=>!client.TestPending,()=>client.TestPoll());
                            Check(client.TestState.PageKind=="chat"&&client.TestState.SessionId!=original&&!client.TestState.SessionId.StartsWith("new:")&&client.TestDraftText=="","First message is confirmed in a new formal chat and its submitted draft clears");
                            await WaitFor(()=>client.TestState.Messages.Any(m=>m.Role=="assistant"&&m.Text.Contains(marker))&&!client.TestState.Generating,()=>client.TestPoll(),60);
                            Check(client.TestState.Messages.Count(m=>m.Role=="user"&&m.Text.Contains(marker))==1,"Exactly one matching test user message and a real assistant reply");
                            string formal=client.TestState.SessionId,newTitle=client.TestState.Title;lines.Add("INFO Marker="+marker+" FormalIdPresent="+!String.IsNullOrEmpty(formal));
                            if(args.Contains("--require-no-rdp"))Check(client.TestState.Runtime!=null&&client.TestState.Runtime.ActiveRdpSessions==0,"RDP remains disconnected after real reply");
                            var old=client.TestState.History.SingleOrDefault(h=>h.Title=="远程聊天测试");Check(old!=null,"Original chat remains available after creating the new chat");await client.TestOpen(old.Handle);await WaitFor(()=>client.TestState.SessionId==original,()=>client.TestPoll());
                            Check(client.TestDraftText=="SL029-local-only-draft","Returning to the old chat restores its own local draft");client.TestClear();
                            var created=client.TestState.History.SingleOrDefault(h=>h.Title==newTitle);Check(created!=null,"Created chat is visible in the recent list");await client.TestOpen(created.Handle);await WaitFor(()=>client.TestState.SessionId==formal,()=>client.TestPoll());Check(client.TestState.Messages.Count(m=>m.Role=="user"&&m.Text.Contains(marker))==1,"Old-to-new navigation returns to the exact formal chat without duplicate user messages");client.Close();
                        }
                    }
                }
            }
            catch(Exception ex){result=1;lines.Add("FAIL "+ex.GetBaseException().Message);}
            lines.Add("RESULT "+(result==0?"PASS":"FAIL")+". Mode="+(args.Contains("--exercise")?"live new-chat send":"read-only inspection"));File.WriteAllLines(args[0],lines);Environment.ExitCode=result;Close();
        }
    }
    static class NewChatLiveProgram
    {
        [STAThread]static int Main(string[] args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new NewChatLiveRunner(args));return Environment.ExitCode;}
    }
}
