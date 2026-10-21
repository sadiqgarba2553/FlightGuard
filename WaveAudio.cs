using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlightGuard;

public sealed record WaveClip(byte[] Pcm, int SampleRate, double TrimmedLeadingMs)
{
    public double Duration => Pcm.Length / (SampleRate * 2d);
    public static WaveClip Load(string path) => Decode(File.ReadAllBytes(path));
    public static WaveClip Decode(byte[] wav)
    {
        using var r = new BinaryReader(new MemoryStream(wav));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF WAV.");
        r.ReadInt32(); if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");
        byte[]? format=null, data=null;
        while (r.BaseStream.Position+8<=r.BaseStream.Length) {
            string chunk=Encoding.ASCII.GetString(r.ReadBytes(4)); uint size=r.ReadUInt32();
            if(size>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException("Truncated WAV chunk.");
            byte[] bytes=r.ReadBytes((int)size);if(chunk=="fmt ")format=bytes;if(chunk=="data")data=bytes;
            if((size&1)!=0 && r.BaseStream.Position<r.BaseStream.Length)r.ReadByte();
        }
        if(format==null||format.Length<16||data==null||data.Length==0)throw new InvalidDataException("Empty or incomplete WAV.");
        int tag=BitConverter.ToUInt16(format,0), channels=BitConverter.ToUInt16(format,2), rate=BitConverter.ToInt32(format,4), align=BitConverter.ToUInt16(format,12), bits=BitConverter.ToUInt16(format,14);
        if(tag==65534 && format.Length>=40)tag=BitConverter.ToUInt16(format,24);
        if(channels<1||channels>8||rate<8000||rate>192000||align!=channels*(bits/8)||!new[]{8,16,24,32}.Contains(bits)||tag!=1&&tag!=3||tag==3&&bits!=32)throw new InvalidDataException("Unsupported WAV sample format.");
        int frames=data.Length/align;var mono=new float[frames];double peak=0;
        for(int i=0;i<frames;i++) {
            double sum=0;
            for(int c=0;c<channels;c++) {
                int p=i*align+c*(bits/8);double value;
                if(tag==3)value=BitConverter.ToSingle(data,p);
                else value=bits switch {8=>(data[p]-128)/128d,16=>BitConverter.ToInt16(data,p)/32768d,24=>((data[p]|data[p+1]<<8|data[p+2]<<16)<<8>>8)/8388608d,32=>BitConverter.ToInt32(data,p)/2147483648d,_=>0};
                sum+=double.IsFinite(value)?value:0;
            }
            mono[i]=(float)(sum/channels);peak=Math.Max(peak,Math.Abs(mono[i]));
        }
        if(peak<.0001)throw new InvalidDataException("WAV contains no usable audio.");
        // Ten-millisecond RMS windows remove dead air without cutting consonant attacks.
        int block=Math.Max(1,rate/100), first=-1,last=-1;double threshold=Math.Max(.001,peak*.018);
        for(int i=0;i<mono.Length;i+=block) {
            int n=Math.Min(block,mono.Length-i);double rms=0;for(int j=0;j<n;j++)rms+=mono[i+j]*mono[i+j];
            if(Math.Sqrt(rms/n)>threshold){if(first<0)first=i;last=i+n;}
        }
        if(first<0)throw new InvalidDataException("WAV contains no audible content.");
        int begin=Math.Max(0,first-rate/100), end=Math.Min(frames,last+rate/40);
        double gain=Math.Min(3,.9/peak);const int outputRate=48000;int count=Math.Max(1,(int)((end-begin)*(double)outputRate/rate));byte[] pcm=new byte[count*2];
        for(int i=0;i<count;i++){
            double position=begin+i*(double)rate/outputRate;int lo=Math.Min(end-1,(int)position),hi=Math.Min(end-1,lo+1);double value=mono[lo]+(mono[hi]-mono[lo])*(position-lo);
            short sample=(short)Math.Clamp((int)(value*gain*32767),short.MinValue,short.MaxValue);pcm[i*2]=(byte)sample;pcm[i*2+1]=(byte)(sample>>8);
        }
        return new(pcm,outputRate,begin*1000d/rate);
    }
}

public static class NativeWave
{
    [StructLayout(LayoutKind.Sequential,Pack=2)] private struct Format { public ushort Tag, Channels; public uint Rate, Bytes; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public IntPtr Data;public uint Length,Recorded;public UIntPtr User;public uint Flags,Loops;public IntPtr Next;public UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle,uint device,ref Format format,IntPtr callback,IntPtr instance,uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
    private static readonly SemaphoreSlim gate=new(1,1);
    private static IntPtr device;
    public static Task WarmUp()=>Task.Run(async()=>{await gate.WaitAsync();try{Open();}finally{gate.Release();}});
    private static void Check(uint result){if(result!=0)throw new IOException("Windows wave output error "+result);}
    private static void Open(){if(device!=IntPtr.Zero)return;var format=new Format{Tag=1,Channels=1,Rate=48000,Bytes=96000,Align=2,Bits=16};Check(waveOutOpen(out device,uint.MaxValue,ref format,IntPtr.Zero,IntPtr.Zero,0));}
    public static Task Shutdown()=>Task.Run(async()=>{await gate.WaitAsync();try{if(device!=IntPtr.Zero){waveOutReset(device);waveOutClose(device);device=IntPtr.Zero;}}finally{gate.Release();}});
    public static Task Play(WaveClip clip,int volume,CancellationToken token,Action<double>? started=null) => Task.Run(async()=>{
        var timer=Stopwatch.StartNew();await gate.WaitAsync(token);IntPtr header=IntPtr.Zero;GCHandle pinned=default;bool prepared=false;
        try {
            token.ThrowIfCancellationRequested();Open();byte[] data=(byte[])clip.Pcm.Clone();
            if(clip.SampleRate!=48000)throw new InvalidDataException("Playback cache must be PCM16 mono at 48kHz.");
            if(volume!=100)for(int i=0;i<data.Length;i+=2){short x=BitConverter.ToInt16(data,i);short y=(short)(x*Math.Clamp(volume,0,100)/100);data[i]=(byte)y;data[i+1]=(byte)(y>>8);}
            pinned=GCHandle.Alloc(data,GCHandleType.Pinned);uint size=(uint)Marshal.SizeOf<Header>();header=Marshal.AllocHGlobal((int)size);
            Marshal.StructureToPtr(new Header{Data=pinned.AddrOfPinnedObject(),Length=(uint)data.Length},header,false);
            Check(waveOutPrepareHeader(device,header,size));prepared=true;token.ThrowIfCancellationRequested();Check(waveOutWrite(device,header,size));started?.Invoke(timer.Elapsed.TotalMilliseconds);
            while((Marshal.PtrToStructure<Header>(header).Flags&1)==0){token.ThrowIfCancellationRequested();await Task.Delay(5,token).ConfigureAwait(false);}
        } finally {
            if(prepared){waveOutReset(device);waveOutUnprepareHeader(device,header,(uint)Marshal.SizeOf<Header>());}
            if(header!=IntPtr.Zero)Marshal.FreeHGlobal(header);if(pinned.IsAllocated)pinned.Free();gate.Release();
        }
    },token);
}

public sealed record PendingCue(SoundCue Cue,DateTime Created,string? Family=null);
public sealed class CueQueue
{
    private readonly List<PendingCue> pending=new();
    public int Count=>pending.Count;
    public static bool Warning(SoundCue c)=>c.Group=="Warnings" || c.Id=="glideslope";
    public static bool Cabin(SoundCue c)=>c.Group is "Cabin" or "Ambience";
    public static bool Timely(SoundCue c)=>c.Group is "Heights" or "Approach" || c.Id is "v1" or "rotate" or "v2" or "80-knots" or "100-knots";
    public static double Lifetime(SoundCue c)=>Warning(c)?1:c.Group=="Heights"?.25:Timely(c)?.6:c.Group=="Cabin"?90:c.Group=="Ambience"?5:1;
    public bool Offer(IEnumerable<SoundCue> cues,DateTime now,SoundCue? playing,DateTime began,string? family=null)
    {
        var items=cues.ToArray();bool interrupt=false;
        foreach(var cue in items) {
            if(!Cabin(cue)&&playing!=null&&Cabin(playing))interrupt=true;
            if(Warning(cue)){pending.RemoveAll(x=>!Warning(x.Cue));if(playing!=null&&cue.Priority>playing.Priority)interrupt=true;}
            else if(Timely(cue)) {
                pending.RemoveAll(x=>!Warning(x.Cue)&&!Timely(x.Cue));
                if(playing!=null&&!Warning(playing)&&!Timely(playing))interrupt=true;
                if(cue.Group=="Heights"){
                    pending.RemoveAll(x=>x.Cue.Group=="Heights");
                    if(playing?.Group=="Heights"&&(now-began).TotalMilliseconds>120)interrupt=true;
                }
                if(cue.Id=="rotate"&&playing?.Id=="v1"&&(now-began).TotalMilliseconds>350)interrupt=true;
            }
            int existing=pending.FindIndex(x=>x.Cue.Id==cue.Id);
            if(Warning(cue)&&existing>=0)pending[existing]=new(cue,now,family);
            else if((playing?.Id!=cue.Id||Warning(cue))&&existing<0)pending.Add(new(cue,now,family));
        }
        return interrupt;
    }
    public PendingCue? Next(DateTime now)
    {
        pending.RemoveAll(x=>(now-x.Created).TotalSeconds>Lifetime(x.Cue));
        var item=pending.OrderByDescending(x=>x.Cue.Priority).ThenBy(x=>x.Created).FirstOrDefault();if(item!=null)pending.Remove(item);return item;
    }
    public void Suppress(int priority)=>pending.RemoveAll(x=>x.Cue.Priority<priority);
    public void WarningState(string active)=>pending.RemoveAll(x=>x.Family==null&&Warning(x.Cue)&&x.Cue.Id!=active);
    public void CancelApproach()=>pending.RemoveAll(x=>x.Family==null && (x.Cue.Group is "Heights" or "Approach"));
    public void CancelCabin(Func<string,bool> allowed)=>pending.RemoveAll(x=>x.Family==null&&Cabin(x.Cue)&&!allowed(x.Cue.Id));
    public void Clear()=>pending.Clear();
}
