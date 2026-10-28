using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace ServerLink
{
    // Isolated preview: no Agent, listener, Claude, pairing or user draft access.
    static class AuthorNoticePreview
    {
        [STAThread]static int Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var lines=new List<string>();int ticks=0,afterClose=0;bool closed=false;
            using(var context=new ApplicationContext())using(var timer=new Timer {Interval=250})
            {
                var notice=AuthorNotice.ShowForContext(context);notice.FormClosed+=(s,e)=>closed=true;
                timer.Tick+=(s,e)=>
                {
                    ticks++;
                    if(ticks==4)
                    {
                        var labels=notice.Controls.Find("AuthorCredit",true);
                        bool valid=notice.Visible&&labels.Length==1&&labels[0].Text=="作者QQ：230154231";
                        lines.Add((valid?"PASS":"FAIL")+" Exact author text is visible; the background message loop continues before dismissal.");
                        File.WriteAllLines(args[0],lines);if(!valid){Environment.ExitCode=1;context.ExitThread();}
                    }
                    if(closed&&++afterClose==3){lines.Add("PASS Dismissing the notice does not stop the background message loop.");lines.Add("RESULT PASS. Isolated popup preview; no server, listener, credentials or draft access.");File.WriteAllLines(args[0],lines);context.ExitThread();}
                    if(ticks>1200){lines.Add("FAIL Preview dismissal timed out.");File.WriteAllLines(args[0],lines);Environment.ExitCode=1;context.ExitThread();}
                };
                timer.Start();Application.Run(context);
            }
            return Environment.ExitCode;
        }
    }
}
