using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ServerLink
{
    static class CdpJson
    {
        public static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        public static object Get(object value,string key){object result;return Map(value).TryGetValue(key,out result)?result:null;}
        public static string Text(object value){return value==null?"":Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
        public static string Text(object value,string key){return Text(Get(value,key));}
        public static bool Yes(object value){return value is bool&&(bool)value;}
        public static object[] Array(object value){return value as object[]??new object[0];}
        public static object Parse(string value){return new JavaScriptSerializer {MaxJsonLength=4000000}.DeserializeObject(value);}
    }
    public sealed class CdpTarget
    {
        public string Id,Url,Title,Socket;
    }
    // This endpoint is local to the SERVER. It is never published in pairing data.
    sealed class CdpChannel : IDisposable
    {
        readonly ClientWebSocket socket=new ClientWebSocket();int next;
        public CdpChannel(string endpoint,int port)
        {
            Uri uri;
            if(!IsLocalEndpoint(endpoint,port))throw new InvalidOperationException("应用控制通道必须绑定服务器本机回环地址。");uri=new Uri(endpoint);
            using(var cancel=new CancellationTokenSource(6000))socket.ConnectAsync(uri,cancel.Token).GetAwaiter().GetResult();
        }
        internal static bool IsLocalEndpoint(string endpoint,int port)
        {
            Uri uri;IPAddress address;return Uri.TryCreate(endpoint,UriKind.Absolute,out uri)&&uri.Scheme=="ws"&&IPAddress.TryParse(uri.Host,out address)&&IPAddress.IsLoopback(address)&&uri.Port==port&&uri.AbsolutePath.StartsWith("/devtools/",StringComparison.Ordinal)&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0;
        }
        public Dictionary<string,object> Call(string method,object parameters)
        {
            int id=++next;byte[] request=Encoding.UTF8.GetBytes(Json.Write(new {id=id,method=method,@params=parameters??new object()}));
            using(var cancel=new CancellationTokenSource(7000))
            {
                socket.SendAsync(new ArraySegment<byte>(request),WebSocketMessageType.Text,true,cancel.Token).GetAwaiter().GetResult();
                for(int events=0;events<256;events++)
                {
                    using(var buffer=new MemoryStream())
                    {
                        var bytes=new byte[16384];WebSocketReceiveResult frame;
                        do{frame=socket.ReceiveAsync(new ArraySegment<byte>(bytes),cancel.Token).GetAwaiter().GetResult();if(frame.MessageType==WebSocketMessageType.Close)throw new IOException("Claude 应用控制连接已关闭。");if(buffer.Length+frame.Count>Frame.MaximumBytes)throw new IOException("应用控制数据超过上限。");buffer.Write(bytes,0,frame.Count);}while(!frame.EndOfMessage);
                        var message=CdpJson.Map(CdpJson.Parse(Encoding.UTF8.GetString(buffer.ToArray())));object responseId;
                        if(!message.TryGetValue("id",out responseId)||Convert.ToInt32(responseId)!=id)continue;
                        if(message.ContainsKey("error"))throw new InvalidOperationException("Claude 应用不支持此控制操作："+method);
                        return CdpJson.Map(CdpJson.Get(message,"result"));
                    }
                }
                throw new IOException("应用事件过多，未取得操作响应。");
            }
        }
        public void Dispose(){socket.Dispose();}
    }
    public static class CdpDiscovery
    {
        public static bool IsChatUrl(string value){Uri uri;return Uri.TryCreate(value,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host.Equals("claude.ai",StringComparison.OrdinalIgnoreCase)&&uri.Port==443&&uri.UserInfo.Length==0;}
        public static List<CdpTarget> Read(int port)
        {
            if(port<1024||port>65535)throw new ArgumentOutOfRangeException("port");
            var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+port+"/json/list");request.Proxy=null;request.Timeout=3500;request.ReadWriteTimeout=3500;request.AllowAutoRedirect=false;
            using(var response=(HttpWebResponse)request.GetResponse())using(var stream=response.GetResponseStream())using(var reader=new StreamReader(stream,Encoding.UTF8))
            {
                char[] data=new char[Frame.MaximumBytes+1];int count=0,n;while(count<data.Length&&(n=reader.Read(data,count,data.Length-count))>0)count+=n;if(count>Frame.MaximumBytes)throw new IOException("应用目标列表超过上限。");
                return ParseTargets(new string(data,0,count));
            }
        }
        internal static List<CdpTarget> ParseTargets(string json)
        {
            return CdpJson.Array(CdpJson.Parse(json)).Where(v=>IsChatUrl(CdpJson.Text(v,"url"))&&new[]{"page","webview","iframe"}.Contains(CdpJson.Text(v,"type"))).Select(v=>new CdpTarget {Id=CdpJson.Text(v,"id"),Url=CdpJson.Text(v,"url"),Title=CdpJson.Text(v,"title"),Socket=CdpJson.Text(v,"webSocketDebuggerUrl")}).ToList();
        }
    }
    // Maps the same accessible message structure observed in the original desktop,
    // obtained from Chromium itself rather than a Windows input/display session.
    public static class CdpAccessibility
    {
        public static DesktopSnapshot ConvertTree(object[] nodes,CdpTarget target,Dictionary<int,int> backendNodes)
        {
            var result=new DesktopSnapshot {CapturedUtc=DateTime.UtcNow.ToString("o"),ProcessVersion="应用内容接入",WindowTitle=target.Title};var ids=new Dictionary<string,int>();var parents=new Dictionary<string,string>();var protectedNodes=new HashSet<string>();
            foreach(var item in nodes){string key=CdpJson.Text(item,"nodeId");parents[key]=CdpJson.Text(item,"parentId");if(CdpJson.Array(CdpJson.Get(item,"properties")).Any(p=>CdpJson.Text(p,"name")=="protected"&&CdpJson.Yes(CdpJson.Get(CdpJson.Get(p,"value"),"value"))))protectedNodes.Add(key);}
            foreach(var item in nodes){string key=CdpJson.Text(item,"nodeId");if(!ids.ContainsKey(key))ids.Add(key,ids.Count);}
            foreach(var item in nodes)
            {
                string ancestor=CdpJson.Text(item,"nodeId");bool sensitive=false;for(int depth=0;depth<64&&ancestor.Length>0;depth++){if(protectedNodes.Contains(ancestor)){sensitive=true;break;}string parentKey;if(!parents.TryGetValue(ancestor,out parentKey))break;ancestor=parentKey;}if(sensitive)continue;
                string role=CdpJson.Text(CdpJson.Get(item,"role"),"value");bool ignored=CdpJson.Yes(CdpJson.Get(item,"ignored"));string name=CdpJson.Text(CdpJson.Get(item,"name"),"value");var properties=new Dictionary<string,object>();
                foreach(var property in CdpJson.Array(CdpJson.Get(item,"properties")))properties[CdpJson.Text(property,"name")]=CdpJson.Get(CdpJson.Get(property,"value"),"value");
                object secret;if(properties.TryGetValue("protected",out secret)&&CdpJson.Yes(secret))continue;
                string type=role=="RootWebArea"?"Document":role=="button"?"Button":role=="textbox"||role=="searchbox"?"Edit":role=="StaticText"?"Text":role=="link"?"Hyperlink":role=="toolbar"?"ToolBar":role=="status"?"StatusBar":role=="group"||role=="complementary"||role=="navigation"?"Group":"Pane";
                int parent;int id=ids[CdpJson.Text(item,"nodeId")];if(!ids.TryGetValue(CdpJson.Text(item,"parentId"),out parent))parent=-1;
                object disabled,readOnly;bool enabled=!properties.TryGetValue("disabled",out disabled)||!CdpJson.Yes(disabled);bool editable=type=="Edit"&&(!properties.TryGetValue("readonly",out readOnly)||!CdpJson.Yes(readOnly));
                var node=new DesktopNode {Id=id,Parent=parent,Type="ControlType."+type,Name=ignored?"":name,Value=type=="Document"?target.Url:CdpJson.Text(CdpJson.Get(item,"value"),"value"),CanInvoke=type=="Button"&&enabled&&!ignored,CanSetValue=editable&&enabled&&!ignored,CanReadText=type=="Document",Offscreen=false};
                result.Nodes.Add(node);object backend=CdpJson.Get(item,"backendDOMNodeId");if(backend!=null)backendNodes[id]=System.Convert.ToInt32(backend);
            }
            return result;
        }
    }
    sealed class CdpHistoryTarget {public string Handle,Name;public int Backend;}
    public sealed class CdpAdapter : IChatAdapter,IDisposable
    {
        readonly int port;readonly string journalPath;List<SendRecord> sends;CdpChannel channel;CdpTarget target;DesktopSnapshot snapshot;ChatState state;readonly Dictionary<int,int> backend=new Dictionary<int,int>();readonly Dictionary<string,CdpHistoryTarget> history=new Dictionary<string,CdpHistoryTarget>();
        public CdpAdapter(string directory,int port){this.port=port;journalPath=Path.Combine(directory,"operations.dat");sends=File.Exists(journalPath)?Secrets.Load<List<SendRecord>>(journalPath):new List<SendRecord>();}
        public ChatState Read()
        {
            try{return ReadCore();}catch{Dispose();throw;}
        }
        ChatState ReadCore()
        {
            var targets=CdpDiscovery.Read(port);if(targets.Count!=1)throw new InvalidOperationException("未找到唯一的服务器 Claude 内容页面。请在服务器完成首次应用接入配置。候选数："+targets.Count);
            if(channel==null||target.Id!=targets[0].Id){Dispose();history.Clear();target=targets[0];channel=new CdpChannel(target.Socket,port);channel.Call("Accessibility.enable",new {});}else target=targets[0];
            backend.Clear();var tree=channel.Call("Accessibility.getFullAXTree",new {});snapshot=CdpAccessibility.ConvertTree(CdpJson.Array(CdpJson.Get(tree,"nodes")),target,backend);state=DesktopParser.Parse(snapshot);state.Capabilities.Add("server-content-control");
            var visibleHistory=new HashSet<string>();foreach(var node in snapshot.Nodes.Where(n=>DesktopParser.IsHistoryNode(n,snapshot)&&backend.ContainsKey(n.Id)))
            {
                string key=target.Id+":"+backend[node.Id];CdpHistoryTarget item;
                visibleHistory.Add(key);
                if(!history.TryGetValue(key,out item)||item.Name!=node.Name){item=new CdpHistoryTarget {Handle=Guid.NewGuid().ToString("N"),Name=node.Name,Backend=backend[node.Id]};history[key]=item;}
                var match=DesktopParser.HistoryName.Match(node.Name);state.History.Add(new ChatHistory {Handle=item.Handle,Title=match.Groups[2].Value,Status=match.Groups[1].Value});
            }
            foreach(var old in history.Keys.Where(key=>!visibleHistory.Contains(key)).ToArray())history.Remove(old);return state;
        }
        void Invoke(int backendNode)
        {
            var resolved=channel.Call("DOM.resolveNode",new {backendNodeId=backendNode});string objectId=CdpJson.Text(CdpJson.Get(resolved,"object"),"objectId");if(objectId.Length==0)throw new InvalidOperationException("应用控件已变化，请刷新。");
            try
            {
                var invoked=channel.Call("Runtime.callFunctionOn",new {objectId=objectId,functionDeclaration="function(){if(!this.isConnected||this.disabled||this.getAttribute('aria-disabled')==='true')return false;this.click();return true;}",returnByValue=true});
                if(!CdpJson.Yes(CdpJson.Get(CdpJson.Get(invoked,"result"),"value")))throw new InvalidOperationException("应用控件当前不可操作。");
            }finally{channel.Call("Runtime.releaseObject",new {objectId=objectId});}
        }
        public ChatState Open(string handle)
        {
            Read();var item=history.Values.FirstOrDefault(h=>h.Handle==handle);if(item==null)throw new InvalidOperationException("历史入口已变化，请刷新后重试。");CheckDraft();if(state.Generating)throw new InvalidOperationException("原会话正在生成回复，请先等待完成。");
            if(!snapshot.Nodes.Any(n=>n.Name==item.Name&&backend.ContainsKey(n.Id)&&backend[n.Id]==item.Backend))throw new InvalidOperationException("历史入口已变化，请刷新后重试。");Invoke(item.Backend);Thread.Sleep(450);return Read();
        }
        void CheckDraft(){var editor=snapshot.Nodes.FirstOrDefault(DesktopParser.IsComposer);if(editor!=null&&!String.IsNullOrWhiteSpace(editor.Value))throw new InvalidOperationException("服务器存在未发送草稿，请先处理；工具不会覆盖草稿。");}
        public ChatState Send(string requestId,string expectedSession,string text)
        {
            Guid id;if(!Guid.TryParseExact(requestId,"N",out id)||String.IsNullOrWhiteSpace(text)||text.Length>32000)throw new ArgumentException("消息内容或请求编号无效。");Read();if(state.SessionId!=expectedSession)throw new InvalidOperationException("服务器当前会话已变化，消息未发送。请刷新确认会话。");
            string hash=Secrets.Digest(Encoding.UTF8.GetBytes(expectedSession+"\n"+text));if(sends.Any(s=>s.Id==requestId))throw new InvalidOperationException("此消息已处理或提交状态待核对，未再次发送。请查看服务器会话。");
            if(!state.CanSend)throw new InvalidOperationException("当前 Claude 界面不支持安全发送，或正在生成回复。");CheckDraft();var composer=snapshot.Nodes.Single(DesktopParser.IsComposer);int editor;if(!backend.TryGetValue(composer.Id,out editor))throw new InvalidOperationException("当前输入控件不可操作，未提交。");
            var record=new SendRecord {Id=requestId,Hash=hash,Stage="prepared"};sends.Add(record);SaveJournal();channel.Call("DOM.focus",new {backendNodeId=editor});
            channel.Call("Input.insertText",new {text=text});
            List<DesktopNode> buttons=null;DateTime ready=DateTime.UtcNow.AddSeconds(4);
            do{Thread.Sleep(180);Read();if(state.SessionId!=expectedSession)throw new InvalidOperationException("填入草稿后原会话发生变化，未提交。");buttons=snapshot.Nodes.Where(n=>n.Type=="ControlType.Button"&&n.CanInvoke&&DesktopParser.IsSend(n.Name)&&backend.ContainsKey(n.Id)).ToList();if(buttons.Count==1)break;}while(DateTime.UtcNow<ready);
            if(buttons.Count!=1)throw new InvalidOperationException("已填入原应用草稿，但未找到唯一发送按钮；尚未提交。请在原应用核对。");
            record.Stage="submitting";SaveJournal();Invoke(backend[buttons[0].Id]);record.Stage="submitted";SaveJournal();Thread.Sleep(300);return Read();
        }
        public ChatState Stop(string expectedSession){Read();if(state.SessionId!=expectedSession)throw new InvalidOperationException("当前会话已变化。");var buttons=snapshot.Nodes.Where(n=>n.Type=="ControlType.Button"&&n.CanInvoke&&DesktopParser.IsStop(n.Name)&&backend.ContainsKey(n.Id)).ToList();if(buttons.Count!=1)throw new InvalidOperationException("没有可安全停止的当前回复。");Invoke(backend[buttons[0].Id]);Thread.Sleep(200);return Read();}
        void SaveJournal(){if(sends.Count>2000)throw new InvalidOperationException("操作记录已达到上限，请维护记录后继续。");Secrets.Save(journalPath,sends);}
        public void Dispose(){if(channel!=null){channel.Dispose();channel=null;}}
    }
}
