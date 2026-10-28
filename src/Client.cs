using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerLink
{
    sealed class PairDialog : Form
    {
        readonly TextBox input=new TextBox {Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical};public Pairing Pair {get;private set;}
        public PairDialog(Pairing existing)
        {
            Text="连接你的服务器 · ServerLink";ClientSize=new Size(660,285);MinimumSize=new Size(660,285);MaximumSize=new Size(660,285);StartPosition=FormStartPosition.CenterParent;Font=new Font("Microsoft YaHei UI",10);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=1,RowCount=3};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,65));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            layout.Controls.Add(new Label {Text="在服务器运行 ServerLink.Agent.exe，启动桥接并复制配对信息。\r\n将完整信息粘贴到这里；它包含服务器地址、证书指纹与访问密钥。",Dock=DockStyle.Fill},0,0);layout.Controls.Add(input,0,1);
            var row=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,10,0,0)};var connect=new Button {Text="连接",Width=100};var cancel=new Button {Text="取消",Width=100,DialogResult=DialogResult.Cancel};row.Controls.Add(connect);row.Controls.Add(cancel);layout.Controls.Add(row,0,2);Controls.Add(layout);CancelButton=cancel;
            if(existing!=null)input.Text=existing.Encode();connect.Click+=(s,e)=>{try{Pair=Pairing.Parse(input.Text);DialogResult=DialogResult.OK;Close();}catch(Exception ex){MessageBox.Show(this,ex.Message,"配对信息无效");}};
        }
    }
    sealed class HistoryItem
    {
        public ChatHistory Entry;public override string ToString(){return Entry.Title+"\n"+Entry.Status;}
    }
    public sealed class PendingClientMessage
    {
        public string SessionId;public string Text;public string OriginalDraftText;public int AfterIndex;public DateTime StartedUtc;
    }
    public sealed class LocalClientDraft
    {
        public string Text {get;set;} public string SessionId {get;set;} public string ServerKey {get;set;}
        public PendingClientMessage Pending {get;set;} public bool Uncertain {get;set;}
    }
    sealed class PendingDraftRecovery {public string SessionId,Text;public bool Prepared;public DateTime StartedUtc;}
    public sealed partial class ClientForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]struct ScrollPoint {public int X,Y;}
        [StructLayout(LayoutKind.Sequential)]struct ScrollInfo {public uint Size,Mask;public int Minimum,Maximum;public uint Page;public int Position,Track;}
        [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern IntPtr SendScrollMessage(IntPtr window,int message,IntPtr wParam,ref ScrollPoint point);
        [DllImport("user32.dll")]static extern bool GetScrollInfo(IntPtr window,int bar,ref ScrollInfo info);
        readonly Color ink=Color.FromArgb(25,44,57),teal=Color.FromArgb(7,121,111),muted=Color.FromArgb(90,105,116);
        readonly ListBox history=new ListBox {Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=65,BackColor=Color.FromArgb(25,44,57),ForeColor=Color.White};
        readonly RichTextBox conversation=new RichTextBox {Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.None,DetectUrls=false,BackColor=Color.White,Font=new Font("Microsoft YaHei UI",11),ScrollBars=RichTextBoxScrollBars.Vertical};
        readonly TextBox composer=new TextBox {Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical,Font=new Font("Microsoft YaHei UI",11),Enabled=false};
        readonly Label title=new Label {Text="连接你的服务器",Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",17,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft};
        readonly Label detail=new Label {Text="Claude 留在服务器执行，本地只显示会话数据。",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(90,105,116),TextAlign=ContentAlignment.MiddleLeft};
        readonly Label status=new Label {Text="尚未连接",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(90,105,116),AutoEllipsis=true};
        readonly Button connect=new Button {Text="连接服务器",AutoSize=true},send=new Button {Text="发送",Width=105,Enabled=false},stop=new Button {Text="停止回复",Width=105,Enabled=false},refresh=new Button {Text="刷新",Width=80,Enabled=false},recover=new Button {Text="取回服务器草稿",AutoSize=true,Enabled=false};
        readonly System.Windows.Forms.Timer poll=new System.Windows.Forms.Timer {Interval=2500};
        readonly System.Windows.Forms.Timer draftSave=new System.Windows.Forms.Timer {Interval=600};
        readonly bool persistDraft;
        readonly string profilePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkClient","connection.dat");
        readonly SemaphoreSlim commandGate=new SemaphoreSlim(1,1);
        readonly ToolTip statusTip=new ToolTip {AutoPopDelay=20000};
        IBridgeConnection connection;Pairing pair;ChatState state;PendingClientMessage pendingMessage;int incompleteReads;bool interfaceReady=true;
        PendingDraftRecovery recoveringDraft;
        bool working,refreshing,updating,closing,uncertain,sending;string renderKey="",historyKey="",actionError="",draftSession="";
        readonly List<ChatMessage> renderedMessages=new List<ChatMessage>();readonly List<int> renderedEnds=new List<int>();string renderedSession="";
        public ClientForm():this(true){}
        internal ClientForm(bool loadSavedPair)
        {
            persistDraft=loadSavedPair;
            Text="ServerLink · 服务器 Claude 工作台";ClientSize=new Size(1180,790);MinimumSize=new Size(980,690);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(246,248,250);
            var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,265));root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var side=new TableLayoutPanel {Dock=DockStyle.Fill,BackColor=ink,Padding=new Padding(20),RowCount=5,ColumnCount=1,Margin=Padding.Empty};side.RowStyles.Add(new RowStyle(SizeType.Absolute,74));side.RowStyles.Add(new RowStyle(SizeType.Absolute,54));side.RowStyles.Add(new RowStyle(SizeType.Absolute,38));side.RowStyles.Add(new RowStyle(SizeType.Percent,100));side.RowStyles.Add(new RowStyle(SizeType.Absolute,75));
            side.Controls.Add(new Label {Text="ServerLink\r\n服务器会话 · 本地操作",Dock=DockStyle.Fill,ForeColor=Color.White,Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold)},0,0);connect.Dock=DockStyle.Fill;Style(connect,true);side.Controls.Add(connect,0,1);side.Controls.Add(new Label {Text="服务器最近会话",ForeColor=Color.FromArgb(180,199,207),Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft},0,2);side.Controls.Add(history,0,3);
            var footer=new Label {Text=ApplicationInfo.Version+" · 单行空行修复\r\n项目与 Code 任务接入尚未启用",ForeColor=Color.FromArgb(180,199,207),Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft};side.Controls.Add(footer,0,4);root.Controls.Add(side,0,0);
            var main=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(28,18,28,16),ColumnCount=1,RowCount=7,Margin=Padding.Empty};main.RowStyles.Add(new RowStyle(SizeType.Absolute,55));main.RowStyles.Add(new RowStyle(SizeType.Absolute,42));main.RowStyles.Add(new RowStyle(SizeType.Percent,100));main.RowStyles.Add(new RowStyle(SizeType.Absolute,30));main.RowStyles.Add(new RowStyle(SizeType.Absolute,100));main.RowStyles.Add(new RowStyle(SizeType.Absolute,50));main.RowStyles.Add(new RowStyle(SizeType.Absolute,32));main.Controls.Add(title,0,0);main.Controls.Add(detail,0,1);
            var body=new Panel {Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(20)};body.Controls.Add(conversation);main.Controls.Add(body,0,2);main.Controls.Add(new Label {Text="Enter 换行，Ctrl+Enter 发送。单行文字末尾的空白行会自动忽略。",ForeColor=muted,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);main.Controls.Add(composer,0,4);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(0,8,0,0)};Style(send,true);Style(stop,false);Style(refresh,false);Style(recover,false);var clear=new Button {Text="清空本地草稿",AutoSize=true};Style(clear,false);actions.Controls.Add(send);actions.Controls.Add(stop);actions.Controls.Add(refresh);actions.Controls.Add(clear);actions.Controls.Add(recover);main.Controls.Add(actions,0,5);main.Controls.Add(status,0,6);root.Controls.Add(main,1,0);Controls.Add(root);
            conversation.Text="请先连接服务器桥接程序。\n\n这里会读取服务器 Claude 已有会话中的真实消息。\n程序不会在本地安装 Claude，也不会显示远程桌面。";
            history.DrawItem+=DrawHistory;history.SelectedIndexChanged+=async(s,e)=>{if(updating||working||connection==null)return;var item=history.SelectedItem as HistoryItem;if(item!=null)await Command("open",null,item.Entry.Handle);};
            connect.Click+=async(s,e)=>await Connect();send.Click+=async(s,e)=>await Send();stop.Click+=async(s,e)=>await Command("stop",null,null);refresh.Click+=async(s,e)=>await RefreshState();poll.Tick+=async(s,e)=>await RefreshState();
            recover.Click+=async(s,e)=>await RecoverServerDraft();
            composer.KeyDown+=async(s,e)=>{if(e.Control&&e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await Send();}};
            composer.TextChanged+=(s,e)=>{if(String.IsNullOrWhiteSpace(composer.Text))draftSession="";else if(String.IsNullOrEmpty(draftSession)&&state!=null)draftSession=state.SessionId;UpdateControls();if(persistDraft){draftSave.Stop();draftSave.Start();}};
            clear.Click+=(s,e)=>{composer.Clear();uncertain=false;pendingMessage=null;actionError="";UpdateControls();};
            status.TextChanged+=(s,e)=>statusTip.SetToolTip(status,status.Text);status.Click+=(s,e)=>{if(!String.IsNullOrEmpty(status.Text))MessageBox.Show(this,status.Text,"当前状态");};
            draftSave.Tick+=(s,e)=>{draftSave.Stop();SaveLocalDraft();};
            FormClosed+=(s,e)=>{closing=true;SaveLocalDraft();draftSave.Stop();draftSave.Dispose();poll.Stop();poll.Dispose();statusTip.Dispose();if(connection!=null)connection.Dispose();};
            if(loadSavedPair&&File.Exists(profilePath))try{pair=Secrets.Load<Pairing>(profilePath);}catch{status.Text="保存的配对信息无法读取，请重新配对。";}
            if(persistDraft&&pair!=null&&File.Exists(DraftPath))try{var saved=Secrets.Load<LocalClientDraft>(DraftPath);if(saved.ServerKey==ServerKey(pair)){composer.Text=saved.Text??"";draftSession=saved.SessionId??"";pendingMessage=saved.Pending;uncertain=saved.Uncertain||pendingMessage!=null;status.Text=pendingMessage!=null?"已恢复待核对的消息，请连接原服务器确认送达；不会自动重发。":String.IsNullOrEmpty(composer.Text)?"已载入服务器配对信息。":"已恢复本地未发送草稿，请连接并核对原会话。";}}catch{status.Text="本地草稿无法读取，未自动覆盖。";}
        }
        string DraftPath {get{return Path.Combine(Path.GetDirectoryName(profilePath),"local-draft.dat");}}
        static string ServerKey(Pairing value){return value.Host.ToLowerInvariant()+":"+value.Port+"/"+value.Pin;}
        void SaveLocalDraft(){if(!persistDraft||pair==null)return;try{Secrets.Save(DraftPath,new LocalClientDraft {Text=composer.Text,SessionId=draftSession,ServerKey=ServerKey(pair),Pending=pendingMessage,Uncertain=uncertain});}catch{if(!closing)status.Text="本地草稿保存失败，请复制文字后再退出。";}}
        void Style(Button button,bool primary){button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=primary?0:1;button.BackColor=primary?teal:Color.White;button.ForeColor=primary?Color.White:ink;button.Height=34;button.Margin=new Padding(0,0,10,0);}
        void DrawHistory(object sender,DrawItemEventArgs e)
        {
            if(e.Index<0)return;bool selected=(e.State&DrawItemState.Selected)!=0;using(var brush=new SolidBrush(selected?Color.FromArgb(45,72,85):ink))e.Graphics.FillRectangle(brush,e.Bounds);var item=(HistoryItem)history.Items[e.Index];var rect=new Rectangle(e.Bounds.X+10,e.Bounds.Y+10,e.Bounds.Width-16,25);TextRenderer.DrawText(e.Graphics,item.Entry.Title,Font,rect,Color.White,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);rect.Y+=27;rect.Height=20;using(var small=new Font(Font.FontFamily,8.5f))TextRenderer.DrawText(e.Graphics,item.Entry.Status,small,rect,Color.FromArgb(168,195,205),TextFormatFlags.SingleLine);
        }
        async Task Connect()
        {
            if(working)return;using(var dialog=new PairDialog(pair)){if(dialog.ShowDialog(this)!=DialogResult.OK)return;if(pair!=null&&ServerKey(pair)!=ServerKey(dialog.Pair)&&(!String.IsNullOrEmpty(composer.Text)||pendingMessage!=null)){status.Text="请先处理当前服务器的草稿和待核对消息，再切换到其他服务器。";return;}pair=dialog.Pair;}
            working=true;poll.Stop();UpdateControls();status.Text="正在连接并验证服务器证书…";await commandGate.WaitAsync();
            try
            {
                if(closing)return;if(connection!=null)connection.Dispose();connection=new BridgeConnection();var current=connection;
                await Task.Run(()=>current.Connect(pair));if(closing)return;Secrets.Save(profilePath,pair);actionError="";status.Text=pendingMessage==null?"已连接 "+pair.Host+":"+pair.Port:"连接已恢复，正在核对上一条消息是否送达…";connect.Text="切换 / 重连";historyKey="";renderKey="";
            }
            catch(Exception ex){if(connection!=null)connection.Dispose();connection=null;status.Text="连接失败："+ex.GetBaseException().Message;}
            finally{commandGate.Release();working=false;if(!closing)UpdateControls();}
            if(!closing&&connection!=null){await RefreshState();if(connection!=null)poll.Start();}
        }
        async Task RefreshState()
        {
            if(working||refreshing||connection==null||closing||!commandGate.Wait(0))return;
            refreshing=true;var current=connection;
            try
            {
                var request=new WireRequest {Id=Guid.NewGuid().ToString("N"),Op="status"};var reply=await Task.Run(()=>current.Call(request));
                if(closing||connection!=current)return;if(!reply.Ok){if(String.IsNullOrEmpty(actionError))status.Text="同步失败："+reply.Error;return;}if(reply.State!=null)Render(reply.State);
            }
            catch(Exception ex){if(!closing&&connection==current){poll.Stop();connection.Dispose();connection=null;status.Text="连接已中断："+ex.GetBaseException().Message;}}
            finally{refreshing=false;commandGate.Release();if(!closing)UpdateControls();}
        }
        async Task Send()
        {
            if(working||connection==null||uncertain||recoveringDraft!=null||!interfaceReady||state==null||!state.CanSend||String.IsNullOrWhiteSpace(composer.Text))return;
            if(composer.Text.Length>32000){status.Text="消息过长，请限制在 32000 字符以内。";return;}
            if(!String.IsNullOrEmpty(draftSession)&&draftSession!=state.SessionId){actionError="服务器当前会话已变化，请核对草稿后重新输入；消息尚未发送。";status.Text=actionError;return;}
            string original=composer.Text;await Command("send",PrepareSubmission(original),null,original);
        }
        static string PrepareSubmission(string text)
        {
            // A single content line followed only by blank lines has no second
            // paragraph to preserve. Do not pass its terminal empty editor blocks
            // through UIA, where the application may add/remove a final sentinel.
            int end=text.IndexOfAny(new[]{'\r','\n'});if(end<0)return text;
            string first=text.Substring(0,end);
            if(String.IsNullOrWhiteSpace(first)||System.Text.RegularExpressions.Regex.IsMatch(first,@"^[ ]{0,3}(`{3,}|~{3,})"))return text;
            for(int i=end;i<text.Length;i++)if(text[i]!='\r'&&text[i]!='\n'&&text[i]!=' '&&text[i]!='\t')return text;
            return first;
        }
        async Task RecoverServerDraft()
        {
            if(working||recoveringDraft!=null||state==null||!state.DraftRecoveryAvailable||connection==null)return;
            string text=state.DraftText;if(!String.IsNullOrEmpty(composer.Text)&&NormalizeMessage(composer.Text)!=NormalizeMessage(text)){status.Text="本地与服务器草稿不同，请先保存本地文字；未改动两边草稿。";return;}
            if(pendingMessage!=null&&(pendingMessage.SessionId!=state.SessionId||NormalizeMessage(pendingMessage.Text)!=NormalizeMessage(text))){status.Text="另一条消息仍在核对，未改动服务器草稿。";return;}
            composer.Text=text;draftSession=state.SessionId;SaveLocalDraft();recoveringDraft=new PendingDraftRecovery {SessionId=state.SessionId,Text=text,Prepared=state.DraftPreparedByBridge,StartedUtc=DateTime.UtcNow};await Command("recover-draft",text,null);if(recoveringDraft!=null&&connection!=null)await RefreshState();
        }
        async Task Command(string operation,string text,string handle,string originalDraft=null)
        {
            if(working||connection==null||closing)return;
            if(operation=="open"&&(pendingMessage!=null||recoveringDraft!=null||uncertain)){status.Text="上一条操作仍在核对，暂不切换会话。请先确认结果。";return;}
            var current=connection;var request=new WireRequest {Id=Guid.NewGuid().ToString("N"),Op=operation,SessionId=state==null?"":state.SessionId,Text=text,Handle=handle};
            int previousIndex=state==null?0:Math.Max(state.TotalMessages,state.Messages.Count==0?0:state.Messages.Max(m=>m.Index));
            working=true;sending=operation=="send";actionError="";UpdateControls();if(sending)status.Text=refreshing?"等待当前同步完成后发送…":"正在发送…";
            await commandGate.WaitAsync();
            try
            {
                if(closing)return;if(connection!=current){status.Text="连接已变化，操作未提交。请重新连接后重试。";return;}
                if(operation=="send")
                {
                    if(state!=null&&state.SessionId==request.SessionId)previousIndex=Math.Max(previousIndex,Math.Max(state.TotalMessages,state.Messages.Count==0?0:state.Messages.Max(m=>m.Index)));
                    pendingMessage=new PendingClientMessage {SessionId=request.SessionId,Text=text,OriginalDraftText=originalDraft??text,AfterIndex=previousIndex,StartedUtc=DateTime.UtcNow};
                    uncertain=true;
                    SaveLocalDraft();
                }
                var reply=await Task.Run(()=>current.Call(request));if(closing)return;
                if(!reply.Ok)
                {
                    if(operation=="recover-draft"&&reply.Error=="草稿已备份，但未确认服务器输入框已清空，请刷新核对。"){status.Text="草稿已备份，正在等待服务器输入框更新…";return;}
                    if(operation=="recover-draft")recoveringDraft=null;
                    actionError=reply.Error;status.Text=actionError;
                    if(operation=="send"){uncertain=reply.ErrorCode!="not_submitted"&&!IsConfirmedRejection(reply.Error);if(!uncertain)pendingMessage=null;}return;
                }
                if(operation=="send")uncertain=true;if(reply.State!=null)Render(reply.State);
                if(operation=="recover-draft"&&recoveringDraft!=null)status.Text="草稿已备份，正在等待服务器输入框更新…";
                if(operation=="send")status.Text=pendingMessage==null?"已确认消息进入服务器原会话。":"已提交发送操作，正在核对服务器消息；程序不会重复发送。";
            }
            catch(Exception ex){if(closing)return;poll.Stop();if(operation=="send")uncertain=true;actionError="连接已中断："+ex.GetBaseException().Message+(operation=="send"?" 请重连后核对消息，程序不会自动重发。":"");status.Text=actionError;if(connection!=null)connection.Dispose();connection=null;}
            finally{commandGate.Release();working=false;sending=false;if(!closing){SaveLocalDraft();UpdateControls();}}
        }
        static void Enable(Control control,bool enabled){if(control.Enabled!=enabled)control.Enabled=enabled;}
        void UpdateControls(){bool active=connection!=null;Enable(send,active&&!working&&!uncertain&&recoveringDraft==null&&interfaceReady&&state!=null&&state.CanSend&&!String.IsNullOrWhiteSpace(composer.Text));send.Text=sending?"发送中…":pendingMessage!=null?"核对中…":"发送";Enable(composer,active);Enable(stop,active&&!working&&state!=null&&state.Generating);Enable(refresh,active&&!working);Enable(recover,active&&!working&&recoveringDraft==null&&state!=null&&state.DraftRecoveryAvailable);Enable(history,active&&!working&&pendingMessage==null&&recoveringDraft==null&&!uncertain);Enable(connect,!working);poll.Interval=pendingMessage!=null||recoveringDraft!=null||state!=null&&state.Generating?700:2500;}
        static bool IsConfirmedRejection(string error)
        {
            // These messages are emitted by the existing 0.2.0 server before InvokePattern.Invoke.
            // All other failures retain the conservative unknown-outcome lock.
            string[] rejected={"服务器存在未发送草稿，请先处理；工具不会覆盖草稿。","服务器当前会话已变化，消息未发送。请刷新确认会话。","当前 Claude 界面不支持安全发送，或正在生成回复。","消息内容或请求编号无效。","已填入原应用草稿，但未找到唯一发送按钮；尚未提交。请在原应用核对。"};
            return rejected.Contains(error??"");
        }
        static string NormalizeMessage(string text){return (text??"").Replace("\r\n","\n").TrimEnd('\n');}
        internal static bool MatchesDisplayedMessage(string displayed,string submitted)
        {
            // Claude's rendered paragraphs collapse repeated/edge blank lines.
            // Only compare a newly indexed user message; do not change submitted
            // text or use this comparison when deciding to clear an edited draft.
            if(NormalizeDisplayedMessage(displayed)==NormalizeDisplayedMessage(submitted))return true;
            return MatchesRenderedCode(displayed,submitted);
        }
        static bool MatchesRenderedCode(string displayed,string submitted)
        {
            var parts=new List<string>();var outside=new List<string>();var code=new List<string>();char fence='\0';int fenceLength=0;bool found=false;
            foreach(string line in (submitted??"").Replace("\r\n","\n").Split('\n'))
            {
                var marker=System.Text.RegularExpressions.Regex.Match(line,@"^[ ]{0,3}(`{3,}|~{3,})(.*)$");
                if(fence!='\0')
                {
                    if(marker.Success&&marker.Groups[1].Value[0]==fence&&marker.Groups[1].Value.Length>=fenceLength&&String.IsNullOrWhiteSpace(marker.Groups[2].Value))
                    {
                        // The accessible body omits the fence/language, but its
                        // code text, indentation and internal blank lines must match.
                        parts.Add(System.Text.RegularExpressions.Regex.Escape(String.Join("\n",code)));code.Clear();fence='\0';found=true;
                    }
                    else code.Add(line);
                    continue;
                }
                if(marker.Success)
                {
                    string text=NormalizeDisplayedMessage(String.Join("\n",outside));if(text.Length>0)parts.Add(ParagraphPattern(text));outside.Clear();fence=marker.Groups[1].Value[0];fenceLength=marker.Groups[1].Value.Length;
                }
                else outside.Add(line);
            }
            if(!found||fence!='\0')return false;
            string tail=NormalizeDisplayedMessage(String.Join("\n",outside));if(tail.Length>0)parts.Add(ParagraphPattern(tail));
            string pattern=@"\A\n*"+String.Join(@"\n{1,2}",parts)+@"\n*\z";
            try{return System.Text.RegularExpressions.Regex.IsMatch((displayed??"").Replace("\r\n","\n"),pattern,System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));}
            catch(System.Text.RegularExpressions.RegexMatchTimeoutException){return false;}
        }
        static string ParagraphPattern(string text)
        {
            var result=new StringBuilder();string[] lines=text.Split('\n');
            for(int i=0;i<lines.Length;i++)
            {
                if(i>0)result.Append("\\n");
                if(lines[i]==""){result.Append(@"(?:[ \t]*\n)+");i++;if(i==lines.Length)break;}
                result.Append(System.Text.RegularExpressions.Regex.Escape(lines[i]));
            }
            return result.ToString();
        }
        static string NormalizeDisplayedMessage(string text)
        {
            var lines=new List<string>();char fence='\0';int fenceLength=0;
            foreach(string line in (text??"").Replace("\r\n","\n").Split('\n'))
            {
                var marker=System.Text.RegularExpressions.Regex.Match(line,@"^[ ]{0,3}(`{3,}|~{3,})(.*)$");
                if(fence!='\0')
                {
                    lines.Add(line);
                    if(marker.Success&&marker.Groups[1].Value[0]==fence&&marker.Groups[1].Value.Length>=fenceLength&&String.IsNullOrWhiteSpace(marker.Groups[2].Value))fence='\0';
                    continue;
                }
                if(marker.Success){fence=marker.Groups[1].Value[0];fenceLength=marker.Groups[1].Value.Length;lines.Add(line);continue;}
                if(String.IsNullOrWhiteSpace(line)){if(lines.Count>0&&lines[lines.Count-1]!="")lines.Add("");}
                else lines.Add(line);
            }
            if(fence=='\0'&&lines.Count>0&&lines[lines.Count-1]=="")lines.RemoveAt(lines.Count-1);
            return String.Join("\n",lines);
        }
        bool ConfirmPending(ChatState value)
        {
            if(pendingMessage==null||value.SessionId!=pendingMessage.SessionId)return false;
            if(!value.Messages.Any(m=>m.Index>pendingMessage.AfterIndex&&m.Role=="user"&&MatchesDisplayedMessage(m.Text,pendingMessage.Text)))return false;
            string draft=pendingMessage.OriginalDraftText??pendingMessage.Text;pendingMessage=null;uncertain=false;actionError="";if((composer.Text??"").Replace("\r\n","\n")== (draft??"").Replace("\r\n","\n"))composer.Clear();SaveLocalDraft();return true;
        }
        void RenderConversation(ChatState value)
        {
            bool sameSession=renderedSession==value.SessionId;int common=0;
            if(sameSession)while(common<renderedMessages.Count&&common<value.Messages.Count){var a=renderedMessages[common];var b=value.Messages[common];if(a.Index!=b.Index||a.Role!=b.Role||a.Text!=b.Text)break;common++;}
            var scroll=new ScrollPoint();SendScrollMessage(conversation.Handle,0x04DD,IntPtr.Zero,ref scroll);
            var info=new ScrollInfo {Size=(uint)Marshal.SizeOf(typeof(ScrollInfo)),Mask=7};bool bottom=!GetScrollInfo(conversation.Handle,1,ref info)||info.Position>=info.Maximum-(int)info.Page-4;
            int selection=conversation.SelectionStart,length=conversation.SelectionLength;SendMessage(conversation.Handle,11,IntPtr.Zero,IntPtr.Zero);
            try
            {
                conversation.ReadOnly=false;
                bool extending=sameSession&&common==renderedMessages.Count-1&&renderedMessages.Count==value.Messages.Count&&common>=0&&renderedMessages[common].Index==value.Messages[common].Index&&renderedMessages[common].Role==value.Messages[common].Role&&(value.Messages[common].Text??"").StartsWith(renderedMessages[common].Text??"",StringComparison.Ordinal);
                if(extending){conversation.Select(Math.Max(0,conversation.TextLength-2),0);conversation.SelectedText=(value.Messages[common].Text??"").Substring((renderedMessages[common].Text??"").Length);renderedEnds[common]=conversation.TextLength;}
                else
                {
                    int start=common==0?0:renderedEnds[common-1];conversation.Select(start,conversation.TextLength-start);conversation.SelectedText="";
                    if(renderedEnds.Count>common)renderedEnds.RemoveRange(common,renderedEnds.Count-common);
                    if(value.Messages.Count==0)conversation.AppendText(String.IsNullOrWhiteSpace(value.Error)?"当前没有可读取的聊天消息。":value.Error);
                    using(var bold=new Font(Font.FontFamily,10,FontStyle.Bold))using(var normal=new Font(Font.FontFamily,11))for(int i=common;i<value.Messages.Count;i++){var message=value.Messages[i];conversation.SelectionColor=message.Role=="user"?teal:ink;conversation.SelectionFont=bold;conversation.AppendText(message.Role=="user"?"你\n":message.Role=="assistant"?"Claude\n":"消息\n");conversation.SelectionColor=ink;conversation.SelectionFont=normal;conversation.AppendText(message.Text+"\n\n");renderedEnds.Add(conversation.TextLength);}
                }
                renderedSession=value.SessionId;renderedMessages.Clear();renderedMessages.AddRange(value.Messages.Select(m=>new ChatMessage {Index=m.Index,Role=m.Role,Text=m.Text}));
                if(!sameSession||bottom){conversation.Select(conversation.TextLength,0);conversation.ScrollToCaret();}
                else{conversation.Select(Math.Min(selection,conversation.TextLength),Math.Min(length,Math.Max(0,conversation.TextLength-selection)));SendScrollMessage(conversation.Handle,0x04DE,IntPtr.Zero,ref scroll);}
            }
            finally{conversation.ReadOnly=true;SendMessage(conversation.Handle,11,new IntPtr(1),IntPtr.Zero);conversation.Invalidate();}
        }
        void Render(ChatState value)
        {
            bool confirmed=ConfirmPending(value);
            bool recovered=recoveringDraft!=null&&value.SessionId==recoveringDraft.SessionId&&String.IsNullOrEmpty(value.Error)&&value.DraftText!=null&&String.IsNullOrWhiteSpace(value.DraftText);
            if(recovered){if(recoveringDraft.Prepared||pendingMessage==null){pendingMessage=null;uncertain=false;}recoveringDraft=null;actionError="";SaveLocalDraft();}
            else if(recoveringDraft!=null&&DateTime.UtcNow-recoveringDraft.StartedUtc>TimeSpan.FromSeconds(15)){recoveringDraft=null;actionError="取回操作尚未确认，本地文字已保留。请刷新核对服务器草稿后再处理。";}
            bool incomplete=state!=null&&value.SessionId==state.SessionId&&String.IsNullOrEmpty(value.Error)&&value.Messages.Count==0&&value.History.Count==0&&(state.Messages.Count>0||state.History.Count>0);
            if(incomplete)
            {
                incompleteReads++;if(incompleteReads>=3)interfaceReady=false;
                if(String.IsNullOrEmpty(actionError))status.Text=interfaceReady?"服务器界面正在变化，正在重新读取…":"服务器界面暂不可操作，请确认 Claude 已打开且桌面可交互。";
                return;
            }
            incompleteReads=0;interfaceReady=true;
            state=value;title.Text=String.IsNullOrWhiteSpace(value.Title)?"服务器 Claude":value.Title;detail.Text="原会话 · Claude "+value.Version+(value.Generating?" · 正在回复":"")+(value.TotalMessages>value.Messages.Count?" · 已读取 "+value.Messages.Count+" / "+value.TotalMessages+" 条消息":"");
            if(value.Runtime!=null)detail.Text+=(value.Runtime.Adapter=="content"?" · 应用内容接入":" · 桌面接入")+(value.Runtime.ActiveRdpSessions==0?" · 服务器无活动 RDP 会话":value.Runtime.ActiveRdpSessions>0?" · 服务器仍有 RDP 连接":" · 服务器连接状态未知");
            string messagesKey=Json.Write(value.Messages);if(messagesKey!=renderKey)
            {
                renderKey=messagesKey;RenderConversation(value);
            }
            string nextHistory=Json.Write(value.History);if(nextHistory!=historyKey){historyKey=nextHistory;updating=true;history.BeginUpdate();try{history.Items.Clear();foreach(var item in value.History)history.Items.Add(new HistoryItem {Entry=item});}finally{history.EndUpdate();updating=false;}}
            if(recovered)status.Text=pendingMessage==null?"草稿已取回，两端均已加密备份。请核对会话后决定是否发送。":"草稿已取回并备份；先前发送结果仍需核对，程序不会自动重发。";
            else if(recoveringDraft!=null)status.Text="草稿已备份，正在等待服务器输入框更新…";
            else if(confirmed)status.Text="已确认消息进入服务器原会话。";
            else if(!String.IsNullOrEmpty(actionError))status.Text=actionError;
            else if(pendingMessage!=null)status.Text=DateTime.UtcNow-pendingMessage.StartedUtc>TimeSpan.FromSeconds(45)?"尚未确认消息是否送达，请核对原会话；程序未自动重发。":"正在核对上一条消息是否进入服务器原会话…";
            else if(!String.IsNullOrWhiteSpace(value.Error))status.Text=value.Error;
            else if(!uncertain&&!working)status.Text="已同步服务器 · "+DateTime.Now.ToString("HH:mm:ss")+(value.CanSend?"":" · 当前未开放安全发送");
        }
    }
    static class ClientProgram
    {
        [STAThread]static int Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);using(var form=new ClientForm()){AuthorNotice.Attach(form);Application.Run(form);}return 0;}
    }
}
