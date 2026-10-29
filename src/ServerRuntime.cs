using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using System.Threading;

namespace ServerLink
{
    static class HostSignals
    {
        public static string Name(string component){return @"Global\ServerLink."+component+"."+WindowsIdentity.GetCurrent().User.Value;}
        public static bool StopAgent(){try{using(var handle=EventWaitHandle.OpenExisting(Name("AgentStop"))){handle.Set();return true;}}catch(WaitHandleCannotBeOpenedException){return false;}}
    }
    public interface IChatAdapter
    {
        ChatState Read();ChatState Open(string handle);ChatState Send(string id,string session,string text);ChatState Stop(string session);
    }
    public sealed class ServerConfiguration
    {
        public string Host {get;set;} public int BridgePort {get;set;} public int ContentPort {get;set;} public string Adapter {get;set;} public string ClaudePath {get;set;} public string ClaudeApplicationId {get;set;}
        public ServerConfiguration(){Host=Environment.MachineName;BridgePort=39443;ContentPort=39444;Adapter="desktop";ClaudePath="";}
        public static string DirectoryPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerLinkAgent");}}
        public static string ConfigPath {get{return Path.Combine(DirectoryPath,"server.dat");}}
        public static ServerConfiguration Load(){return File.Exists(ConfigPath)?Secrets.Load<ServerConfiguration>(ConfigPath):new ServerConfiguration();}
        public void Validate()
        {
            if(BridgePort<1024||BridgePort>65535||ContentPort<1024||ContentPort>65535||BridgePort==ContentPort)throw new ArgumentException("服务器端口配置无效。");
            Uri host;if(String.IsNullOrWhiteSpace(Host)||!Uri.TryCreate("https://"+Host.Trim(),UriKind.Absolute,out host)||host.Host.Length==0||host.UserInfo.Length>0||host.PathAndQuery!="/"||host.Fragment.Length>0||host.Port!=443)throw new ArgumentException("请填写服务器 IP 或域名，不要包含路径、端口或账号。");
            if(Adapter!="desktop"&&Adapter!="content")throw new ArgumentException("服务器接入模式无效。");
        }
        public void Save(){Validate();Secrets.Save(ConfigPath,this);}
    }
    public static class SessionRuntime
    {
        static readonly string build=ReadBuild();
        static string ReadBuild(){try{return Secrets.Digest(File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location));}catch{return "unavailable";}}
        [StructLayout(LayoutKind.Sequential)]struct Session {public int Id;public IntPtr Name;public int State;}
        [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode)]static extern bool WTSEnumerateSessions(IntPtr server,int reserved,int version,out IntPtr buffer,out int count);
        [DllImport("wtsapi32.dll")]static extern bool WTSQuerySessionInformation(IntPtr server,int id,int kind,out IntPtr buffer,out int count);
        [DllImport("wtsapi32.dll")]static extern void WTSFreeMemory(IntPtr buffer);
        [DllImport("user32.dll",SetLastError=true)]static extern IntPtr OpenInputDesktop(int flags,bool inherit,int access);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder data,int length,out int needed);
        [DllImport("user32.dll")]static extern bool CloseDesktop(IntPtr handle);
        static readonly string[] states={"Active","Connected","ConnectQuery","Shadow","Disconnected","Idle","Listen","Reset","Down","Init"};
        public static ServerRuntime Capture(string adapter)
        {
            var result=new ServerRuntime {BridgeVersion=ApplicationInfo.Version,BridgeBuild=build,SessionId=Process.GetCurrentProcess().SessionId,Adapter=adapter,SessionState="Unknown",InputDesktop="Unavailable",ActiveRdpSessions=-1,Error=""};
            try
            {
                IntPtr buffer;int size;
                if(WTSQuerySessionInformation(IntPtr.Zero,result.SessionId,8,out buffer,out size)){try{int state=Marshal.ReadInt32(buffer);if(state>=0&&state<states.Length)result.SessionState=states[state];}finally{WTSFreeMemory(buffer);}}
                int count;
                if(WTSEnumerateSessions(IntPtr.Zero,0,1,out buffer,out count))
                {
                    try{result.ActiveRdpSessions=0;int stride=Marshal.SizeOf(typeof(Session));for(int i=0;i<count;i++){var item=(Session)Marshal.PtrToStructure(IntPtr.Add(buffer,i*stride),typeof(Session));if(item.State!=0)continue;IntPtr protocol;int bytes;if(WTSQuerySessionInformation(IntPtr.Zero,item.Id,16,out protocol,out bytes)){try{if(bytes>=2&&Marshal.ReadInt16(protocol)==2)result.ActiveRdpSessions++;}finally{WTSFreeMemory(protocol);}}}}finally{WTSFreeMemory(buffer);}
                }
                IntPtr desktop=OpenInputDesktop(0,false,1);if(desktop!=IntPtr.Zero){try{var name=new StringBuilder(256);int needed;if(GetUserObjectInformation(desktop,2,name,name.Capacity*2,out needed))result.InputDesktop=name.ToString();}finally{CloseDesktop(desktop);}}
            }catch(Exception ex){result.Error=ex.GetType().Name;}
            return result;
        }
    }
}
