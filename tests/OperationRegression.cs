using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerLink
{
    static class OperationRegression
    {
        static void Assert(bool ok,string text){if(!ok)throw new Exception(text);}
        static WireRequest Request(string op="send"){return new WireRequest {Id=Guid.NewGuid().ToString("N"),Op=op,Owner="controller-a",ContextId="context-a",SessionId="chat-a",Text="fixture"};}
        static int Main(string[] args)
        {
            var results=new List<string>();int failures=0;
            Action<string,Action> check=(name,run)=>{try{run();results.Add("PASS "+name);}catch(Exception e){failures++;results.Add("FAIL "+name+": "+e.Message);}};
            check("A repeated ID is never executed twice and a different payload is rejected",()=>{
                var ledger=new OperationLedger(null,x=>{});var r=Request();OperationReceipt old;Assert(ledger.Begin(r,out old),"first rejected");Assert(!ledger.Begin(r,out old)&&old.Phase=="queued","repeated request accepted");r.Text="changed";bool rejected=false;try{ledger.Begin(r,out old);}catch(BridgeFault f){rejected=f.Code=="request_conflict";}Assert(rejected,"ID conflict accepted");});
            check("An expired queued write cannot later be claimed",()=>{
                var ledger=new OperationLedger(null,x=>{});var r=Request();OperationReceipt old;ledger.Begin(r,out old);Assert(!ledger.Claim(r.Id,true),"expired task claimed");Assert(!ledger.Claim(r.Id,false),"expired task later ran");Assert(ledger.Query(r.Id).Phase=="not_submitted","wrong result");});
            check("A timeout cannot cancel a task already claimed",()=>{
                var ledger=new OperationLedger(null,x=>{});var r=Request();OperationReceipt old;ledger.Begin(r,out old);Assert(ledger.Claim(r.Id,false),"claim rejected");Assert(!ledger.Claim(r.Id,true)&&ledger.Query(r.Id).Phase=="running","running task declared cancelled");});
            check("Unknown Invoke outcomes block all later writes including other controllers",()=>{
                var ledger=new OperationLedger(null,x=>{});var r=Request();OperationReceipt old;ledger.Begin(r,out old);ledger.Claim(r.Id,false);ledger.Invoking(r.Id);ledger.Fail(r.Id,new IOException());Assert(ledger.Query(r.Id).Phase=="unknown","unknown lost");bool blocked=false;try{var other=Request("create-chat");other.Owner="controller-b";ledger.Begin(other,out old);}catch(BridgeFault f){blocked=f.Code=="operation_pending";}Assert(blocked,"unknown operation bypassed");});
            check("Crash recovery cancels queued tasks but preserves an Invoke boundary",()=>{
                var entries=new List<OperationEntry>();var ledger=new OperationLedger(null,x=>entries=Json.Read<List<OperationEntry>>(Json.Write(x)));var r=Request();OperationReceipt old;ledger.Begin(r,out old);var restored=new OperationLedger(entries,x=>{});Assert(restored.Query(r.Id).Phase=="not_submitted","queued request survived restart as runnable");ledger.Claim(r.Id,false);ledger.Invoking(r.Id);restored=new OperationLedger(entries,x=>{});Assert(!restored.Query(r.Id).Terminal,"Invoke outcome cleared on restart");});
            check("Initial journal failure prevents request admission",()=>{
                bool fail=false;var ledger=new OperationLedger(null,x=>{if(fail)throw new IOException("fixture disk full");});fail=true;var r=Request();OperationReceipt old;bool rejected=false;try{ledger.Begin(r,out old);}catch(IOException){rejected=true;}Assert(rejected&&ledger.Query(r.Id)==null,"unpersisted request accepted");});
            check("First message binds only with matching context, process, first user body and formal page",()=>{
                var ledger=new OperationLedger(null,x=>{});var r=Request();r.SessionId="new:context-a";OperationReceipt old;ledger.Begin(r,out old);ledger.Claim(r.Id,false);var before=new ChatState {SessionId=r.SessionId,ContextId=r.ContextId,PageKind="blank",CanSend=true};ledger.Prepare(r.Id,before,"process-a");ledger.Invoking(r.Id);ledger.Complete(r.Id,before);
                var after=new ChatState {SessionId="formal-chat",ContextId="wrong",PageKind="chat",CanSend=true,TotalMessages=1};after.Messages.Add(new ChatMessage {Index=1,Role="user",Text="fixture"});ledger.Observe(after,"process-a");Assert(!ledger.Query(r.Id).Terminal,"wrong context confirmed");after.ContextId=r.ContextId;ledger.Observe(after,"different-process");Assert(!ledger.Query(r.Id).Terminal,"wrong process confirmed");after.Messages[0].Role="assistant";ledger.Observe(after,"process-a");Assert(!ledger.Query(r.Id).Terminal,"assistant confirmed send");after.Messages[0].Role="user";ledger.Observe(after,"process-a");Assert(ledger.Query(r.Id).Phase=="confirmed"&&ledger.Query(r.Id).SessionId=="formal-chat","first user message not bound");});
            check("Old identical messages cannot confirm a later send",()=>{
                var ledger=new OperationLedger(null,x=>{});var r=Request();OperationReceipt old;ledger.Begin(r,out old);ledger.Claim(r.Id,false);var state=new ChatState {SessionId="chat-a",ContextId="context-a",CanSend=true,TotalMessages=4};state.Messages.Add(new ChatMessage {Index=1,Role="user",Text="fixture"});ledger.Prepare(r.Id,state,"process");ledger.Invoking(r.Id);ledger.Complete(r.Id,state);ledger.Observe(state,"process");Assert(!ledger.Query(r.Id).Terminal,"old message confirmed");state.Messages.Add(new ChatMessage {Index=5,Role="user",Text="fixture"});ledger.Observe(state,"process");Assert(ledger.Query(r.Id).Terminal,"new message not confirmed");});
            results.Add("RESULT "+(failures==0?"PASS":"FAIL")+". Pure ledger fixtures; no Agent, listener, credentials or live Claude.");File.WriteAllLines(args[0],results);foreach(var result in results)Console.WriteLine(result);return failures==0?0:1;
        }
    }
}
