namespace FenBrowser.Svg.Fuzz;

/// <summary>
/// Built-in seeds cover every major renderer area so mutation starts from
/// inputs that reach deep code paths. An optional directory adds real-world
/// seeds for campaigns.
/// </summary>
internal static class SeedCorpus
{
    private const int MaxExternalSeeds = 4000;
    private const long MaxExternalSeedBytes = 256 * 1024;
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    private static readonly Lazy<IReadOnlyList<string>> All = new(Load);

    public static IReadOnlyList<string> Seeds => All.Value;

    private static readonly string[] BuiltIn =
    {
        $"<svg {Ns} width='64' height='64'><rect x='4' y='4' width='56' height='56' rx='8' fill='green' stroke='black' stroke-width='2'/></svg>",
        $"<svg {Ns} viewBox='0 0 100 100' width='80' height='40' preserveAspectRatio='xMinYMax slice'><circle cx='50' cy='50' r='40'/><ellipse cx='50' cy='50' rx='10' ry='30' fill='red'/></svg>",
        $"<svg {Ns} width='64' height='64'><path d='M10 10 L50 10 Q60 30 50 50 C40 60 20 60 10 50 A20 20 0 0 1 10 10 Z' fill-rule='evenodd' stroke-dasharray='4 2' stroke='blue'/></svg>",
        $"<svg {Ns} width='64' height='64'><polyline points='0,0 10,20 20,0 30,20'/><polygon points='40,40 60,40 50,60'/><line x1='0' y1='63' x2='63' y2='0' stroke='black' stroke-linecap='round'/></svg>",
        $"<svg {Ns} width='64' height='64'><defs><linearGradient id='g' x1='0' x2='1' gradientTransform='rotate(30)'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue' stop-opacity='.5'/></linearGradient><radialGradient id='r' href='#g' fx='.3' spreadMethod='reflect'/></defs><rect width='64' height='32' fill='url(#g)'/><rect y='32' width='64' height='32' fill='url(#r)'/></svg>",
        $"<svg {Ns} width='64' height='64'><defs><pattern id='p' width='8' height='8' patternUnits='userSpaceOnUse' patternTransform='skewX(10)'><rect width='4' height='4'/></pattern></defs><rect width='64' height='64' fill='url(#p)'/></svg>",
        $"<svg {Ns} width='64' height='64'><defs><clipPath id='c' clipPathUnits='objectBoundingBox'><circle cx='.5' cy='.5' r='.4'/></clipPath><mask id='m'><rect width='64' height='64' fill='white'/><circle cx='32' cy='32' r='8'/></mask></defs><g clip-path='url(#c)' mask='url(#m)'><rect width='64' height='64' fill='teal'/></g></svg>",
        $"<svg {Ns} width='64' height='64'><defs><filter id='f' x='-20%' y='-20%' width='140%' height='140%'><feGaussianBlur in='SourceGraphic' stdDeviation='2' result='b'/><feOffset dx='2' dy='2'/><feColorMatrix type='saturate' values='.5'/><feMerge><feMergeNode in='b'/><feMergeNode in='SourceGraphic'/></feMerge></filter></defs><rect x='8' y='8' width='40' height='40' filter='url(#f)'/></svg>",
        $"<svg {Ns} width='64' height='64'><defs><filter id='t'><feTurbulence baseFrequency='.05' numOctaves='2'/><feDisplacementMap in='SourceGraphic' scale='4'/><feMorphology operator='dilate' radius='1'/><feConvolveMatrix order='3' kernelMatrix='0 1 0 1 -4 1 0 1 0'/></filter></defs><rect width='64' height='64' filter='url(#t)'/></svg>",
        $"<svg {Ns} width='120' height='40' font-family='sans-serif' font-size='12'><text x='4' y='16' text-anchor='start' letter-spacing='1'>Hello<tspan dx='2' dy='4' fill='red'>svg</tspan></text><text x='4' y='34' textLength='80' lengthAdjust='spacingAndGlyphs' direction='rtl' unicode-bidi='bidi-override'>abc</text></svg>",
        $"<svg {Ns} width='120' height='60'><defs><path id='track' d='M10 50 Q60 0 110 50' pathLength='100'/></defs><text font-size='10'><textPath href='#track' startOffset='10%'>along the path</textPath></text></svg>",
        $"<svg {Ns} width='64' height='64'><defs><symbol id='s' viewBox='0 0 10 10'><rect width='10' height='10'/></symbol><g id='u'><use href='#s' width='8' height='8'/></g></defs><use xlink:href='#u' x='4' y='4'/><use href='#u' transform='translate(20 20) scale(2)'/></svg>",
        $"<svg {Ns} width='64' height='64'><defs><marker id='mk' markerWidth='6' markerHeight='6' refX='3' refY='3' orient='auto-start-reverse'><circle cx='3' cy='3' r='2'/></marker></defs><path d='M5 5 L30 30 L60 5' stroke='black' fill='none' marker-start='url(#mk)' marker-mid='url(#mk)' marker-end='url(#mk)'/></svg>",
        $"<svg {Ns} width='64' height='64'><style>.a {{ fill: var(--c, orange); }} rect:nth-child(2) {{ stroke: black; }} @media (min-width: 10px) {{ .b {{ opacity: .5 }} }}</style><g style='--c: purple'><rect class='a b' width='32' height='32'/><rect x='32' width='32' height='32' style='fill: currentColor; color: green'/></g></svg>",
        $"<svg {Ns} width='64' height='64'><image width='16' height='16' href='data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27 width=%274%27 height=%274%27%3E%3Crect width=%274%27 height=%274%27/%3E%3C/svg%3E'/><switch><rect systemLanguage='en' width='8' height='8'/><circle r='4'/></switch><a href='#x'><rect x='40' width='8' height='8'/></a></svg>",
        $"<svg {Ns} width='64' height='64'><rect width='10' height='10'><animate attributeName='width' from='10' to='60' dur='2s' fill='freeze'/><set attributeName='fill' to='red' begin='0s'/></rect><circle r='5'><animateTransform attributeName='transform' type='rotate' from='0' to='90' dur='1s'/><animateMotion path='M0 0 L50 50' dur='1s'/></circle></svg>",
        $"<svg {Ns} width='64' height='64' style='transform: rotate(5deg); transform-origin: center'><g opacity='.5' style='mix-blend-mode: multiply; isolation: isolate'><rect width='40' height='40' vector-effect='non-scaling-stroke' transform='matrix(1 .2 .1 1 3 4)'/></g><svg x='40' y='40' width='20' height='20' viewBox='0 0 4 4' overflow='visible'><rect width='8' height='8'/></svg></svg>",
        "<?xml version='1.0' encoding='UTF-8'?><!DOCTYPE svg [<!ENTITY a 'x'>]><!-- c --><svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'><![CDATA[ raw ]]><rect width='8' height='8' fill='&#x67;reen'/></svg>"
    };

    private static IReadOnlyList<string> Load()
    {
        var seeds = new List<string>(BuiltIn);
        string? directory = FuzzSettings.CorpusDirectory;
        if (directory == null)
        {
            return seeds;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.svg", SearchOption.AllDirectories)
                                         .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (seeds.Count - BuiltIn.Length >= MaxExternalSeeds)
            {
                break;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Length <= MaxExternalSeedBytes)
                {
                    seeds.Add(File.ReadAllText(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return seeds;
    }
}
