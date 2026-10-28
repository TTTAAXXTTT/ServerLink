using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Forms;

namespace ServerLink
{
    // Test-only instrumentation. Uses the actual paired server and production ClientForm.
    // Never starts an Agent, opens a listener, prints pairing secrets, or resends a message.
    sealed class ObservedLiveConnection : IBridgeConnection
    {
        readonly BridgeConnection inner=new BridgeConnection();
        public readonly ManualResetEventSlim RefreshEntered=new ManualResetEventSlim();
        public readonly ManualResetEventSlim ReleaseRefresh=new ManualResetEventSlim();
        public bool HoldNextRefresh;public int Sends;public long SendMilliseconds,ServerSendMilliseconds,QueueMilliseconds;public string SentText;
        public void Connect(Pairing pair){inner.Connect(pair);}
        public WireReply Call(WireRequest request)
        {
            if(request.Op=="status"&&HoldNextRefresh){HoldNextRefresh=false;RefreshEntered.Set();if(!ReleaseRefresh.Wait(10000))throw new TimeoutException("Test refresh hold timed out.");}
            if(request.Op=="send"){Interlocked.Increment(ref Sends);SentText=request.Text;}
            var timer=Stopwatch.StartNew();try{var reply=inner.Call(request);if(request.Op=="send"&&reply.State!=null&&reply.State.Runtime!=null){ServerSendMilliseconds=reply.State.Runtime.OperationMilliseconds;QueueMilliseconds=reply.State.Runtime.QueueMilliseconds;}return reply;}finally{if(request.Op=="send")SendMilliseconds=timer.ElapsedMilliseconds;}
        }
        public void Dispose(){inner.Dispose();}
    }
    sealed class LiveClientFormRunner : Form
    {
        readonly string report,blankCase;readonly bool requireNoRdp,shortMessage;readonly List<string> lines=new List<string>();
        public LiveClientFormRunner(string path,bool requireNoRdp,bool shortMessage,string blankCase){report=path;this.requireNoRdp=requireNoRdp;this.shortMessage=shortMessage;this.blankCase=blankCase;ShowInTaskbar=false;WindowState=FormWindowState.Minimized;Shown+=async(s,e)=>await Run();}
        static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        void Log(string value){lines.Add(value);Console.WriteLine(value);File.WriteAllLines(report,lines);}
        async Task Run()
        {
            ClientForm form=null;ObservedLiveConnection transport=null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));
                string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkClient","connection.dat");
                var pair=Secrets.Load<Pairing>(profile);transport=new ObservedLiveConnection();await Task.Run(()=>transport.Connect(pair));Log("PASS Existing remote server connection authenticated; credentials not recorded.");
                var initial=await Task.Run(()=>transport.Call(new WireRequest {Id=Guid.NewGuid().ToString("N"),Op="status"}));
                Assert(initial.Ok&&initial.State!=null,"Initial server read failed.");Assert(initial.State.Title=="远程聊天测试","Current conversation is not the dedicated test conversation; no message was sent.");Assert(initial.State.CanSend&&!initial.State.Generating,"Server UI is not ready; no message was sent.");
                if(requireNoRdp)AssertNoRdp(initial.State);if(initial.State.Runtime!=null)Log("INFO Server adapter="+initial.State.Runtime.Adapter+"; sessionState="+initial.State.Runtime.SessionState+"; activeRdpSessions="+initial.State.Runtime.ActiveRdpSessions);
                string marker="SL026-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
                string prompt="这是 ServerLink 0.2.6 客户端多行发送验证。\r\n请只回复“"+marker+" 收发成功”。\r\n不要调用工具，也不要修改文件。";
                if(shortMessage)prompt="只回复“"+marker+" 收发成功”。不要调用工具或修改文件。";
                string instruction="请只回复“"+marker+" 收发成功”。不要调用工具或修改文件。";
                if(blankCase=="middle")prompt="第一段空行验证。\r\n\r\n第二段空行验证。\r\n"+instruction;
                if(blankCase=="multiple")prompt="第一段连续空行验证。\r\n\r\n\r\n\r\n第二段连续空行验证。\r\n"+instruction;
                if(blankCase=="edges")prompt="\r\n\r\n首尾空行验证。\r\n"+instruction+"\r\n\r\n";
                if(blankCase=="spaces")prompt="第一段。\r\n  \r\n\t\r\n第二段。\r\n"+instruction;
                if(blankCase=="code")prompt="含代码块空行验证。\r\n\r\n```text\r\nalpha\r\n\r\nbeta\r\n```\r\n\r\n"+instruction;
                if(blankCase=="code-indent")prompt="含缩进代码空行验证。\r\n\r\n```python\r\ndef sample():\r\n\r\n\r\n    return 1\r\n```\r\n\r\n"+instruction;
                Log("INFO Fixture="+(String.IsNullOrEmpty(blankCase)?shortMessage?"single-line":"multiline":blankCase));
                int originalIndex=initial.State.TotalMessages;
                form=new ClientForm(false){ShowInTaskbar=false,WindowState=FormWindowState.Minimized};form.Show();form.TestAttach(transport,initial.State);form.TestDraft(prompt);
                transport.HoldNextRefresh=true;var poll=form.TestPoll();
                DateTime enteredLimit=DateTime.UtcNow.AddSeconds(5);while(!transport.RefreshEntered.IsSet){Assert(DateTime.UtcNow<enteredLimit,"Refresh did not begin.");await Task.Delay(20);}
                Assert(form.TestSendEnabled,"Background refresh disabled Send.");var sending=form.TestSend();await form.TestSend();Assert(transport.Sends==0,"Send did not wait behind the active refresh.");transport.ReleaseRefresh.Set();await poll;await sending;
                Assert(transport.Sends==1,"The production client did not dispatch exactly one send.");Assert(transport.SentText==prompt,"The production client rewrote the original whitespace.");Log("PASS Raw outgoing text, including all blank lines, is unchanged.");Log("PASS Actual ClientForm queued one send behind synchronization; repeated click did not duplicate it. SEND_CALL_MS "+transport.SendMilliseconds+" SERVER_OP_MS "+transport.ServerSendMilliseconds+" QUEUE_MS "+transport.QueueMilliseconds);
                DateTime limit=DateTime.UtcNow.AddSeconds(150);bool delivered=false,replied=false;
                while(DateTime.UtcNow<limit)
                {
                    var state=form.TestState;
                    if(requireNoRdp)AssertNoRdp(state);
                    delivered=state.SessionId==initial.State.SessionId&&state.Messages.Any(m=>m.Index>originalIndex&&m.Role=="user"&&ClientForm.MatchesDisplayedMessage(m.Text,prompt));
                    replied=delivered&&state.Messages.Any(m=>m.Index>originalIndex&&m.Role=="assistant"&&m.Text.Contains(marker)&&m.Text.Contains("收发成功"));
                    if(!delivered&&!String.IsNullOrEmpty(blankCase)&&!state.Generating&&state.Messages.Any(m=>m.Index>originalIndex&&m.Role=="assistant"&&m.Text.Contains(marker)&&m.Text.Contains("收发成功"))){var own=state.Messages.FirstOrDefault(m=>m.Index>originalIndex&&m.Role=="user"&&m.Text.Contains(marker));if(own!=null){Log("INFO Own fixture expected="+Json.Write(prompt.Replace("\r\n","\n")));Log("INFO Own fixture readback="+Json.Write(own.Text));throw new InvalidOperationException("Real Claude replied, but the original transcript readback changed whitespace; the client did not confirm exact delivery.");}}
                    if(replied)break;if(!form.TestPending&&!delivered)throw new InvalidOperationException("Server rejected the test send: "+form.TestStatus);
                    await Task.Delay(2000);await form.TestPoll();
                }
                Assert(delivered,"Submitted message was not confirmed in the original server conversation. No automatic retry was made. Status: "+form.TestStatus);
                Assert(!form.TestPending&&form.TestDraftText=="","Client did not clear its draft after transcript confirmation.");Log("PASS The new user message appeared in the original server transcript and unlocked the local client.");
                Assert(replied,"No matching real Claude reply observed. No automatic retry was made.");Log("PASS Real Claude reply contains the unique marker "+marker+" and 收发成功.");
                form.TestDraft("未发送的下一条草稿");Assert(form.TestSendEnabled,"Send did not recover for a new local draft.");form.TestDraft("");Log("PASS Send is available for the next draft after the reply; no additional message sent.");
                Log("RESULT PASS");Environment.ExitCode=0;
            }
            catch(Exception ex){Log("FAIL "+ex.GetBaseException().Message);Log("RESULT FAIL");Environment.ExitCode=1;}
            finally{if(transport!=null)transport.ReleaseRefresh.Set();if(form!=null){form.Close();form.Dispose();}else if(transport!=null)transport.Dispose();Close();}
        }
        static void AssertNoRdp(ChatState state){Assert(state.Runtime!=null&&(state.Runtime.Adapter=="content"||state.Runtime.Adapter=="desktop")&&state.Runtime.ActiveRdpSessions==0&&(state.Runtime.SessionState=="Disconnected"||state.Runtime.SessionState=="Active"),"No-RDP acceptance requires a known server adapter, a known running/disconnected session and confirmed zero active RDP sessions.");}
    }
    static class LiveClientFormProgram
    {
        [STAThread]static int Main(string[] args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var blank=args.FirstOrDefault(a=>a.StartsWith("--blank-",StringComparison.Ordinal));Application.Run(new LiveClientFormRunner(args[0],args.Contains("--require-no-rdp"),args.Contains("--short"),blank==null?"":blank.Substring(8)));return Environment.ExitCode;}
    }
}
