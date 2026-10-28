using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ServerLink
{
    // Read-only timing against the user's already-paired bridge. Never prints chat or credentials.
    static class LiveLatencyCheck
    {
        static int Main(string[] args)
        {
            var lines=new List<string>();int result=0;
            try
            {
                var pair=Secrets.Load<Pairing>(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkClient","connection.dat"));
                using(var connection=new BridgeConnection())
                {
                    var timer=Stopwatch.StartNew();connection.Connect(pair);lines.Add("CONNECT_MS "+timer.ElapsedMilliseconds);
                    foreach(string operation in new[]{"health","status","status","status","status","status","health"})
                    {
                        timer.Restart();var reply=connection.Call(new WireRequest {Id=Guid.NewGuid().ToString("N"),Op=operation});timer.Stop();
                        if(!reply.Ok||reply.State==null)throw new InvalidOperationException("Read refused: "+reply.Error);
                        var state=reply.State;lines.Add(operation.ToUpperInvariant()+"_MS "+timer.ElapsedMilliseconds+" MESSAGES "+state.Messages.Count+" HISTORY "+state.History.Count+" GENERATING "+state.Generating+" RDP "+(state.Runtime==null?"unknown":state.Runtime.ActiveRdpSessions.ToString())+(state.Runtime==null?"":" BRIDGE "+state.Runtime.BridgeVersion+" OP_MS "+state.Runtime.OperationMilliseconds+" QUEUE_MS "+state.Runtime.QueueMilliseconds));
                    }
                }
                lines.Add("RESULT PASS. Read-only; no messages submitted.");
            }
            catch(Exception ex){result=1;lines.Add("FAIL "+ex.GetBaseException().Message);}
            File.WriteAllLines(args[0],lines);foreach(string line in lines)Console.WriteLine(line);return result;
        }
    }
}
