using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Rewind;

internal sealed class VideoCropOverlay : FrameworkElement
{
    private Rect frame=Rect.Empty;
    private ClipFiles.VideoCrop crop=ClipFiles.VideoCrop.Full,atStart;
    private Point origin;
    private int horizontal,vertical;
    private bool moving;
    private static readonly Brush Shade=MakeShade();
    internal event Action<ClipFiles.VideoCrop>? Changed;
    private static Brush MakeShade(){var brush=new SolidColorBrush(Color.FromArgb(140,0,0,0));brush.Freeze();return brush;}

    internal void Refresh(Rect frame,ClipFiles.VideoCrop crop)
    {
        if(this.frame==frame&&this.crop==crop)return;
        this.frame=frame;
        this.crop=crop;
        InvalidateVisual();
    }

    private Rect Selection=>new(frame.Left+frame.Width*crop.Left,frame.Top+frame.Height*crop.Top,
        frame.Width*(crop.Right-crop.Left),frame.Height*(crop.Bottom-crop.Top));

    protected override void OnRender(DrawingContext dc)
    {
        if(frame.IsEmpty)return;
        Rect selected=Selection;
        var hitArea=frame;hitArea.Inflate(5,5);
        dc.DrawRectangle(Brushes.Transparent,null,hitArea);
        void ShadeRegion(double left,double top,double right,double bottom)
        {
            if(right>left&&bottom>top)dc.DrawRectangle(Shade,null,new Rect(new Point(left,top),new Point(right,bottom)));
        }
        ShadeRegion(frame.Left,frame.Top,frame.Right,selected.Top);
        ShadeRegion(frame.Left,selected.Bottom,frame.Right,frame.Bottom);
        ShadeRegion(frame.Left,selected.Top,selected.Left,selected.Bottom);
        ShadeRegion(selected.Right,selected.Top,frame.Right,selected.Bottom);
        dc.DrawRectangle(null,new Pen(Brushes.White,1.5),selected);
        for(int row=0;row<3;row++)
        for(int column=0;column<3;column++)
        {
            if(row==1&&column==1)continue;
            double x=Math.Clamp(selected.Left+column*selected.Width/2,frame.Left,frame.Right);
            double y=Math.Clamp(selected.Top+row*selected.Height/2,frame.Top,frame.Bottom);
            dc.DrawRectangle(Brushes.White,null,new Rect(x-3,y-3,6,6));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if(frame.IsEmpty)return;
        var point=e.GetPosition(this);
        Edges(point,out horizontal,out vertical,out moving);
        if(!moving&&horizontal==0&&vertical==0)return;
        origin=point;
        atStart=crop;
        if(CaptureMouse())e.Handled=true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if(frame.IsEmpty)return;
        var pos=e.GetPosition(this);
        if(!IsMouseCaptured)
        {
            Edges(pos,out int x,out int y,out bool move);
            Cursor=move?Cursors.SizeAll:x!=0&&y!=0?x==y?Cursors.SizeNWSE:Cursors.SizeNESW:
                x!=0?Cursors.SizeWE:y!=0?Cursors.SizeNS:Cursors.Arrow;
            return;
        }
        double dx=(pos.X-origin.X)/frame.Width,dy=(pos.Y-origin.Y)/frame.Height;
        double left=atStart.Left,right=atStart.Right,top=atStart.Top,bottom=atStart.Bottom;
        if(moving)
        {
            dx=Math.Clamp(dx,-left,1-right);dy=Math.Clamp(dy,-top,1-bottom);
            left+=dx;right+=dx;top+=dy;bottom+=dy;
        }
        else
        {
            const double minimum=.02;
            if(horizontal<0)left=Math.Clamp(left+dx,0,right-minimum);
            if(horizontal>0)right=Math.Clamp(right+dx,left+minimum,1);
            if(vertical<0)top=Math.Clamp(top+dy,0,bottom-minimum);
            if(vertical>0)bottom=Math.Clamp(bottom+dy,top+minimum,1);
        }
        Changed?.Invoke(new(left,top,right,bottom));
        e.Handled=true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if(!IsMouseCaptured)return;
        e.Handled=true;
        ReleaseMouseCapture();
    }

    private void Edges(Point point,out int x,out int y,out bool move)
    {
        var selected=Selection;
        double px=Math.Min(10,selected.Width/3),py=Math.Min(10,selected.Height/3);
        x=0;y=0;move=false;
        if(point.X<selected.Left-px||point.X>selected.Right+px||point.Y<selected.Top-py||point.Y>selected.Bottom+py)return;
        x=Math.Abs(point.X-selected.Left)<=px?-1:Math.Abs(point.X-selected.Right)<=px?1:0;
        y=Math.Abs(point.Y-selected.Top)<=py?-1:Math.Abs(point.Y-selected.Bottom)<=py?1:0;
        move=x==0&&y==0&&selected.Contains(point);
    }
}
