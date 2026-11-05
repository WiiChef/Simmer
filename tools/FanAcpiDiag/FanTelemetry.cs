using System;
using System.Runtime.InteropServices;
public static class FanTelemetry {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr CreateFile(string name,uint access,uint share,IntPtr sa,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool DeviceIoControl(IntPtr h,uint code,byte[] input,uint size,byte[] output,uint capacity,out uint returned,IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static byte[] ReadDstsBytes(uint device) {
        IntPtr h=CreateFile(@"\\.\ATKACPI",0xc0000000,3,IntPtr.Zero,3,0x80,IntPtr.Zero);
        if(h==new IntPtr(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            byte[] input=new byte[16],output=new byte[16];uint returned;
            BitConverter.GetBytes(0x53545344u).CopyTo(input,0);
            BitConverter.GetBytes(8u).CopyTo(input,4);BitConverter.GetBytes(device).CopyTo(input,8);
            if(!DeviceIoControl(h,0x0022240c,input,16,output,16,out returned,IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if(returned<4)throw new InvalidOperationException("Short ASUS telemetry response");
            byte[] result=new byte[returned];Array.Copy(output,result,returned);return result;
        } finally {CloseHandle(h);}
    }
    public static uint ReadDsts(uint device) {return BitConverter.ToUInt32(ReadDstsBytes(device),0);}
    public static uint FanRpm(uint id) {
        byte[] response=ReadDstsBytes(id);
        // 0x110035 returns a two-fan package: first DWORD is fan1, second is fan2.
        int offset=id==0x00110035 ? 4 : 0;
        if(response.Length<offset+4)throw new InvalidOperationException("Missing second fan in ASUS package");
        uint value=BitConverter.ToUInt32(response,offset);
        if((value&0x10000)==0)throw new InvalidOperationException("Fan telemetry presence bit absent");
        return (value&0xffff)*100;
    }
}
