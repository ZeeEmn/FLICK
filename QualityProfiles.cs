namespace Rewind;
internal sealed record QualityProfile(string Name,int Bitrate,bool Native,string Parameters,string Description);
internal sealed record CapturePlan(int SourceWidth,int SourceHeight,int Width,int Height,int Fps,int Bitrate,bool Downscale,string Profile);
internal static class QualityProfiles
{
    internal static readonly QualityProfile[] All={
        new("Very High",60_000_000,true,"Native Resolution, Very High Bitrate: 60 Mbps","Best detail, largest files."),
        new("High",40_000_000,true,"Native Resolution, High Bitrate: 40 Mbps","Sharp video, larger files."),
        new("Default",25_000_000,true,"Native Resolution, Average Bitrate: 25 Mbps","Balanced quality and file size."),
        new("Low",12_000_000,false,"1280x720 Downscaled, Average-to-Low Bitrate: 12 Mbps","Smaller 720p clips."),
        new("Very Low",6_000_000,false,"1280x720 Downscaled, Low Bitrate: 6 Mbps","Smallest 720p clips.")};
    internal static string Normalize(string? name)=>name switch{"Very High" or "High" or "Default" or "Low" or "Very Low"=>name,"Standard"=>"Low","Extreme"=>"Very Low",_=>"Default"};
    internal static QualityProfile Get(string? name)=>All.First(p=>p.Name==Normalize(name));
    internal static CapturePlan Plan(string? profile,int fps,int width,int height){var p=Get(profile);if(width<320||height<180||width>3840||height>2160)throw new InvalidOperationException("This capture build supports unrotated primary displays up to 3840 × 2160. Choose a supported display resolution.");if((width&1)!=0||(height&1)!=0)throw new InvalidOperationException("H.264 capture requires even display dimensions.");return new(width,height,p.Native?width:1280,p.Native?height:720,fps==30?30:60,p.Bitrate,!p.Native,p.Name);}
    internal static string Worker(CapturePlan plan)=>plan.Downscale?"flick-transcoder":"flick-profile-capture";
    internal static string Filter(CapturePlan plan,EncoderChoice encoder){string? download=EncoderCatalog.AcquisitionFilter(encoder);if(!plan.Downscale)return download??"";string scale=$"scale_d3d11=width={plan.Width}:height={plan.Height}:format=nv12";if(!encoder.Download)return scale;return scale+",hwdownload,format=nv12"+(encoder.Cpu?",format=yuv420p":"");}
    internal static string[] EncoderArguments(CapturePlan plan,EncoderChoice encoder){var args=EncoderCatalog.OutputArguments(encoder,plan.Bitrate).ToList();int g=args.IndexOf("-g");if(g>=0)args[g+1]=(plan.Fps*2).ToString(System.Globalization.CultureInfo.InvariantCulture);args.InsertRange(args.IndexOf("-movflags"),new[]{"-force_key_frames","expr:gte(t,n_forced*2)"});if(plan.Profile=="Very Low"&&encoder.Name.EndsWith("_nvenc",StringComparison.Ordinal))args.InsertRange(args.IndexOf("-movflags"),new[]{"-spatial-aq","1","-aq-strength","8"});return args.ToArray();}
}
