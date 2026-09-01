using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TeletextRecoveReese.Core;

internal sealed class OpenClVhsPatternMatcher : IDisposable
{
    private const string OpenCl = "TeletextRecoveReese.OpenCL";
    private const ulong DeviceTypeGpu = 1UL << 2;
    private const ulong DeviceTypeAll = 0xFFFFFFFFFFFFFFFF;
    private const ulong MemReadWrite = 1UL;
    private const ulong MemReadOnly = 1UL << 2;
    private const uint True = 1;
    private const uint DeviceName = 0x102B;
    private const uint ProgramBuildLog = 0x1183;
    private const int Success = 0;
    private const int DeviceNotFound = -1;

    private const string KernelSource = """
        __kernel void correlate(
            __global const float *input, const int input_offset,
            __global const uchar *patterns, __global float *result,
            const int pattern_width, const int range_low, const int range_high)
        {
            int ch = get_global_id(0), pattern = get_global_id(1);
            int input_index = input_offset + ch * 8 + range_low;
            int pattern_index = pattern * pattern_width + range_low;
            int length = range_high - range_low;
            float score = 0.0f;
            int i = 0;
            for (; i + 3 < length; i += 4) {
                float4 pattern_values = convert_float4(vload4(0, patterns + pattern_index + i));
                float4 d = vload4(0, input + input_index + i) - pattern_values;
                score += dot(d, d);
            }
            for (; i < length; ++i) {
                float d = input[input_index + i] - (float)patterns[pattern_index + i];
                score += d * d;
            }
            result[ch * get_global_size(1) + pattern] = score;
        }

        __kernel void reduce_patterns(
            __global const float *input, __global float *temporary_values,
            __global int *temporary_indices, const int pattern_count, const int partitions)
        {
            int ch = get_global_id(0), partition = get_global_id(1);
            int width = get_global_size(0);
            int step = pattern_count / partitions;
            int first = partition * step, last = first + step;
            int input_index = ch * pattern_count + first;
            float best_value = input[input_index];
            int best_index = first;
            for (int p = first; p + 3 < last; p += 4) {
                float4 values = vload4(0, input + ch * pattern_count + p);
                if (any(values < best_value)) {
                    if (values.s0 < best_value) { best_value = values.s0; best_index = p; }
                    if (values.s1 < best_value) { best_value = values.s1; best_index = p + 1; }
                    if (values.s2 < best_value) { best_value = values.s2; best_index = p + 2; }
                    if (values.s3 < best_value) { best_value = values.s3; best_index = p + 3; }
                }
            }
            int output_index = partition * width + ch;
            temporary_values[output_index] = best_value;
            temporary_indices[output_index] = best_index;
        }

        __kernel void finish_reduction(
            __global const float *temporary_values, __global const int *temporary_indices,
            __global const uchar *values, __global uchar *output, const int partitions)
        {
            int ch = get_global_id(0), width = get_global_size(0);
            float best_value = temporary_values[ch];
            int best_index = temporary_indices[ch];
            int partition = 0;
            for (; partition + 3 < partitions; partition += 4) {
                int offset = partition * width + ch;
                float4 values = (float4)(
                    temporary_values[offset],
                    temporary_values[offset + width],
                    temporary_values[offset + width * 2],
                    temporary_values[offset + width * 3]);
                if (any(values < best_value)) {
                    if (values.s0 < best_value) { best_value = values.s0; best_index = temporary_indices[offset]; }
                    if (values.s1 < best_value) { best_value = values.s1; best_index = temporary_indices[offset + width]; }
                    if (values.s2 < best_value) { best_value = values.s2; best_index = temporary_indices[offset + width * 2]; }
                    if (values.s3 < best_value) { best_value = values.s3; best_index = temporary_indices[offset + width * 3]; }
                }
            }
            output[ch] = values[best_index];
        }

        __kernel void match_fused(
            __global const float *input, const int input_offset,
            __global const uchar *patterns, __global const uchar *values,
            __global uchar *output, const int pattern_width,
            const int range_low, const int range_high, const int pattern_count,
            __local float *local_scores, __local int *local_indices)
        {
            int lane = get_local_id(0);
            int lanes = get_local_size(0);
            int ch = get_group_id(1);
            int input_index = input_offset + ch * 8 + range_low;
            int length = range_high - range_low;
            float best_score = 3.402823466e+38F;
            int best_index = 0;

            for (int pattern = lane; pattern < pattern_count; pattern += lanes) {
                int pattern_index = pattern * pattern_width + range_low;
                float score = 0.0f;
                int i = 0;
                for (; i + 3 < length; i += 4) {
                    float4 pattern_values = convert_float4(vload4(0, patterns + pattern_index + i));
                    float4 d = vload4(0, input + input_index + i) - pattern_values;
                    score += dot(d, d);
                }
                for (; i < length; ++i) {
                    float d = input[input_index + i] - (float)patterns[pattern_index + i];
                    score += d * d;
                }
                if (score < best_score || (score == best_score && pattern < best_index)) {
                    best_score = score;
                    best_index = pattern;
                }
            }

            local_scores[lane] = best_score;
            local_indices[lane] = best_index;
            barrier(CLK_LOCAL_MEM_FENCE);
            for (int stride = lanes / 2; stride > 0; stride >>= 1) {
                if (lane < stride) {
                    float other_score = local_scores[lane + stride];
                    int other_index = local_indices[lane + stride];
                    if (other_score < local_scores[lane] ||
                        (other_score == local_scores[lane] && other_index < local_indices[lane])) {
                        local_scores[lane] = other_score;
                        local_indices[lane] = other_index;
                    }
                }
                barrier(CLK_LOCAL_MEM_FENCE);
            }
            if (lane == 0) output[ch] = values[local_indices[0]];
        }
        """;

