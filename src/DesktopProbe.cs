using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ServerLink
{
    public sealed class DesktopNode
    {
        public int Id { get; set; }
        public int Parent { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string AutomationId { get; set; }
        public bool CanInvoke { get; set; }
        public bool CanSetValue { get; set; }
        public bool CanReadText { get; set; }
        public bool Offscreen { get; set; }
        public bool Disabled { get; set; }
    }
    public sealed class DesktopSnapshot
    {
        public string CapturedUtc { get; set; }
        public string WindowTitle { get; set; }
        public string ProcessVersion { get; set; }
        public int ProcessId { get; set; }
        public int SessionId { get; set; }
        public string DocumentText { get; set; }
        public string Error { get; set; }
        public bool Truncated { get; set; }
        public int CodeTranscriptCount { get; set; }
        public List<DesktopNode> Nodes { get; set; }
        public DesktopSnapshot() { Nodes=new List<DesktopNode>();DocumentText="";Error=""; }
    }
    public sealed class DesktopReader
    {
        public readonly Dictionary<int,AutomationElement> Elements=new Dictionary<int,AutomationElement>();
        public AutomationElement Root {get;private set;}
        public DesktopSnapshot Read(bool includeDiagnostics=false)
        {
            var result=new DesktopSnapshot {CapturedUtc=DateTime.UtcNow.ToString("o")};
            Elements.Clear();Root=null;
            var candidates=Process.GetProcessesByName("Claude").Where(p=>p.MainWindowHandle!=IntPtr.Zero && p.SessionId==Process.GetCurrentProcess().SessionId).ToList();
            if(candidates.Count!=1) {result.Error="未找到唯一的当前用户 Claude 主窗口。请保持服务器原版 Claude 已打开。候选窗口数："+candidates.Count;return result;}
            var process=candidates[0];result.ProcessId=process.Id;result.SessionId=process.SessionId;result.WindowTitle=process.MainWindowTitle;
            try{result.ProcessVersion=process.MainModule.FileVersionInfo.FileVersion;}catch{result.ProcessVersion="unknown";}
            Root=AutomationElement.FromHandle(process.MainWindowHandle);
            // Prefetch one coherent tree. Current property/pattern calls on every node caused
            // thousands of cross-process round trips for each polling cycle.
            var timer=Stopwatch.StartNew();var cache=new CacheRequest {TreeScope=TreeScope.Subtree,TreeFilter=Automation.ControlViewCondition,AutomationElementMode=AutomationElementMode.Full};
            foreach(var property in new[]{AutomationElement.NameProperty,AutomationElement.ControlTypeProperty,AutomationElement.AutomationIdProperty,AutomationElement.IsPasswordProperty,AutomationElement.IsOffscreenProperty,AutomationElement.IsEnabledProperty,AutomationElement.IsInvokePatternAvailableProperty,AutomationElement.IsValuePatternAvailableProperty,AutomationElement.IsTextPatternAvailableProperty,ValuePattern.ValueProperty,ValuePattern.IsReadOnlyProperty})cache.Add(property);
            Root=Root.GetUpdatedCache(cache);var stack=new Stack<Tuple<AutomationElement,int,int>>();stack.Push(Tuple.Create(Root,-1,0));
            var texts=new List<string>();int totalCharacters=0;
            while(stack.Count>0)
            {
                if(result.Nodes.Count>=3000||timer.ElapsedMilliseconds>18000||totalCharacters>800000){result.Truncated=true;break;}
                var item=stack.Pop();var element=item.Item1;
                try
                {
                    var current=element.Cached;if(current.IsPassword)continue;
                    object pattern;var node=new DesktopNode {Id=result.Nodes.Count,Parent=item.Item2,Type=current.ControlType.ProgrammaticName,Name=Limit(current.Name,12000),AutomationId=current.AutomationId,Offscreen=current.IsOffscreen,Disabled=!current.IsEnabled};
                    node.CanInvoke=(bool)element.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty);
                    if((bool)element.GetCachedPropertyValue(AutomationElement.IsValuePatternAvailableProperty))
                    {node.CanSetValue=!(bool)element.GetCachedPropertyValue(ValuePattern.IsReadOnlyProperty);node.Value=Limit((string)element.GetCachedPropertyValue(ValuePattern.ValueProperty),50000);}
                    if((bool)element.GetCachedPropertyValue(AutomationElement.IsTextPatternAvailableProperty))
                    {
                        node.CanReadText=true;
                        if(includeDiagnostics&&current.ControlType==ControlType.Document&&element.TryGetCurrentPattern(TextPattern.Pattern,out pattern)){string text=((TextPattern)pattern).DocumentRange.GetText(250000);if(!String.IsNullOrWhiteSpace(text))texts.Add(text);}
                    }
                    result.Nodes.Add(node);Elements[node.Id]=element;totalCharacters+=(node.Name??"").Length+(node.Value??"").Length;
                    if(item.Item3<36)
                    {
                        var children=element.CachedChildren;if(children.Count>600)result.Truncated=true;
                        for(int i=Math.Min(children.Count,600)-1;i>=0;i--)stack.Push(Tuple.Create(children[i],node.Id,item.Item3+1));
                    }
                }catch(ElementNotAvailableException){}catch(InvalidOperationException){}
            }
            result.DocumentText=texts.OrderByDescending(t=>t.Length).FirstOrDefault()??"";
            string projects=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude","projects");
            if(includeDiagnostics&&Directory.Exists(projects))try{result.CodeTranscriptCount=Directory.EnumerateFiles(projects,"*.jsonl",SearchOption.AllDirectories).Take(10001).Count();}catch{}
            return result;
        }
        static string Limit(string text,int max){text=text??"";return text.Length>max?text.Substring(0,max):text;}
    }
    public sealed class ProbeForm : Form
    {
        readonly TextBox report=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,WordWrap=false,Font=new Font("Consolas",10)};
        readonly Button scan=new Button {Text="检查服务器现有 Claude",AutoSize=true,Padding=new Padding(12,8,12,8)};
        readonly Button copy=new Button {Text="复制兼容性报告",AutoSize=true,Padding=new Padding(12,8,12,8),Enabled=false};
        string summary="";
        public ProbeForm()
        {
            Text="ServerLink · 服务器接入验证";ClientSize=new Size(960,680);MinimumSize=new Size(760,500);Font=new Font("Microsoft YaHei UI",10);StartPosition=FormStartPosition.CenterScreen;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=new Padding(18)};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,66));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,56));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.Controls.Add(new Label {Text="此验证程序在服务器运行，读取当前用户已打开的 Claude 的无障碍界面。\r\n不登录账号、不读取凭据、不修改 Claude、不建立远程桌面连接。",Dock=DockStyle.Fill},0,0);
            var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill};buttons.Controls.Add(scan);buttons.Controls.Add(copy);layout.Controls.Add(buttons,0,1);layout.Controls.Add(report,0,2);Controls.Add(layout);
            scan.Click+=(s,e)=>Scan();copy.Click+=(s,e)=>{Clipboard.SetText(summary);};Shown+=(s,e)=>Scan();
        }
        void Scan()
        {
            scan.Enabled=false;copy.Enabled=false;report.Text="正在读取 Claude 的兼容性数据…";
            var thread=new Thread(()=>{
                try
                {
                    var snapshot=new DesktopReader().Read(true);string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkProbe");Directory.CreateDirectory(dir);
                    string json=new JavaScriptSerializer {MaxJsonLength=4000000}.Serialize(snapshot);File.WriteAllText(Path.Combine(dir,"snapshot.json"),json,new UTF8Encoding(false));
                    var b=new StringBuilder();b.AppendLine("ServerLink desktop compatibility probe 0.2.0");b.AppendLine("UTC: "+snapshot.CapturedUtc);b.AppendLine("Claude version: "+snapshot.ProcessVersion);b.AppendLine("Window: "+snapshot.WindowTitle);b.AppendLine("Error: "+snapshot.Error);b.AppendLine("Nodes: "+snapshot.Nodes.Count);b.AppendLine("Document characters: "+snapshot.DocumentText.Length);b.AppendLine("Editable controls: "+snapshot.Nodes.Count(n=>n.CanSetValue));b.AppendLine("Invokable controls: "+snapshot.Nodes.Count(n=>n.CanInvoke));b.AppendLine("Code transcript files: "+snapshot.CodeTranscriptCount);b.AppendLine("Truncated: "+snapshot.Truncated);b.AppendLine("Snapshot: "+Path.Combine(dir,"snapshot.json"));b.AppendLine();
                    b.AppendLine("Current accessible content (inspect privately; do not distribute personal history):");
                    foreach(var n in snapshot.Nodes)b.AppendLine(n.Id+" parent="+n.Parent+" "+n.Type+" invoke="+n.CanInvoke+" edit="+n.CanSetValue+" "+(n.Name??"").Replace("\r"," ").Replace("\n"," "));
                    summary=b.ToString();BeginInvoke(new Action(()=>{report.Text=summary;copy.Enabled=true;scan.Enabled=true;}));
                }catch(Exception ex){BeginInvoke(new Action(()=>{report.Text="检查失败："+ex.Message;scan.Enabled=true;}));}
            });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
        }
    }
    static class ProbeProgram
    {
        [STAThread]static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new ProbeForm());}
    }
}
