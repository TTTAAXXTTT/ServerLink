using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerLink
{
    static class BlankLineInspection
    {
        static string Lines(string text){text=(text??"").Replace("\r\n","\n");var lines=text.Split('\n');return "chars="+text.Length+" lines="+lines.Length+" blankLines="+lines.Count(String.IsNullOrWhiteSpace)+" terminalLF="+(text.Length-text.TrimEnd('\n').Length);}
        static int Main(string[] args)
        {
            var output=new List<string>();int result=0;
            try
            {
                string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkClient");var pair=Secrets.Load<Pairing>(Path.Combine(directory,"connection.dat"));
                using(var connection=new BridgeConnection())
                {
                    connection.Connect(pair);var reply=connection.Call(new WireRequest {Id=Guid.NewGuid().ToString("N"),Op="status"});if(!reply.Ok||reply.State==null)throw new InvalidOperationException(reply.Error);var state=reply.State;
                    output.Add("INFO DedicatedTestConversation="+(state.Title=="远程聊天测试")+" messages="+state.Messages.Count+" CanSend="+state.CanSend+" Generating="+state.Generating+" RDP="+(state.Runtime==null?-1:state.Runtime.ActiveRdpSessions));output.Add("INFO Server draft shape: "+Lines(state.DraftText));
                    output.Add("INFO Bridge version="+(state.Runtime==null?"unknown":state.Runtime.BridgeVersion)+" Claude process version="+state.Version+" sessionState="+(state.Runtime==null?"unknown":state.Runtime.SessionState)+" UI error="+state.Error);
                    if(args.Contains("--require-ready")&&(state.Runtime==null||state.Runtime.BridgeVersion!=ApplicationInfo.Version||state.Runtime.ActiveRdpSessions!=0||state.Runtime.SessionState!="Disconnected"||!state.CanSend||state.Generating||!String.IsNullOrEmpty(state.Error)||state.Title!="远程聊天测试"||!String.IsNullOrWhiteSpace(state.DraftText)))throw new InvalidOperationException("Final no-RDP bridge readiness requirements are not met.");
                    var lastUser=state.Messages.LastOrDefault(m=>m.Role=="user");if(lastUser!=null)output.Add("INFO Last user message shape: "+Lines(lastUser.Text));
                    string draftPath=Path.Combine(directory,"local-draft.dat");if(File.Exists(draftPath)){var draft=Secrets.Load<LocalClientDraft>(draftPath);output.Add("INFO Local draft shape: "+Lines(draft.Text)+" Pending="+(draft.Pending!=null));if(draft.Pending!=null){output.Add("INFO Pending message shape: "+Lines(draft.Pending.Text)+" SameSession="+(draft.Pending.SessionId==state.SessionId)+" AfterIndex="+draft.Pending.AfterIndex);var candidates=state.Messages.Where(m=>m.Role=="user"&&m.Index>draft.Pending.AfterIndex).ToList();output.Add("INFO New user messages after confirmation boundary="+candidates.Count);foreach(var item in candidates)output.Add("INFO Candidate index="+item.Index+" "+Lines(item.Text)+" ExactWithEol="+(item.Text.Replace("\r\n","\n").TrimEnd('\n')==draft.Pending.Text.Replace("\r\n","\n").TrimEnd('\n')));}}
                }
                output.Add("RESULT PASS. Read-only shape metadata; no message text or credentials recorded.");
            }
            catch(Exception ex){result=1;output.Add("FAIL "+ex.GetBaseException().Message);}
            File.WriteAllLines(args[0],output);foreach(var line in output)Console.WriteLine(line);return result;
        }
    }
}
