using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FuzzyFindAnything.App.Services;

public static class ThumbnailLoader
{
    private static readonly SemaphoreSlim Slots = new(2);

    public static async Task<BitmapSource> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await Slots.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Load(path, cancellationToken), cancellationToken);
        }
        finally
        {
            Slots.Release();
        }
    }

    private static BitmapSource Load(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            throw new FileNotFoundException("The image has been moved or deleted.", path);
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid interfaceId = typeof(IShellItemImageFactory).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out factory));
            uint flags = Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase) ? 0u : 8u;
            Marshal.ThrowExceptionForHR(factory.GetImage(new NativeSize { Width = 360, Height = 240 }, flags, out bitmap));
            cancellationToken.ThrowIfCancellationRequested();
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (factory is not null)
                Marshal.FinalReleaseComObject(factory);
            CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
