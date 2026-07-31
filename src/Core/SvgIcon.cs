using System.IO;
using System.Windows;
using System.Xml.Linq;
using Media = System.Windows.Media;

namespace BeeX.DeskNest;

static class SvgIcon
{
    public static Media.DrawingImage Load(string name, double size=24, Media.Brush? fill=null, double strokeScale=1.0)
    {
        try
        {
            var uri=new Uri($"pack://application:,,,/Assets/Icons/{name}.svg",UriKind.Absolute);
            var stream=System.Windows.Application.GetResourceStream(uri)?.Stream;
            if(stream==null)return Fallback(name,size,fill);
            using(stream)
            {
                var doc=XDocument.Load(stream);
                var svg=doc.Root!;
                var ns=svg.GetDefaultNamespace();
                // Read stroke/fill on <svg> as defaults
                var defaultStroke=svg.Attribute("stroke")?.Value;
                var defaultFill=svg.Attribute("fill")?.Value;
                var defaultStrokeWidth=svg.Attribute("stroke-width")?.Value;
                var defaultLineCap=svg.Attribute("stroke-linecap")?.Value;
                var drawing=new Media.DrawingGroup();
                var color=fill??Media.Brushes.Black;
                foreach(var path in svg.Descendants(ns+"path"))
                {
                    var d=path.Attribute("d")?.Value;
                    if(string.IsNullOrWhiteSpace(d))continue;
                    var stroke=path.Attribute("stroke")?.Value??defaultStroke;
                    var fillAttr=path.Attribute("fill")?.Value??defaultFill;
                    var strokeWidth=path.Attribute("stroke-width")?.Value??defaultStrokeWidth;
                    var cap=path.Attribute("stroke-linecap")?.Value??defaultLineCap;
                    try
                    {
                        var geo=new Media.GeometryDrawing{Geometry=Media.Geometry.Parse(d)};
                        bool hasStroke=!string.IsNullOrWhiteSpace(stroke)&&stroke!="none";
                        bool hasFill=!string.IsNullOrWhiteSpace(fillAttr)&&fillAttr!="none";
                        if(hasStroke)
                        {
                            var sc=stroke=="currentColor"?color:new Media.SolidColorBrush(Parse(stroke));
                            var sw=(double.TryParse(strokeWidth,out var w)?w:1)*size/24*strokeScale; // SVG 24x24 viewBox -> target size
                            geo.Pen=new Media.Pen(sc,sw){StartLineCap=Cap(cap),EndLineCap=Cap(cap),LineJoin=Media.PenLineJoin.Round};
                        }
                        if(hasFill)
                            geo.Brush=fillAttr=="currentColor"?color:new Media.SolidColorBrush(Parse(fillAttr));
                        else if(!hasStroke)
                            geo.Brush=color; // no stroke, no fill -> fill with the passed-in color
                        drawing.Children.Add(geo);
                    }catch{}
                }
                return drawing.Children.Count>0?new Media.DrawingImage(drawing):Fallback(name,size,fill);
            }
        }
        catch{return Fallback(name,size,fill);}
    }

    static Media.DrawingImage Fallback(string name,double size,Media.Brush? fill)
    {
        var ft=new Media.FormattedText(name,System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,new Media.Typeface("Segoe UI"),size,fill??Media.Brushes.Black,1);
        return new Media.DrawingImage(new Media.DrawingGroup{Children={new Media.GeometryDrawing(fill??Media.Brushes.Black,null,ft.BuildGeometry(new System.Windows.Point(0,0)))}});
    }

    static System.Windows.Media.Color Parse(string hex)
    {
        if(hex.StartsWith("#"))hex=hex[1..];
        if(hex.Length==3)hex=$"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        if(hex.Length==6)hex="FF"+hex;
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#"+hex);
    }

    static Media.PenLineCap Cap(string? c)=>c switch{"round"=>Media.PenLineCap.Round,"square"=>Media.PenLineCap.Square,_=>Media.PenLineCap.Flat};
}
