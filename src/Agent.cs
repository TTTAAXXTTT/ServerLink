using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerLink
{
    public sealed class AgentIdentity : IDisposable
    {
        public X509Certificate2 Certificate {get;private set;}
        public string Token {get;private set;}
        public string Pin {get{return Secrets.Digest(Certificate.RawData);}}
        public static AgentIdentity Create()
        {
            using(var rsa=RSA.Create())
            {
                rsa.KeySize=2048;var request=new CertificateRequest("CN=ServerLink Private Bridge",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
                var usages=new OidCollection();usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages,false));
                using(var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(1)))
                return Import(new IdentityData {Certificate=Convert.ToBase64String(certificate.Export(X509ContentType.Pfx)),Token=Secrets.Random()});
            }
        }
        static AgentIdentity Import(IdentityData data){return new AgentIdentity {Certificate=new X509Certificate2(Convert.FromBase64String(data.Certificate),(string)null,X509KeyStorageFlags.UserKeySet|X509KeyStorageFlags.Exportable),Token=data.Token};}
        public static AgentIdentity Load(string directory)
        {
            string path=Path.Combine(directory,"identity.dat");if(File.Exists(path))return Import(Secrets.Load<IdentityData>(path));var identity=Create();Secrets.Save(path,new IdentityData {Certificate=Convert.ToBase64String(identity.Certificate.Export(X509ContentType.Pfx)),Token=identity.Token});return identity;
        }
        public void Dispose(){if(Certificate!=null)Certificate.Dispose();}
    }
    public sealed class DesktopWorker : IDisposable
    {
        readonly BlockingCollection<Action> queue=new BlockingCollection<Action>(16);readonly Thread thread;IChatAdapter adapter;Exception initializationError;readonly string adapterMode;
        public DesktopWorker(string directory)
        {
            var config=ServerConfiguration.Load();adapterMode=config.Adapter=="content"?"content":"desktop";
            thread=new Thread(()=>{try{adapter=adapterMode=="content"?(IChatAdapter)new CdpAdapter(directory,config.ContentPort):new DesktopAdapter(directory);}catch(Exception ex){initializationError=ex;}foreach(var action in queue.GetConsumingEnumerable())action();});thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
        }
        public WireReply Handle(WireRequest request)
        {
            if(request.Op=="health")return new WireReply {Id=request.Id,Ok=true,State=new ChatState {Runtime=SessionRuntime.Capture(adapterMode),Error=initializationError==null?"":"接入组件初始化失败。"}};
            var elapsed=Stopwatch.StartNew();
            var completion=new TaskCompletionSource<WireReply>();if(!queue.TryAdd(()=>{
                try
                {
                    long queued=elapsed.ElapsedMilliseconds;elapsed.Restart();
                    if(initializationError!=null&&request.Op!="health")throw new InvalidOperationException("桥接数据无法读取，请检查服务器数据目录。",initializationError);ChatState state;
                    switch(request.Op){case "health":state=new ChatState {Error=initializationError==null?"":"接入组件初始化失败。"};break;case "status":state=adapter.Read();break;case "open":state=adapter.Open(request.Handle);break;case "send":state=adapter.Send(request.Id,request.SessionId,request.Text);break;case "stop":state=adapter.Stop(request.SessionId);break;case "recover-draft":var recovery=adapter as IDraftRecoveryAdapter;if(recovery==null)throw new InvalidOperationException("当前接入方式不支持取回草稿。");state=recovery.RecoverDraft(request.SessionId,request.Text);break;default:throw new ArgumentException("不支持的操作。");}
                    state.Runtime=SessionRuntime.Capture(adapterMode);state.Runtime.QueueMilliseconds=queued;state.Runtime.OperationMilliseconds=elapsed.ElapsedMilliseconds;completion.SetResult(new WireReply {Id=request.Id,Ok=true,State=state});
                }catch(Exception ex){completion.SetResult(new WireReply {Id=request.Id,Ok=false,Error=ex.Message,ErrorCode=ex is RequestNotSubmittedException?"not_submitted":null});}
            }))return new WireReply {Id=request.Id,Ok=false,Error="服务器操作队列已满，请稍后重试。"};
            if(!completion.Task.Wait(26000))return new WireReply {Id=request.Id,Ok=false,Error="服务器界面读取超时，操作结果需要核对。请勿重复发送。"};return completion.Task.Result;
        }
        public void Dispose(){queue.CompleteAdding();}
    }
    public sealed class BridgeServer : IDisposable
    {
        readonly AgentIdentity identity;readonly Func<WireRequest,WireReply> handler;readonly SemaphoreSlim capacity=new SemaphoreSlim(4);readonly ConcurrentDictionary<TcpClient,byte> clients=new ConcurrentDictionary<TcpClient,byte>();
        TcpListener listener;volatile bool stopped;
        public int Port {get;private set;}
        public Action<string> Status;
        public BridgeServer(AgentIdentity identity,Func<WireRequest,WireReply> handler){this.identity=identity;this.handler=handler;}
        public void Start(IPAddress address,int port)
        {
            listener=new TcpListener(address,port);listener.Start(8);Port=((IPEndPoint)listener.LocalEndpoint).Port;stopped=false;Task.Run((Action)Accept);
        }
        void Accept()
        {
            while(!stopped)
            {
                TcpClient client;try{client=listener.AcceptTcpClient();}catch{if(stopped)return;continue;}
                if(!capacity.Wait(0)){client.Close();continue;}clients.TryAdd(client,0);Task.Run(()=>Serve(client));
            }
        }
        void Serve(TcpClient client)
        {
            try
            {
                client.NoDelay=true;client.ReceiveTimeout=12000;client.SendTimeout=12000;
                using(var tls=new SslStream(client.GetStream(),false))
                {
                    tls.AuthenticateAsServer(identity.Certificate,false,SslProtocols.Tls12,false);var first=Frame.Read<WireRequest>(tls);
                    if(first==null||first.Op!="hello"||first.Id!="hello"||!Secrets.Equal(first.Token,identity.Token)){Frame.Write(tls,new WireReply {Id="hello",Ok=false,Error="配对认证失败。"});return;}
                    Frame.Write(tls,new WireReply {Id="hello",Ok=true});if(Status!=null)Status("客户端已通过配对认证。");client.ReceiveTimeout=45000;client.SendTimeout=30000;
                    for(int i=0;i<20000&&!stopped;i++)
                    {
                        var request=Frame.Read<WireRequest>(tls);if(request==null||String.IsNullOrWhiteSpace(request.Id)||request.Id.Length>100)throw new IOException("请求格式无效。");
                        Frame.Write(tls,handler(request));
                    }
                }
            }catch(IOException){}catch(AuthenticationException){}catch(System.Security.Cryptography.CryptographicException){}catch(Exception){if(Status!=null)Status("客户端连接结束或数据无效。");}
            finally{byte unused;clients.TryRemove(client,out unused);client.Close();capacity.Release();}
        }
        public void Dispose(){stopped=true;if(listener!=null)listener.Stop();foreach(var client in clients.Keys)client.Close();}
    }
    public sealed class AgentForm : Form
    {
        readonly TextBox address=new TextBox {Width=260,Text=Environment.MachineName};readonly NumericUpDown port=new NumericUpDown {Minimum=1024,Maximum=65535,Value=39443,Width=100};
        readonly TextBox pairing=new TextBox {Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical};readonly TextBox status=new TextBox {Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical};
        readonly Button start=new Button {Text="启动桥接",AutoSize=true};readonly Button copy=new Button {Text="复制配对信息",AutoSize=true,Enabled=false};readonly Button inspect=new Button {Text="检查 Claude 接入",AutoSize=true};
        readonly string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkAgent");AgentIdentity identity;DesktopWorker worker;BridgeServer server;
        public AgentForm():this(false){}
        public AgentForm(bool background)
        {
            Text="ServerLink · 服务器桥接";ClientSize=new Size(860,620);MinimumSize=new Size(760,540);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(246,248,250);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=6};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,75));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,55));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,75));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.Controls.Add(new Label {Text="服务器原会话桥接 · "+ApplicationInfo.Version+"\r\n使用原应用无障碍接口。客户端通过 TLS 连接本程序。",Dock=DockStyle.Fill},0,0);
            var row=new FlowLayoutPanel {Dock=DockStyle.Fill};row.Controls.Add(new Label {Text="公网地址 / 域名",AutoSize=true,Padding=new Padding(0,6,0,0)});row.Controls.Add(address);row.Controls.Add(new Label {Text="端口",AutoSize=true,Padding=new Padding(0,6,0,0)});row.Controls.Add(port);layout.Controls.Add(row,0,1);
            var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill};buttons.Controls.Add(start);buttons.Controls.Add(copy);buttons.Controls.Add(inspect);layout.Controls.Add(buttons,0,2);layout.Controls.Add(pairing,0,3);
            layout.Controls.Add(new Label {Text="配对信息包含访问密钥，仅交给你自己的客户端。程序不自动修改 Windows 或云防火墙。",Dock=DockStyle.Fill},0,4);layout.Controls.Add(status,0,5);Controls.Add(layout);
            start.Click+=(s,e)=>StartBridge();copy.Click+=(s,e)=>{if(!String.IsNullOrEmpty(pairing.Text))Clipboard.SetText(pairing.Text);};inspect.Click+=async(s,e)=>{inspect.Enabled=false;try{var reply=await Task.Run(()=>worker.Handle(new WireRequest {Id=Guid.NewGuid().ToString("N"),Op="status"}));Log(reply.Ok&&reply.State!=null?"Claude "+reply.State.Version+"；原会话："+reply.State.Title+"；消息 "+reply.State.Messages.Count+"；最近会话 "+reply.State.History.Count+"；可安全发送："+reply.State.CanSend+"；"+reply.State.Error:reply.Error);}catch(Exception ex){Log(ex.Message);}finally{inspect.Enabled=true;}};
            identity=AgentIdentity.Load(directory);worker=new DesktopWorker(directory);Log("已生成 / 载入本机配对身份。证书有效期至 "+identity.Certificate.NotAfter.ToString("yyyy-MM-dd")+"。\r\n请先填写客户端可到达的服务器地址，再启动桥接。\r\n桌面适配已验证版本：3.6556.2。Code、项目及后台任务接入尚未启用。");
            var config=ServerConfiguration.Load();address.Text=config.Host;port.Value=config.BridgePort;
            if(background){ShowInTaskbar=false;WindowState=FormWindowState.Minimized;Shown+=(s,e)=>{StartBridge();Hide();};}
            AuthorNotice.Attach(this);
            FormClosed+=(s,e)=>{if(server!=null)server.Dispose();worker.Dispose();identity.Dispose();};
        }
        void StartBridge()
        {
            try
            {
                if(server!=null){server.Dispose();server=null;start.Text="启动桥接";copy.Enabled=false;address.Enabled=true;port.Enabled=true;pairing.Text="";Log("桥接已停止。");return;}
                Uri host;if(!Uri.TryCreate("https://"+address.Text.Trim(),UriKind.Absolute,out host)||host.Host.Length==0||host.PathAndQuery!="/"||host.UserInfo.Length>0)throw new ArgumentException("请填写服务器 IP 或域名，不要包含路径或端口。");
                server=new BridgeServer(identity,worker.Handle);server.Status=Log;server.Start(IPAddress.Any,(int)port.Value);pairing.Text=new Pairing {Host=host.Host,Port=(int)port.Value,Pin=identity.Pin,Token=identity.Token}.Encode();start.Text="停止桥接";copy.Enabled=true;address.Enabled=false;port.Enabled=false;Log("桥接已启动，TCP "+port.Value+"。需要允许客户端到达此端口；请仅为需要使用的地址配置防火墙。");
            }catch(Exception ex){if(server!=null){server.Dispose();server=null;}Log("无法启动："+ex.Message);}
        }
        void Log(string text){if(IsDisposed)return;if(InvokeRequired){try{BeginInvoke(new Action(()=>Log(text)));}catch{}return;}status.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+text+Environment.NewLine);}
    }
    static class AgentProgram
    {
        [STAThread]static int Main(string[] args)
        {
            if(args.Length>0&&args[0]=="--self-test")return Validation.Run(args.Length>1?args[1]:"validation.txt");Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                bool owned;using(var single=new Mutex(true,HostSignals.Name("Agent"),out owned))
                {
                    if(!owned){if(!(args.Length==1&&args[0]=="--background"))using(var notice=AuthorNotice.Create())notice.ShowDialog();return 0;}
                    try
                    {
                        using(var stop=new EventWaitHandle(false,EventResetMode.AutoReset,HostSignals.Name("AgentStop")))using(var form=new AgentForm(args.Length==1&&args[0]=="--background"))
                        {
                            var wait=ThreadPool.RegisterWaitForSingleObject(stop,(s,t)=>{try{form.BeginInvoke(new Action(()=>form.Close()));}catch(InvalidOperationException){}},null,Timeout.Infinite,true);
                            try{Application.Run(form);}finally{wait.Unregister(null);}
                        }
                    }finally{single.ReleaseMutex();}
                }
                return 0;
            }catch(Exception ex){MessageBox.Show("无法启动桥接："+ex.Message,"ServerLink");return 1;}
        }
    }
}
