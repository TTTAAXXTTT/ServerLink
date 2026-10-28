using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Security.Cryptography.X509Certificates;

namespace ServerLink
{
    public sealed class SavedDraft {public string SessionId {get;set;} public string Text {get;set;} public string SavedUtc {get;set;}}
    static class ServerHost
    {
        public static string DraftPath {get{return Path.Combine(ServerConfiguration.DirectoryPath,"saved-draft.dat");}}
        public static Process[] ClaudeProcesses(){int session=Process.GetCurrentProcess().SessionId;return Process.GetProcessesByName("Claude").Where(p=>p.SessionId==session).ToArray();}
        public static string FindClaude(){var process=ClaudeProcesses().FirstOrDefault(p=>p.MainWindowHandle!=IntPtr.Zero);if(process==null)throw new InvalidOperationException("请先在此服务器用户中打开 Claude，完成首次配置。");return process.MainModule.FileName;}
        public static string LastLaunch="尚未启动。";
        public static void StartClaude(ServerConfiguration config)
        {
            string arguments="--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
            if(!String.IsNullOrEmpty(config.ClaudeApplicationId)){int pid=WindowsAppActivation.Start(config.ClaudeApplicationId,arguments);LastLaunch="MSIX 激活返回进程 "+pid+"；应用身份 "+config.ClaudeApplicationId;return;}
            if(!File.Exists(config.ClaudePath))throw new InvalidOperationException("Claude 安装路径已变化，请重新读取配置。");
            if(config.ClaudePath.IndexOf(@"\WindowsApps\",StringComparison.OrdinalIgnoreCase)>=0)throw new InvalidOperationException("尚未确认原应用的 MSIX 身份，未使用其他账号环境启动。");
            var launched=Process.Start(new ProcessStartInfo(config.ClaudePath,arguments){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(config.ClaudePath)});LastLaunch="桌面启动返回进程 "+launched.Id;
        }
        public static string LaunchStatus(){var processes=ClaudeProcesses();return LastLaunch+"\r\n当前 Claude 进程数："+processes.Length+"；有主窗口："+processes.Count(p=>p.MainWindowHandle!=IntPtr.Zero);}
        public static void RestoreOriginal(ServerConfiguration config){if(ClaudeProcesses().Any(p=>p.MainWindowHandle!=IntPtr.Zero))return;if(!String.IsNullOrEmpty(config.ClaudeApplicationId))WindowsAppActivation.Start(config.ClaudeApplicationId,"");else Process.Start(new ProcessStartInfo(config.ClaudePath){UseShellExecute=true});}
        public static void RestartClaude(ServerConfiguration config)
        {
            var snapshot=new DesktopReader().Read();if(!String.IsNullOrEmpty(snapshot.Error)||snapshot.Truncated)throw new InvalidOperationException("无法完整确认原应用状态，未重启 Claude。");if(DesktopParser.Parse(snapshot).Generating)throw new InvalidOperationException("原应用正在生成回复，请等待完成后再配置。");var editor=snapshot.Nodes.FirstOrDefault(DesktopParser.IsComposer);
            if(editor!=null&&!String.IsNullOrWhiteSpace(editor.Value)){var state=DesktopParser.Parse(snapshot);Secrets.Save(DraftPath,new SavedDraft {SessionId=state.SessionId,Text=editor.Value,SavedUtc=DateTime.UtcNow.ToString("o")});}
            var processes=ClaudeProcesses();foreach(var process in processes.Where(p=>p.MainWindowHandle!=IntPtr.Zero))try{process.CloseMainWindow();}catch(InvalidOperationException){}
            Thread.Sleep(1200);
            foreach(var process in processes)try{if(!process.HasExited&&String.Equals(process.MainModule.FileName,config.ClaudePath,StringComparison.OrdinalIgnoreCase))process.Kill();}catch(InvalidOperationException){}
            Thread.Sleep(800);StartClaude(config);
        }
        public static void StartAgent(bool replace)
        {
            var current=Process.GetProcessesByName("ServerLink.Agent").Where(p=>p.SessionId==Process.GetCurrentProcess().SessionId).ToArray();if(current.Length>0&&!replace)return;
            bool signalled=current.Length>0&&HostSignals.StopAgent();foreach(var process in current)if((!signalled&&!process.CloseMainWindow())||!process.WaitForExit(5000))throw new InvalidOperationException("旧桥接仍在运行，请在服务器关闭旧桥接后重试。");
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ServerLink.Agent.exe");if(!File.Exists(path))throw new FileNotFoundException("请把新版 Agent 与 Host 放在同一文件夹。",path);
            Process.Start(new ProcessStartInfo(path,"--background"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
        }
        public static string PairingText(ServerConfiguration config)
        {
            string path=Path.Combine(ServerConfiguration.DirectoryPath,"identity.dat");if(!File.Exists(path))throw new InvalidOperationException("请先启动新版桥接，生成服务器配对身份。");var data=Secrets.Load<IdentityData>(path);
            using(var cert=new X509Certificate2(Convert.FromBase64String(data.Certificate),(string)null,X509KeyStorageFlags.UserKeySet))return new Pairing {Host=config.Host,Port=config.BridgePort,Pin=Secrets.Digest(cert.RawData),Token=data.Token}.Encode();
        }
        public static void SetStartup(bool enabled)
        {
            using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){if(enabled)key.SetValue("ServerLinkHost","\""+Application.ExecutablePath+"\" --watch");else key.DeleteValue("ServerLinkHost",false);}
        }
    }
    sealed class HostWatch : ApplicationContext
    {
        readonly NotifyIcon icon;readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=10000};bool checking;DateTime nextAttempt=DateTime.MinValue;
        public HostWatch()
        {
            var menu=new ContextMenuStrip();menu.Items.Add("打开服务器配置",null,(s,e)=>new HostForm().Show());menu.Items.Add("退出守护",null,(s,e)=>ExitThread());icon=new NotifyIcon {Icon=SystemIcons.Application,Text="ServerLink 服务器守护",Visible=true,ContextMenuStrip=menu};timer.Tick+=async(s,e)=>await Check();timer.Start();AuthorNotice.ShowForContext(this);
        }
        async Task Check()
        {
            if(checking||DateTime.UtcNow<nextAttempt)return;checking=true;
            try{await Task.Run(()=>{var config=ServerConfiguration.Load();if(config.Adapter!="desktop")return;if(ServerHost.ClaudeProcesses().Length==0)ServerHost.StartClaude(config);ServerHost.StartAgent(false);});}catch{nextAttempt=DateTime.UtcNow.AddSeconds(60);}finally{checking=false;}
        }
        protected override void ExitThreadCore(){timer.Stop();timer.Dispose();icon.Visible=false;icon.Dispose();base.ExitThreadCore();}
    }
    public sealed class HostForm : Form
    {
        readonly TextBox host=new TextBox {Width=230},report=new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
        readonly Button inspect=new Button {Text="读取服务器环境",AutoSize=true},enable=new Button {Text="配置后台运行并重启 Claude",AutoSize=true},bridge=new Button {Text="启动新版桥接",AutoSize=true},copy=new Button {Text="复制检查报告",AutoSize=true},pairing=new Button {Text="复制客户端配对信息",AutoSize=true},rollback=new Button {Text="恢复桌面接入",AutoSize=true};
        readonly CheckBox startup=new CheckBox {Text="登录 Windows 后自动启动守护",AutoSize=true};ServerConfiguration config;
        public HostForm()
        {
            Text="ServerLink · 服务器独立接入配置 "+ApplicationInfo.Version;ClientSize=new Size(950,680);MinimumSize=new Size(850,600);Font=new Font("Microsoft YaHei UI",10);StartPosition=FormStartPosition.CenterScreen;config=ServerConfiguration.Load();host.Text=config.Host;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=1,RowCount=5};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,78));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text="通过 Windows 无障碍接口操作原应用；此版本不启动调试通道。\r\n配置后台运行会重启 Claude；已读取的未发送草稿会先由 Windows 加密保存。"},0,0);
            var address=new FlowLayoutPanel {Dock=DockStyle.Fill};address.Controls.Add(new Label {Text="服务器公网地址",AutoSize=true});address.Controls.Add(host);address.Controls.Add(startup);layout.Controls.Add(address,0,1);
            layout.RowStyles[2].Height=82;var actions=new FlowLayoutPanel {Dock=DockStyle.Fill};foreach(var button in new[]{inspect,enable,bridge,pairing,rollback,copy})actions.Controls.Add(button);layout.Controls.Add(actions,0,2);layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text="守护在已登录的用户会话中运行。重启后无人登录的恢复尚未验收；程序不修改 Windows 登录或锁屏设置。"},0,3);layout.Controls.Add(report,0,4);Controls.Add(layout);
            inspect.Click+=async(s,e)=>await Work(()=>Inspect());enable.Click+=async(s,e)=>{if(!Save())return;bool auto=startup.Checked;await Work(()=>EnableContent(auto));};bridge.Click+=async(s,e)=>{if(!Save())return;await Work(()=>{ServerHost.StartAgent(true);return Inspect();});};pairing.Click+=(s,e)=>{try{Clipboard.SetText(ServerHost.PairingText(config));MessageBox.Show(this,"配对信息已复制，请只粘贴到你自己的客户端。","ServerLink");}catch(Exception ex){report.Text=ex.Message;}};rollback.Click+=async(s,e)=>{config.Adapter="desktop";if(Save())await Work(()=>{ServerHost.StartAgent(true);return "已恢复桌面接入；此模式仍依赖服务器桌面。\r\n"+Inspect();});};copy.Click+=(s,e)=>Clipboard.SetText(report.Text);Shown+=async(s,e)=>await Work(()=>Inspect());
            AuthorNotice.Attach(this);
        }
        bool Save(){try{config.Host=host.Text.Trim();if(config.Host.Length==0)throw new ArgumentException("请填写客户端能访问的服务器地址。");config.Save();return true;}catch(Exception ex){report.Text=ex.Message;return false;}}
        async Task Work(Func<string> action){inspect.Enabled=enable.Enabled=bridge.Enabled=pairing.Enabled=rollback.Enabled=false;try{report.Text=await Task.Run(action);}catch(Exception ex){report.Text="操作未完成："+ex.GetBaseException().Message;}finally{inspect.Enabled=enable.Enabled=bridge.Enabled=pairing.Enabled=rollback.Enabled=true;}}
        string Inspect()
        {
            var runtime=SessionRuntime.Capture(config.Adapter);string path;try{path=ServerHost.FindClaude();}catch(Exception ex){path=ex.Message;}string targets="未启用（当前使用 Windows 无障碍接口）";if(config.Adapter=="content")try{targets=CdpDiscovery.Read(config.ContentPort).Count+" 个 Claude 页面";}catch(Exception ex){targets="未连接（"+ex.GetType().Name+"）";}
            return "Windows 会话："+runtime.SessionId+" / "+runtime.SessionState+"\r\n服务器活动 RDP 会话数："+runtime.ActiveRdpSessions+"\r\n输入桌面："+runtime.InputDesktop+"\r\n接入模式："+config.Adapter+"\r\nClaude 路径："+path+"\r\n本机内容通道："+targets+"\r\n已加密保存草稿："+File.Exists(ServerHost.DraftPath)+"\r\n配置文件："+ServerConfiguration.ConfigPath;
        }
        string EnableContent(bool auto)
        {
            config.ClaudePath=ServerHost.FindClaude();var original=ServerHost.ClaudeProcesses().Single(p=>p.MainWindowHandle!=IntPtr.Zero);config.ClaudeApplicationId=WindowsAppActivation.ReadId(original);
            if(config.ClaudePath.IndexOf(@"\WindowsApps\",StringComparison.OrdinalIgnoreCase)>=0&&String.IsNullOrEmpty(config.ClaudeApplicationId))throw new InvalidOperationException("无法确认服务器原应用的 MSIX 身份，未重启 Claude。");
            ServerHost.RestartClaude(config);
            var deadline=DateTime.UtcNow.AddSeconds(35);
            while(DateTime.UtcNow<deadline){Thread.Sleep(500);if(ServerHost.ClaudeProcesses().Count(p=>p.MainWindowHandle!=IntPtr.Zero)==1){config.Adapter="desktop";config.Save();ServerHost.SetStartup(auto);if(auto)Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--watch"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});return "原应用已启动，已使用标准后台运行参数。请启动新版桥接后实际验证断开远程桌面的收发；启动成功不等于验收通过。\r\n"+ServerHost.LaunchStatus()+"\r\n\r\n"+Inspect();}}
            string diagnostic=ServerHost.LaunchStatus();string restore;
            try{ServerHost.RestoreOriginal(config);restore="已请求重新打开原 Claude，未携带内容接入参数。";}catch(Exception ex){restore="恢复原应用失败："+ex.GetType().Name;}
            throw new InvalidOperationException("未确认原 Claude 主窗口，后台运行配置未通过。\r\n"+diagnostic+"\r\n"+restore);
        }
    }
    static class HostProgram
    {
        [STAThread]static void Main(string[] args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);if(args.Length==1&&args[0]=="--watch"){bool owned;using(var single=new Mutex(true,HostSignals.Name("HostWatch"),out owned)){if(!owned)return;try{Application.Run(new HostWatch());}finally{single.ReleaseMutex();}}}else Application.Run(new HostForm());}
    }
}
