using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlightGuard;

public sealed record GeoPoint(double Latitude,double Longitude);
public sealed record TerrainSample(double Seconds,double ElevationFeet);
public sealed record ForwardTerrainAssessment(DateTime Time,string Aircraft,double Latitude,double Longitude,double TrackDegrees,double SpeedKnots,string Alert,string Reason,double? ConflictSeconds,string Source,double VerticalSpeedFpm=0,bool Available=true)
{
    public bool ValidFor(Frame f)=>Available&&f.Active&&!f.Ground&&f.Aircraft==Aircraft&&f.Latitude!=null&&f.Longitude!=null&&(f.Time-Time).TotalSeconds is >=0 and <=3&&Geo.Distance(new(Latitude,Longitude),new(f.Latitude.Value,f.Longitude.Value))<Math.Max(400,SpeedKnots*.514444*4)&&Math.Abs((f.GroundSpeedKnots??SpeedKnots)-SpeedKnots)<Math.Max(30,SpeedKnots*.2)&&Math.Abs(f.Vs-VerticalSpeedFpm)<Math.Max(500,Math.Abs(VerticalSpeedFpm)*.5);
}
public static class Geo
{
    private const double R=6371000;
    private static double Rad(double degrees)=>degrees*Math.PI/180;
    public static GeoPoint Destination(GeoPoint p,double bearing,double metres){double d=metres/R,b=Rad(bearing),lat=Rad(p.Latitude),lon=Rad(p.Longitude);double la=Math.Asin(Math.Sin(lat)*Math.Cos(d)+Math.Cos(lat)*Math.Sin(d)*Math.Cos(b));double lo=lon+Math.Atan2(Math.Sin(b)*Math.Sin(d)*Math.Cos(lat),Math.Cos(d)-Math.Sin(lat)*Math.Sin(la));return new(la*180/Math.PI,((lo*180/Math.PI+540)%360)-180);}
    public static double Distance(GeoPoint a,GeoPoint b){double la=Rad(b.Latitude-a.Latitude),lo=Rad(b.Longitude-a.Longitude);double h=Math.Pow(Math.Sin(la/2),2)+Math.Cos(Rad(a.Latitude))*Math.Cos(Rad(b.Latitude))*Math.Pow(Math.Sin(lo/2),2);return 2*R*Math.Asin(Math.Sqrt(Math.Clamp(h,0,1)));}
    public static double Bearing(GeoPoint a,GeoPoint b){double la=Rad(a.Latitude),lb=Rad(b.Latitude),dl=Rad(b.Longitude-a.Longitude);return (Math.Atan2(Math.Sin(dl)*Math.Cos(lb),Math.Cos(la)*Math.Sin(lb)-Math.Sin(la)*Math.Cos(lb)*Math.Cos(dl))*180/Math.PI+360)%360;}
    public static double Turn(double a,double b)=>Math.Abs((a-b+540)%360-180);
}

public static class TerrainPrediction
{
    public static ForwardTerrainAssessment Evaluate(Frame f,double track,IReadOnlyList<TerrainSample> profile,bool landingConfigured,string source)
    {
        string alert="",reason="Forward terrain clear within sampled corridor";double? conflict=null;
        double ground=f.Msl-f.Agl,origin=profile[0].ElevationFeet;
        if(Math.Abs(ground-origin)>500)return new(f.Time,f.Aircraft,f.Latitude!.Value,f.Longitude!.Value,track,f.GroundSpeedKnots!.Value,"","Terrain / simulator elevation differs by over 500 ft; forward estimate withheld",null,source,f.Vs,false);
        bool landing=landingConfigured&&f.Agl<2500&&f.Vs is <0 and >-1500;
        foreach(var p in profile.Skip(1)){
            double rise=p.ElevationFeet-origin,terrain=ground+rise,projected=f.Msl+f.Vs*p.Seconds/60,clearance=projected-terrain;
            // Flat ground at the expected touchdown is not an obstacle on a configured landing.
            if(landing&&rise<120)continue;
            if(rise<100&&p.Seconds>8)continue;
            bool warning=clearance<100&&p.Seconds<=20||clearance<=0&&p.Seconds<=30;
            bool caution=clearance<300;
            if(warning){alert="terrain-ahead-pull-up";conflict=p.Seconds;reason=$"Predicted terrain conflict in {p.Seconds:0} s; projected clearance {clearance:0} ft · external terrain estimate";break;}
            if(caution&&alert==""){alert="terrain-ahead";conflict=p.Seconds;reason=$"Reduced terrain clearance ahead in {p.Seconds:0} s; projected clearance {clearance:0} ft · external terrain estimate";}
        }
        return new(f.Time,f.Aircraft,f.Latitude!.Value,f.Longitude!.Value,track,f.GroundSpeedKnots!.Value,alert,reason,conflict,source,f.Vs);
    }
}

