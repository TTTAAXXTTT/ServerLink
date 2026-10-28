using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace ServerLink
{
    sealed class FragmentStream : MemoryStream
    {
        public FragmentStream(byte[] data):base(data){}
        public override int Read(byte[] buffer,int offset,int count){return base.Read(buffer,offset,Math.Min(count,3));}
    }
    static class Validation
    {
        static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        static void Reject(Action action){bool rejected=false;try{action();}catch{rejected=true;}Assert(rejected,"Invalid operation was accepted.");}
        public static int Run(string reportPath)
        {
            var lines=new List<string>();int failed=0;Action<string,Action> test=(name,action)=>{try{action();lines.Add("PASS "+name);}catch(Exception ex){failed++;lines.Add("FAIL "+name+": "+ex.GetBaseException().Message);}};
            string directory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)),"isolated-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            test("Framing handles fragmented reads and rejects oversized or truncated payloads",()=>{
                var original=new WireRequest {Id="one",Op="status",Text="中文"};using(var memory=new MemoryStream()){Frame.Write(memory,original);using(var fragments=new FragmentStream(memory.ToArray()))Assert(Frame.Read<WireRequest>(fragments).Text=="中文","UTF-8 round trip failed");}
                Reject(()=>Frame.Read<WireRequest>(new MemoryStream(new byte[]{127,255,255,255})));Reject(()=>Frame.Read<WireRequest>(new MemoryStream(new byte[]{0,0,0,10,65})));
            });
            test("Pairing requires private protocol, certificate pin and 256-bit token",()=>{
                var pair=new Pairing {Host="127.0.0.1",Port=39443,Pin=new string('a',64),Token=Secrets.Random()};var next=Pairing.Parse(pair.Encode());Assert(next.Host==pair.Host&&next.Token==pair.Token,"Pairing round trip");
                Reject(()=>Pairing.Parse("https://example.com"));Reject(()=>Pairing.Parse(pair.Encode()+"&pin="+pair.Pin));Reject(()=>Pairing.Parse("serverlink://localhost:39443/?pin=wrong&token=abc"));
            });
            test("Credentials and operation records are protected with Windows DPAPI",()=>{
                string path=Path.Combine(directory,"private.dat"),token=Secrets.Random();Secrets.Save(path,new IdentityData {Token=token,Certificate="test"});Assert(Secrets.Load<IdentityData>(path).Token==token,"DPAPI round trip");Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(token),"Plaintext token found");
            });
            test("Real TLS loopback: correct peer works; wrong pin and wrong token rejected; reconnect works",()=>{
                int calls=0;using(var identity=AgentIdentity.Create())using(var server=new BridgeServer(identity,request=>{Interlocked.Increment(ref calls);return new WireReply {Id=request.Id,Ok=true,State=new ChatState {Title="TLS transport fixture"}};}))
                {
                    server.Start(IPAddress.Loopback,0);var pair=new Pairing {Host="127.0.0.1",Port=server.Port,Pin=identity.Pin,Token=identity.Token};
                    using(var connection=new BridgeConnection()){connection.Connect(pair);Assert(connection.Call(new WireRequest {Id="status",Op="status"}).State.Title=="TLS transport fixture","TLS request failed");}
                    Reject(()=>{using(var connection=new BridgeConnection())connection.Connect(new Pairing {Host=pair.Host,Port=pair.Port,Pin=new string('0',64),Token=pair.Token});});
                    Reject(()=>{using(var connection=new BridgeConnection())connection.Connect(new Pairing {Host=pair.Host,Port=pair.Port,Pin=pair.Pin,Token=Secrets.Random()});});Assert(calls==1,"Unauthenticated request reached handler");
                    using(var connection=new BridgeConnection()){connection.Connect(pair);Assert(connection.Call(new WireRequest {Id="again",Op="status"}).Ok,"Reconnect failed");}Assert(calls==2,"Unexpected handler count");
                }
            });
            test("Parser reads original conversation and composer from sanitized real-server accessibility capture",()=>{
                var snapshot=RecordedSample();var state=DesktopParser.Parse(snapshot);
                Assert(state.Messages.Count==4&&state.TotalMessages==4,"Expected four original messages");Assert(state.Messages[0].Role=="user"&&state.Messages[1].Role=="assistant","Role parsing");Assert(state.Messages[1].Text.Contains("391")&&state.Messages[3].Text.Contains("重连后收发成功"),"Original transcript parsing");Assert(state.CanSend,"Composer not detected");Assert(snapshot.Nodes.Count(n=>DesktopParser.HistoryName.IsMatch(n.Name??""))==2,"Recent history parsing");
                Assert(DesktopParser.IsSend("傳送訊息")&&DesktopParser.IsSend("Send message"),"Send labels");Assert(!DesktopParser.IsSend("重試")&&!DesktopParser.IsSend("複製"),"Unsafe button matched");
                Assert(!DesktopParser.IsHistoryNode(new DesktopNode {Id=999,Parent=16,Type="ControlType.Button",Name="閒置 untrusted content",CanInvoke=true},snapshot),"Non-sidebar button accepted as history");
            });
            lines.Add("RESULT "+(failed==0?"PASS":"FAIL")+". Transport tests use loopback fixtures; live desktop sending and RDP-independent operation require separate server validation.");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));File.WriteAllLines(reportPath,lines,new UTF8Encoding(false));return failed==0?0:1;
        }
        // Minimal transcription of the observed server's UIA structure, containing only our test messages.
        // Excludes server paths, user account name, real conversation ID and other personal history.
        static DesktopSnapshot RecordedSample()
        {
            var sample=new DesktopSnapshot {ProcessVersion="3.6556.2"};
            Action<int,int,string,string,string,bool> add=(id,parent,type,name,value,editable)=>sample.Nodes.Add(new DesktopNode {Id=id,Parent=parent,Type="ControlType."+type,Name=name,Value=value,CanSetValue=editable});
            add(16,-1,"Document","测试会话 - Claude","https://claude.ai/chat/00000000-0000-0000-0000-000000000001",false);
            add(37,16,"Group","側邊欄",null,false);add(50,37,"Button","閒置 远程聊天测试",null,false);add(54,37,"Button","閒置 示例历史",null,false);
            add(68,16,"Group","對話訊息",null,false);
            add(70,68,"Group","第 1 則訊息，共 4 則",null,false);add(71,70,"Text","您說：计算 17 × 23",null,false);add(72,70,"Text","计算 17 × 23",null,false);
            add(81,68,"Group","第 2 則訊息，共 4 則",null,false);add(82,81,"Text","Claude 已回覆：远程聊天测试成功",null,false);add(83,81,"Text","远程聊天测试成功",null,false);add(84,81,"Text","17 × 23 = 391",null,false);
            add(95,68,"Group","第 3 則訊息，共 4 則",null,false);add(96,95,"Text","您說：重连测试",null,false);add(97,95,"Text","重连测试",null,false);
            add(106,68,"Group","第 4 則訊息，共 4 則",null,false);add(107,106,"Text","Claude 已回覆：重连后收发成功",null,false);add(108,106,"Text","重连后收发成功",null,false);add(109,106,"Text","上一条计算 17 × 23 的结果是 391。",null,false);
            add(126,16,"Edit","撰寫您要傳送給 Claude 的提示","\n",true);return sample;
        }
    }
}
