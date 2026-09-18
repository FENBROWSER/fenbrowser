using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FenBrowser.Media.Codecs.Ffmpeg;

/// <summary>
/// Locates and loads the dynamically linked libavcodec/libavutil pair (ADR-0001) and pins
/// the version whose ABI this adapter was written against. Nothing here runs at type load;
/// callers ask <see cref="TryLoad"/> and register decoders only when it succeeds.
/// </summary>
/// <remarks>
/// Search order: <c>FEN_FFMPEG_DIR</c>, the directory of the running application, then the
/// system's default library search (PATH). The names are the versioned Windows DLLs and the
/// unversioned <c>libavcodec.so</c>/<c>.dylib</c> elsewhere.
/// </remarks>
public static class FfmpegLibrary
{
    /// <summary>The libavcodec major version this adapter reads structures from.</summary>
    public const int RequiredAvcodecMajor = 63;

    /// <summary>The libavutil major that ships with that libavcodec.</summary>
    public const int RequiredAvutilMajor = 61;

    private const int AvLogQuiet = -8;

    internal const string AvcodecName = "avcodec";
    internal const string AvutilName = "avutil";

    private static readonly Lock s_gate = new();
    private static bool s_resolverInstalled;
    private static bool? s_loaded;
    private static string s_failure = string.Empty;
    private static string s_directory = string.Empty;
    private static string s_versions = string.Empty;

    /// <summary>The versions and where the libraries were found, for logs and THIRD_PARTY_DEPENDENCIES.md.</summary>
    public static string Location => string.IsNullOrEmpty(s_versions) ? s_directory : $"{s_versions} from {s_directory}";

    /// <summary>
    /// Loads and version-checks the libraries once. Returns false with a reason when they
    /// are missing or of another major version; the adapter then registers no decoders.
    /// </summary>
    public static bool TryLoad(out string reason)
    {
        lock (s_gate)
        {
            if (s_loaded is { } loaded)
            {
                reason = s_failure;
                return loaded;
            }

            try
            {
                InstallResolver();
                uint avcodec = Native.avcodec_version();
                uint avutil = Native.avutil_version();
                int avcodecMajor = (int)(avcodec >> 16);
                int avutilMajor = (int)(avutil >> 16);
                if (avcodecMajor != RequiredAvcodecMajor || avutilMajor != RequiredAvutilMajor)
                {
                    s_failure = $"libavcodec {FormatVersion(avcodec)} / libavutil {FormatVersion(avutil)} found; this build needs majors {RequiredAvcodecMajor}/{RequiredAvutilMajor}.";
                    s_loaded = false;
                }
                else
                {
                    // libavcodec's own text log goes to stderr; every failure the adapter
                    // cares about surfaces as a return code and is logged as a media event.
                    Native.av_log_set_level(AvLogQuiet);
                    s_failure = string.Empty;
                    s_versions = $"libavcodec {FormatVersion(avcodec)}, libavutil {FormatVersion(avutil)}";
                    s_loaded = true;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                s_failure = $"{ex.GetType().Name}: {ex.Message}";
                s_loaded = false;
            }

            reason = s_failure;
            return s_loaded.Value;
        }
    }

    private static string FormatVersion(uint packed) =>
        $"{packed >> 16}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";

    private static void InstallResolver()
    {
        if (s_resolverInstalled)
            return;
        s_resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(FfmpegLibrary).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        int major = libraryName switch
        {
            AvcodecName => RequiredAvcodecMajor,
            AvutilName => RequiredAvutilMajor,
            _ => -1,
        };
        if (major < 0)
            return IntPtr.Zero;

        foreach (string candidate in CandidateNames(libraryName, major))
        {
            foreach (string directory in CandidateDirectories())
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                {
                    s_directory = directory;
                    return handle;
                }
            }

            if (NativeLibrary.TryLoad(candidate, assembly, DllImportSearchPath.SafeDirectories | DllImportSearchPath.UserDirectories, out var fromSearch))
            {
                s_directory = "the library search path";
                return fromSearch;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateNames(string library, int major)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return $"{library}-{major.ToString(CultureInfo.InvariantCulture)}.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return $"lib{library}.{major.ToString(CultureInfo.InvariantCulture)}.dylib";
            yield return $"lib{library}.dylib";
        }
        else
        {
            yield return $"lib{library}.so.{major.ToString(CultureInfo.InvariantCulture)}";
            yield return $"lib{library}.so";
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        string? configured = Environment.GetEnvironmentVariable("FEN_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;
        yield return AppContext.BaseDirectory;

        // The winget "FFmpeg (Shared)" package is how the dev machines get the libraries;
        // its bin directory is on PATH only for shells started after the install.
        if (OperatingSystem.IsWindows())
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (string package in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg.Shared_*"))
                {
                    foreach (string build in Directory.EnumerateDirectories(package, "ffmpeg-*-shared"))
                        yield return Path.Combine(build, "bin");
                }
            }
        }
    }

    /// <summary>The few entry points the adapter uses. Structures are read by documented offsets, never marshalled.</summary>
    internal static partial class Native
    {
        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint avcodec_version();

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint avutil_version();

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr avcodec_find_decoder_by_name([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr avcodec_alloc_context3(IntPtr codec);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void avcodec_free_context(ref IntPtr context);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_open2(IntPtr context, IntPtr codec, IntPtr options);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr avcodec_parameters_alloc();

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void avcodec_parameters_free(ref IntPtr parameters);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_parameters_to_context(IntPtr context, IntPtr parameters);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_send_packet(IntPtr context, IntPtr packet);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int avcodec_receive_frame(IntPtr context, IntPtr frame);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void avcodec_flush_buffers(IntPtr context);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr av_packet_alloc();

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_packet_free(ref IntPtr packet);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_new_packet(IntPtr packet, int size);

        [DllImport(AvcodecName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_packet_unref(IntPtr packet);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr av_frame_alloc();

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_frame_free(ref IntPtr frame);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_frame_unref(IntPtr frame);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr av_mallocz(nuint size);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_opt_set(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value, int searchFlags);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_opt_get_int(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string name, int searchFlags, out long value);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_opt_get_chlayout(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string name, int searchFlags, IntPtr layout);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_channel_layout_uninit(IntPtr layout);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int av_strerror(int errnum, IntPtr buffer, nuint size);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void av_log_set_level(int level);

        [DllImport(AvutilName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr av_get_pix_fmt_name(int pixelFormat);
    }
}
