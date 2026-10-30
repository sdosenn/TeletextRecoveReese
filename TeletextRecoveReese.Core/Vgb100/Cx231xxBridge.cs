// SPDX-License-Identifier: GPL-2.0-or-later
// Register, I2C and GPIO access to the Conexant CX23102 USB bridge, ported
// from the Linux cx231xx driver (cx231xx-core.c, cx231xx-i2c.c).
namespace TeletextRecoveReese.Core.Vgb100;

/// <summary>USB operations the bridge needs; real devices use libusb, tests replay a trace.</summary>
public interface ICx231xxTransport
{
    int ControlIn(byte request, ushort value, ushort index, Span<byte> data);
    int ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data);
    void SetInterfaceAltSetting(int interfaceNumber, int alternateSetting);
    void ClearHalt(byte endpoint);
    void Delay(int milliseconds);
}

internal sealed class Cx231xxBridge
{
    // cx231xx-pcb-cfg.h vendor requests
    public const byte VrtSetRegister = 0x0c;
    public const byte VrtGetRegister = 0x0d;
    private const byte VrtSetGpio = 0x08;
    private const byte VrtGetGpio = 0x09;
    private const byte VrtSetGpie = 0x0a;
    private const byte VrtGetGpie = 0x0b;

    // cx231xx-reg.h bridge registers
    public const ushort BoardCfgStat = 0x00;
    public const ushort TsModeReg = 0x04;
    public const ushort EpModeSet = 0x18;
    public const ushort PwrCtlEn = 0x74;

    // cx231xx.h I2C device addresses (8-bit form)
    public const byte AfeDeviceAddress = 0x60;
    public const byte I2sBlockDeviceAddress = 0x98;
    public const byte VideoBlockI2cAddress = 0x88;

    private const int I2cSpeed100K = 0x2;
    private const int I2cSync = 0x1;
    private const int UrbMaxControlSize = 80;

    private readonly ICx231xxTransport _usb;
    private uint _gpioDirection;
    private uint _gpioValue;

    public Cx231xxBridge(ICx231xxTransport usb) => _usb = usb;

    public ICx231xxTransport Usb => _usb;

    public void Delay(int milliseconds) => _usb.Delay(milliseconds);

    private static ushort ByteEnable(int length) => length switch
    {
        1 => 0x1,
        2 => 0x3,
        3 => 0x7,
        4 => 0xf,
        _ => throw new ArgumentOutOfRangeException(nameof(length)),
    };

    public int ReadCtrlReg(byte request, ushort register, Span<byte> buffer) =>
        _usb.ControlIn(request, ByteEnable(buffer.Length), register, buffer);

    public int WriteCtrlReg(byte request, ushort register, ReadOnlySpan<byte> buffer) =>
        _usb.ControlOut(request, ByteEnable(buffer.Length), register, buffer);

    public uint ReadRegister(ushort register)
    {
        Span<byte> value = stackalloc byte[4];
        Check(ReadCtrlReg(VrtGetRegister, register, value), $"read bridge register 0x{register:x2}");
        return BitConverter.ToUInt32(value);
    }

    public int WriteRegister(ushort register, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        return WriteCtrlReg(VrtSetRegister, register, bytes);
    }

    /// <summary>cx231xx_mode_register(): OR bits into a bridge register.</summary>
    public int ModeRegister(ushort address, uint mode)
    {
        Span<byte> value = stackalloc byte[4];
        int status = ReadCtrlReg(VrtGetRegister, address, value);
        if (status < 0) return status;
        uint updated = BitConverter.ToUInt32(value) | mode;
        BitConverter.TryWriteBytes(value, updated);
        return WriteCtrlReg(VrtSetRegister, address, value);
    }

