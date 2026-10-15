using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FuzzyFindAnything.Core;

public sealed class NativeIndex : IDisposable
{
    private readonly IndexHandle handle;
    private readonly int dimensions;
    public static string Version => Marshal.PtrToStringUTF8(ffa_diskann_version()) ?? throw new InvalidDataException("Missing native version.");

    public NativeIndex(int dimensions, int capacity, int threads)
    {
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The DiskANN coordinator must run as Windows x64.");
        this.dimensions = dimensions;
        Check(ffa_diskann_create((uint)dimensions, (uint)capacity, (uint)threads, out IntPtr value));
        handle = new IndexHandle(value);
    }

    public void Insert(uint id, float[] vector)
    {
        if (vector.Length != dimensions) throw new ArgumentException("Vector dimension mismatch.");
        Check(ffa_diskann_insert(handle, vector, (uint)vector.Length, id));
    }
    public void Delete(uint id) => Check(ffa_diskann_delete(handle, id));
    public uint[] Search(float[] vector, int count)
    {
        if (vector.Length != dimensions || count < 1) throw new ArgumentException("Invalid search dimensions/count.");
        uint[] tags = new uint[count]; float[] distances = new float[count];
        Check(ffa_diskann_search(handle, vector, (uint)vector.Length, (uint)count, tags, distances, out uint written));
        if (written > tags.Length) throw new InvalidDataException("Native search overflowed its result capacity.");
        return tags.AsSpan(0, (int)written).ToArray();
    }
    public void Load(string prefix) => Check(ffa_diskann_load(handle, prefix));
    public void SavePacked(string prefix, IReadOnlyList<(uint Id, float[] Vector)> vectors)
    {
        if (vectors.Count == 0) throw new ArgumentException("Empty indexes have no native snapshot.");
        float[] data = new float[checked(vectors.Count * dimensions)];
        uint[] tags = new uint[vectors.Count];
        for (int i = 0; i < vectors.Count; i++)
        {
            if (vectors[i].Vector.Length != dimensions) throw new InvalidDataException("Invalid persisted vector dimensions.");
            vectors[i].Vector.CopyTo(data, i * dimensions);
            tags[i] = vectors[i].Id;
        }
        Check(ffa_diskann_save_packed(handle, data, tags, (uint)tags.Length, prefix));
    }
    public void Dispose() => handle.Dispose();
    private static void Check(int status)
    {
        if (status != 0) throw new NativeIndexException("DiskANN: " + (Marshal.PtrToStringUTF8(ffa_diskann_error()) ?? "Unknown native failure."));
    }

    private sealed class IndexHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public IndexHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => ffa_diskann_destroy(handle) == 0;
    }

    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ffa_diskann_version();
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ffa_diskann_error();
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern int ffa_diskann_create(uint dimensions, uint capacity, uint threads, out IntPtr handle);
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern int ffa_diskann_destroy(IntPtr handle);
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern int ffa_diskann_insert(IndexHandle handle, float[] values, uint count, uint tag);
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern int ffa_diskann_delete(IndexHandle handle, uint tag);
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern int ffa_diskann_search(IndexHandle handle, float[] values, uint dimensions, uint count, [Out] uint[] tags, [Out] float[] distances, out uint written);
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern int ffa_diskann_load(IndexHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string prefix);
    [DllImport("ffa_diskann", CallingConvention = CallingConvention.Cdecl)] private static extern int ffa_diskann_save_packed(IndexHandle handle, float[] values, uint[] tags, uint count, [MarshalAs(UnmanagedType.LPUTF8Str)] string prefix);
}
