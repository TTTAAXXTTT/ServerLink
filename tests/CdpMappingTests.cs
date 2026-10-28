using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerLink
{
    static class CdpMappingTests
    {
        static readonly List<string> results=new List<string>();static int failures;
        static void Assert(bool value,string description){if(!value)throw new Exception(description);}
        static void Check(string name,Action action){try{action();results.Add("PASS "+name);}catch(Exception ex){failures++;results.Add("FAIL "+name+": "+ex.Message);}}
        static object Node(string id,string parent,string role,string name,string value=null,object[] properties=null)
        {
            return new {nodeId=id,parentId=parent,role=new {value=role},name=new {value=name},value=new {value=value},backendDOMNodeId=Int32.Parse(id)+100,properties=properties??new object[0]};
        }
        static object Property(string name,bool value){return new {name=name,value=new {value=value}};}
        static object[] Fixture()
        {
            return new[]{Node("1","","RootWebArea","验证会话 - Claude"),Node("2","1","group","側邊欄"),Node("3","2","button","閒置 验证会话"),Node("4","1","group","第 1 則訊息，共 2 則"),Node("5","4","StaticText","您說："),Node("6","4","StaticText","测试输入"),Node("7","6","InlineTextBox","测试输入"),Node("8","1","group","第 2 則訊息，共 2 則"),Node("9","8","StaticText","Claude 已回覆："),Node("10","8","StaticText","测试回复"),Node("11","8","toolbar","工具"),Node("12","11","StaticText","不属于正文"),Node("13","1","textbox","撰寫您要傳送給 Claude 的提示",""),Node("14","1","button","傳送訊息",null,new[]{Property("disabled",true)})};
        }
        static int Main(string[] args)
        {
            Check("Application targets are limited to the exact HTTPS Claude origin and page-like targets",()=>{
                var pages=CdpDiscovery.ParseTargets(Json.Write(new[]{new {id="allowed",url="https://claude.ai/chat/11111111-1111-1111-1111-111111111111",title="test",type="webview",webSocketDebuggerUrl="ws://127.0.0.1:39444/devtools/page/allowed"},new {id="worker",url="https://claude.ai/worker.js",title="worker",type="service_worker",webSocketDebuggerUrl=""},new {id="other",url="https://claude.ai.example.com/",title="other",type="page",webSocketDebuggerUrl=""}}));Assert(pages.Count==1&&pages[0].Id=="allowed","Target scope widened");Assert(!CdpDiscovery.IsChatUrl("https://user@claude.ai/")&&!CdpDiscovery.IsChatUrl("https://claude.ai:444/")&&!CdpDiscovery.IsChatUrl("http://claude.ai/"),"Unexpected origin accepted");
            });
            Check("Control sockets require a literal loopback endpoint on the configured port",()=>{
                Assert(CdpChannel.IsLocalEndpoint("ws://127.0.0.1:39444/devtools/page/test",39444),"Expected local endpoint rejected");foreach(var endpoint in new[]{"ws://192.0.2.1:39444/devtools/page/test","ws://127.0.0.1:9222/devtools/page/test","ws://user@127.0.0.1:39444/devtools/page/test","ws://127.0.0.1:39444/other","ws://127.0.0.1:39444/devtools/page/test?secret=value"})Assert(!CdpChannel.IsLocalEndpoint(endpoint,39444),"Unsafe endpoint accepted");
            });
            Check("Chromium accessibility maps original messages, identity, history and editor without duplicate inline text",()=>{
                var backend=new Dictionary<int,int>();var snapshot=CdpAccessibility.ConvertTree(CdpJson.Array(CdpJson.Parse(Json.Write(Fixture()))),new CdpTarget {Url="https://claude.ai/chat/11111111-1111-1111-1111-111111111111",Title="验证会话"},backend);var state=DesktopParser.Parse(snapshot);Assert(state.SessionId=="11111111-1111-1111-1111-111111111111"&&state.Messages.Count==2&&state.TotalMessages==2,"Message identity mapping failed");Assert(state.Messages[0].Role=="user"&&state.Messages[0].Text=="测试输入","Inline text duplicated");Assert(state.Messages[1].Role=="assistant"&&state.Messages[1].Text=="测试回复","Toolbar leaked into message");Assert(state.CanSend&&snapshot.Nodes.Count(n=>DesktopParser.IsHistoryNode(n,snapshot))==1,"History or composer missing");Assert(!snapshot.Nodes.First(n=>n.Name=="傳送訊息").CanInvoke,"Disabled control was invokable");
            });
            Check("Protected fields and their descendants are excluded",()=>{
                var nodes=new[]{Node("1","","RootWebArea","test"),Node("2","1","textbox","password","secret",new[]{Property("protected",true)}),Node("3","2","StaticText","secret child")};var result=CdpAccessibility.ConvertTree(CdpJson.Array(CdpJson.Parse(Json.Write(nodes))),new CdpTarget {Url="https://claude.ai/",Title="test"},new Dictionary<int,int>());Assert(result.Nodes.Count==1&&!Json.Write(result).Contains("secret"),"Protected data retained");
            });
            Check("Server configuration rejects conflicting ports, invalid addresses and unknown adapter modes before saving",()=>{
                var config=new ServerConfiguration {Host="example.com",Adapter="content",ClaudeApplicationId="ExamplePackage!App"};config.Validate();var restored=Json.Read<ServerConfiguration>(Json.Write(config));Assert(restored.ClaudeApplicationId==config.ClaudeApplicationId,"MSIX identity not retained by configuration serialization");
                foreach(var bad in new[]{new ServerConfiguration {Host="example.com",BridgePort=39444,ContentPort=39444},new ServerConfiguration {Host="user@example.com"},new ServerConfiguration {Host="example.com/path"},new ServerConfiguration {Host="example.com:5555"},new ServerConfiguration {Host="example.com",Adapter="unknown"}}){bool rejected=false;try{bad.Validate();}catch(ArgumentException){rejected=true;}Assert(rejected,"Invalid configuration accepted");}
            });
            results.Add("RESULT "+(failures==0?"PASS":"FAIL")+". Synthetic mapping/security contract fixtures only; no Agent, listener, credentials, Claude runtime or original-app behavioral claim.");File.WriteAllLines(args[0],results);foreach(var line in results)Console.WriteLine(line);return failures==0?0:1;
        }
    }
}