    /// <summary>cx231xx_send_vendor_cmd(), including the 4-byte chopping of master 2/3 transfers.</summary>
    private int SendVendorCommand(bool directionIn, byte request, ushort value, ushort index, Span<byte> buffer)
    {
        if (buffer.Length > UrbMaxControlSize) return -22;

        int Transfer(ushort wValue, Span<byte> chunk) => directionIn
            ? _usb.ControlIn(request, wValue, index, chunk)
            : _usb.ControlOut(request, wValue, index, chunk);

        if (buffer.Length > 4 && request is 0x2 or 0x4 or 0x5 or 0x6)
        {
            int offset = 0;
            int unsent = buffer.Length;

            value = (ushort)(value & 0xfffb);
            value = (ushort)((value & 0xffbd) | 0x2);
            int status = Transfer(value, buffer.Slice(offset, 4));
            unsent -= 4;

            value = (ushort)((value & 0xffbd) | 0x42);
            while (unsent - 4 > 0)
            {
                offset += 4;
                status = Transfer(value, buffer.Slice(offset, 4));
                unsent -= 4;
            }

            value = (ushort)((value & 0xffbd) | 0x40);
            offset += 4;
            return Transfer(value, buffer.Slice(offset, unsent));
        }

        return Transfer(value, buffer);
    }

    /// <summary>cx231xx_send_usb_command(): one I2C transaction on master <paramref name="bus"/>.</summary>
    public int SendUsbCommand(
        int bus, byte deviceAddress7Bit, bool read, int subAddressLength, ushort subAddress,
        Span<byte> buffer, bool noStop = false, bool reserve = false)
    {
        ushort value = (ushort)(deviceAddress7Bit << 9 | I2cSpeed100K << 4 | subAddressLength << 2
                                | (noStop ? 1 : 0) << 1 | I2cSync | (reserve ? 1 : 0) << 6);
        byte request = (byte)(read ? bus + 4 : bus);
        ushort index = subAddressLength switch
        {
            0 => 0,
            1 => (ushort)(subAddress & 0xff),
            _ => subAddress,
        };
        if (read) buffer.Clear();
        return SendVendorCommand(read, request, value, index, buffer);
    }

    /// <summary>cx231xx_read_i2c_data(): register read through I2C master 0.</summary>
    public int ReadI2cData(byte deviceAddress, ushort subAddress, int subAddressLength, out uint data, int dataLength)
    {
        if (subAddressLength == 0) subAddress = 0;
        else if (subAddressLength == 1) subAddress &= 0xff;
        Span<byte> value = stackalloc byte[4];
        int status = SendUsbCommand(0, (byte)(deviceAddress >> 1), true, subAddressLength, subAddress,
            value[..dataLength]);
        data = dataLength == 1 ? value[0] : BitConverter.ToUInt32(value);
        if (status < 0) data = 0;
        return status;
    }

    /// <summary>cx231xx_write_i2c_data(): register write through I2C master 0.</summary>
    public int WriteI2cData(byte deviceAddress, ushort subAddress, int subAddressLength, uint data, int dataLength)
    {
        if (subAddressLength == 0) subAddress = 0;
        else if (subAddressLength == 1) subAddress &= 0xff;
        Span<byte> value = stackalloc byte[4];
        BitConverter.TryWriteBytes(value, data);
        return SendUsbCommand(0, (byte)(deviceAddress >> 1), false, subAddressLength, subAddress,
            value[..dataLength]);
    }

    /// <summary>cx231xx_reg_mask_write().</summary>
    public int RegisterMaskWrite(byte deviceAddress, int size, ushort register, int bitStart, int bitEnd, uint value)
    {
        if (bitStart > size - 1 || bitEnd > size - 1) return -1;
        int status = ReadI2cData(deviceAddress, register, 2, out uint current, size == 8 ? 1 : 4);
        if (status < 0) return status;

        uint mask = 1u << bitEnd;
        for (int i = bitEnd; i > bitStart && i > 0; i--)
            mask += 1u << (i - 1);
        value <<= bitStart;
        current &= ~mask;
        current |= value;
        if (size == 8)
            return WriteI2cData(deviceAddress, register, 2, current & 0xff, 1);
        return WriteI2cData(deviceAddress, register, 2, current, 4);
    }

