using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace ServerLink
{
    public static class DesktopParser
    {
        public static readonly Regex MessageGroup=new Regex(@"(?:第\s*(\d+)\s*[則条條].*?共\s*(\d+)\s*[則条條])|(?:Message\s+(\d+)\s+of\s+(\d+))",RegexOptions.IgnoreCase);
        public static readonly Regex HistoryName=new Regex(@"^(閒置|闲置|空闲|Idle|執行中|运行中|Working|需要注意|Needs attention)\s+(.+)$",RegexOptions.IgnoreCase);
        public static ChatState Parse(DesktopSnapshot snapshot)
        {
            var state=new ChatState {Version=snapshot.ProcessVersion,CapturedUtc=snapshot.CapturedUtc,Error=snapshot.Truncated?"服务器界面内容超出本次读取范围，暂未开放安全发送。":snapshot.Error};var children=snapshot.Nodes.ToLookup(n=>n.Parent);
            var document=snapshot.Nodes.FirstOrDefault(n=>n.Type=="ControlType.Document"&&(n.Value??"").StartsWith("https://claude.ai/",StringComparison.OrdinalIgnoreCase));
            if(document!=null)
            {
                var match=Regex.Match(document.Value,@"^https://claude\.ai/chat/([a-f0-9-]{36})(?:[/?#]|$)",RegexOptions.IgnoreCase);state.SessionId=match.Success?match.Groups[1].Value:"new";state.Title=(document.Name??"").Replace(" - Claude","");
            }
            foreach(var group in snapshot.Nodes.Where(n=>n.Type=="ControlType.Group"&&MessageGroup.IsMatch(n.Name??"")))
            {
                var match=MessageGroup.Match(group.Name);int index=Int32.Parse(match.Groups[1].Success?match.Groups[1].Value:match.Groups[3].Value),total=Int32.Parse(match.Groups[2].Success?match.Groups[2].Value:match.Groups[4].Value);state.TotalMessages=Math.Max(state.TotalMessages,total);
                var nodes=new List<DesktopNode>();var pending=new Stack<DesktopNode>(children[group.Id].Reverse());
                while(pending.Count>0){var node=pending.Pop();if(node.Type=="ControlType.ToolBar"||node.Type=="ControlType.StatusBar")continue;nodes.Add(node);foreach(var child in children[node.Id].Reverse())pending.Push(child);}
                var marker=nodes.FirstOrDefault(n=>n.Type=="ControlType.Text"&&Regex.IsMatch(n.Name??"",@"^(您說[：:]|你说[：:]|您说[：:]|You said:|Claude 已回覆[：:]|Claude 已回复[：:]|Claude replied:)",RegexOptions.IgnoreCase));
                string role=marker==null?"unknown":Regex.IsMatch(marker.Name,@"^(您|你|You)",RegexOptions.IgnoreCase)?"user":"assistant";
                var text=new StringBuilder();
                foreach(var node in nodes.Where(n=>n.Type=="ControlType.Text"&&n!=marker&&!String.IsNullOrEmpty(n.Name)))
                {
                    // Chromium exposes soft line breaks as their own Text nodes, but
                    // adjacent paragraph nodes have no intervening newline node.
                    // Never trim a text run: that also removes code indentation.
                    string run=node.Name.Replace("\r\n","\n");
                    if(text.Length>0&&text[text.Length-1]!='\n'&&run[0]!='\n')text.Append(role=="user"?"\n\n":"\n");
                    text.Append(run);
                }
                state.Messages.Add(new ChatMessage {Index=index,Role=role,Text=text.ToString()});
            }
            state.Generating=snapshot.Nodes.Any(n=>n.Type=="ControlType.Button"&&IsStop(n.Name));
            var composers=snapshot.Nodes.Where(IsComposer).ToList();state.DraftText=composers.Count==1?composers[0].Value:null;
            state.CanSend=!snapshot.Truncated&&String.IsNullOrEmpty(snapshot.Error)&&!state.Generating&&snapshot.Nodes.Count(IsComposer)==1&&document!=null;
            state.Capabilities.Add("read-current-chat");state.Capabilities.Add("open-recent-chat");if(state.CanSend)state.Capabilities.Add("send-to-current-chat");if(state.Generating)state.Capabilities.Add("stop-response");
            return state;
        }
        public static bool IsComposer(DesktopNode n){return n.Type=="ControlType.Edit"&&!n.Disabled&&n.CanSetValue&&Regex.IsMatch(n.Name??"",@"Claude.*(?:prompt|message)|(?:提示|訊息|消息|提示词).*Claude|(?:撰寫|撰写).*Claude|^Reply to Claude$|^Message Claude$",RegexOptions.IgnoreCase);}
        public static bool IsHistoryNode(DesktopNode node,DesktopSnapshot snapshot){return node.Type=="ControlType.Button"&&node.CanInvoke&&HistoryName.IsMatch(node.Name??"")&&snapshot.Nodes.Any(parent=>parent.Id==node.Parent&&Regex.IsMatch(parent.Name??"",@"^(側邊欄|侧边栏|Sidebar)$",RegexOptions.IgnoreCase));}
        public static bool IsStop(string name){return Regex.IsMatch(name??"",@"^(停止|Stop)(?:回應|回复|响应| generating| response)?$",RegexOptions.IgnoreCase);}
        public static bool IsSend(string name){return Regex.IsMatch(name??"",@"^(傳送|发送|發送|Send|Submit|提交)(?:訊息|消息|提示| message| prompt)?(?:\s*\(.+\))?$",RegexOptions.IgnoreCase);}
    }
    sealed class HistoryTarget {public string Handle;public string Name;public AutomationElement Element;public DateTime Seen;}
    public sealed class SendRecord {public string Id {get;set;} public string Hash {get;set;} public string Stage {get;set;}}
    public sealed class DesktopAdapter : IChatAdapter,IDraftRecoveryAdapter
    {
        readonly DesktopReader reader=new DesktopReader();readonly Dictionary<string,HistoryTarget> history=new Dictionary<string,HistoryTarget>();readonly string journalPath;
        List<SendRecord> sends;DesktopSnapshot snapshot;ChatState state;
        public DesktopAdapter(string directory){journalPath=Path.Combine(directory,"operations.dat");sends=File.Exists(journalPath)?Secrets.Load<List<SendRecord>>(journalPath):new List<SendRecord>();}
        public ChatState Read()
        {
            snapshot=reader.Read();state=DesktopParser.Parse(snapshot);DateTime now=DateTime.UtcNow;
            state.DraftPreparedByBridge=CanRecoverDraft(sends,state.SessionId,state.DraftText);state.DraftRecoveryAvailable=!String.IsNullOrWhiteSpace(state.DraftText)&&state.CanSend;
            foreach(var key in history.Where(p=>now-p.Value.Seen>TimeSpan.FromMinutes(10)).Select(p=>p.Key).ToList())history.Remove(key);
            foreach(var node in snapshot.Nodes.Where(n=>DesktopParser.IsHistoryNode(n,snapshot)))
            {
                var element=reader.Elements[node.Id];string identity=snapshot.ProcessId+":"+String.Join(".",element.GetRuntimeId());HistoryTarget target;
                if(!history.TryGetValue(identity,out target)||target.Name!=node.Name){target=new HistoryTarget {Handle=Guid.NewGuid().ToString("N"),Name=node.Name,Element=element};history[identity]=target;}
                target.Seen=now;var match=DesktopParser.HistoryName.Match(node.Name);state.History.Add(new ChatHistory {Handle=target.Handle,Status=match.Groups[1].Value,Title=match.Groups[2].Value});
            }
            return state;
        }
        public static bool CanRecoverDraft(IEnumerable<SendRecord> records,string session,string text)
        {
            if(String.IsNullOrWhiteSpace(text)||String.IsNullOrEmpty(session))return false;string normalized=text.Replace("\r\n","\n");string hash=Secrets.Digest(Encoding.UTF8.GetBytes(session+"\n"+text)),withoutSentinel=Secrets.Digest(Encoding.UTF8.GetBytes(session+"\n"+(normalized.EndsWith("\n",StringComparison.Ordinal)?normalized.Substring(0,normalized.Length-1):normalized)));var last=records.LastOrDefault(r=>r.Hash==hash||r.Hash==withoutSentinel);return last!=null&&last.Stage=="prepared";
        }
        public ChatState RecoverDraft(string expectedSession,string expectedText)
        {
            Read();if(state.SessionId!=expectedSession||state.DraftText!=expectedText||!state.DraftRecoveryAvailable)throw new InvalidOperationException("服务器会话或草稿已变化，没有清除内容。请刷新核对。");
            var editor=snapshot.Nodes.Single(DesktopParser.IsComposer);var value=(ValuePattern)reader.Elements[editor.Id].GetCurrentPattern(ValuePattern.Pattern);if(value.Current.Value!=expectedText)throw new InvalidOperationException("服务器草稿已变化，没有清除内容。请刷新核对。");
            // Recovery moves only the currently visible draft; it never infers an earlier Invoke outcome.
            Secrets.Save(Path.Combine(Path.GetDirectoryName(journalPath),"recovered-draft.dat"),new RecoveredServerDraft {SessionId=expectedSession,Text=expectedText,SavedUtc=DateTime.UtcNow.ToString("o")});
            value.SetValue("");Thread.Sleep(100);var updated=Read();if(updated.SessionId!=expectedSession||!String.IsNullOrWhiteSpace(updated.DraftText))throw new InvalidOperationException("草稿已备份，但未确认服务器输入框已清空，请刷新核对。");return updated;
        }
        public ChatState Open(string handle)
        {
            Read();var target=history.Values.FirstOrDefault(h=>h.Handle==handle);if(target==null||target.Element.Current.Name!=target.Name)throw new InvalidOperationException("历史入口已变化，请刷新后重试。");
            if(state.Generating)throw new InvalidOperationException("原会话正在生成回复，请先等待完成。");
            var editor=snapshot.Nodes.FirstOrDefault(DesktopParser.IsComposer);if(editor!=null&&!String.IsNullOrWhiteSpace(editor.Value))throw new InvalidOperationException("原应用存在未发送草稿，请先处理草稿。");
            ((InvokePattern)target.Element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();Thread.Sleep(650);return Read();
        }
        public ChatState Send(string requestId,string expectedSession,string text)
        {
            bool invocationAttempted=false;
            try
            {
            Guid id;if(!Guid.TryParseExact(requestId,"N",out id)||String.IsNullOrWhiteSpace(text)||text.Length>32000)throw new ArgumentException("消息内容或请求编号无效。");
            Read();if(state.SessionId!=expectedSession)throw new InvalidOperationException("服务器当前会话已变化，消息未发送。请刷新确认会话。");
            string hash=Secrets.Digest(Encoding.UTF8.GetBytes(expectedSession+"\n"+text));var previous=sends.FirstOrDefault(r=>r.Id==requestId);
            if(previous!=null){if(previous.Hash!=hash)throw new InvalidOperationException("请求编号已被其他消息使用。");throw new InvalidOperationException("此消息已处理或提交状态待核对，未再次发送。请查看服务器会话。");}
            if(!state.CanSend)throw new InvalidOperationException("当前 Claude 界面不支持安全发送，或正在生成回复。");
            var composer=snapshot.Nodes.Single(DesktopParser.IsComposer);bool resume=DraftMatches(composer.Value,text)&&CanRecoverDraft(sends,expectedSession,composer.Value);if(!String.IsNullOrWhiteSpace(composer.Value)&&!resume)throw new InvalidOperationException("服务器存在未发送草稿，请先处理；工具不会覆盖草稿。");
            var record=new SendRecord {Id=requestId,Hash=hash,Stage="prepared"};sends.Add(record);SaveJournal();
            if(!resume)((ValuePattern)reader.Elements[composer.Id].GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);
            List<DesktopNode> button=null;var ready=DateTime.UtcNow.AddSeconds(4);
            do{Thread.Sleep(120);Read();if(state.SessionId!=expectedSession)throw new InvalidOperationException("填入草稿后原会话发生变化，未提交。");var current=snapshot.Nodes.Where(DesktopParser.IsComposer).ToList();if(current.Count!=1||!DraftMatches(current[0].Value,text))throw new InvalidOperationException("服务器草稿已变化，未提交。请核对原应用。");button=snapshot.Nodes.Where(n=>n.Type=="ControlType.Button"&&!n.Disabled&&n.CanInvoke&&DesktopParser.IsSend(n.Name)).ToList();if(button.Count==1)break;}while(DateTime.UtcNow<ready);
            if(button.Count!=1)throw new InvalidOperationException("已填入原应用草稿，但未找到唯一发送按钮；尚未提交。请在原应用核对。");
            record.Stage="submitting";SaveJournal();var invoke=(InvokePattern)reader.Elements[button[0].Id].GetCurrentPattern(InvokePattern.Pattern);invocationAttempted=true;invoke.Invoke();record.Stage="submitted";SaveJournal();Thread.Sleep(120);return Read();
            }
            catch(Exception ex)
            {
                // Existing IDs may represent an earlier Invoke whose outcome is still unknown.
                if(!invocationAttempted&&ex.Message!="此消息已处理或提交状态待核对，未再次发送。请查看服务器会话。")throw new RequestNotSubmittedException(ex.Message,ex);
                throw;
            }
        }
        public static bool DraftMatches(string actual,string expected){string a=(actual??"").Replace("\r\n","\n"),b=(expected??"").Replace("\r\n","\n");return a==b||a==b+"\n";}
        public ChatState Stop(string expectedSession)
        {
            Read();if(state.SessionId!=expectedSession)throw new InvalidOperationException("当前会话已变化。");var button=snapshot.Nodes.Where(n=>n.Type=="ControlType.Button"&&n.CanInvoke&&DesktopParser.IsStop(n.Name)).ToList();if(button.Count!=1)throw new InvalidOperationException("没有可安全停止的当前回复。");((InvokePattern)reader.Elements[button[0].Id].GetCurrentPattern(InvokePattern.Pattern)).Invoke();Thread.Sleep(250);return Read();
        }
        void SaveJournal(){if(sends.Count>2000)throw new InvalidOperationException("操作记录已达到上限，请维护记录后继续。");Secrets.Save(journalPath,sends);}
    }
}
