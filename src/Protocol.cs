using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

namespace ServerLink
{
    public sealed class IdentityData {public string Certificate {get;set;} public string Token {get;set;}}
    public sealed class ChatMessage { public int Index {get;set;} public string Role {get;set;} public string Text {get;set;} }
    public sealed class ChatHistory { public string Handle {get;set;} public string Title {get;set;} public string Status {get;set;} }
    public sealed class ChatState
    {
        public string Version {get;set;}
        public string SessionId {get;set;}
        public string Title {get;set;}
        public string CapturedUtc {get;set;}
        public string Error {get;set;}
        public bool CanSend {get;set;}
        public bool Generating {get;set;}
        public string DraftText {get;set;}
        public bool DraftRecoveryAvailable {get;set;}
        public bool DraftPreparedByBridge {get;set;}
        public int TotalMessages {get;set;}
        public List<ChatMessage> Messages {get;set;}
        public List<ChatHistory> History {get;set;}
        public List<string> Capabilities {get;set;}
        public ServerRuntime Runtime {get;set;}
        public ChatState(){Messages=new List<ChatMessage>();History=new List<ChatHistory>();Capabilities=new List<string>();Error="";Title="";SessionId="";}
    }
    public sealed class ServerRuntime
    {
        public int SessionId {get;set;} public string SessionState {get;set;} public int ActiveRdpSessions {get;set;}
        public string InputDesktop {get;set;} public string Adapter {get;set;} public string Error {get;set;}
        public string BridgeVersion {get;set;} public long OperationMilliseconds {get;set;} public long QueueMilliseconds {get;set;}
    }
    public sealed class WireRequest { public string Id {get;set;} public string Op {get;set;} public string Token {get;set;} public string SessionId {get;set;} public string Text {get;set;} public string Handle {get;set;} }
    public sealed class WireReply { public string Id {get;set;} public bool Ok {get;set;} public string Error {get;set;} public string ErrorCode {get;set;} public ChatState State {get;set;} }
    public sealed class RequestNotSubmittedException : InvalidOperationException
    {
        public RequestNotSubmittedException(string message,Exception inner):base(message,inner){}
    }
    public interface IDraftRecoveryAdapter {ChatState RecoverDraft(string expectedSession,string expectedText);}
    public sealed class RecoveredServerDraft {public string SessionId {get;set;} public string Text {get;set;} public string SavedUtc {get;set;}}
    public static class Json
    {
        public static string Write(object value){return new JavaScriptSerializer {MaxJsonLength=4000000}.Serialize(value);}
        public static T Read<T>(string value){return new JavaScriptSerializer {MaxJsonLength=4000000}.Deserialize<T>(value);}
    }
    public static class Frame
    {
        public const int MaximumBytes=4000000;
        public static void Write(Stream stream,object value)
        {
            byte[] data=Encoding.UTF8.GetBytes(Json.Write(value));if(data.Length>MaximumBytes)throw new IOException("消息超过传输上限。");
            byte[] packet=new byte[data.Length+4];packet[0]=(byte)(data.Length>>24);packet[1]=(byte)(data.Length>>16);packet[2]=(byte)(data.Length>>8);packet[3]=(byte)data.Length;Buffer.BlockCopy(data,0,packet,4,data.Length);stream.Write(packet,0,packet.Length);stream.Flush();
        }
        public static T Read<T>(Stream stream)
        {
            byte[] head=ReadExactly(stream,4);int length=(head[0]<<24)|(head[1]<<16)|(head[2]<<8)|head[3];
            if(length<1||length>MaximumBytes)throw new IOException("无效的消息长度。");return Json.Read<T>(new UTF8Encoding(false,true).GetString(ReadExactly(stream,length)));
        }
        static byte[] ReadExactly(Stream stream,int length){byte[] data=new byte[length];int offset=0;while(offset<length){int n=stream.Read(data,offset,length-offset);if(n==0)throw new EndOfStreamException("连接已关闭。");offset+=n;}return data;}
    }
    public static class Secrets
    {
        public static string Random(){byte[] b=new byte[32];using(var r=RandomNumberGenerator.Create())r.GetBytes(b);return Convert.ToBase64String(b);}
        public static string Digest(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
        public static bool Equal(string a,string b){byte[] x=Encoding.UTF8.GetBytes(a??""),y=Encoding.UTF8.GetBytes(b??"");int diff=x.Length^y.Length;for(int i=0;i<Math.Max(x.Length,y.Length);i++)diff|=(i<x.Length?x[i]:0)^(i<y.Length?y[i]:0);return diff==0;}
        public static void Save(string path,object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));byte[] plain=Encoding.UTF8.GetBytes(Json.Write(value));byte[] encrypted=ProtectedData.Protect(plain,Encoding.UTF8.GetBytes("ServerLink/0.2"),DataProtectionScope.CurrentUser);
            string temporary=path+".tmp";File.WriteAllBytes(temporary,encrypted);if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
        public static T Load<T>(string path){return Json.Read<T>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path),Encoding.UTF8.GetBytes("ServerLink/0.2"),DataProtectionScope.CurrentUser)));}
    }
    public sealed class Pairing
    {
        public string Host {get;set;}
        public int Port {get;set;}
        public string Pin {get;set;}
        public string Token {get;set;}
        public string Encode(){var uri=new UriBuilder("serverlink",Host,Port);uri.Query="pin="+Pin+"&token="+Uri.EscapeDataString(Token);return uri.Uri.AbsoluteUri;}
        public static Pairing Parse(string text)
        {
            if((text??"").Length>4096)throw new ArgumentException("配对信息过长。");Uri uri;
            if(!Uri.TryCreate((text??"").Trim(),UriKind.Absolute,out uri)||uri.Scheme!="serverlink"||uri.Port<1||uri.Port>65535||uri.Host.Length==0||uri.UserInfo.Length>0||uri.Fragment.Length>0||uri.AbsolutePath!="/")throw new ArgumentException("请粘贴服务器生成的完整配对信息。");
            var map=new Dictionary<string,string>();foreach(var field in uri.Query.TrimStart('?').Split('&')){var parts=field.Split(new[]{'='},2);if(parts.Length!=2||map.ContainsKey(parts[0]))throw new ArgumentException("配对信息格式错误。");map.Add(parts[0],Uri.UnescapeDataString(parts[1]));}
            string pin,token;if(map.Count!=2||!map.TryGetValue("pin",out pin)||!map.TryGetValue("token",out token)||!System.Text.RegularExpressions.Regex.IsMatch(pin,"^[a-f0-9]{64}$")||Convert.FromBase64String(token).Length!=32)throw new ArgumentException("配对指纹或密钥无效。");
            return new Pairing {Host=uri.Host,Port=uri.Port,Pin=pin,Token=token};
        }
    }
    public interface IBridgeConnection : IDisposable
    {
        void Connect(Pairing pair);
        WireReply Call(WireRequest request);
    }
    public sealed class BridgeConnection : IBridgeConnection
    {
        readonly object gate=new object();TcpClient tcp;SslStream stream;
        public void Connect(Pairing pair)
        {
            lock(gate)
            {
                Dispose();tcp=new TcpClient {NoDelay=true};try
                {
                    var task=tcp.ConnectAsync(pair.Host,pair.Port);if(!task.Wait(6000))throw new TimeoutException("无法连接服务器桥接端口，请检查云防火墙、Windows 防火墙和桥接程序。");
                    tcp.ReceiveTimeout=30000;tcp.SendTimeout=10000;stream=new SslStream(tcp.GetStream(),false,(sender,cert,chain,errors)=>VerifyCertificate(cert,pair.Pin));
                    stream.AuthenticateAsClient(pair.Host,null,SslProtocols.Tls12,false);Frame.Write(stream,new WireRequest {Id="hello",Op="hello",Token=pair.Token});var reply=Frame.Read<WireReply>(stream);if(reply==null||!reply.Ok||reply.Id!="hello")throw new AuthenticationException("服务器拒绝配对认证。");
                }catch{Dispose();throw;}
            }
        }
        static bool VerifyCertificate(X509Certificate certificate,string pin)
        {
            if(certificate==null||!Secrets.Equal(Secrets.Digest(certificate.GetRawCertData()),pin))return false;using(var cert=new X509Certificate2(certificate))return cert.NotBefore.ToUniversalTime()<=DateTime.UtcNow&&cert.NotAfter.ToUniversalTime()>DateTime.UtcNow;
        }
        public WireReply Call(WireRequest request)
        {
            lock(gate){var current=stream;if(current==null)throw new IOException("尚未连接服务器。");Frame.Write(current,request);var result=Frame.Read<WireReply>(current);if(result==null||result.Id!=request.Id)throw new IOException("响应编号不匹配。");return result;}
        }
        // Closing the socket interrupts a blocked read instead of waiting on Call's lock on the UI thread.
        public void Dispose(){var oldTcp=System.Threading.Interlocked.Exchange(ref tcp,null);if(oldTcp!=null)oldTcp.Close();var oldStream=System.Threading.Interlocked.Exchange(ref stream,null);if(oldStream!=null)oldStream.Dispose();}
    }
}
