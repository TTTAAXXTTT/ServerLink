using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyVersion("0.2.8.0")]
[assembly: AssemblyFileVersion("0.2.8.0")]

namespace ServerLink
{
    public static class ApplicationInfo
    {
        public const string Version="0.2.8";
        public const string Author="作者QQ：230154231";
    }

    internal static class AuthorNotice
    {
        internal static Form Create()
        {
            var notice=new Form {Text="ServerLink · 作者信息",ClientSize=new Size(420,160),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterScreen,Font=new Font("Microsoft YaHei UI",10),BackColor=Color.White};
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(20,16,20,16),ColumnCount=1,RowCount=2};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            layout.Controls.Add(new Label {Name="AuthorCredit",Text=ApplicationInfo.Author,AccessibleName=ApplicationInfo.Author,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Font=new Font("Microsoft YaHei UI",14,FontStyle.Bold)},0,0);
            var row=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
            var confirm=new Button {Text="确定",Width=96,Height=32,Margin=Padding.Empty};
            confirm.Click+=(s,e)=>notice.Close();notice.AcceptButton=confirm;notice.CancelButton=confirm;
            row.Controls.Add(confirm);layout.Controls.Add(row,0,1);notice.Controls.Add(layout);
            return notice;
        }

        internal static void Attach(Form main)
        {
            Form notice=null;
            main.Shown+=(s,e)=>{notice=Create();notice.Show();};
            main.FormClosed+=(s,e)=>{if(notice!=null)notice.Dispose();};
        }

        internal static Form ShowForContext(ApplicationContext context)
        {
            var notice=Create();context.ThreadExit+=(s,e)=>notice.Dispose();
            // The notice must have no hidden owner and must never block the
            // server message loop, listener or guardian while awaiting a click.
            notice.Show();return notice;
        }
    }
}
