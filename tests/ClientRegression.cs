using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerLink
{
    // Compiled only into the isolated client test executable, not the shipped client.
    public sealed partial class ClientForm
    {
        internal void TestAttach(IBridgeConnection transport,ChatState initial){connection=transport;Render(initial);UpdateControls();}
        internal void TestDraft(string value){composer.Text=value;}
        internal string TestDraftText {get{return composer.Text;}}
        internal bool TestSendEnabled {get{return send.Enabled;}}
        internal string TestStatus {get{return status.Text;}}
        internal string TestSendText {get{return send.Text;}}
        internal void TestObserveEnabled(EventHandler handler){send.EnabledChanged+=handler;}
        internal Task TestPoll(){return RefreshState();}
        internal Task TestSend(){return Send();}
        internal Task TestOpen(string handle){return Command("open",null,handle);}
        internal bool TestPending {get{return pendingMessage!=null;}}
        internal void TestRestorePending(PendingClientMessage value){pendingMessage=value;uncertain=value!=null;UpdateControls();}
        internal ChatState TestState {get{return state;}}
        internal void TestRender(ChatState value){Render(value);UpdateControls();}
        internal string TestConversation {get{return conversation.Text;}}
        internal bool TestHistoryEnabled {get{return history.Enabled;}}
        internal Task TestRecoverDraft(){return RecoverServerDraft();}
    }
    sealed class ClientFakeTransport : IBridgeConnection
    {
        public Func<WireRequest,WireReply> OnCall;
        public readonly ConcurrentQueue<WireRequest> Requests=new ConcurrentQueue<WireRequest>();
        public void Connect(Pairing unused){}
        public WireReply Call(WireRequest request){Requests.Enqueue(request);return OnCall(request);}
        public void Dispose(){}
        public int Count(string op){int count=0;foreach(var r in Requests)if(r.Op==op)count++;return count;}
    }
    sealed class ClientRegressionRunner : Form
    {
        readonly string report;readonly List<string> results=new List<string>();int failures;
        public ClientRegressionRunner(string report)
        {
            this.report=report;ShowInTaskbar=false;WindowState=FormWindowState.Minimized;Shown+=async(s,e)=>await Run();
        }
        static ChatState State(string id="session-a"){return new ChatState {SessionId=id,Title="isolated client test",Version="fixture",CanSend=true,DraftText=""};}
        static WireReply Ok(WireRequest request,ChatState state){return new WireReply {Id=request.Id,Ok=true,State=state};}
        static ChatState Accepted(WireRequest request){var result=State(request.SessionId);result.TotalMessages=1;result.Messages.Add(new ChatMessage {Index=1,Role="user",Text=request.Text});return result;}
        static void Assert(bool value,string message){if(!value)throw new Exception(message);}
        static async Task WaitFor(Func<bool> ready){DateTime limit=DateTime.UtcNow.AddSeconds(4);while(!ready()){if(DateTime.UtcNow>limit)throw new TimeoutException("Test operation did not start.");await Task.Delay(10);}}
        static ClientForm View(ClientFakeTransport transport,ChatState state){var form=new ClientForm(false){ShowInTaskbar=false,WindowState=FormWindowState.Minimized};form.Show();form.TestAttach(transport,state);return form;}
        async Task Check(string name,Func<Task> test){try{await test();results.Add("PASS "+name);}catch(Exception ex){failures++;results.Add("FAIL "+name+": "+ex.Message);}}
        async Task Run()
        {
            await Check("A single content line with trailing empty lines is submitted once as that line and clears its original draft",async()=>{
                string[] drafts={"单行正文\r\n","单行正文\r\n\r\n","单行正文\n","单行正文\n\n","单行正文\r","单行正文\r\n \t\r\n\t","  保留正文缩进  \r\n\r\n"};
                foreach(string draft in drafts)
                {
                    string expected=draft.Substring(0,draft.IndexOfAny(new[]{'\r','\n'}));var transport=new ClientFakeTransport();
                    transport.OnCall=request=>{Assert(request.Op=="send"&&request.Text==expected,"Single-line trailing whitespace reached the server: "+Json.Write(request.Text));return Ok(request,Accepted(request));};
                    using(var form=View(transport,State())){form.TestDraft(draft);await form.TestSend();Assert(transport.Count("send")==1&&!form.TestPending&&form.TestDraftText=="","Trailing-line normalization left the original draft pending");form.TestDraft("下一条");Assert(form.TestSendEnabled,"Next message stayed disabled");}
                }
            });
            await Check("Trailing-line handling leaves multiline bodies, leading blank lines and code fences unchanged",async()=>{
                string[] drafts={"甲\r\n\r\n乙\r\n\r\n","甲\r\n乙\r\n","\r\n甲\r\n\r\n","```python\r\n\r\n","~~~text\n\n","```python\r\n    x = 1\r\n\r\n```\r\n\r\n"};
                foreach(string draft in drafts){var transport=new ClientFakeTransport {OnCall=request=>{Assert(request.Text==draft,"Meaningful multiline content was rewritten");return Ok(request,Accepted(request));}};using(var form=View(transport,State())){form.TestDraft(draft);await form.TestSend();Assert(!form.TestPending&&form.TestDraftText=="","Unchanged multiline send failed");}}
            });
            await Check("Editing only trailing blank lines while a send is in flight preserves the new draft",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();var transport=new ClientFakeTransport();transport.OnCall=request=>{entered.Set();if(!release.Wait(4000))throw new TimeoutException();return Ok(request,Accepted(request));};
                using(var form=View(transport,State())){form.TestDraft("单行正文\r\n");var sending=form.TestSend();await WaitFor(()=>entered.IsSet);form.TestDraft("单行正文\r\n\r\n");release.Set();await sending;Assert(!form.TestPending&&form.TestDraftText=="单行正文\r\n\r\n"&&form.TestSendEnabled,"Delivery confirmation erased a newer trailing-blank-line edit");}
            });
            await Check("A definitive rejection keeps the complete original single-line draft for manual retry",async()=>{
                var transport=new ClientFakeTransport {OnCall=request=>new WireReply {Id=request.Id,Ok=false,Error="Fixture pre-submit rejection",ErrorCode="not_submitted"}};
                using(var form=View(transport,State())){const string draft="保留原文\r\n \t\r\n";form.TestDraft(draft);await form.TestSend();Assert(!form.TestPending&&form.TestSendEnabled&&form.TestDraftText==draft,"Rejection lost the original trailing lines");}
            });
            await Check("New and legacy pending drafts survive serialization and confirm after reconnect without any resend",async()=>{
                var current=Json.Read<PendingClientMessage>(Json.Write(new PendingClientMessage {SessionId="session-a",Text="单行正文",OriginalDraftText="单行正文\r\n \t\r\n",AfterIndex=1,StartedUtc=DateTime.UtcNow}));
                var legacy=Json.Read<PendingClientMessage>(Json.Write(new {SessionId="session-a",Text="旧版正文\r\n\r\n",AfterIndex=1,StartedUtc=DateTime.UtcNow}));
                Assert(current.OriginalDraftText=="单行正文\r\n \t\r\n"&&legacy.OriginalDraftText==null,"Original draft or legacy compatibility lost during serialization");
                foreach(var pending in new[]{current,legacy})
                {
                    var transport=new ClientFakeTransport();using(var form=View(transport,State()))
                    {
                        form.TestDraft(pending.OriginalDraftText??pending.Text);form.TestRestorePending(pending);
                        var wrong=State("other-session");wrong.Messages.Add(new ChatMessage {Index=2,Role="user",Text=pending.Text.TrimEnd('\r','\n')});form.TestRender(wrong);Assert(form.TestPending,"Another conversation confirmed restored delivery");
                        var accepted=State();accepted.Messages.Add(new ChatMessage {Index=1,Role="user",Text=pending.Text.TrimEnd('\r','\n')});form.TestRender(accepted);Assert(form.TestPending,"An old message confirmed restored delivery");
                        accepted.Messages[0].Index=2;form.TestRender(accepted);Assert(!form.TestPending&&form.TestDraftText==""&&transport.Count("send")==0,"Reconnection lost the original draft or resent it");
                    }
                }
                var emptyTransport=new ClientFakeTransport();using(var form=View(emptyTransport,State())){form.TestDraft(" \r\n\t\r\n");await form.TestSend();Assert(emptyTransport.Count("send")==0&&!form.TestSendEnabled,"A whitespace-only draft was sent");}await Task.Yield();
            });
            await Check("Rendered fenced code omits only fences and keeps code indentation and internal blank lines exact",async()=>{
                string submitted="说明。\r\n\r\n```text\r\n  alpha\r\n\r\n\r\nbeta\r\n```\r\n\r\n结束。";
                Assert(ClientForm.MatchesDisplayedMessage("说明。\n\n  alpha\n\n\nbeta\n\n结束。",submitted),"Rendered code was not recognized");
                Assert(!ClientForm.MatchesDisplayedMessage("说明。\n\nalpha\n\n\nbeta\n\n结束。",submitted)&&!ClientForm.MatchesDisplayedMessage("说明。\n\n  alpha\n\nbeta\n\n结束。",submitted),"Code indentation or blank-line loss was ignored");
                Assert(ClientForm.MatchesDisplayedMessage("a\n\n  x\n\n  y\n\nb\n\nz", "a\n\n~~~text\n  x\n\n  y\n~~~\n\nb\n\n```\nz\n```"),"Multiple fenced blocks failed");await Task.Yield();
            });
            await Check("Paragraph display folding confirms only a newly indexed matching body and keeps raw outgoing text",async()=>{
                string raw="\r\n\r\n第一段。\r\n \r\n\t\r\n\r\n第二段。\r\n\r\n";var transport=new ClientFakeTransport();
                transport.OnCall=request=>{if(request.Op!="send")return Ok(request,State());Assert(request.Text==raw,"Outgoing whitespace was rewritten");var accepted=Accepted(request);accepted.Messages[0].Text="第一段。\n\n第二段。";return Ok(request,accepted);};
                using(var form=View(transport,State())){form.TestDraft(raw);await form.TestSend();Assert(!form.TestPending&&form.TestDraftText=="","Rendered paragraphs left delivery pending");}
                Assert(!ClientForm.MatchesDisplayedMessage("a\nb","a\n\nb")&&!ClientForm.MatchesDisplayedMessage("a\n  code","a\ncode")&&!ClientForm.MatchesDisplayedMessage("a b","a  b")&&!ClientForm.MatchesDisplayedMessage("```text\na\n\nb\n```","```text\na\n\n\nb\n```"),"Confirmation ignored a meaningful body, indentation or fenced-code difference");
            });
            await Check("Blank-line display matching does not clear a new edited draft or accept an old message",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();var transport=new ClientFakeTransport();transport.OnCall=request=>{entered.Set();if(!release.Wait(4000))throw new TimeoutException();var accepted=Accepted(request);accepted.Messages[0].Text="a\n\nb";return Ok(request,accepted);};
                using(var form=View(transport,State())){form.TestDraft("a\r\n\r\n\r\nb");var send=form.TestSend();await WaitFor(()=>entered.IsSet);form.TestDraft("a\r\n\r\nb");release.Set();await send;Assert(!form.TestPending&&form.TestDraftText=="a\r\n\r\nb","Confirmation erased whitespace edits in a newer draft");}
                var initial=State();initial.TotalMessages=1;initial.Messages.Add(new ChatMessage {Index=1,Role="user",Text="a\n\nb"});var stale=new ClientFakeTransport {OnCall=request=>Ok(request,initial)};using(var form=View(stale,initial)){form.TestDraft("a\r\n\r\n\r\nb");await form.TestSend();Assert(form.TestPending,"An older normalized message confirmed a new submission");}
            });
            await Check("Background synchronization does not toggle Send; a click queues exactly once behind an in-flight refresh",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();var state=State();var transport=new ClientFakeTransport();
                transport.OnCall=request=>{if(request.Op=="status"){entered.Set();if(!release.Wait(4000))throw new TimeoutException();}return Ok(request,request.Op=="send"?Accepted(request):state);};
                using(var form=View(transport,state))
                {
                    form.TestDraft("中文排队消息");Assert(form.TestSendEnabled,"Draft should enable Send");int changes=0;form.TestObserveEnabled((s,e)=>changes++);
                    var poll=form.TestPoll();await WaitFor(()=>entered.IsSet);await Task.Delay(100);Assert(form.TestSendEnabled&&changes==0,"Refresh disabled Send");
                    var send=form.TestSend();await form.TestSend();Assert(!form.TestSendEnabled&&form.TestSendText.Contains("发送中"),"Foreground send should lock once");Assert(transport.Count("send")==0,"Send bypassed the pending refresh");
                    release.Set();await poll;await send;Assert(transport.Count("send")==1,"Send was lost or duplicated");Assert(form.TestDraftText=="","Accepted draft was not cleared");Assert(!form.TestSendEnabled,"An empty draft must not enable Send");
                }
            });
            await Check("A draft typed while an earlier send is pending is preserved",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();var state=State();var transport=new ClientFakeTransport();transport.OnCall=request=>{if(request.Op=="send"){entered.Set();if(!release.Wait(4000))throw new TimeoutException();}return Ok(request,request.Op=="send"?Accepted(request):state);};
                using(var form=View(transport,state)){form.TestDraft("first");var send=form.TestSend();await WaitFor(()=>entered.IsSet);form.TestDraft("second draft");release.Set();await send;Assert(form.TestDraftText=="second draft","New draft was cleared by an earlier response");Assert(form.TestSendEnabled,"New draft remained disabled");}
            });
            await Check("Drafts are not silently sent into a different server conversation",async()=>{
                var transport=new ClientFakeTransport {OnCall=request=>Ok(request,State("session-b"))};using(var form=View(transport,State())){form.TestDraft("belongs to session-a");await form.TestPoll();await form.TestSend();Assert(transport.Count("send")==0,"Draft sent to a changed conversation");Assert(form.TestStatus.Contains("会话已变化"),"Conversation change was not explained");}
            });
            await Check("A queued send retains its original conversation and errors survive later refreshes",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();WireRequest submitted=null;var transport=new ClientFakeTransport();transport.OnCall=request=>{if(request.Op=="status"){entered.Set();if(!release.Wait(4000))throw new TimeoutException();return Ok(request,State("session-b"));}submitted=request;return new WireReply {Id=request.Id,Ok=false,Error="原会话已变化，未提交"};};
                using(var form=View(transport,State())){form.TestDraft("captured before refresh");var poll=form.TestPoll();await WaitFor(()=>entered.IsSet);var send=form.TestSend();release.Set();await poll;await send;Assert(submitted!=null&&submitted.SessionId=="session-a","Queued send was retargeted");await form.TestPoll();Assert(form.TestStatus=="原会话已变化，未提交","Refresh erased send error");Assert(!form.TestSendEnabled,"Uncertain send was enabled automatically");Assert(form.TestDraftText=="captured before refresh","Unaccepted draft lost");}
            });
            await Check("Connection failure during a refresh does not submit a waiting message",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();var transport=new ClientFakeTransport();transport.OnCall=request=>{entered.Set();if(!release.Wait(4000))throw new TimeoutException();throw new IOException("test disconnect");};using(var form=View(transport,State())){form.TestDraft("keep me");var poll=form.TestPoll();await WaitFor(()=>entered.IsSet);var send=form.TestSend();release.Set();await poll;await send;Assert(transport.Count("send")==0,"Message sent after connection failure");Assert(form.TestDraftText=="keep me","Draft lost on disconnect");Assert(!form.TestSendEnabled,"Disconnected Send remained enabled");}
            });
            await Check("History selection also queues behind synchronization instead of disappearing",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();var transport=new ClientFakeTransport();transport.OnCall=request=>{if(request.Op=="status"){entered.Set();if(!release.Wait(4000))throw new TimeoutException();}return Ok(request,State());};using(var form=View(transport,State())){var poll=form.TestPoll();await WaitFor(()=>entered.IsSet);var open=form.TestOpen("history-handle");release.Set();await poll;await open;Assert(transport.Count("open")==1,"History command was dropped");}
            });
            await Check("A definite server draft rejection preserves local text and permits a later manual send",async()=>{
                bool blocked=true;var transport=new ClientFakeTransport();transport.OnCall=request=>request.Op=="send"&&blocked?new WireReply {Id=request.Id,Ok=false,Error="服务器存在未发送草稿，请先处理；工具不会覆盖草稿。"}:Ok(request,request.Op=="send"?Accepted(request):State());
                using(var form=View(transport,State())){form.TestDraft("retry after clearing server draft");await form.TestSend();Assert(form.TestSendEnabled&&!form.TestPending,"Definite rejection locked Send");Assert(form.TestDraftText!="","Rejected text was lost");blocked=false;await form.TestPoll();Assert(form.TestSendEnabled,"Refresh failed to recover after server draft was removed");await form.TestSend();Assert(transport.Count("send")==2&&!form.TestPending&&form.TestDraftText=="","Second manual send did not complete");}
            });
            await Check("Bridge acknowledgement alone does not clear the draft or allow duplicate sends",async()=>{
                var transport=new ClientFakeTransport {OnCall=request=>Ok(request,State())};using(var form=View(transport,State())){form.TestDraft("waiting for transcript");await form.TestSend();Assert(form.TestPending&&!form.TestSendEnabled&&form.TestDraftText!="","Unconfirmed submission was treated as delivery");await form.TestSend();Assert(transport.Count("send")==1,"An unconfirmed message was resent");var actual=State();actual.TotalMessages=1;actual.Messages.Add(new ChatMessage {Index=1,Role="user",Text="waiting for transcript"});form.TestRender(actual);Assert(!form.TestPending&&form.TestDraftText=="","Real transcript did not confirm delivery");}
            });
            await Check("An old identical user message cannot confirm a new submission",async()=>{
                var initial=State();initial.TotalMessages=1;initial.Messages.Add(new ChatMessage {Index=1,Role="user",Text="same text"});var transport=new ClientFakeTransport {OnCall=request=>Ok(request,initial)};using(var form=View(transport,initial)){form.TestDraft("same text");await form.TestSend();Assert(form.TestPending,"Old identical message confirmed a new send");var later=State();later.TotalMessages=3;later.Messages.Add(new ChatMessage {Index=3,Role="user",Text="same text"});form.TestRender(later);Assert(!form.TestPending,"New matching message was not confirmed");}
            });
            await Check("A temporary empty UI snapshot preserves the transcript and repeated failures block keyboard sends",async()=>{
                var initial=State();initial.TotalMessages=1;initial.Messages.Add(new ChatMessage {Index=1,Role="assistant",Text="keep visible"});var transport=new ClientFakeTransport {OnCall=request=>Ok(request,State())};using(var form=View(transport,initial)){form.TestDraft("waiting");await form.TestPoll();Assert(form.TestState.Messages.Count==1&&form.TestSendEnabled,"Single transient snapshot erased or disabled the conversation");await form.TestPoll();await form.TestPoll();Assert(!form.TestSendEnabled,"Persistent inaccessible UI did not disable Send");await form.TestSend();Assert(transport.Count("send")==0,"Keyboard send bypassed unavailable UI protection");form.TestRender(initial);Assert(form.TestSendEnabled,"Recovered UI did not restore Send");}
            });
            await Check("A queued refresh advances the confirmation baseline before submitting an identical message",async()=>{
                var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();var refreshed=State();refreshed.TotalMessages=1;refreshed.Messages.Add(new ChatMessage {Index=1,Role="user",Text="identical"});var transport=new ClientFakeTransport();transport.OnCall=request=>{if(request.Op=="status"){entered.Set();if(!release.Wait(4000))throw new TimeoutException();}return Ok(request,refreshed);};
                using(var form=View(transport,State())){form.TestDraft("identical");var poll=form.TestPoll();await WaitFor(()=>entered.IsSet);var sending=form.TestSend();release.Set();await poll;await sending;Assert(form.TestPending,"A message read before dispatch falsely confirmed delivery");}
            });
            await Check("An unconfirmed message blocks history changes without losing its confirmation state",async()=>{
                var transport=new ClientFakeTransport {OnCall=request=>Ok(request,State())};using(var form=View(transport,State())){form.TestDraft("still waiting");await form.TestSend();await form.TestOpen("other");Assert(transport.Count("open")==0&&form.TestPending&&!form.TestHistoryEnabled,"Conversation changed during unresolved submission");}
            });
            await Check("A structured pre-submission rejection unlocks Send and preserves the complete draft",async()=>{
                var transport=new ClientFakeTransport {OnCall=request=>new WireReply {Id=request.Id,Ok=false,Error="different localized pre-invoke error",ErrorCode="not_submitted"}};using(var form=View(transport,State())){form.TestDraft("中文\r\n第二行");await form.TestSend();Assert(!form.TestPending&&form.TestSendEnabled&&form.TestDraftText=="中文\r\n第二行","Definite rejection became permanently pending");}
            });
            await Check("Streaming extends the final message and later append/edit does not duplicate earlier messages",async()=>{
                var transport=new ClientFakeTransport();var initial=State();initial.Messages.Add(new ChatMessage {Index=1,Role="user",Text="hello"});initial.Messages.Add(new ChatMessage {Index=2,Role="assistant",Text="答"});using(var form=View(transport,initial)){var update=State();update.Messages.Add(new ChatMessage {Index=1,Role="user",Text="hello"});update.Messages.Add(new ChatMessage {Index=2,Role="assistant",Text="答案\n第二行"});form.TestRender(update);Assert(form.TestConversation=="你\nhello\n\nClaude\n答案\n第二行\n\n","Streaming append mismatch: "+Json.Write(form.TestConversation));update.Messages.Add(new ChatMessage {Index=3,Role="user",Text="next"});form.TestRender(update);update.Messages[1].Text="修改后的答案";form.TestRender(update);Assert(form.TestConversation=="你\nhello\n\nClaude\n修改后的答案\n\n你\nnext\n\n","Tail rebuild duplicated or lost messages");}await Task.Yield();
            });
            await Check("Draft recovery preserves local text and only addresses the exact current server draft",async()=>{
                var initial=State();initial.DraftText="owned failed draft";initial.DraftRecoveryAvailable=true;var transport=new ClientFakeTransport {OnCall=request=>Ok(request,State())};using(var form=View(transport,initial)){form.TestDraft("different local draft");await form.TestRecoverDraft();Assert(transport.Count("recover-draft")==0,"Different local draft was overwritten");form.TestDraft("owned failed draft");await form.TestRecoverDraft();Assert(transport.Count("recover-draft")==1&&form.TestDraftText=="owned failed draft"&&form.TestSendEnabled,"Recovery lost draft or remained locked");}
            });
            await Check("Recovering a draft does not declare a previous unknown submission delivered or rejected",async()=>{
                var initial=State();initial.DraftText="unknown outcome";initial.DraftRecoveryAvailable=true;var transport=new ClientFakeTransport {OnCall=request=>Ok(request,request.Op=="send"?initial:State())};using(var form=View(transport,initial)){form.TestDraft("unknown outcome");await form.TestSend();await form.TestRecoverDraft();Assert(transport.Count("recover-draft")==1&&form.TestPending&&!form.TestSendEnabled,"Draft recovery incorrectly resolved an unknown earlier Invoke");}
            });
            await Check("Persisted drafts retain the pending message and confirmation boundary through serialization",async()=>{
                var saved=new LocalClientDraft {Text="下一条草稿",SessionId="original-session",ServerKey="fixture",Uncertain=true,Pending=new PendingClientMessage {Text="previous prompt",SessionId="original-session",AfterIndex=12,StartedUtc=DateTime.UtcNow}};var restored=Json.Read<LocalClientDraft>(Json.Write(saved));Assert(restored.Text==saved.Text&&restored.Pending!=null&&restored.Pending.AfterIndex==12&&restored.Pending.Text=="previous prompt"&&restored.Uncertain,"Pending draft did not survive serialization");await Task.Yield();
            });
            await Check("Delayed draft clearing is confirmed by a fresh whitespace-only UIA value without another mutation",async()=>{
                var initial=State();initial.DraftText="recover this";initial.DraftRecoveryAvailable=true;var transport=new ClientFakeTransport {OnCall=request=>request.Op=="recover-draft"?new WireReply {Id=request.Id,Ok=false,Error="草稿已备份，但未确认服务器输入框已清空，请刷新核对。"}:Ok(request,new ChatState {SessionId="session-a",CanSend=true,DraftText="\n"})};using(var form=View(transport,initial)){form.TestDraft("recover this");await form.TestRecoverDraft();Assert(transport.Count("recover-draft")==1&&form.TestSendEnabled&&form.TestDraftText=="recover this","Delayed UIA update left recovery locked");}
            });
            results.Add("RESULT "+(failures==0?"PASS":"FAIL")+". Isolated client-only in-memory transport; no server Agent, TLS listener, pairing credentials or Claude access.");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));File.WriteAllLines(report,results);Environment.ExitCode=failures==0?0:1;Close();
        }
    }
    static class ClientRegressionProgram
    {
        [STAThread]static int Main(string[] args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new ClientRegressionRunner(args[0]));return Environment.ExitCode;}
    }
}