public sealed class TerrainTiles : IDisposable
{
    private static int networkRequests;
    public static int NetworkRequests=>Volatile.Read(ref networkRequests);
    private readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(8),MaxResponseContentBufferSize=1024*1024};
    private readonly string folder;
    private readonly Dictionary<(int,int),byte[]> memory=new();
    public int Downloads {get;private set;}
    public TerrainTiles(string folder){this.folder=folder;http.DefaultRequestHeaders.UserAgent.ParseAdd("FlightGuard/0.6");}
    public static (int X,int Y,int PixelX,int PixelY) Cell(GeoPoint p){const int n=4096;double lat=Math.Clamp(p.Latitude,-85,85)*Math.PI/180;double x=(p.Longitude+180)/360*n,y=(1-Math.Log(Math.Tan(lat)+1/Math.Cos(lat))/Math.PI)/2*n;return (((int)Math.Floor(x)%n+n)%n,Math.Clamp((int)Math.Floor(y),0,n-1),Math.Clamp((int)((x-Math.Floor(x))*256),0,255),Math.Clamp((int)((y-Math.Floor(y))*256),0,255));}
    public static double Decode(byte red,byte green,byte blue)=>(red*256+green+blue/256d-32768)*3.280839895;
    private static byte[] Pixels(byte[] png){using var stream=new MemoryStream(png);var decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);var frame=decoder.Frames[0];if(frame.PixelWidth!=256||frame.PixelHeight!=256)throw new InvalidDataException("Invalid terrain tile dimensions");var converted=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);converted.Freeze();var pixels=new byte[256*256*4];converted.CopyPixels(pixels,256*4,0);return pixels;}
    private async Task<byte[]> Tile(int x,int y,CancellationToken token){
        if(memory.TryGetValue((x,y),out var pixels))return pixels;
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,$"12-{x}-{y}.png");byte[]? data=null;
        if(File.Exists(path)){try{data=await File.ReadAllBytesAsync(path,token);pixels=Pixels(data);}catch(Exception ex)when(ex is not OperationCanceledException){data=null;}}
        if(data==null){Interlocked.Increment(ref networkRequests);data=await http.GetByteArrayAsync($"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/12/{x}/{y}.png",token);if(data.Length>1024*1024)throw new InvalidDataException("Terrain tile too large");pixels=Pixels(data);string temporary=path+".tmp";await File.WriteAllBytesAsync(temporary,data,token);File.Move(temporary,path,true);Downloads++;}
        if(memory.Count>=32)memory.Remove(memory.Keys.First());memory[(x,y)]=pixels!;
        if(Downloads>0&&Downloads%16==0)foreach(var old in new DirectoryInfo(folder).GetFiles("12-*.png").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(256))old.Delete();
        return pixels!;
    }
    public async Task<double> Elevation(GeoPoint point,CancellationToken token=default){var c=Cell(point);byte[] pixels=await Tile(c.X,c.Y,token);double elevation=double.NegativeInfinity;
        // Sample the surrounding cells conservatively, including raster peaks near the corridor.
        for(int y=Math.Max(0,c.PixelY-1);y<=Math.Min(255,c.PixelY+1);y++)for(int x=Math.Max(0,c.PixelX-1);x<=Math.Min(255,c.PixelX+1);x++){int i=(y*256+x)*4;elevation=Math.Max(elevation,Decode(pixels[i+2],pixels[i+1],pixels[i]));}
        return elevation;
    }
    public void Dispose()=>http.Dispose();
}

