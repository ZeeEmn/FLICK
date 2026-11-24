using NAudio.Wave;

namespace Rewind;

internal sealed class AudioInputChannels
{
    private static readonly Guid Pcm=new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float=new("00000003-0000-0010-8000-00aa00389b71");
    private readonly int sampleBytes,frameBytes,channels;
    private readonly bool floatingPoint;

    internal AudioInputChannels(WaveFormat format)
    {
        Guid subtype=format is WaveFormatExtensible extended?extended.SubFormat:Guid.Empty;
        floatingPoint=format.Encoding==WaveFormatEncoding.IeeeFloat||subtype==Float;
        bool pcm=format.Encoding==WaveFormatEncoding.Pcm||subtype==Pcm;
        channels=format.Channels;
        sampleBytes=format.BitsPerSample/8;
        frameBytes=format.BlockAlign;
        if((floatingPoint&&sampleBytes!=4)||(!floatingPoint&&(!pcm||sampleBytes is <1 or >4))||
            channels<1||frameBytes!=channels*sampleBytes)
            throw new NotSupportedException("The input device uses an unsupported audio sample format.");
        MonoFormat=new WaveFormat(format.SampleRate,16,1);
    }

    internal WaveFormat MonoFormat{get;}
    internal int FrameCount(int bytes)=>bytes/frameBytes;

    internal void CopyChannel(ReadOnlySpan<byte> input,int channel,Span<byte> output)
    {
        if((uint)channel>=(uint)channels)throw new ArgumentOutOfRangeException(nameof(channel));
        int source=channel*sampleBytes;
        for(int target=0;target<output.Length;target+=2,source+=frameBytes)
        {
            ReadOnlySpan<byte> sample=input.Slice(source,sampleBytes);
            short value=floatingPoint?FromFloat(System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(sample)):
                sampleBytes switch
                {
                    1=>(short)((sample[0]-128)<<8),
                    2=>System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(sample),
                    3=>(short)(sample[1]|sample[2]<<8),
                    _=>(short)(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(sample)>>16)
                };
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(output[target..],value);
        }
    }

    private static short FromFloat(float value)=>float.IsNaN(value)?(short)0:
        (short)Math.Clamp(value*32768f,short.MinValue,short.MaxValue);
}
