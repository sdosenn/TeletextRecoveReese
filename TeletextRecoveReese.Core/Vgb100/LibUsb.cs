// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;

namespace TeletextRecoveReese.Core.Vgb100;

/// <summary>
/// Minimal libusb-1.0 binding. The same native API is available on Linux
/// (system package), macOS (Homebrew or a bundled dylib) and Windows (bundled
/// libusb-1.0.dll with a WinUSB-bound device).
/// </summary>
internal static unsafe partial class LibUsb
{
    private const string Library = "libusb-1.0";

    public const int Success = 0;
    public const int ErrorTimeout = -7;
    public const int ErrorNotSupported = -12;

    public const byte RequestTypeVendor = 0x40;
    public const byte RecipientDevice = 0x00;
    public const byte EndpointIn = 0x80;
    public const byte EndpointOut = 0x00;

    public const byte TransferTypeIsochronous = 1;
    public const byte TransferTypeBulk = 2;

    public enum TransferStatus
    {
        Completed = 0,
        Error = 1,
        TimedOut = 2,
        Cancelled = 3,
        Stall = 4,
        NoDevice = 5,
        Overflow = 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public ushort UsbVersion;
        public byte DeviceClass;
        public byte DeviceSubClass;
        public byte DeviceProtocol;
        public byte MaxPacketSize0;
        public ushort VendorId;
        public ushort ProductId;
        public ushort DeviceVersion;
        public byte ManufacturerIndex;
        public byte ProductIndex;
        public byte SerialNumberIndex;
        public byte ConfigurationCount;
    }

    // struct libusb_transfer without the trailing isochronous descriptors.
    [StructLayout(LayoutKind.Sequential)]
    public struct Transfer
    {
        public IntPtr DeviceHandle;
        public byte Flags;
        public byte Endpoint;
        public byte Type;
        public uint Timeout;
        public TransferStatus Status;
        public int Length;
        public int ActualLength;
        public IntPtr Callback;
        public IntPtr UserData;
        public IntPtr Buffer;
        public int IsoPacketCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IsoPacketDescriptor
    {
        public uint Length;
        public uint ActualLength;
        public TransferStatus Status;
    }

    // The iso_packet_desc[] flexible array follows num_iso_packets directly.
    public static readonly int IsoPacketDescriptorOffset =
        (int)Marshal.OffsetOf<Transfer>(nameof(Transfer.IsoPacketCount)) + sizeof(int);

    public static IsoPacketDescriptor* IsoPackets(IntPtr transfer) =>
        (IsoPacketDescriptor*)((byte*)transfer + IsoPacketDescriptorOffset);

    static LibUsb()
    {
        NativeLibraryResolver.Register(Library, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? ["libusb-1.0.dll", "libusb-1.0"]
            : OperatingSystem.IsMacOS()
                ? ["libusb-1.0.0.dylib", "libusb-1.0.dylib",
                   "/opt/homebrew/lib/libusb-1.0.0.dylib", "/usr/local/lib/libusb-1.0.0.dylib"]
                : ["libusb-1.0.so.0", "libusb-1.0.so"];
        // Release packages ship libusb beside the executable; prefer that copy.
        foreach (string candidate in candidates)
        {
            if (System.IO.Path.IsPathRooted(candidate)) continue;
            string bundled = System.IO.Path.Combine(AppContext.BaseDirectory, candidate);
            if (File.Exists(bundled) && NativeLibrary.TryLoad(bundled, out IntPtr handle))
                return handle;
        }
        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle)
                || NativeLibrary.TryLoad(candidate, out handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                IntPtr context;
                if (libusb_init(&context) != Success) return false;
                libusb_exit(context);
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    public static string ErrorName(int code)
    {
        try
        {
            IntPtr name = libusb_error_name(code);
            return Marshal.PtrToStringAnsi(name) ?? $"libusb error {code}";
        }
        catch
        {
            return $"libusb error {code}";
        }
    }

    public static void Check(int result, string operation)
    {
        if (result < 0)
            throw new IOException($"{operation} failed: {ErrorName(result)} ({result}).");
    }

    [LibraryImport(Library)] public static partial int libusb_init(IntPtr* context);
    [LibraryImport(Library)] public static partial void libusb_exit(IntPtr context);
    [LibraryImport(Library)] public static partial IntPtr libusb_error_name(int code);
    [LibraryImport(Library)] public static partial nint libusb_get_device_list(IntPtr context, IntPtr** list);
    [LibraryImport(Library)] public static partial void libusb_free_device_list(IntPtr* list, int unrefDevices);
    [LibraryImport(Library)] public static partial int libusb_get_device_descriptor(IntPtr device, DeviceDescriptor* descriptor);
    [LibraryImport(Library)] public static partial byte libusb_get_bus_number(IntPtr device);
    [LibraryImport(Library)] public static partial byte libusb_get_device_address(IntPtr device);
    [LibraryImport(Library)] public static partial int libusb_get_device_speed(IntPtr device);
    [LibraryImport(Library)] public static partial int libusb_get_max_alt_packet_size(
        IntPtr device, int interfaceNumber, int alternateSetting, byte endpoint);
    [LibraryImport(Library)] public static partial int libusb_open(IntPtr device, IntPtr* handle);
    [LibraryImport(Library)] public static partial void libusb_close(IntPtr handle);
    [LibraryImport(Library)] public static partial int libusb_kernel_driver_active(IntPtr handle, int interfaceNumber);
    [LibraryImport(Library)] public static partial int libusb_claim_interface(IntPtr handle, int interfaceNumber);
    [LibraryImport(Library)] public static partial int libusb_release_interface(IntPtr handle, int interfaceNumber);
    [LibraryImport(Library)] public static partial int libusb_set_interface_alt_setting(IntPtr handle, int interfaceNumber, int alternateSetting);
    [LibraryImport(Library)] public static partial int libusb_clear_halt(IntPtr handle, byte endpoint);
    [LibraryImport(Library)] public static partial int libusb_control_transfer(
        IntPtr handle, byte requestType, byte request, ushort value, ushort index,
        byte* data, ushort length, uint timeout);
    [LibraryImport(Library)] public static partial IntPtr libusb_alloc_transfer(int isoPackets);
    [LibraryImport(Library)] public static partial void libusb_free_transfer(IntPtr transfer);
    [LibraryImport(Library)] public static partial int libusb_submit_transfer(IntPtr transfer);
    [LibraryImport(Library)] public static partial int libusb_cancel_transfer(IntPtr transfer);
    [LibraryImport(Library)] public static partial int libusb_handle_events_timeout_completed(
        IntPtr context, TimeVal* timeout, int* completed);

    // struct timeval uses C long fields: 32-bit on Windows, pointer-sized on
    // Linux. macOS declares tv_usec as int, which the little-endian low half
    // of a 64-bit long still fills correctly.
    [StructLayout(LayoutKind.Sequential)]
    public struct TimeVal
    {
        public CLong Seconds;
        public CLong Microseconds;
    }
}
