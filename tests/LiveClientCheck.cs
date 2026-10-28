using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ServerLink
{
    // Uses the normal client protocol against the already-running, paired server.
    // No Agent is started, no local listener is created, and pairing secrets are never printed.
    static class LiveClientCheck
    {
        static int Main(string[] args)
        {
            string mode=args.Length>0?args[0]:"--inspect";string report=args.Length>1?args[1]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"live-client-check.txt");var lines=new List<string>();int exit=0;
            try
            {
                if(mode!="--inspect"&&mode!="--open-test"&&mode!="--send-test"&&mode!="--health")throw new ArgumentException("Unknown check mode.");
                string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkClient","connection.dat");var pair=Secrets.Load<Pairing>(profile);
                using(var connection=new BridgeConnection())
                {
                    connection.Connect(pair);lines.Add("PASS Existing server TLS connection and saved pairing authentication.");var initial=Call(connection,mode=="--health"?"health":"status",null,null,null);if(!initial.Ok||initial.State==null)throw new InvalidOperationException("Server read failed: "+initial.Error);var state=initial.State;Summary(lines,state);
                    if(mode!="--inspect"&&mode!="--health")
                    {
                        var entry=state.History.SingleOrDefault(h=>h.Title=="远程聊天测试");if(entry==null)throw new InvalidOperationException("No unique dedicated test conversation found.");
                        var opened=Call(connection,"open",state.SessionId,null,entry.Handle);if(!opened.Ok)throw new InvalidOperationException("Server open refused: "+opened.Error);
                        state=opened.State;for(int i=0;i<5&&state.Title!="远程聊天测试";i++){Thread.Sleep(900);state=Call(connection,"status",null,null,null).State;}
                        if(state.Title!="远程聊天测试")throw new InvalidOperationException("History action returned, but server conversation did not change.");lines.Add("PASS Opened the existing dedicated test conversation.");Summary(lines,state);
                        if(mode=="--send-test")
                        {
                            const string text="这是 ServerLink 0.2.1 发送修复验证。请只回复“发送修复验证成功”，不要调用工具，也不要修改文件。";
                            var sent=Call(connection,"send",state.SessionId,text,null);if(!sent.Ok)throw new InvalidOperationException("Server send refused: "+sent.Error);lines.Add("INFO Server accepted one test send; no automatic retry will be made.");
                            bool found=false;for(int i=0;i<24;i++){Thread.Sleep(1600);state=Call(connection,"status",null,null,null).State;if(state.Messages.Any(m=>m.Role=="assistant"&&m.Text.Contains("发送修复验证成功"))&&state.Messages.Any(m=>m.Role=="user"&&m.Text==text)){found=true;break;}}
                            if(!found)throw new InvalidOperationException("No verified matching user message and assistant reply within the observation window.");lines.Add("PASS Dedicated test message and real assistant reply read back through the client protocol.");
                        }
                    }
                }
            }
            catch(Exception ex){exit=1;lines.Add("FAIL "+ex.GetBaseException().Message);}
            lines.Add("RESULT "+(exit==0?"PASS":"FAIL"));Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));File.WriteAllLines(report,lines);foreach(string line in lines)Console.WriteLine(line);return exit;
        }
        static WireReply Call(IBridgeConnection connection,string op,string session,string text,string handle)
        {
            var reply=connection.Call(new WireRequest {Id=Guid.NewGuid().ToString("N"),Op=op,SessionId=session,Text=text,Handle=handle});if(op=="status"&&(!reply.Ok||reply.State==null))throw new InvalidOperationException("Status failed: "+reply.Error);return reply;
        }
        static void Summary(List<string> lines,ChatState state)
        {
            lines.Add("INFO Current title: "+state.Title+"; messages="+state.Messages.Count+"; history="+state.History.Count+"; CanSend="+state.CanSend+"; Generating="+state.Generating+"; error="+state.Error);
            if(state.Runtime!=null)lines.Add("INFO Server adapter="+state.Runtime.Adapter+"; session="+state.Runtime.SessionId+"; sessionState="+state.Runtime.SessionState+"; activeRdpSessions="+state.Runtime.ActiveRdpSessions+"; inputDesktop="+state.Runtime.InputDesktop+"; runtimeError="+state.Runtime.Error);
            else lines.Add("INFO Server does not yet provide session telemetry; no disconnected-session claim can be verified.");
            if(state.Title=="远程聊天测试")foreach(var message in state.Messages.Where(m=>m.Text.Contains("ServerLink 0.2.1")||m.Text.Contains("发送修复验证成功")))lines.Add("INFO Test message role="+message.Role+"; text="+Json.Write(message.Text));
        }
    }
}