    /// <summary>cx231xx_read_modify_write_i2c_dword().</summary>
    public int ReadModifyWriteI2cDword(byte deviceAddress, ushort subAddress, uint mask, uint value)
    {
        int status = ReadI2cData(deviceAddress, subAddress, 2, out uint current, 4);
        if (status < 0) return status;
        current &= ~mask;
        current |= value;
        return WriteI2cData(deviceAddress, subAddress, 2, current, 4);
    }

    /// <summary>cx231xx_set_field(): shift a value into the position of a field mask.</summary>
    public static uint SetField(uint fieldMask, uint data)
    {
        for (uint mask = fieldMask; (mask & 1) == 0; mask >>= 1)
            data <<= 1;
        return data;
    }

    public int AfeWriteByte(ushort subAddress, byte data) =>
        WriteI2cData(AfeDeviceAddress, subAddress, 2, data, 1);

    public int AfeReadByte(ushort subAddress, out byte data)
    {
        int status = ReadI2cData(AfeDeviceAddress, subAddress, 2, out uint value, 1);
        data = (byte)value;
        return status;
    }

    public int VideoBlockWriteByte(ushort subAddress, byte data) =>
        WriteI2cData(VideoBlockI2cAddress, subAddress, 2, data, 1);

    public int VideoBlockReadByte(ushort subAddress, out byte data)
    {
        int status = ReadI2cData(VideoBlockI2cAddress, subAddress, 2, out uint value, 1);
        data = (byte)value;
        return status;
    }

    public int VideoBlockWriteWord(ushort subAddress, uint data) =>
        WriteI2cData(VideoBlockI2cAddress, subAddress, 2, data, 4);

    public int VideoBlockReadWord(ushort subAddress, out uint data) =>
        ReadI2cData(VideoBlockI2cAddress, subAddress, 2, out data, 4);

    /// <summary>cx231xx_send_gpio_cmd().</summary>
    public int SendGpioCommand(uint gpioBit, Span<byte> gpioValue, bool isGpio, bool read)
    {
        byte request = isGpio
            ? read ? VrtGetGpio : VrtSetGpio
            : read ? VrtGetGpie : VrtSetGpie;
        ushort value = (ushort)(gpioBit >> 16);
        ushort index = (ushort)(gpioBit & 0xffff);
        if (read) gpioValue.Clear();
        return SendVendorCommand(read, request, value, index, gpioValue);
    }

    private int SetGpioBit(uint gpioBit, uint gpioValue)
    {
        Span<byte> value = stackalloc byte[4];
        BitConverter.TryWriteBytes(value, gpioValue);
        return SendGpioCommand(gpioBit, value, isGpio: true, read: false);
    }

    /// <summary>cx231xx_set_gpio_direction().</summary>
    public int SetGpioDirection(int pin, bool output)
    {
        if (pin >= 32) return -22;
        uint value = output ? _gpioDirection | 1u << pin : _gpioDirection & ~(1u << pin);
        int status = SetGpioBit(value, _gpioValue);
        _gpioDirection = value;
        return status;
    }

    /// <summary>cx231xx_set_gpio_value().</summary>
    public int SetGpioValue(int pin, bool high)
    {
        if (pin >= 32) return -22;
        int status;
        if ((_gpioDirection & 1u << pin) == 0)
        {
            _gpioDirection |= 1u << pin;
            status = SetGpioBit(_gpioDirection, _gpioValue);
        }
        _gpioValue = high ? _gpioValue | 1u << pin : _gpioValue & ~(1u << pin);
        status = SetGpioBit(_gpioDirection, _gpioValue);
        return status;
    }

    public static void Check(int status, string operation)
    {
        if (status < 0)
            throw new IOException($"VGB100: {operation} failed ({LibUsbErrorText(status)}).");
    }

    private static string LibUsbErrorText(int status) => status switch
    {
        -1 => "I/O error",
        -4 => "device disconnected",
        -7 => "timeout",
        -9 => "pipe stall",
        _ => $"error {status}",
    };
}