public sealed class ForwardTerrainService : IDisposable
{
    private readonly TerrainTiles tiles;
    private readonly CancellationTokenSource closed=new();
    private Frame? anchor,previous;
    private double? track;
    private Task? pending;
    private DateTime requested,retryAfter;
    private int generation;
    private ForwardTerrainAssessment? current;
    private string status="Forward terrain: waiting for position / track";
    public string Status=>Volatile.Read(ref status);
    public ForwardTerrainService(string folder){tiles=new(folder);}
    public void Reset(){Interlocked.Increment(ref generation);current=null;anchor=previous=null;track=null;requested=default;retryAfter=default;status="Forward terrain: waiting for position / track";}
    public ForwardTerrainAssessment? Observe(Frame f,Settings s,bool replay)
    {
        if(replay)return null;
        if(f.Ground){
            if(previous!=null)Reset();
            if(s.ForwardTerrain&&s.Terrain&&f.Usable&&f.Active&&f.Latitude!=null&&f.Longitude!=null&&Math.Abs(f.Latitude.Value)<=85&&(pending==null||pending.IsCompleted)&&(f.Time-requested).TotalSeconds>30){requested=f.Time;var gatePoint=new GeoPoint(f.Latitude.Value,f.Longitude.Value);status="Forward terrain: preloading nearby tile at the gate";
                pending=Task.Run(async()=>{try{await tiles.Elevation(gatePoint,closed.Token);status="Forward terrain: nearby tile cached; waiting for takeoff / track";}catch(Exception ex)when(!closed.IsCancellationRequested){status="Forward terrain cache unavailable · "+ex.Message;}},closed.Token);}
            return null;
        }
        if(!s.ForwardTerrain||!s.Terrain||!s.Warnings||!f.Usable||!f.Active||f.Ground||f.Latitude==null||f.Longitude==null||Math.Abs(f.Latitude.Value)>85||f.GroundSpeedKnots is null or <35){if(previous!=null)Reset();status=s.ForwardTerrain?"Forward terrain: needs airborne position and ground speed":"Forward terrain disabled";return null;}
        if(previous!=null&&(f.Aircraft!=previous.Aircraft||(f.Time-previous.Time).TotalSeconds>3||f.Time<=previous.Time))Reset();
        previous=f;var point=new GeoPoint(f.Latitude.Value,f.Longitude.Value);
        if(anchor?.Latitude is double lat&&anchor.Longitude is double lon){double distance=Geo.Distance(new(lat,lon),point),dt=(f.Time-anchor.Time).TotalSeconds;
            if(dt>0&&distance>Math.Max(500,f.GroundSpeedKnots.Value*.514444*dt*3)){Reset();previous=f;}
            else if(distance>=12&&dt>0){double next=Geo.Bearing(new(lat,lon),point);if(track!=null&&Geo.Turn(next,track.Value)>12){Interlocked.Increment(ref generation);current=null;}track=next;anchor=f;}
        }
        anchor??=f;
        if(track==null){status="Forward terrain: learning ground track";return null;}
        var result=Volatile.Read(ref current);
        if(result!=null&&(!result.ValidFor(f)||Geo.Turn(result.TrackDegrees,track.Value)>12)){result=null;current=null;}
        if((pending==null||pending.IsCompleted)&&(f.Time-requested).TotalSeconds>=1&&f.Time>=retryAfter){
            requested=f.Time;int epoch=generation;double bearing=track.Value;int horizon=s.TerrainLookAheadSeconds;bool configured=AircraftConfiguration.GearReady(f)&&AircraftConfiguration.FlapsInsufficient(f,s.GpwsLandingFlapState)==false;
            if(result==null)status="Forward terrain: loading cached / online elevation tiles";
            pending=Task.Run(async()=>{
                try{var profile=new List<TerrainSample>{new(0,await tiles.Elevation(point,closed.Token))};double metresPerSecond=f.GroundSpeedKnots.Value*.514444;
                    for(int second=2;second<=horizon;second+=2){var centre=Geo.Destination(point,bearing,metresPerSecond*second);double width=Math.Clamp(metresPerSecond*.6,40,120);double high=await tiles.Elevation(centre,closed.Token);foreach(double side in new[]{-90d,90d})high=Math.Max(high,await tiles.Elevation(Geo.Destination(centre,bearing+side,width),closed.Token));profile.Add(new(second,high));}
                    var assessment=TerrainPrediction.Evaluate(f,bearing,profile,configured,"Mapzen / AWS Terrain Tiles (cached external DEM)");
                    if(epoch==generation&&!closed.IsCancellationRequested){Volatile.Write(ref current,assessment);Volatile.Write(ref status,(assessment.Available?"Forward terrain ready · ":"Forward terrain unavailable · ")+assessment.Reason);}
                }catch(Exception ex)when(!closed.IsCancellationRequested){if(epoch==generation){current=null;retryAfter=f.Time.AddSeconds(10);Volatile.Write(ref status,"Forward terrain unavailable · "+ex.Message+" · local GPWS continues");}}
            },closed.Token);
        }
        return result;
    }
    public void Dispose(){closed.Cancel();Interlocked.Increment(ref generation);_=(pending??Task.CompletedTask).ContinueWith(_=>{tiles.Dispose();closed.Dispose();},TaskScheduler.Default);}
}

public static class TerrainDataVerification
{
    public static async Task<int> Run(string cache,string output){
        try{using var tiles=new TerrainTiles(cache);double airport=await tiles.Elevation(new(39.8561,-104.6737));double mountain=await tiles.Elevation(new(39.85,-105.5));if(!double.IsFinite(airport)||!double.IsFinite(mountain)||mountain<airport+1000)throw new InvalidDataException("Terrain samples did not distinguish airport and mountain terrain");
            File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(new{Status="PASS",Source="Mapzen / AWS Terrain Tiles",AirportSampleFeet=airport,MountainSampleFeet=mountain,Downloads=tiles.Downloads},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return 0;
        }catch(Exception ex){File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(new{Status="FAIL",Error=ex.ToString()}));return 1;}
    }
}
