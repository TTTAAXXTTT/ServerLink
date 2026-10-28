using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ServerLink
{
    public sealed partial class ClientForm
    {
        internal void ApplyBenchmark(ChatState value){Render(value);}
    }
    sealed class RenderBenchmark : Form
    {
        readonly string report;public RenderBenchmark(string report){this.report=report;WindowState=FormWindowState.Minimized;ShowInTaskbar=false;Shown+=(s,e)=>Run();}
        void Run()
        {
            using(var form=new ClientForm(false){ShowInTaskbar=false,WindowState=FormWindowState.Minimized})
            {
                form.Show();var state=new ChatState {SessionId="benchmark",CanSend=true,Title="Synthetic fixture"};string body=new string('文',2000);
                for(int i=0;i<60;i++)state.Messages.Add(new ChatMessage {Index=i+1,Role=i%2==0?"user":"assistant",Text=body+i});form.ApplyBenchmark(state);
                var timer=Stopwatch.StartNew();for(int i=0;i<20;i++){state.Messages[59].Text+="追加文本";form.ApplyBenchmark(state);}timer.Stop();
                File.WriteAllText(report,"SYNTHETIC 60 messages, about 120000 characters, 20 final-message updates.\r\nUI_UPDATE_TOTAL_MS "+timer.ElapsedMilliseconds+"\r\nNo server, socket, Claude or credentials accessed.\r\n");form.Close();
            }
            Close();
        }
    }
    static class RenderBenchmarkProgram
    {
        [STAThread]static void Main(string[] args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new RenderBenchmark(args[0]));}
    }
}