    private sealed class GpuTable
    {
        public required int Width;
        public required int Count;
        public required int Start;
        public required int End;
        public required int Partitions;
        public required IntPtr Patterns;
        public required IntPtr Values;
    }

    private readonly IntPtr _context;
    private readonly IntPtr _queue;
    private readonly IntPtr _program;
    private readonly IntPtr _correlateKernel;
    private readonly IntPtr _reduceKernel;
    private readonly IntPtr _finishKernel;
    private readonly IntPtr _fusedKernel;
    private readonly IntPtr _input;
    private readonly IntPtr _output;
    private readonly GpuTable _full;
    private readonly GpuTable _parity;
    private readonly GpuTable _hamming;
    private bool _disposed;

    public string DeviceDescription { get; }

    static OpenClVhsPatternMatcher()
    {
        NativeLibrary.SetDllImportResolver(typeof(OpenClVhsPatternMatcher).Assembly, ResolveOpenClLibrary);
    }

    private static IntPtr ResolveOpenClLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName != OpenCl) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsMacOS()
            ? ["/System/Library/Frameworks/OpenCL.framework/OpenCL"]
            : OperatingSystem.IsWindows()
                ? ["OpenCL.dll"]
                : ["libOpenCL.so.1", "libOpenCL.so"];
        foreach (string candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
                return handle;
        throw new DllNotFoundException(
            "No OpenCL loader was found. Install an OpenCL ICD loader and a driver for the capture machine's GPU.");
    }

    public OpenClVhsPatternMatcher()
    {
        Check(clGetPlatformIDs(0, IntPtr.Zero, out uint platformCount), "enumerating OpenCL platforms");
        if (platformCount == 0) throw new InvalidOperationException("No OpenCL platform was found.");
        var platforms = new IntPtr[platformCount];
        IntPtr platformMemory = Marshal.AllocHGlobal(checked((int)platformCount * IntPtr.Size));
        try
        {
            Check(clGetPlatformIDs(platformCount, platformMemory, out _), "reading OpenCL platforms");
            for (int i = 0; i < platforms.Length; i++) platforms[i] = Marshal.ReadIntPtr(platformMemory, i * IntPtr.Size);
        }
        finally { Marshal.FreeHGlobal(platformMemory); }

        IntPtr device = IntPtr.Zero;
        foreach (IntPtr platform in platforms)
        {
            int error = clGetDeviceIDs(platform, DeviceTypeGpu, 0, IntPtr.Zero, out uint count);
            if (error == Success && count > 0)
            {
                IntPtr deviceMemory = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
                try
                {
                    Check(clGetDeviceIDs(platform, DeviceTypeGpu, count, deviceMemory, out _), "selecting an OpenCL GPU");
                    device = Marshal.ReadIntPtr(deviceMemory);
                }
                finally { Marshal.FreeHGlobal(deviceMemory); }
                break;
            }
            if (error != DeviceNotFound && error != Success) Check(error, "enumerating OpenCL GPUs");
        }
        if (device == IntPtr.Zero)
        {
            foreach (IntPtr platform in platforms)
            {
                int error = clGetDeviceIDs(platform, DeviceTypeAll, 0, IntPtr.Zero, out uint count);
                if (error != Success || count == 0) continue;
                IntPtr deviceMemory = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
                try
                {
                    Check(clGetDeviceIDs(platform, DeviceTypeAll, count, deviceMemory, out _), "selecting an OpenCL device");
                    device = Marshal.ReadIntPtr(deviceMemory);
                }
                finally { Marshal.FreeHGlobal(deviceMemory); }
                break;
            }
        }
        if (device == IntPtr.Zero) throw new InvalidOperationException("No usable OpenCL device was found.");
        DeviceDescription = ReadDeviceString(device, DeviceName);

        _context = clCreateContext(IntPtr.Zero, 1, new[] { device }, IntPtr.Zero, IntPtr.Zero, out int createError);
        Check(createError, "creating the OpenCL context");
        _queue = clCreateCommandQueue(_context, device, 0, out createError);
        Check(createError, "creating the OpenCL command queue");
        _program = clCreateProgramWithSource(_context, 1, new[] { KernelSource }, null, out createError);
        Check(createError, "creating the OpenCL program");
        int buildError = clBuildProgram(_program, 1, new[] { device }, string.Empty, IntPtr.Zero, IntPtr.Zero);
        if (buildError != Success)
            throw new InvalidOperationException($"OpenCL kernel build failed ({buildError}): {ReadBuildLog(_program, device)}");
        _correlateKernel = clCreateKernel(_program, "correlate", out createError);
        Check(createError, "creating the OpenCL correlation kernel");
        _reduceKernel = clCreateKernel(_program, "reduce_patterns", out createError);
        Check(createError, "creating the OpenCL reduction kernel");
        _finishKernel = clCreateKernel(_program, "finish_reduction", out createError);
        Check(createError, "creating the OpenCL final reduction kernel");
        _fusedKernel = clCreateKernel(_program, "match_fused", out createError);
        Check(createError, "creating the fused OpenCL matcher kernel");
        _input = CreateBuffer(MemReadOnly, 368 * sizeof(float));
        _output = CreateBuffer(MemReadWrite, 42);
        _full = LoadTable(VbiPatternResources.OpenVhsFull());
        _parity = LoadTable(VbiPatternResources.OpenVhsParity());
        _hamming = LoadTable(VbiPatternResources.OpenVhsHamming());
    }

    public void UploadLine(float[] bits) => Write(_input, bits);
    public byte[] MatchHamming(int inputOffset, int count) => Match(_hamming, inputOffset, count);
    public byte[] MatchParity(int inputOffset, int count) => Match(_parity, inputOffset, count);
    public byte[] MatchFull(int inputOffset, int count) => Match(_full, inputOffset, count);

    private byte[] Match(GpuTable table, int inputOffset, int count)
    {
        const int localSize = 256;
        SetArg(_fusedKernel, 0, _input);
        SetArg(_fusedKernel, 1, inputOffset);
        SetArg(_fusedKernel, 2, table.Patterns);
        SetArg(_fusedKernel, 3, table.Values);
        SetArg(_fusedKernel, 4, _output);
        SetArg(_fusedKernel, 5, table.Width);
        SetArg(_fusedKernel, 6, table.Start);
        SetArg(_fusedKernel, 7, table.End);
        SetArg(_fusedKernel, 8, table.Count);
        SetLocalArg(_fusedKernel, 9, localSize * sizeof(float));
        SetLocalArg(_fusedKernel, 10, localSize * sizeof(int));
        Check(clEnqueueNDRangeKernel(
            _queue, _fusedKernel, 2, null,
            new nuint[] { localSize, (nuint)count },
            new nuint[] { localSize, 1 }, 0, null, null),
            "running fused OpenCL pattern matching");

        var result = new byte[count];
        Read(_output, result);
        return result;
    }

    private GpuTable LoadTable(Stream stream)
    {
        using (stream)
        using (var reader = new BinaryReader(stream))
        {
            Span<byte> header = stackalloc byte[14];
            reader.ReadExactly(header);
            int width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
            int outputWidth = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[4..]));
            int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]));
            if (outputWidth != 1) throw new InvalidDataException("Unsupported VHS pattern output width.");
            byte[] patternBytes = reader.ReadBytes(checked(width * count));
            byte[] values = reader.ReadBytes(count);
            if (patternBytes.Length != width * count || values.Length != count)
                throw new EndOfStreamException("A bundled VHS pattern table is truncated.");
            IntPtr patternBuffer = CreateBuffer(MemReadOnly, patternBytes.Length);
            IntPtr valueBuffer = CreateBuffer(MemReadOnly, values.Length);
            Write(patternBuffer, patternBytes); Write(valueBuffer, values);
            return new GpuTable
            {
                Width = width,
                Count = count,
                Start = header[12],
                End = header[13],
                Partitions = count > 32768 ? 1024 : 512,
                Patterns = patternBuffer,
                Values = valueBuffer,
            };
        }
    }

    private IntPtr CreateBuffer(ulong flags, int bytes)
    {
        IntPtr value = clCreateBuffer(_context, flags, (nuint)bytes, IntPtr.Zero, out int error);
        Check(error, "allocating OpenCL memory"); return value;
    }

    private void Write<T>(IntPtr buffer, T[] data) where T : unmanaged
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { Check(clEnqueueWriteBuffer(_queue, buffer, True, 0, (nuint)(data.Length * Marshal.SizeOf<T>()), handle.AddrOfPinnedObject(), 0, null, null), "uploading OpenCL data"); }
        finally { handle.Free(); }
    }

    private void Read(IntPtr buffer, byte[] data)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { Check(clEnqueueReadBuffer(_queue, buffer, True, 0, (nuint)data.Length, handle.AddrOfPinnedObject(), 0, null, null), "reading OpenCL output"); }
        finally { handle.Free(); }
    }

    private static void SetArg(IntPtr kernel, uint index, IntPtr value) => Check(clSetKernelArgPtr(kernel, index, (nuint)IntPtr.Size, ref value), "setting an OpenCL buffer argument");
    private static void SetArg(IntPtr kernel, uint index, int value) => Check(clSetKernelArgInt(kernel, index, sizeof(int), ref value), "setting an OpenCL integer argument");
    private static void SetLocalArg(IntPtr kernel, uint index, int bytes) => Check(clSetKernelArgLocal(kernel, index, (nuint)bytes, IntPtr.Zero), "setting OpenCL local memory");
    private static void Check(int error, string operation) { if (error != Success) throw new InvalidOperationException($"OpenCL error {error} while {operation}."); }

    private static string ReadDeviceString(IntPtr device, uint parameter)
    {
        clGetDeviceInfo(device, parameter, 0, IntPtr.Zero, out nuint size);
        byte[] bytes = new byte[(int)size]; GCHandle h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { Check(clGetDeviceInfo(device, parameter, size, h.AddrOfPinnedObject(), out _), "reading the OpenCL device name"); }
        finally { h.Free(); }
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static string ReadBuildLog(IntPtr program, IntPtr device)
    {
        clGetProgramBuildInfo(program, device, ProgramBuildLog, 0, IntPtr.Zero, out nuint size);
        byte[] bytes = new byte[(int)size]; GCHandle h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { clGetProgramBuildInfo(program, device, ProgramBuildLog, size, h.AddrOfPinnedObject(), out _); }
        finally { h.Free(); }
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (IntPtr value in new[]
                 {
                     _full.Patterns, _full.Values, _parity.Patterns, _parity.Values,
                     _hamming.Patterns, _hamming.Values, _input, _output,
                 })
            if (value != IntPtr.Zero) clReleaseMemObject(value);
        if (_correlateKernel != IntPtr.Zero) clReleaseKernel(_correlateKernel);
        if (_reduceKernel != IntPtr.Zero) clReleaseKernel(_reduceKernel);
        if (_finishKernel != IntPtr.Zero) clReleaseKernel(_finishKernel);
        if (_fusedKernel != IntPtr.Zero) clReleaseKernel(_fusedKernel);
        if (_program != IntPtr.Zero) clReleaseProgram(_program);
        if (_queue != IntPtr.Zero) clReleaseCommandQueue(_queue);
        if (_context != IntPtr.Zero) clReleaseContext(_context);
    }

    [DllImport(OpenCl)] private static extern int clGetPlatformIDs(uint n, IntPtr ids, out uint count);
    [DllImport(OpenCl)] private static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint n, IntPtr ids, out uint count);
    [DllImport(OpenCl)] private static extern IntPtr clCreateContext(IntPtr props, uint n, IntPtr[] devices, IntPtr notify, IntPtr data, out int error);
    [DllImport(OpenCl)] private static extern IntPtr clCreateCommandQueue(IntPtr context, IntPtr device, ulong properties, out int error);
    [DllImport(OpenCl)] private static extern IntPtr clCreateProgramWithSource(IntPtr context, uint count, string[] strings, nuint[]? lengths, out int error);
    [DllImport(OpenCl)] private static extern int clBuildProgram(IntPtr program, uint n, IntPtr[] devices, string options, IntPtr notify, IntPtr data);
    [DllImport(OpenCl)] private static extern IntPtr clCreateKernel(IntPtr program, string name, out int error);
    [DllImport(OpenCl)] private static extern IntPtr clCreateBuffer(IntPtr context, ulong flags, nuint size, IntPtr host, out int error);
    [DllImport(OpenCl)] private static extern int clEnqueueWriteBuffer(IntPtr queue, IntPtr buffer, uint blocking, nuint offset, nuint size, IntPtr ptr, uint events, IntPtr[]? wait, IntPtr[]? result);
    [DllImport(OpenCl)] private static extern int clEnqueueReadBuffer(IntPtr queue, IntPtr buffer, uint blocking, nuint offset, nuint size, IntPtr ptr, uint events, IntPtr[]? wait, IntPtr[]? result);
    [DllImport(OpenCl, EntryPoint = "clSetKernelArg")] private static extern int clSetKernelArgPtr(IntPtr kernel, uint index, nuint size, ref IntPtr value);
    [DllImport(OpenCl, EntryPoint = "clSetKernelArg")] private static extern int clSetKernelArgInt(IntPtr kernel, uint index, nuint size, ref int value);
    [DllImport(OpenCl, EntryPoint = "clSetKernelArg")] private static extern int clSetKernelArgLocal(IntPtr kernel, uint index, nuint size, IntPtr value);
    [DllImport(OpenCl)] private static extern int clEnqueueNDRangeKernel(IntPtr queue, IntPtr kernel, uint dimensions, nuint[]? offset, nuint[] global, nuint[]? local, uint events, IntPtr[]? wait, IntPtr[]? result);
    [DllImport(OpenCl)] private static extern int clGetDeviceInfo(IntPtr device, uint parameter, nuint size, IntPtr value, out nuint returned);
    [DllImport(OpenCl)] private static extern int clGetProgramBuildInfo(IntPtr program, IntPtr device, uint parameter, nuint size, IntPtr value, out nuint returned);
    [DllImport(OpenCl)] private static extern int clReleaseMemObject(IntPtr value);
    [DllImport(OpenCl)] private static extern int clReleaseKernel(IntPtr value);
    [DllImport(OpenCl)] private static extern int clReleaseProgram(IntPtr value);
    [DllImport(OpenCl)] private static extern int clReleaseCommandQueue(IntPtr value);
    [DllImport(OpenCl)] private static extern int clReleaseContext(IntPtr value);
}
