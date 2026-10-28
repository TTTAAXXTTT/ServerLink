using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ServerLink
{
    // Pure fixtures only. Does not construct a DesktopReader or run an Agent.
    static class ParserRegression
    {
        static void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static int Main(string[] args)
        {
            var lines=new List<string>();int exit=0;
            try
            {
                var snapshot=new DesktopSnapshot();snapshot.Nodes.Add(new DesktopNode {Id=0,Parent=-1,Type="ControlType.Document",Name="fixture - Claude",Value="https://claude.ai/chat/00000000-0000-0000-0000-000000000001"});snapshot.Nodes.Add(new DesktopNode {Id=1,Parent=0,Type="ControlType.Edit",Name="Message Claude",CanSetValue=true,Value=""});
                for(int i=0;i<150;i++){int id=snapshot.Nodes.Count;snapshot.Nodes.Add(new DesktopNode {Id=id,Parent=0,Type="ControlType.Group",Name="Message "+(i+1)+" of 150"});snapshot.Nodes.Add(new DesktopNode {Id=id+1,Parent=id,Type="ControlType.Text",Name=i%2==0?"You said:":"Claude replied:"});snapshot.Nodes.Add(new DesktopNode {Id=id+2,Parent=id,Type="ControlType.Text",Name="body-"+i});snapshot.Nodes.Add(new DesktopNode {Id=id+3,Parent=id,Type="ControlType.ToolBar"});snapshot.Nodes.Add(new DesktopNode {Id=id+4,Parent=id+3,Type="ControlType.Text",Name="exclude toolbar"});}
                var watch=Stopwatch.StartNew();var state=DesktopParser.Parse(snapshot);watch.Stop();Assert(state.CanSend&&state.Messages.Count==150&&state.Messages[149].Text=="body-149"&&state.Messages[149].Role=="assistant","Parser lost roles, messages or excluded toolbar text");lines.Add("PASS 150 message groups retain their roles and exact text; toolbar excluded. Parse ms="+watch.ElapsedMilliseconds);
                snapshot.Truncated=true;state=DesktopParser.Parse(snapshot);Assert(!state.CanSend&&!String.IsNullOrEmpty(state.Error),"Truncated snapshot allowed send");lines.Add("PASS Truncated snapshots block sending.");
                var blankSnapshot=new DesktopSnapshot();blankSnapshot.Nodes.Add(new DesktopNode {Id=0,Parent=-1,Type="ControlType.Group",Name="Message 1 of 1"});
                string[] runs={"You said:","第一段。","第二段。","\n","  保留缩进  ","\n","\n","末段。"};
                for(int i=0;i<runs.Length;i++)blankSnapshot.Nodes.Add(new DesktopNode {Id=i+1,Parent=0,Type="ControlType.Text",Name=runs[i]});
                Assert(DesktopParser.Parse(blankSnapshot).Messages[0].Text=="第一段。\n\n第二段。\n  保留缩进  \n\n末段。","Paragraph boundaries, explicit soft breaks or indentation changed");lines.Add("PASS Observed Chromium paragraph nodes, explicit newline nodes and indentation survive readback.");
                Assert(DesktopAdapter.DraftMatches("第一行\n第二行\n","第一行\r\n第二行")&&DesktopAdapter.DraftMatches("a\n\n","a\n")&&!DesktopAdapter.DraftMatches("changed\n","a")&&!DesktopAdapter.DraftMatches("a\n\n","a"),"Draft normalization accepted changed text or rejected one UIA sentinel");lines.Add("PASS CRLF and exactly one extra UIA newline accepted; changed text or extra blank lines rejected.");
                const string session="fixture-session",draft="fixture-draft";string hash=Secrets.Digest(Encoding.UTF8.GetBytes(session+"\n"+draft));var records=new List<SendRecord> {new SendRecord {Hash=hash,Stage="prepared"}};Assert(DesktopAdapter.CanRecoverDraft(records,session,draft),"Prepared draft rejected");Assert(!DesktopAdapter.CanRecoverDraft(records,"another-session",draft)&&!DesktopAdapter.CanRecoverDraft(records,session,"different text"),"Foreign draft accepted");records.Add(new SendRecord {Hash=hash,Stage="submitting"});Assert(!DesktopAdapter.CanRecoverDraft(records,session,draft),"Unknown Invoke outcome was cleared");lines.Add("PASS Recovery requires exact session/text and latest prepared stage; unknown/submitting outcome rejected.");
                var stream=new CountingStream();Frame.Write(stream,new WireRequest {Id="fixture",Op="status"});Assert(stream.Writes==1,"Frame was split into multiple writes");stream.Position=0;var request=Frame.Read<WireRequest>(stream);Assert(request.Id=="fixture"&&request.Op=="status","Frame roundtrip changed payload");lines.Add("PASS Protocol frame uses one write with a correct length prefix and unchanged payload.");
                lines.Add("RESULT PASS. Pure data fixtures; no desktop, socket, server or credentials accessed.");
            }
            catch(Exception ex){exit=1;lines.Add("FAIL "+ex.Message);}
            File.WriteAllLines(args[0],lines);foreach(string line in lines)Console.WriteLine(line);return exit;
        }
        sealed class CountingStream : MemoryStream {public int Writes;public override void Write(byte[] buffer,int offset,int count){Writes++;base.Write(buffer,offset,count);}}
    }
}
