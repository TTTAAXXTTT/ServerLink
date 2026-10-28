using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ServerLink
{
    // Preserve the installed MSIX application's identity and existing user data.
    // Starting the PE directly must not silently substitute an unpackaged profile.
    static class WindowsAppActivation
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetApplicationUserModelId(IntPtr process,ref uint length,StringBuilder value);
        [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IActivationManager
        {
            [PreserveSig]int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.LPWStr)]string arguments,uint options,out uint processId);
            [PreserveSig]int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr items,[MarshalAs(UnmanagedType.LPWStr)]string verb,out uint processId);
            [PreserveSig]int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr items,out uint processId);
        }
        public static string ReadId(Process process)
        {
            uint length=0;int code=GetApplicationUserModelId(process.Handle,ref length,null);if(code!=122||length<1||length>4096)return "";var id=new StringBuilder((int)length);return GetApplicationUserModelId(process.Handle,ref length,id)==0?id.ToString():"";
        }
        public static int Start(string id,string arguments)
        {
            object manager=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));try{uint pid;int result=((IActivationManager)manager).ActivateApplication(id,arguments,0,out pid);Marshal.ThrowExceptionForHR(result);return checked((int)pid);}finally{Marshal.FinalReleaseComObject(manager);}
        }
    }
}
