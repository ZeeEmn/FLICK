using System;
using System.Windows.Media;
namespace Rewind;
internal static class Typography
{
    internal static void Verify(){foreach(var weight in new[]{System.Windows.FontWeights.Normal,System.Windows.FontWeights.Medium,System.Windows.FontWeights.SemiBold,System.Windows.FontWeights.Bold}){var face=new Typeface(Inter,System.Windows.FontStyles.Normal,weight,System.Windows.FontStretches.Normal);if(!face.TryGetGlyphTypeface(out var glyph)||!glyph.FontUri.ToString().Contains("FLICK;component/Assets/Fonts/",StringComparison.OrdinalIgnoreCase))throw new System.IO.InvalidDataException("Bundled Inter font resolution failed: "+weight);}}
    internal static readonly FontFamily Inter=new(new Uri("pack://application:,,,/FLICK;component/"),"./Assets/Fonts/#Inter");
}
