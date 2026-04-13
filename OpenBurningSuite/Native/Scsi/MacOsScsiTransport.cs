// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>
/// macOS implementation of IScsiTransport using the IOKit SCSI Architecture Model framework.
/// Sends SCSI commands to optical drives via IOSCSIPeripheralDeviceType05 (MMC devices).
///
/// On macOS, SCSI passthrough requires:
///   1. Finding the IOService matching the BSD disk device (e.g. /dev/disk2)
///   2. Creating an SCSITaskDeviceInterface via IOKit plug-in
///   3. Obtaining exclusive access to the device
///   4. Creating SCSITask objects, configuring CDB/buffers, and executing them
///
/// References:
///   - IOKit SCSI Architecture Model Family (SCSITaskLib.h)
///   - Technical Note TN2166: Secrets of the GPU/IO Kit
///   - MMC device user client: IOSCSIPeripheralDeviceType05UserClient
/// </summary>
public sealed class MacOsScsiTransport : IScsiTransport
{
    private IntPtr _pluginInterface = IntPtr.Zero;
    private IntPtr _mmcDeviceInterface = IntPtr.Zero;
    private IntPtr _scsiTaskDeviceInterface = IntPtr.Zero;
    private bool _hasExclusiveAccess;
    private bool _hasCallbackDispatcher;
    private bool _disposed;
    private string? _bsdName;

    // Disk Arbitration framework handles for exclusive device claiming
    private IntPtr _daSession = IntPtr.Zero;
    private IntPtr _daDisk = IntPtr.Zero;
    private bool _daDiskClaimed;

    // Store the run loop and mode used for DA session scheduling so
    // ReleaseDiskArbitration can unschedule from the correct run loop,
    // even if called from a different thread (e.g. Dispose/finalizer).
    private IntPtr _daRunLoop = IntPtr.Zero;
    private IntPtr _daRunLoopMode = IntPtr.Zero;

    public bool IsOpen => _scsiTaskDeviceInterface != IntPtr.Zero && _hasExclusiveAccess;

    // -----------------------------------------------------------------------
    // IOKit constants
    // -----------------------------------------------------------------------

    // IOKit master port (kIOMasterPortDefault = 0)
    private static readonly IntPtr kIOMasterPortDefault = IntPtr.Zero;

    // kIOReturnSuccess
    private const int kIOReturnSuccess = 0;

    // kIOReturnExclusiveAccess — device is exclusively owned by another process
    private const int kIOReturnExclusiveAccess = unchecked((int)0xe00002c5);

    // kIOReturnUnsupported — the requested function is not supported by this device/driver
    private const int kIOReturnUnsupported = unchecked((int)0xe00002c7);

    // kIOReturnNotPermitted — insufficient permissions for the operation
    private const int kIOReturnNotPermitted = unchecked((int)0xe00002be);

    // kSCSITaskStatus values
    private const int kSCSITaskStatus_GOOD = 0x00;
    private const int kSCSITaskStatus_CHECK_CONDITION = 0x02;

    // kSCSIDataTransferDirection values (per Apple's SCSITask.h)
    // IMPORTANT: These must match the values defined in SCSITask.h exactly:
    //   kSCSIDataTransfer_NoDataTransfer        = 0x00
    //   kSCSIDataTransfer_FromInitiatorToTarget  = 0x01  (Host → Device = Write/Out)
    //   kSCSIDataTransfer_FromTargetToInitiator  = 0x02  (Device → Host = Read/In)
    private const byte kSCSIDataTransferDirection_NoTransfer = 0x00;
    private const byte kSCSIDataTransferDirection_FromInitiatorToTarget = 0x01;
    private const byte kSCSIDataTransferDirection_FromTargetToInitiator = 0x02;

    // SCSITaskSGElement size
    private const int kSCSITaskSGElementSize = 16; // {address (8), length (8)}

    // CFAllocator
    private static readonly IntPtr kCFAllocatorDefault = IntPtr.Zero;

    // kCFNumberSInt32Type
    private const int kCFNumberSInt32Type = 3;

    // kCFStringEncodingUTF8
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    // IORegistry iteration options (IOKitKeys.h)
    private const uint kIORegistryIterateRecursively = 0x00000001;
    private const uint kIORegistryIterateParents = 0x00000002;

    // Per Apple's SCSITaskLib.h, optical drives (MMC devices) require the MMC
    // user client, not the generic SCSI task user client. The generic SCSI task
    // user client (kIOSCSITaskDeviceUserClientTypeID) is only for SCSI devices
    // that do NOT have an in-kernel driver. Optical drives always have the
    // IOSCSIPeripheralDeviceType05 in-kernel driver, so the MMC user client
    // must be used for authoring access.

    // -----------------------------------------------------------------------
    // UUID handling for IOKit/CoreFoundation APIs
    // -----------------------------------------------------------------------
    // IMPORTANT: Apple's IOKit APIs (IOCreatePlugInInterfaceForService) expect
    // CFUUIDRef objects (CoreFoundation UUID references), NOT raw GUID structs.
    // Apple's COM-style QueryInterface expects CFUUIDBytes (16 bytes in
    // big-endian/network byte order), NOT .NET Guid structs.
    //
    // .NET's Guid struct stores Data1/Data2/Data3 in little-endian format
    // (platform native), but CFUUIDBytes uses big-endian for all fields.
    // Passing 'ref Guid' directly produces byte-swapped UUIDs that don't match
    // any registered IOKit plugin, causing IOCreatePlugInInterfaceForService to
    // return kIOReturnUnsupported.
    //
    // Solution: Use CFUUIDGetConstantUUIDWithBytes to create proper CFUUIDRef
    // objects for IOCreatePlugInInterfaceForService, and use CFUUIDBytes structs
    // for COM QueryInterface calls.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Apple's CFUUIDBytes struct: 16 bytes in big-endian (network) byte order.
    /// Used by Apple's COM-style QueryInterface (REFIID = const CFUUIDBytes&amp;).
    /// This is NOT the same byte layout as .NET's Guid struct on little-endian platforms.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct CFUUIDBytes
    {
        public byte byte0, byte1, byte2, byte3;
        public byte byte4, byte5;
        public byte byte6, byte7;
        public byte byte8, byte9, byte10, byte11;
        public byte byte12, byte13, byte14, byte15;
    }

    // kIOSCSITaskDeviceInterfaceID = 1BBC4132-08A5-11D5-90ED-0030657D052A
    // InterfaceID for SCSITaskDeviceInterface (SCSITaskLib.h).
    // Used with COM QueryInterface on the GENERIC path (kIOSCSITaskDeviceUserClientTypeID).
    private static readonly CFUUIDBytes kIOSCSITaskDeviceInterfaceID = new()
    {
        byte0 = 0x1B, byte1 = 0xBC, byte2 = 0x41, byte3 = 0x32,
        byte4 = 0x08, byte5 = 0xA5,
        byte6 = 0x11, byte7 = 0xD5,
        byte8 = 0x90, byte9 = 0xED, byte10 = 0x00, byte11 = 0x30,
        byte12 = 0x65, byte13 = 0x7D, byte14 = 0x05, byte15 = 0x2A
    };

    // kIOMMCDeviceInterfaceID = 1F651106-23CC-11D5-BBDB-003065704866
    // InterfaceID for MMCDeviceInterface (SCSITaskLib.h).
    // Used with COM QueryInterface on the MMC path (kIOMMCDeviceUserClientTypeID).
    // Per Apple's SCSITaskLib.h and cdrtools, the correct flow for MMC devices is:
    //   1. IOCreatePlugInInterfaceForService with kIOMMCDeviceUserClientTypeID
    //   2. QueryInterface for kIOMMCDeviceInterfaceID → MMCDeviceInterface
    //   3. MMCDeviceInterface->GetSCSITaskDeviceInterface() → SCSITaskDeviceInterface
    // Do NOT QueryInterface for kIOSCSITaskDeviceInterfaceID on the MMC path — that
    // is only valid for the generic path (kIOSCSITaskDeviceUserClientTypeID).
    private static readonly CFUUIDBytes kIOMMCDeviceInterfaceID = new()
    {
        byte0 = 0x1F, byte1 = 0x65, byte2 = 0x11, byte3 = 0x06,
        byte4 = 0x23, byte5 = 0xCC,
        byte6 = 0x11, byte7 = 0xD5,
        byte8 = 0xBB, byte9 = 0xDB, byte10 = 0x00, byte11 = 0x30,
        byte12 = 0x65, byte13 = 0x70, byte14 = 0x48, byte15 = 0x66
    };

    // Cached CFUUIDRef objects for IOCreatePlugInInterfaceForService.
    // Created lazily via CFUUIDGetConstantUUIDWithBytes (no release needed).
    private static IntPtr _cfUuidMMCDeviceUserClient;
    private static IntPtr _cfUuidSCSITaskDeviceUserClient;
    private static IntPtr _cfUuidCFPlugInInterface;

    /// <summary>
    /// Gets or creates the CFUUIDRef for kIOMMCDeviceUserClientTypeID.
    /// 97ABCF2C-23CC-11D5-A0E8-003065704866
    /// </summary>
    private static IntPtr GetMMCDeviceUserClientCFUUID()
    {
        if (_cfUuidMMCDeviceUserClient != IntPtr.Zero) return _cfUuidMMCDeviceUserClient;
        _cfUuidMMCDeviceUserClient = CFUUIDGetConstantUUIDWithBytes(kCFAllocatorDefault,
            0x97, 0xAB, 0xCF, 0x2C, 0x23, 0xCC, 0x11, 0xD5,
            0xA0, 0xE8, 0x00, 0x30, 0x65, 0x70, 0x48, 0x66);
        return _cfUuidMMCDeviceUserClient;
    }

    /// <summary>
    /// Gets or creates the CFUUIDRef for kIOSCSITaskDeviceUserClientTypeID.
    /// 7D66678E-08A2-11D5-A1B8-0030657D052A
    /// </summary>
    private static IntPtr GetSCSITaskDeviceUserClientCFUUID()
    {
        if (_cfUuidSCSITaskDeviceUserClient != IntPtr.Zero) return _cfUuidSCSITaskDeviceUserClient;
        _cfUuidSCSITaskDeviceUserClient = CFUUIDGetConstantUUIDWithBytes(kCFAllocatorDefault,
            0x7D, 0x66, 0x67, 0x8E, 0x08, 0xA2, 0x11, 0xD5,
            0xA1, 0xB8, 0x00, 0x30, 0x65, 0x7D, 0x05, 0x2A);
        return _cfUuidSCSITaskDeviceUserClient;
    }

    /// <summary>
    /// Gets or creates the CFUUIDRef for kIOCFPlugInInterfaceID.
    /// C244E858-109C-11D4-91D4-0050E4C6426F
    /// </summary>
    private static IntPtr GetCFPlugInInterfaceCFUUID()
    {
        if (_cfUuidCFPlugInInterface != IntPtr.Zero) return _cfUuidCFPlugInInterface;
        _cfUuidCFPlugInInterface = CFUUIDGetConstantUUIDWithBytes(kCFAllocatorDefault,
            0xC2, 0x44, 0xE8, 0x58, 0x10, 0x9C, 0x11, 0xD4,
            0x91, 0xD4, 0x00, 0x50, 0xE4, 0xC6, 0x42, 0x6F);
        return _cfUuidCFPlugInInterface;
    }

    // -----------------------------------------------------------------------
    // IOKit P/Invoke declarations
    // -----------------------------------------------------------------------

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IOServiceGetMatchingServices(
        IntPtr masterPort, IntPtr matching, out uint existingIterator);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern uint IOIteratorNext(uint iterator);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IOObjectRelease(uint obj);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOServiceMatching(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOBSDNameMatching(
        IntPtr masterPort, uint options,
        [MarshalAs(UnmanagedType.LPStr)] string bsdName);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IORegistryEntryGetParentEntry(
        uint entry, [MarshalAs(UnmanagedType.LPStr)] string plane, out uint parent);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IORegistryEntryGetChildIterator(
        uint entry, [MarshalAs(UnmanagedType.LPStr)] string plane, out uint iterator);

    /// <summary>
    /// Gets the registry path of an IOKit service entry in the specified plane.
    /// The path buffer must be at least 512 bytes (io_string_t = char[512]).
    /// Returns kIOReturnSuccess on success.
    /// </summary>
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IORegistryEntryGetPath(
        uint entry, [MarshalAs(UnmanagedType.LPStr)] string plane,
        [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder path);

    /// <summary>
    /// Returns an IORegistry entry from a path string (e.g., "IOService:/AppleACPIPlatformExpert/...").
    /// The returned entry has +1 retain count — caller must IOObjectRelease.
    /// Returns 0 (MACH_PORT_NULL) if the path does not match any entry.
    /// Per Apple's IOKitLib.h: io_registry_entry_t IORegistryEntryFromPath(
    ///   mach_port_t masterPort, const io_string_t path);
    /// </summary>
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern uint IORegistryEntryFromPath(
        IntPtr masterPort,
        [MarshalAs(UnmanagedType.LPStr)] string path);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IORegistryEntryCreateCFProperty(
        uint entry, IntPtr key, IntPtr allocator, uint options);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern bool IOObjectConformsTo(
        uint obj, [MarshalAs(UnmanagedType.LPStr)] string className);

    /// <summary>
    /// Creates a plug-in interface for a given IOService.
    /// IMPORTANT: pluginType and interfaceType are CFUUIDRef objects (not raw GUIDs).
    /// Use CFUUIDGetConstantUUIDWithBytes to create proper CFUUIDRef values.
    /// Per Apple's IOKitLib.h: kern_return_t IOCreatePlugInInterfaceForService(
    ///   io_service_t service, CFUUIDRef pluginType, CFUUIDRef interfaceType,
    ///   IOCFPlugInInterface ***theInterface, SInt32 *theScore);
    /// </summary>
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IOCreatePlugInInterfaceForService(
        uint service, IntPtr pluginType, IntPtr interfaceType,
        out IntPtr theInterface, out int theScore);

    // CoreFoundation
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr alloc, [MarshalAs(UnmanagedType.LPStr)] string cStr, uint encoding);

    /// <summary>
    /// Returns a cached, immortal CFUUIDRef for the given UUID bytes.
    /// The returned reference must NOT be released (it is owned by CoreFoundation).
    /// Bytes are in big-endian (network) order matching the UUID string representation.
    /// Per Apple's CFUUID.h: CFUUIDRef CFUUIDGetConstantUUIDWithBytes(
    ///   CFAllocatorRef alloc, UInt8 byte0, ..., UInt8 byte15);
    /// </summary>
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFUUIDGetConstantUUIDWithBytes(
        IntPtr alloc,
        byte byte0, byte byte1, byte byte2, byte byte3,
        byte byte4, byte byte5,
        byte byte6, byte byte7,
        byte byte8, byte byte9,
        byte byte10, byte byte11, byte byte12, byte byte13, byte byte14, byte byte15);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRetain(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    // Apple's Boolean type is 'unsigned char' (1 byte), unlike Win32 BOOL (4 bytes).
    // Use MarshalAs(UnmanagedType.U1) to match Apple's ABI exactly.
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, out int value);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern int CFStringGetLength(IntPtr theString);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(
        IntPtr theString, IntPtr buffer, int bufferSize, uint encoding);

    /// <summary>
    /// Searches the IORegistry tree from the given entry for a property with the
    /// specified key. The search direction is controlled by the options parameter:
    ///   kIORegistryIterateRecursively — search children recursively
    ///   kIORegistryIterateParents — search parents
    ///   Both flags combined — search parents recursively
    /// Returns a CF object with +1 retain count (caller must CFRelease), or IntPtr.Zero.
    /// </summary>
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IORegistryEntrySearchCFProperty(
        uint entry,
        [MarshalAs(UnmanagedType.LPStr)] string plane,
        IntPtr key,
        IntPtr allocator,
        uint options);

    // -----------------------------------------------------------------------
    // Disk Arbitration framework P/Invoke declarations
    // -----------------------------------------------------------------------
    // The Disk Arbitration (DA) framework provides a mechanism to manage disk
    // access on macOS. It allows applications to unmount volumes, claim disks
    // exclusively, and prevent the OS from auto-mounting media.
    //
    // References:
    //   - DiskArbitration/DASession.h
    //   - DiskArbitration/DADisk.h
    //   - Technical Note TN2166
    // -----------------------------------------------------------------------

    [DllImport("/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration")]
    private static extern IntPtr DASessionCreate(IntPtr allocator);

    [DllImport("/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration")]
    private static extern void DASessionScheduleWithRunLoop(
        IntPtr session, IntPtr runLoop, IntPtr runLoopMode);

    [DllImport("/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration")]
    private static extern void DASessionUnscheduleFromRunLoop(
        IntPtr session, IntPtr runLoop, IntPtr runLoopMode);

    [DllImport("/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration")]
    private static extern IntPtr DADiskCreateFromBSDName(
        IntPtr allocator, IntPtr session,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    /// <summary>
    /// Unmounts the specified disk. The callback is invoked when the operation completes.
    /// Per DiskArbitration/DADisk.h:
    ///   options: kDADiskUnmountOptionDefault (0) or kDADiskUnmountOptionForce (0x00080000)
    ///   callback: DADiskUnmountCallback (nullable) — receives (DADiskRef, DADissenterRef, context)
    ///   context: user-supplied context pointer for the callback
    /// </summary>
    [DllImport("/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration")]
    private static extern void DADiskUnmount(
        IntPtr disk, uint options, IntPtr callback, IntPtr context);

    /// <summary>
    /// Claims a disk for exclusive access. While claimed, macOS will not auto-mount
    /// volumes on this disk and other clients' mount/unmount requests will be denied.
    /// Per DiskArbitration/DADisk.h:
    ///   disk: the DADiskRef to claim
    ///   options: reserved (pass 0)
    ///   claim: DADiskClaimCallback for arbitrating other claims (nullable)
    ///   claimContext: context pointer for the claim callback
    ///   callback: DADiskClaimReleaseCallback invoked when claim is released (nullable)
    ///   callbackContext: context pointer for the release callback
    /// </summary>
    [DllImport("/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration")]
    private static extern void DADiskClaim(
        IntPtr disk, uint options, IntPtr claim, IntPtr claimContext,
        IntPtr callback, IntPtr callbackContext);

    /// <summary>
    /// Releases a previously claimed disk, allowing macOS to resume normal management.
    /// </summary>
    [DllImport("/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration")]
    private static extern void DADiskUnclaim(IntPtr disk);

    // Disk Arbitration unmount options
    private const uint kDADiskUnmountOptionDefault = 0x00000000;
    private const uint kDADiskUnmountOptionForce = 0x00080000;
    private const uint kDADiskUnmountOptionWhole = 0x00000001;

    // CoreFoundation RunLoop references needed for DA scheduling
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern int CFRunLoopRunInMode(
        IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopCopyCurrentMode(IntPtr runLoop);

    // kCFRunLoopDefaultMode is a CFStringRef constant — use dlsym to get the actual
    // global symbol from CoreFoundation for maximum compatibility. CFRunLoopRunInMode
    // and DASessionScheduleWithRunLoop may use pointer comparison on some macOS versions,
    // so using the exact global constant is more reliable than creating a new CFString
    // with the same content.
    [DllImport("libSystem.dylib")]
    private static extern IntPtr dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);

    // RTLD_DEFAULT (-2): search all loaded libraries for the symbol
    private static readonly IntPtr RTLD_DEFAULT = new(-2);

    private static IntPtr? _cachedRunLoopMode;
    private static IntPtr GetDefaultRunLoopMode()
    {
        if (_cachedRunLoopMode.HasValue)
            return _cachedRunLoopMode.Value;

        // Try to get the actual kCFRunLoopDefaultMode global constant via dlsym.
        // This returns a pointer to the global variable (which is itself a CFStringRef).
        try
        {
            var symbolPtr = dlsym(RTLD_DEFAULT, "kCFRunLoopDefaultMode");
            if (symbolPtr != IntPtr.Zero)
            {
                var globalValue = Marshal.ReadIntPtr(symbolPtr);
                if (globalValue != IntPtr.Zero)
                {
                    _cachedRunLoopMode = globalValue;
                    return globalValue;
                }
            }
        }
        catch
        {
            // dlsym not available — fall through to CFString creation
        }

        // Fallback: create a CFString with the known constant value.
        // The string content of kCFRunLoopDefaultMode is "kCFRunLoopDefaultMode".
        _cachedRunLoopMode = CFStringCreateWithCString(kCFAllocatorDefault, "kCFRunLoopDefaultMode", kCFStringEncodingUTF8);
        return _cachedRunLoopMode.Value;
    }

    // -----------------------------------------------------------------------
    // MMCDeviceInterface vtable offsets (CFPlugIn COM-style interface)
    // -----------------------------------------------------------------------
    // The MMCDeviceInterface struct (from Apple's SCSITaskLib.h) provides
    // access to MMC (optical drive) devices. Its vtable follows IUNKNOWN_C_GUTS:
    //
    // Slot 0:  _reserved (void *)
    // Slot 1:  QueryInterface
    // Slot 2:  AddRef
    // Slot 3:  Release
    // Slot 4:  version (UInt16) + revision (UInt16) + padding
    // Slot 5:  Inquiry
    // Slot 6:  TestUnitReady
    // Slot 7:  GetPerformance
    // Slot 8:  GetConfiguration
    // Slot 9:  ModeSense10
    // Slot 10: SetWriteParametersModePage
    // Slot 11: GetTrayState
    // Slot 12: SetTrayState
    // Slot 13: ReadTableOfContents
    // Slot 14: ReadDiscInformation
    // Slot 15: ReadTrackInformation
    // Slot 16: ReadDVDStructure
    // Slot 17: GetSCSITaskDeviceInterface — returns SCSITaskDeviceInterface **
    // Slot 18: GetPerformanceV2 (added in macOS 10.2)
    // Slot 19: SetCDSpeed (added in macOS 10.3)
    // Slot 20: ReadFormatCapacities (added in macOS 10.3)

    private const int VtableOffset_MMC_GetSCSITaskDeviceInterface = 17;

    // -----------------------------------------------------------------------
    // SCSITaskDeviceInterface vtable offsets (CFPlugIn COM-style interface)
    // -----------------------------------------------------------------------

    // The SCSITaskDeviceInterface struct (from Apple's SCSITaskLib.h) has inline
    // function pointers following the IUNKNOWN_C_GUTS macro pattern. On 64-bit
    // macOS, each slot is 8 bytes (pointer-sized). The struct layout is:
    //
    // Slot 0:  _reserved (void *)              — internal CFPlugIn field
    // Slot 1:  QueryInterface                  — IUnknown method
    // Slot 2:  AddRef                          — IUnknown method
    // Slot 3:  Release                         — IUnknown method
    // Slot 4:  version (UInt16) + revision (UInt16) + padding — data, not a function
    // Slot 5:  IsExclusiveAccessAvailable
    // Slot 6:  AddCallbackDispatcherToRunLoop
    // Slot 7:  RemoveCallbackDispatcherFromRunLoop
    // Slot 8:  ObtainExclusiveAccess
    // Slot 9:  ReleaseExclusiveAccess
    // Slot 10: CreateSCSITask

    private const int VtableOffset_AddCallbackDispatcher = 6;
    private const int VtableOffset_RemoveCallbackDispatcher = 7;
    private const int VtableOffset_ObtainExclusiveAccess = 8;
    private const int VtableOffset_ReleaseExclusiveAccess = 9;
    private const int VtableOffset_CreateSCSITask = 10;

    // SCSITaskInterface struct layout (from Apple's SCSITaskLib.h):
    //
    // Slot 0:  _reserved (void *)              — internal CFPlugIn field
    // Slot 1:  QueryInterface
    // Slot 2:  AddRef
    // Slot 3:  Release
    // Slot 4:  version (UInt16) + revision (UInt16) + padding — data, not a function
    // Slot 5:  IsTaskActive
    // Slot 6:  SetTaskAttribute
    // Slot 7:  GetTaskAttribute
    // Slot 8:  SetCommandDescriptorBlock
    // Slot 9:  GetCommandDescriptorBlockSize
    // Slot 10: GetCommandDescriptorBlock
    // Slot 11: SetScatterGatherEntries
    // Slot 12: SetTimeoutDuration
    // Slot 13: GetTimeoutDuration
    // Slot 14: SetTaskCompletionCallback
    // Slot 15: ExecuteTaskAsync
    // Slot 16: ExecuteTaskSync
    // Slot 17: AbortTask
    // Slot 18: GetSCSIServiceResponse
    // Slot 19: GetTaskState
    // Slot 20: GetTaskStatus
    // Slot 21: GetRealizedDataTransferCount
    // Slot 22: GetAutoSenseData
    // Slot 23: SetAutoSenseDataBuffer

    private const int SCSITask_Release = 3;
    private const int SCSITask_SetTaskAttribute = 6;
    private const int SCSITask_SetCommandDescriptorBlock = 8;
    private const int SCSITask_SetScatterGatherEntries = 11;
    private const int SCSITask_SetTimeoutDuration = 12;
    private const int SCSITask_ExecuteTaskSync = 16;
    private const int SCSITask_GetSCSIServiceResponse = 18;
    private const int SCSITask_GetTaskStatus = 20;
    private const int SCSITask_GetRealizedDataTransferCount = 21;
    private const int SCSITask_GetAutoSenseData = 22;

    // kSCSITaskAttribute_Simple
    private const int kSCSITaskAttribute_Simple = 0;

    // kSCSIServiceResponse values (per Apple's SCSITask.h)
    // kSCSIServiceResponse_Request_In_Process                  = 0
    // kSCSIServiceResponse_SERVICE_DELIVERY_OR_TARGET_FAILURE  = 1
    // kSCSIServiceResponse_TASK_COMPLETE                       = 2
    // kSCSIServiceResponse_LINK_COMMAND_COMPLETE               = 3
    private const uint kSCSIServiceResponse_TASK_COMPLETE = 2;

    // -----------------------------------------------------------------------
    // Helper: call a COM-style vtable function
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads a function pointer from a COM-style vtable at the given slot index.
    /// The interface pointer points to a pointer to the vtable.
    /// Layout: interfacePtr → *vtablePtr → [fn0, fn1, fn2, ...]
    /// </summary>
    private static IntPtr GetVtableSlot(IntPtr interfacePtr, int slotIndex)
    {
        if (interfacePtr == IntPtr.Zero)
            throw new InvalidOperationException("Cannot read vtable slot: interface pointer is null.");
        if (slotIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), "Vtable slot index must be non-negative.");

        // Guard against integer overflow in offset calculation (slotIndex * IntPtr.Size)
        const int maxReasonableSlot = 64; // IOKit COM vtables never exceed ~30 slots
        if (slotIndex > maxReasonableSlot)
            throw new ArgumentOutOfRangeException(nameof(slotIndex),
                $"Vtable slot index {slotIndex} exceeds maximum expected value ({maxReasonableSlot}).");

        // interfacePtr → vtable pointer
        var vtable = Marshal.ReadIntPtr(interfacePtr);
        if (vtable == IntPtr.Zero)
            throw new InvalidOperationException("Cannot read vtable slot: vtable pointer is null.");

        // vtable[slotIndex]
        return Marshal.ReadIntPtr(vtable, slotIndex * IntPtr.Size);
    }

    // -----------------------------------------------------------------------
    // IOKit COM vtable function pointer types
    // -----------------------------------------------------------------------
    // All IOKit COM vtable calls now use unsafe C# 9 function pointers
    // (delegate* unmanaged[Cdecl]<...>) instead of managed delegate types.
    // This eliminates Marshal.GetDelegateForFunctionPointer thunk generation
    // that can cause SIGBUS on Apple Silicon due to W^X page protection
    // issues with JIT-generated reverse P/Invoke thunks.
    //
    // CRITICAL ABI NOTE: Apple's REFIID is 'typedef CFUUIDBytes REFIID'
    // (see CFPlugInCOM.h). This is a 16-byte struct passed BY VALUE on the
    // stack/in registers, NOT by pointer. On ARM64:
    //   QueryInterface(void *self, REFIID iid, void **ppv) uses:
    //     x0=self, (x1,x2)=iid (16 bytes), x3=ppv
    // Passing REFIID by pointer (ref/&) corrupts the register layout,
    // causing the native function to write to a garbage address → SIGBUS.
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Open / Execute / Dispose
    // -----------------------------------------------------------------------

    public void Open(string devicePath)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MacOsScsiTransport));

        // Close any previous connection
        ReleaseResources();

        // Determine if the path is an IORegistry path (used for drives without media)
        // or a BSD device path (used for drives with media).
        // IORegistry paths start with "IOService:" and look like:
        //   IOService:/AppleACPIPlatformExpert/.../IOSCSIPeripheralDeviceType05
        // BSD paths look like: /dev/disk2, /dev/rdisk2
        bool isIoRegistryPath = devicePath.StartsWith("IOService:", StringComparison.Ordinal);

        uint service;

        if (isIoRegistryPath)
        {
            // IORegistry path provided — drive may or may not have media.
            // Use IORegistryEntryFromPath to directly get the IOService from the
            // IORegistry path. This allows SCSI passthrough to the drive even
            // without media, enabling INQUIRY and GET CONFIGURATION.
            service = IORegistryEntryFromPath(kIOMasterPortDefault, devicePath);
            if (service == 0)
            {
                throw new InvalidOperationException(
                    $"Could not find IORegistry entry for path '{devicePath}'. " +
                    "The optical drive may have been disconnected.");
            }

            // Verify this is an optical drive (IOSCSIPeripheralDeviceType05)
            if (!IOObjectConformsTo(service, "IOSCSIPeripheralDeviceType05") &&
                !IOObjectConformsTo(service, "IOSCSIPeripheralDeviceNub"))
            {
                // Walk up to find the Type05 service — the IORegistry path may point
                // to a child service (e.g., IOBlockStorageServices)
                var type05Service = FindSCSITaskParent(service);
                if (type05Service != 0 && type05Service != service)
                {
                    IOObjectRelease(service);
                    service = type05Service;
                }
                else if (type05Service == 0)
                {
                    IOObjectRelease(service);
                    throw new InvalidOperationException(
                        $"IORegistry path '{devicePath}' does not correspond to an optical drive.");
                }
                // If type05Service == service, it's already the right one
            }

            // Try to resolve a BSD name from the IORegistry service.
            // If the user inserted media after discovery (which stored the IOService: path),
            // the drive now has an IOMedia child with a "BSD Name" property. Resolving it
            // enables Disk Arbitration operations (unmount/claim) for blank/format/eject.
            var resolvedBsd = ReadRegistryStringProperty(
                service, "BSD Name", kIORegistryIterateRecursively);
            _bsdName = !string.IsNullOrEmpty(resolvedBsd) ? resolvedBsd : null;
        }
        else
        {
            // Convert device path to BSD name
            // e.g. "/dev/disk2" → "disk2", "/dev/rdisk2" → "rdisk2"
            var bsdName = devicePath.TrimEnd('/');
            if (bsdName.StartsWith("/dev/"))
                bsdName = bsdName[5..];
            // Convert rdisk to disk for IOKit matching (rdisk is the raw, unbuffered device)
            if (bsdName.StartsWith("rdisk"))
                bsdName = bsdName[1..]; // "rdisk2" → "disk2"

            _bsdName = bsdName;

            // IMPORTANT: Check if this device is an optical drive via IOKit registry lookup
            // BEFORE performing any Disk Arbitration (DA) operations. The IOKit registry
            // lookup is instant, while DA operations (unmount + claim + runloop pumping)
            // take 2.5+ seconds per device. During discovery fallback scans of /dev/diskN,
            // this avoids wasting seconds on every non-optical disk (system disk, APFS
            // volumes, etc.), reducing total scan time from minutes to milliseconds.
            service = FindSCSITaskService(bsdName);
            if (service == 0)
            {
                throw new InvalidOperationException(
                    $"Could not find SCSI task service for device '{devicePath}'. " +
                    "Ensure the device is an optical drive and is connected.");
            }
        }

        try
        {
            // Unmount and claim the disk via Disk Arbitration if we have a BSD name.
            // On macOS, the disc may be auto-mounted. ObtainExclusiveAccess will fail
            // unless the volume is first unmounted. Use the Disk Arbitration framework
            // to unmount and claim the disk exclusively before attempting IOKit access.
            // Skip DA operations for IORegistry paths — no media means nothing to unmount.
            if (!string.IsNullOrEmpty(_bsdName))
            {
                TryClaimDiskExclusive(_bsdName);
                // Fallback: also try diskutil unmount in case DA claim wasn't fully effective
                TryUnmountDisc(_bsdName);
            }

            // Create plug-in interface for the service.
            // Per Apple's SCSITaskLib.h, optical drives (MMC devices) require the
            // kIOMMCDeviceUserClientTypeID. The generic kIOSCSITaskDeviceUserClientTypeID
            // is only for SCSI devices without an in-kernel driver; optical drives always
            // have the IOSCSIPeripheralDeviceType05 in-kernel driver, so the MMC user
            // client must be used. Fall back to the generic SCSI task user client if
            // the MMC user client fails (e.g., for non-optical SCSI devices).
            CreatePluginAndObtainAccess(service, devicePath);
        }
        finally
        {
            IOObjectRelease(service);
        }
    }

    /// <summary>
    /// Creates the IOKit plugin interface and obtains exclusive SCSI access.
    /// If exclusive access fails (device busy/mounted), retries after a brief
    /// unmount attempt. Optical drives on macOS often need a retry because:
    ///   1. The DA unmount may not have fully completed (asynchronous operation)
    ///   2. The Finder or another process may have re-mounted the disc
    ///   3. The drive may have a UNIT ATTENTION condition pending from media insertion
    ///
    /// Per Apple SCSITaskLib.h, cdrtools (scsi-mac-iokit.c), and dvdisaster:
    ///   MMC path (optical drives):
    ///     1. IOCreatePlugInInterfaceForService with kIOMMCDeviceUserClientTypeID
    ///     2. QueryInterface for kIOMMCDeviceInterfaceID → MMCDeviceInterface
    ///     3. MMCDeviceInterface->GetSCSITaskDeviceInterface → SCSITaskDeviceInterface
    ///     4. ObtainExclusiveAccess on SCSITaskDeviceInterface
    ///   Generic path (non-MMC SCSI devices):
    ///     1. IOCreatePlugInInterfaceForService with kIOSCSITaskDeviceUserClientTypeID
    ///     2. QueryInterface for kIOSCSITaskDeviceInterfaceID → SCSITaskDeviceInterface
    ///     3. ObtainExclusiveAccess on SCSITaskDeviceInterface
    ///
    /// The service target can be either the IOSCSIPeripheralDeviceType05 driver or
    /// a block storage service child (IOCompactDiscServices, IODVDServices, IOBDServices).
    /// Per cdrtools, the block storage service is the canonical target. IOKit routes
    /// the user client request up to the Type05 driver in both cases.
    /// </summary>
    private unsafe void CreatePluginAndObtainAccess(uint service, string devicePath)
    {
        // Create CFUUIDRef objects for the IOKit plugin API.
        var interfaceType = GetCFPlugInInterfaceCFUUID();

        // ---------------------------------------------------------------
        // Strategy: try multiple service + user-client combinations.
        //
        // 1. First, try block storage services (IODVDServices, IOCompactDiscServices,
        //    IOBDServices) as the target — this is what cdrtools and dvdisaster do.
        //    Block storage services only exist when media is present.
        // 2. If no block storage service found, or if it fails, try the Type05
        //    service directly (works regardless of media state).
        // 3. For each service target, try kIOMMCDeviceUserClientTypeID first,
        //    then fall back to kIOSCSITaskDeviceUserClientTypeID.
        // ---------------------------------------------------------------

        // Try finding a block storage service child of the Type05 service.
        // Per cdrtools, IOCompactDiscServices/IODVDServices/IOBDServices are
        // the canonical services for IOCreatePlugInInterfaceForService with
        // kIOMMCDeviceUserClientTypeID on macOS optical drives.
        uint blockStorageService = FindBlockStorageServiceChild(service);

        // Build list of services to try (block storage first, then Type05)
        var servicesToTry = new System.Collections.Generic.List<(uint svc, string name)>();
        if (blockStorageService != 0)
            servicesToTry.Add((blockStorageService, "BlockStorageService"));
        servicesToTry.Add((service, "IOSCSIPeripheralDeviceType05"));

        int kr = 0;
        bool pluginCreated = false;
        string lastError = "";

        foreach (var (svc, svcName) in servicesToTry)
        {
            if (pluginCreated) break;

            // --- Attempt 1: MMC user client ---
            var pluginType = GetMMCDeviceUserClientCFUUID();
            kr = IOCreatePlugInInterfaceForService(
                svc, pluginType, interfaceType,
                out _pluginInterface, out _);

            if (kr == kIOReturnSuccess && _pluginInterface != IntPtr.Zero)
            {
                // MMC path: QueryInterface for kIOMMCDeviceInterfaceID, then
                // call GetSCSITaskDeviceInterface to get the SCSITaskDeviceInterface.
                // This is the correct flow per Apple's SCSITaskLib.h and cdrtools.
                //
                // CRITICAL: Apple's REFIID is 'typedef CFUUIDBytes REFIID' — the 16-byte
                // UUID struct is passed BY VALUE (in registers x1+x2 on ARM64), NOT by
                // pointer. Passing by pointer corrupts the register layout: the native
                // function reads the pointer value as UUID bytes and uses the ppv
                // pointer as the second half of the UUID, then writes the output to
                // whatever garbage is in x3 → SIGBUS.
                var mmcIid = kIOMMCDeviceInterfaceID;
                var queryFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, CFUUIDBytes, IntPtr*, int>)
                    GetVtableSlot(_pluginInterface, 1);
                IntPtr mmcOut = IntPtr.Zero;
                var hresult = queryFnPtr(_pluginInterface, mmcIid, &mmcOut);
                _mmcDeviceInterface = mmcOut;

                if (hresult == 0 && _mmcDeviceInterface != IntPtr.Zero)
                {
                    // Get SCSITaskDeviceInterface from MMCDeviceInterface (vtable slot 17)
                    var getTaskFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)
                        GetVtableSlot(_mmcDeviceInterface, VtableOffset_MMC_GetSCSITaskDeviceInterface);
                    _scsiTaskDeviceInterface = getTaskFnPtr(_mmcDeviceInterface);

                    if (_scsiTaskDeviceInterface != IntPtr.Zero)
                    {
                        pluginCreated = true;
                        System.Diagnostics.Debug.WriteLine(
                            $"[MacOsScsiTransport] MMC user client created via {svcName}");
                        break;
                    }
                    else
                    {
                        lastError = $"GetSCSITaskDeviceInterface returned null from MMCDeviceInterface (service={svcName})";
                        System.Diagnostics.Debug.WriteLine(
                            $"[MacOsScsiTransport] {lastError}");
                        // Release MMCDeviceInterface on failure
                        try
                        {
                            var relFnPtr2 = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                                GetVtableSlot(_mmcDeviceInterface, 3);
                            relFnPtr2(_mmcDeviceInterface);
                        }
                        catch { }
                        _mmcDeviceInterface = IntPtr.Zero;
                    }
                }
                else
                {
                    lastError = $"QueryInterface for kIOMMCDeviceInterfaceID failed: 0x{hresult:X8} (service={svcName})";
                    System.Diagnostics.Debug.WriteLine(
                        $"[MacOsScsiTransport] {lastError}");
                }

                // Release plugin interface on failure — we'll try again
                try
                {
                    var relFnPtr3 = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                        GetVtableSlot(_pluginInterface, 3);
                    relFnPtr3(_pluginInterface);
                }
                catch { }
                _pluginInterface = IntPtr.Zero;
            }
            else
            {
                lastError = $"IOCreatePlugInInterfaceForService with kIOMMCDeviceUserClientTypeID failed: 0x{kr:X8} (service={svcName})";
                System.Diagnostics.Debug.WriteLine(
                    $"[MacOsScsiTransport] {lastError}");
                _pluginInterface = IntPtr.Zero;
            }

            // --- Attempt 2: Generic SCSI task user client ---
            pluginType = GetSCSITaskDeviceUserClientCFUUID();
            kr = IOCreatePlugInInterfaceForService(
                svc, pluginType, interfaceType,
                out _pluginInterface, out _);

            if (kr == kIOReturnSuccess && _pluginInterface != IntPtr.Zero)
            {
                // Generic path: QueryInterface for kIOSCSITaskDeviceInterfaceID directly.
                // CRITICAL: REFIID is passed BY VALUE (see comment above for Attempt 1).
                var scsiIid = kIOSCSITaskDeviceInterfaceID;
                var queryFnPtr2 = (delegate* unmanaged[Cdecl]<IntPtr, CFUUIDBytes, IntPtr*, int>)
                    GetVtableSlot(_pluginInterface, 1);
                IntPtr scsiOut = IntPtr.Zero;
                var hresult = queryFnPtr2(_pluginInterface, scsiIid, &scsiOut);
                _scsiTaskDeviceInterface = scsiOut;

                if (hresult == 0 && _scsiTaskDeviceInterface != IntPtr.Zero)
                {
                    pluginCreated = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[MacOsScsiTransport] Generic SCSI user client created via {svcName}");
                    break;
                }
                else
                {
                    lastError = $"QueryInterface for kIOSCSITaskDeviceInterfaceID failed: 0x{hresult:X8} (service={svcName})";
                    System.Diagnostics.Debug.WriteLine(
                        $"[MacOsScsiTransport] {lastError}");
                }

                // Release plugin interface on failure
                try
                {
                    var relFnPtr4 = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                        GetVtableSlot(_pluginInterface, 3);
                    relFnPtr4(_pluginInterface);
                }
                catch { }
                _pluginInterface = IntPtr.Zero;
            }
            else
            {
                lastError = $"IOCreatePlugInInterfaceForService with kIOSCSITaskDeviceUserClientTypeID failed: 0x{kr:X8} (service={svcName})";
                System.Diagnostics.Debug.WriteLine(
                    $"[MacOsScsiTransport] {lastError}");
                _pluginInterface = IntPtr.Zero;
            }
        }

        // Release the block storage service reference (if we obtained one)
        if (blockStorageService != 0 && blockStorageService != service)
        {
            IOObjectRelease(blockStorageService);
        }

        if (!pluginCreated)
        {
            // Use the kIOReturnUnsupported constant value in hex string form for log message matching
            var isUnsupported = kr == kIOReturnUnsupported ||
                lastError.Contains($"0x{unchecked((uint)kIOReturnUnsupported):X8}");
            var errorDetail = isUnsupported
                ? "The IOKit MMC/SCSI user client is not available for this device. " +
                  "Ensure the optical drive is properly connected and the " +
                  "IOSCSIArchitectureModelFamily kext is loaded. " +
                  "Run 'kextstat | grep SCSI' to verify. " +
                  "The application requires Full Disk Access: go to " +
                  "System Preferences > Security & Privacy > Privacy > Full Disk Access " +
                  "and add this application. Run with sudo for elevated privileges."
                : kr == kIOReturnNotPermitted
                    ? "Insufficient permissions to access the optical drive. " +
                      "Run the application with elevated privileges (sudo) and ensure " +
                      "Full Disk Access is granted in System Preferences > " +
                      "Security & Privacy > Privacy > Full Disk Access."
                    : $"IOKit error 0x{kr:X8}. {lastError}. " +
                      "The optical drive's kernel driver may not be loaded, or the " +
                      "device may not support the requested user client. " +
                      "Ensure Full Disk Access is granted and try running with sudo.";
            throw new InvalidOperationException(
                $"IOCreatePlugInInterfaceForService failed for '{devicePath}': {errorDetail}");
        }

        // ---------------------------------------------------------------
        // Add callback dispatcher to run loop.
        // Per Apple's SCSITaskDeviceInterface documentation, the callback
        // dispatcher should be registered on a CFRunLoop for task completion
        // notifications. While ExecuteTaskSync does not strictly require it,
        // registering it ensures correct behavior on all macOS versions and
        // allows the system to deliver device notifications properly.
        // ---------------------------------------------------------------
        try
        {
            var runLoop = CFRunLoopGetCurrent();
            var addDispatcherFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)
                GetVtableSlot(_scsiTaskDeviceInterface, VtableOffset_AddCallbackDispatcher);
            kr = addDispatcherFnPtr(_scsiTaskDeviceInterface, runLoop);
            if (kr == kIOReturnSuccess)
            {
                _hasCallbackDispatcher = true;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MacOsScsiTransport] AddCallbackDispatcherToRunLoop failed: 0x{kr:X8} (non-fatal)");
            }
        }
        catch
        {
            // Non-fatal — synchronous operations may still work without it
        }

        // Obtain exclusive access with retry.
        // The first attempt may fail if the DA unmount hasn't fully completed
        // or if another process briefly held the device. A retry after a short
        // delay and another unmount attempt usually succeeds.
        var obtainFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            GetVtableSlot(_scsiTaskDeviceInterface, VtableOffset_ObtainExclusiveAccess);

        kr = obtainFnPtr(_scsiTaskDeviceInterface);
        if (kr != kIOReturnSuccess && _bsdName != null)
        {
            // Retry: the unmount may not have completed. Try again with diskutil.
            System.Threading.Thread.Sleep(500);
            TryUnmountDisc(_bsdName);
            System.Threading.Thread.Sleep(500);
            kr = obtainFnPtr(_scsiTaskDeviceInterface);
        }

        if (kr != kIOReturnSuccess && _bsdName != null)
        {
            // Second retry: try a more aggressive unmount + DA reclaim cycle
            System.Threading.Thread.Sleep(1000);
            TryClaimDiskExclusive(_bsdName);
            TryUnmountDisc(_bsdName);
            System.Threading.Thread.Sleep(500);
            kr = obtainFnPtr(_scsiTaskDeviceInterface);
        }

        // For IORegistry path connections (no BSD name — drives without media),
        // there's nothing to unmount, but another process may temporarily hold
        // the device. Retry with brief delays to handle transient exclusive access.
        if (kr != kIOReturnSuccess && _bsdName == null)
        {
            System.Threading.Thread.Sleep(500);
            kr = obtainFnPtr(_scsiTaskDeviceInterface);
        }
        if (kr != kIOReturnSuccess && _bsdName == null)
        {
            System.Threading.Thread.Sleep(1000);
            kr = obtainFnPtr(_scsiTaskDeviceInterface);
        }

        if (kr != kIOReturnSuccess)
        {
            throw new InvalidOperationException(
                kr == kIOReturnExclusiveAccess
                    ? $"Device '{devicePath}' is in use by another process. " +
                      "Automatic unmount was attempted but the device is still busy. " +
                      "Close any applications using the disc (including Finder) and try again. " +
                      "If the problem persists, run 'diskutil unmountDisk /dev/diskN' manually."
                    : $"ObtainExclusiveAccess failed for '{devicePath}': 0x{kr:X8}. " +
                      "The device may be in use by another process or the system " +
                      "may not permit exclusive access to this optical drive. " +
                      "Ensure Full Disk Access is granted in System Preferences > " +
                      "Security & Privacy > Privacy > Full Disk Access.");
        }

        _hasExclusiveAccess = true;
    }

    /// <summary>
    /// Finds a block storage service child of an IOSCSIPeripheralDeviceType05 service.
    /// Per cdrtools (scsi-mac-iokit.c), the canonical service target for
    /// IOCreatePlugInInterfaceForService with kIOMMCDeviceUserClientTypeID is one of:
    ///   - IOCompactDiscServices (CD drives)
    ///   - IODVDServices (DVD drives)
    ///   - IOBDServices (Blu-ray drives)
    /// These services only exist when media is present in the drive.
    /// Returns the retained IOService or 0 if not found.
    /// Recursion is limited to maxDepth levels (IOKit optical drive trees are
    /// typically only 2-3 levels deep: Type05 → BlockStorageService → Driver → Media).
    /// </summary>
    private static uint FindBlockStorageServiceChild(uint parentService, int maxDepth = 3)
    {
        if (maxDepth <= 0) return 0;

        var kr = IORegistryEntryGetChildIterator(parentService, "IOService", out var iterator);
        if (kr != kIOReturnSuccess) return 0;

        try
        {
            uint child;
            while ((child = IOIteratorNext(iterator)) != 0)
            {
                // Check if this child is a block storage service
                if (IOObjectConformsTo(child, "IOCompactDiscServices") ||
                    IOObjectConformsTo(child, "IODVDServices") ||
                    IOObjectConformsTo(child, "IOBDServices"))
                {
                    return child; // Return retained — caller must release
                }

                // Check grandchildren (the block storage service may be one level deeper)
                var grandchild = FindBlockStorageServiceChild(child, maxDepth - 1);
                IOObjectRelease(child);
                if (grandchild != 0) return grandchild;
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        return 0;
    }

    /// <summary>
    /// Prepares the device for destructive operations (FORMAT UNIT, BLANK, burn).
    /// On macOS, attempts to unmount any mounted volumes on the disk device to
    /// prevent data corruption and stale mount state after the operation.
    /// The DA framework claim from Open() prevents auto-remounting, but if new
    /// media was inserted or the OS re-mounted between Open and write, this
    /// ensures the volume is unmounted before the SCSI write begins.
    /// Always returns true because the unmount is best-effort: SCSI commands
    /// through the IOKit exclusive-access interface bypass mounted filesystems.
    /// </summary>
    public bool PrepareForWrite()
    {
        if (string.IsNullOrEmpty(_bsdName))
            return true;

        try
        {
            // Re-claim via Disk Arbitration framework to ensure the disc is
            // fully unclaimed and unmounted before destructive operations.
            // Between Open() and the write operation, macOS may have re-mounted
            // the volume (e.g., after a media change or auto-mount daemon activity).
            // Re-claiming prevents auto-mount from racing with BLANK/FORMAT UNIT.
            TryClaimDiskExclusive(_bsdName);
        }
        catch
        {
            // Best effort — DA may not be available
        }

        try
        {
            TryUnmountDisc(_bsdName);
        }
        catch
        {
            // Best effort — SCSI commands via IOKit exclusive access will still work.
        }

        // Allow the IOKit driver and kernel to settle after DA unmount + claim cycle.
        // Without this delay, BLANK/FORMAT UNIT commands issued immediately after
        // unmount can fail with NOT READY (SK 0x02) or transport errors (status 0xFF)
        // because the IOKit SCSITask pipeline hasn't fully stabilized after the
        // filesystem teardown. 500ms is sufficient per cdrtools behavior analysis.
        System.Threading.Thread.Sleep(500);

        return true;
    }

    /// <summary>
    /// Attempts to unmount the disc before gaining exclusive access.
    /// On macOS, auto-mounted discs must be unmounted before IOKit exclusive access.
    /// Uses diskutil unmountDisk which unmounts all volumes on the disk device.
    /// </summary>
    private static void TryUnmountDisc(string bsdName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("diskutil")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("unmountDisk");
            psi.ArgumentList.Add($"/dev/{bsdName}");

            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            // Drain stdout/stderr to prevent deadlock
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { /* best effort */ }
            }
        }
        catch { /* best effort — diskutil may not be available or disc isn't mounted */ }
    }

    /// <summary>
    /// Uses the Disk Arbitration framework to unmount and exclusively claim a disk device.
    /// This prevents macOS from automatically mounting volumes on the disk and blocks
    /// other system components from accessing the device during burn operations.
    ///
    /// The Disk Arbitration framework (DiskArbitration.framework) is the macOS system
    /// service for managing disk device access. It provides:
    ///   - DADiskUnmount: Unmounts all volumes on a disk, making the raw device available
    ///   - DADiskClaim: Claims exclusive access, preventing auto-mount and other clients
    ///
    /// Per Apple's DiskArbitration documentation:
    ///   - DASessionCreate creates a session with the DA daemon
    ///   - The session must be scheduled on a run loop for callbacks to fire
    ///   - DADiskCreateFromBSDName creates a DADiskRef from the BSD device name
    ///   - DADiskUnmount with kDADiskUnmountOptionWhole | kDADiskUnmountOptionForce
    ///     unmounts all volumes including force-unmounting busy ones
    ///   - DADiskClaim marks the disk as exclusively owned by this process
    ///   - Resources must be released via CFRelease when done
    ///
    /// This method is called before IOKit's ObtainExclusiveAccess to ensure the disk
    /// is fully unmounted and claimed. Without this, auto-mount can race with IOKit
    /// exclusive access, causing kIOReturnExclusiveAccess errors.
    /// </summary>
    private void TryClaimDiskExclusive(string bsdName)
    {
        try
        {
            // If we already have a DA session with an active claim, reuse it.
            // Re-creating a new session while the old one holds a claim leaks
            // CoreFoundation resources and can cause the new claim to fail
            // because the old session still holds the disk exclusively.
            // Only issue another unmount + claim cycle on the existing session.
            if (_daSession != IntPtr.Zero && _daDisk != IntPtr.Zero && _daDiskClaimed)
            {
                var existingRunLoop = CFRunLoopGetCurrent();
                var existingRunLoopMode = GetDefaultRunLoopMode();
                // Re-unmount: macOS may have re-mounted the volume since Open().
                DADiskUnmount(_daDisk, kDADiskUnmountOptionWhole | kDADiskUnmountOptionForce,
                    IntPtr.Zero, IntPtr.Zero);
                CFRunLoopRunInMode(existingRunLoopMode, 1.0, true);
                return;
            }

            // Release any stale DA resources from a previous (failed) claim
            // attempt before creating new ones. This prevents handle leaks
            // when PrepareForWrite is called after a partial TryClaimDiskExclusive.
            ReleaseDiskArbitration();

            // Create a Disk Arbitration session
            _daSession = DASessionCreate(kCFAllocatorDefault);
            if (_daSession == IntPtr.Zero)
                return; // DA not available — will fall back to diskutil

            // Schedule the session on the current run loop so DA callbacks fire
            var runLoop = CFRunLoopGetCurrent();
            var runLoopMode = GetDefaultRunLoopMode();
            try
            {
                DASessionScheduleWithRunLoop(_daSession, runLoop, runLoopMode);

                // Store the run loop and mode used for scheduling so
                // ReleaseDiskArbitration can unschedule from the same run loop.
                // CFRunLoopGetCurrent() returns a non-retained reference per CF
                // naming conventions ("Get" rule). The run loop lives as long as
                // its thread, but .NET thread pool threads are ephemeral — they
                // can be recycled/destroyed between async continuations. If the
                // thread dies, its run loop is deallocated, making the stored
                // pointer dangling. CFRetain keeps the run loop alive until we
                // explicitly CFRelease it in ReleaseDiskArbitration.
                CFRetain(runLoop);
                _daRunLoop = runLoop;
                _daRunLoopMode = runLoopMode;

                // Create a DADisk reference from the BSD name
                _daDisk = DADiskCreateFromBSDName(kCFAllocatorDefault, _daSession, bsdName);
                if (_daDisk == IntPtr.Zero)
                {
                    // Cleanup session if disk creation fails
                    DASessionUnscheduleFromRunLoop(_daSession, runLoop, runLoopMode);
                    CFRelease(_daSession);
                    _daSession = IntPtr.Zero;
                    // Release the retained run loop reference
                    CFRelease(_daRunLoop);
                    _daRunLoop = IntPtr.Zero;
                    _daRunLoopMode = IntPtr.Zero;
                    return;
                }

                // Unmount all volumes on the disk with force option.
                // kDADiskUnmountOptionWhole (0x01) unmounts all partitions on the whole disk.
                // kDADiskUnmountOptionForce (0x00080000) force-unmounts even if files are open.
                // Pass null callback — the operation completes synchronously when we pump
                // the run loop below.
                DADiskUnmount(_daDisk, kDADiskUnmountOptionWhole | kDADiskUnmountOptionForce,
                    IntPtr.Zero, IntPtr.Zero);

                // Pump the run loop briefly to allow the unmount callback to complete.
                // DA operations are asynchronous; CFRunLoopRunInMode processes pending
                // DA events on the current thread for up to the specified timeout.
                // With returnAfterSourceHandled=true, the call returns as soon as a
                // DA callback fires (typically 100-200ms), rather than always waiting
                // the full timeout. This significantly reduces discovery time from
                // 1.5+ seconds to ~300ms per device.
                CFRunLoopRunInMode(runLoopMode, 1.0, true);

                // Claim the disk for exclusive access. While claimed:
                //   - macOS will not auto-mount volumes on this disk
                //   - Other DA clients' mount/unmount requests will be denied
                //   - The OS won't send media-change polling commands
                // Pass null for all callbacks — we don't need arbitration or release notifications.
                DADiskClaim(_daDisk, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                // Pump run loop again for the claim to take effect.
                // With returnAfterSourceHandled=true, returns as soon as claim completes.
                CFRunLoopRunInMode(runLoopMode, 0.5, true);

                _daDiskClaimed = true;
            }
            catch
            {
                // Best effort — run loop mode retrieval or DA operations may fail
            }
        }
        catch
        {
            // Best effort — if DA framework is not available or fails,
            // we fall back to diskutil unmount and IOKit exclusive access
        }
    }

    /// <summary>
    /// Releases Disk Arbitration resources: unclaims the disk and releases the session.
    /// Called during cleanup to restore normal macOS disk management.
    /// Uses the stored run loop reference from TryClaimDiskExclusive to ensure
    /// unscheduling happens on the same run loop that was used for scheduling,
    /// even if called from a different thread (e.g. Dispose or finalizer).
    /// </summary>
    private void ReleaseDiskArbitration()
    {
        try
        {
            if (_daDiskClaimed && _daDisk != IntPtr.Zero)
            {
                DADiskUnclaim(_daDisk);
                _daDiskClaimed = false;
            }

            if (_daDisk != IntPtr.Zero)
            {
                CFRelease(_daDisk);
                _daDisk = IntPtr.Zero;
            }

            if (_daSession != IntPtr.Zero)
            {
                // Unschedule from the same run loop used during scheduling.
                // Fall back to CFRunLoopGetCurrent if no stored reference
                // (e.g. if scheduling was never completed).
                var runLoop = _daRunLoop != IntPtr.Zero ? _daRunLoop : CFRunLoopGetCurrent();
                var runLoopMode = _daRunLoopMode != IntPtr.Zero ? _daRunLoopMode : GetDefaultRunLoopMode();
                DASessionUnscheduleFromRunLoop(_daSession, runLoop, runLoopMode);
                CFRelease(_daSession);
                _daSession = IntPtr.Zero;

                // Release the retained run loop reference. The run loop was
                // retained in TryClaimDiskExclusive to prevent it from being
                // deallocated if the originating thread pool thread was recycled.
                if (_daRunLoop != IntPtr.Zero)
                {
                    CFRelease(_daRunLoop);
                    _daRunLoop = IntPtr.Zero;
                }
                _daRunLoopMode = IntPtr.Zero;
            }
        }
        catch { /* best effort */ }
    }

    public unsafe ScsiResult Execute(ScsiCommand command)
    {
        if (!IsOpen)
            throw new InvalidOperationException("SCSI transport is not open.");

        // Validate the SCSI task device interface is still accessible.
        // On macOS, if the IOKit user client was torn down (e.g., due to
        // StartStopUnit invalidating the device state), the vtable pointer
        // may reference freed memory. Calling through it would crash with
        // SIGILL (illegal hardware instruction) because the function pointer
        // points to deallocated or non-executable memory.
        // Read the vtable pointer and validate it before making the call.
        var vtablePtr = Marshal.ReadIntPtr(_scsiTaskDeviceInterface);
        if (vtablePtr == IntPtr.Zero)
            return FailResult();

        // Create a SCSI task via vtable slot 10 (CreateSCSITask).
        // Use unsafe function pointers (delegate* unmanaged[Cdecl]) instead of
        // Marshal.GetDelegateForFunctionPointer to avoid delegate thunk generation.
        // On ARM64 macOS, Marshal.GetDelegateForFunctionPointer creates a managed
        // delegate wrapper that must generate a reverse P/Invoke thunk at runtime.
        // This thunk uses MAP_JIT memory and W^X transitions, which can fail with
        // SIGBUS on Apple Silicon due to JIT page protection issues. Direct function
        // pointer calls via 'calli' bypass this entirely.
        var createFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)
            GetVtableSlot(_scsiTaskDeviceInterface, VtableOffset_CreateSCSITask);
        if ((IntPtr)createFnPtr == IntPtr.Zero)
            return FailResult();

        var task = createFnPtr(_scsiTaskDeviceInterface);
        if (task == IntPtr.Zero)
            return FailResult();

        // Use Marshal.AllocHGlobal for ALL buffers passed to IOKit native functions.
        // On Apple Silicon, GCHandle-pinned managed arrays reside in the .NET managed
        // heap, which uses memory pages allocated by the .NET runtime's custom allocator.
        // IOKit's kernel extensions create IOMemoryDescriptor objects from user-space
        // virtual addresses for DMA transfers. The .NET managed heap pages may have
        // memory attributes (e.g., tagged pointers, GC metadata in page tables) that
        // are incompatible with IOKit's IOMemoryDescriptor::withAddress(), causing
        // SIGBUS during the DMA operation. Marshal.AllocHGlobal uses the system's
        // native malloc, which returns memory from the standard C library heap —
        // guaranteed to be compatible with all system frameworks including IOKit.
        IntPtr cdbBuffer = IntPtr.Zero;
        IntPtr dataBuffer = IntPtr.Zero;
        IntPtr sgBuffer = IntPtr.Zero;

        // Per SPC-5, sense data can be up to 252 bytes (8-byte header + 244 additional).
        // Allocate 252 bytes instead of 64 to avoid truncating detailed diagnostics
        // from modern drives that return descriptor-format sense data.
        const int SenseBufferSize = 252;
        var senseBuffer = Marshal.AllocHGlobal(SenseBufferSize);

        try
        {
            // Allocate unmanaged memory for the CDB and copy data into it.
            cdbBuffer = Marshal.AllocHGlobal(command.Cdb.Length);
            Marshal.Copy(command.Cdb, 0, cdbBuffer, command.Cdb.Length);

            // Set CDB via vtable slot 8 (SetCommandDescriptorBlock).
            var setCdbFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, int>)
                GetVtableSlot(task, SCSITask_SetCommandDescriptorBlock);
            setCdbFnPtr(task, cdbBuffer, (byte)command.Cdb.Length);

            // Set task attribute to SIMPLE (required by IOKit before execution)
            // via vtable slot 6 (SetTaskAttribute).
            // Note: SCSITaskAttribute is UInt8 in Apple's header, but using int for the
            // argument is ABI-compatible on ARM64 (value is zero-extended in the register).
            var setAttrFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)
                GetVtableSlot(task, SCSITask_SetTaskAttribute);
            setAttrFnPtr(task, kSCSITaskAttribute_Simple);

            // Set transfer direction and scatter-gather if there's a data buffer
            // Per Apple SCSITask.h: direction is UInt8 with values:
            //   0x00 = No data, 0x01 = Host→Device (write), 0x02 = Device→Host (read)
            byte direction = command.Direction switch
            {
                ScsiDataDirection.In => kSCSIDataTransferDirection_FromTargetToInitiator,
                ScsiDataDirection.Out => kSCSIDataTransferDirection_FromInitiatorToTarget,
                _ => kSCSIDataTransferDirection_NoTransfer
            };

            if (command.DataBuffer.Length > 0)
            {
                // Allocate unmanaged memory for the data buffer.
                // This is the DMA buffer that IOKit will use for the SCSI transfer.
                dataBuffer = Marshal.AllocHGlobal(command.DataBuffer.Length);

                // For write operations (Host→Device), copy source data to the unmanaged buffer.
                if (command.Direction == ScsiDataDirection.Out)
                    Marshal.Copy(command.DataBuffer, 0, dataBuffer, command.DataBuffer.Length);
                else
                {
                    // For read operations, zero out the buffer to avoid returning stale data.
                    unsafe
                    {
                        new Span<byte>((void*)dataBuffer, command.DataBuffer.Length).Clear();
                    }
                }

                // Create a single scatter-gather element (IOVirtualRange) in unmanaged memory.
                // IOVirtualRange layout on 64-bit macOS: { IOVirtualAddress address (8 bytes),
                //   IOByteCount length (8 bytes) } = 16 bytes, naturally aligned.
                // Using AllocHGlobal guarantees the struct is properly aligned for IOKit.
                sgBuffer = Marshal.AllocHGlobal(16);
                Marshal.WriteIntPtr(sgBuffer, 0, dataBuffer);           // address field
                Marshal.WriteInt64(sgBuffer, 8, command.DataBuffer.Length); // length field

                // Set scatter-gather entries via vtable slot 11 (SetScatterGatherEntries).
                var setSgFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, ulong, byte, int>)
                    GetVtableSlot(task, SCSITask_SetScatterGatherEntries);
                setSgFnPtr(task, sgBuffer, 1, (ulong)command.DataBuffer.Length, direction);
            }

            // Set timeout via vtable slot 12 (SetTimeoutDuration).
            var setTimeoutFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, uint, int>)
                GetVtableSlot(task, SCSITask_SetTimeoutDuration);
            setTimeoutFnPtr(task, (uint)command.TimeoutMs);

            // Zero out sense buffer before execution
            for (int i = 0; i < SenseBufferSize; i++)
                Marshal.WriteByte(senseBuffer, i, 0);

            // Execute synchronously via vtable slot 16 (ExecuteTaskSync).
            // Per IOKit SCSITaskLib.h:
            //   IOReturn ExecuteTaskSync(void *self, SCSI_Sense_Data *senseDataBuffer,
            //     SCSITaskStatus *taskStatus, UInt64 *realizedTransferCount);
            // Local variables on the stack can be addressed directly with '&' in
            // unsafe context — no 'fixed' needed since they're already stack-allocated
            // and cannot be moved by the GC.
            uint taskStatus = 0;
            ulong realizedTransferCount = 0;
            int kr;

            // Take addresses of stack-allocated locals directly (safe in unsafe context).
            var execFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint*, ulong*, int>)
                GetVtableSlot(task, SCSITask_ExecuteTaskSync);
            kr = execFnPtr(task, senseBuffer, &taskStatus, &realizedTransferCount);

            if (kr != kIOReturnSuccess)
            {
                // Log the IOKit error for diagnostics. Common error codes:
                //   0xE00002C7 (kIOReturnUnsupported) — command not supported by driver
                //   0xE00002BE (kIOReturnNotPermitted) — insufficient permissions
                //   0xE00002C5 (kIOReturnExclusiveAccess) — device busy
                System.Diagnostics.Debug.WriteLine(
                    $"[MacOsScsiTransport] ExecuteTaskSync failed: 0x{kr:X8} " +
                    $"(CDB opcode=0x{command.Cdb[0]:X2}, direction={command.Direction})");

                // Still capture any sense data that was written even though
                // the IOKit call itself failed — this aids diagnostics.
                var failSenseData = CopySenseData(senseBuffer, SenseBufferSize);

                return new ScsiResult
                {
                    Status = 0xFF,
                    SenseData = failSenseData,
                    DataTransferred = 0
                };
            }

            // Get service response via vtable slot 18 (GetSCSIServiceResponse).
            uint serviceResponse = 0;
            var getSvcFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)
                GetVtableSlot(task, SCSITask_GetSCSIServiceResponse);
            getSvcFnPtr(task, &serviceResponse);

            // Use the realized transfer count from ExecuteTaskSync directly.
            // Also query via GetRealizedDataTransferCount as a cross-check; prefer
            // the larger value in case ExecuteTaskSync returned an incomplete count.
            var getCountFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, ulong>)
                GetVtableSlot(task, SCSITask_GetRealizedDataTransferCount);
            var transferred = Math.Max(realizedTransferCount, getCountFnPtr(task));

            // Copy data back from unmanaged buffer to managed array for read operations.
            // Use the final 'transferred' count which incorporates both ExecuteTaskSync's
            // realizedTransferCount and GetRealizedDataTransferCount.
            if (dataBuffer != IntPtr.Zero && command.Direction == ScsiDataDirection.In &&
                transferred > 0)
            {
                var bytesToCopy = (int)Math.Min(transferred,
                    (ulong)command.DataBuffer.Length);
                Marshal.Copy(dataBuffer, command.DataBuffer, 0, bytesToCopy);
            }

            // Copy sense data — scan for actual content since ExecuteTaskSync
            // does not return a sense data length.
            var senseData = CopySenseData(senseBuffer, SenseBufferSize);

            // If we got CHECK CONDITION but no sense data from ExecuteTaskSync,
            // try GetAutoSenseData via vtable slot 22.
            if (taskStatus == kSCSITaskStatus_CHECK_CONDITION && senseData.Length == 0)
            {
                var getAutoSenseFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)
                    GetVtableSlot(task, SCSITask_GetAutoSenseData);
                var autoSenseResult = getAutoSenseFnPtr(task, senseBuffer);
                if (autoSenseResult == kIOReturnSuccess)
                {
                    senseData = CopySenseData(senseBuffer, SenseBufferSize);
                }
            }

            return new ScsiResult
            {
                Status = (byte)taskStatus,
                SenseData = senseData,
                DataTransferred = (int)Math.Min(transferred, int.MaxValue)
            };
        }
        finally
        {
            // Release the SCSI task via vtable slot 3 (Release).
            if (task != IntPtr.Zero)
            {
                var releaseFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                    GetVtableSlot(task, SCSITask_Release);
                releaseFnPtr(task);
            }

            // Free all unmanaged memory allocations.
            if (cdbBuffer != IntPtr.Zero) Marshal.FreeHGlobal(cdbBuffer);
            if (dataBuffer != IntPtr.Zero) Marshal.FreeHGlobal(dataBuffer);
            if (sgBuffer != IntPtr.Zero) Marshal.FreeHGlobal(sgBuffer);
            Marshal.FreeHGlobal(senseBuffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Copies sense data from an unmanaged buffer, determining the actual length
    /// by parsing the sense data response format rather than trimming trailing zeros
    /// (which could incorrectly truncate valid sense data containing zero bytes).
    /// </summary>
    private static byte[] CopySenseData(IntPtr senseBuffer, int maxLen)
    {
        var raw = new byte[maxLen];
        Marshal.Copy(senseBuffer, raw, 0, maxLen);

        // Determine actual sense data length from the response code and structure.
        // Per SPC-5, sense data has a well-defined format that includes a length field.
        if (maxLen < 1 || raw[0] == 0)
            return Array.Empty<byte>();

        var responseCode = (byte)(raw[0] & 0x7F);

        int actualLen;
        switch (responseCode)
        {
            case 0x70:
            case 0x71:
                // Fixed-format sense data (SPC-5 §4.5.3):
                // Byte 7 = Additional Sense Length (number of bytes after byte 7)
                // Total length = 8 + additional_sense_length
                if (maxLen >= 8)
                {
                    actualLen = Math.Min(8 + raw[7], maxLen);
                }
                else
                {
                    actualLen = Math.Min(8, maxLen);
                }
                break;

            case 0x72:
            case 0x73:
                // Descriptor-format sense data (SPC-5 §4.5.2):
                // Byte 7 = Additional Sense Length (number of bytes after byte 7)
                // Total length = 8 + additional_sense_length
                if (maxLen >= 8)
                {
                    actualLen = Math.Min(8 + raw[7], maxLen);
                }
                else
                {
                    actualLen = Math.Min(8, maxLen);
                }
                break;

            default:
                // Unknown format — fall back to trimming trailing zeros
                actualLen = maxLen;
                while (actualLen > 0 && raw[actualLen - 1] == 0) actualLen--;
                break;
        }

        if (actualLen <= 0)
            return Array.Empty<byte>();

        return raw[..actualLen];
    }

    private unsafe void ReleaseResources()
    {
        if (_hasExclusiveAccess && _scsiTaskDeviceInterface != IntPtr.Zero)
        {
            try
            {
                var releaseFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                    GetVtableSlot(_scsiTaskDeviceInterface, VtableOffset_ReleaseExclusiveAccess);
                releaseFnPtr(_scsiTaskDeviceInterface);
            }
            catch { /* best effort */ }
            _hasExclusiveAccess = false;
        }

        // Remove callback dispatcher from run loop before releasing the interface
        if (_hasCallbackDispatcher && _scsiTaskDeviceInterface != IntPtr.Zero)
        {
            try
            {
                var removeFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                    GetVtableSlot(_scsiTaskDeviceInterface, VtableOffset_RemoveCallbackDispatcher);
                removeFnPtr(_scsiTaskDeviceInterface);
            }
            catch { /* best effort */ }
            _hasCallbackDispatcher = false;
        }

        if (_scsiTaskDeviceInterface != IntPtr.Zero)
        {
            // Release via COM Release (vtable slot 3: _reserved=0, QueryInterface=1, AddRef=2, Release=3)
            try
            {
                var relFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                    GetVtableSlot(_scsiTaskDeviceInterface, 3);
                relFnPtr(_scsiTaskDeviceInterface);
            }
            catch { /* best effort */ }
            _scsiTaskDeviceInterface = IntPtr.Zero;
        }

        // Release MMCDeviceInterface (obtained via QueryInterface for kIOMMCDeviceInterfaceID)
        if (_mmcDeviceInterface != IntPtr.Zero)
        {
            try
            {
                var relFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                    GetVtableSlot(_mmcDeviceInterface, 3);
                relFnPtr(_mmcDeviceInterface);
            }
            catch { /* best effort */ }
            _mmcDeviceInterface = IntPtr.Zero;
        }

        if (_pluginInterface != IntPtr.Zero)
        {
            try
            {
                // Release plugin interface: slot 3 = Release
                var relFnPtr = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                    GetVtableSlot(_pluginInterface, 3);
                relFnPtr(_pluginInterface);
            }
            catch { /* best effort */ }
            _pluginInterface = IntPtr.Zero;
        }

        // Release Disk Arbitration resources after IOKit resources
        ReleaseDiskArbitration();
    }

    /// <summary>
    /// Finds the IOService matching the BSD device name that supports SCSI task submission.
    /// Walks up the IORegistry tree from the BSD device to find an MMC device service
    /// that conforms to IOSCSIPeripheralDeviceType05 (optical drives).
    /// </summary>
    private static uint FindSCSITaskService(string bsdName)
    {
        // Match by BSD name first to find the media/partition service
        var matchingDict = IOBSDNameMatching(kIOMasterPortDefault, 0, bsdName);
        if (matchingDict == IntPtr.Zero) return 0;

        var kr = IOServiceGetMatchingServices(kIOMasterPortDefault, matchingDict, out var iterator);
        if (kr != kIOReturnSuccess) return 0;

        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                // Walk up the IORegistry tree to find the SCSITaskDeviceCategory parent.
                // FindSCSITaskParent may return the same service object if it
                // directly conforms, so we must not release service in that case.
                var scsiService = FindSCSITaskParent(service);
                if (scsiService != 0 && scsiService != service)
                {
                    // Found a parent — release the original BSD service
                    IOObjectRelease(service);
                    return scsiService;
                }
                if (scsiService != 0)
                {
                    // scsiService == service: the BSD node itself conforms.
                    // Do NOT release it — return it directly (caller will use it).
                    return scsiService;
                }
                IOObjectRelease(service);
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        // Fallback: if BSD name matching and parent walk failed, the device is
        // not accessible through IOKit's SCSI Architecture Model.
        // DO NOT fall back to FindSCSITaskServiceByClass() here — it returns
        // the FIRST IOSCSIPeripheralDeviceType05 service regardless of which
        // physical device was requested. On systems with multiple optical drives,
        // this would silently send SCSI commands to the wrong drive.
        return 0;
    }

    /// <summary>
    /// Walks up the IORegistry tree from a service to find a parent that
    /// represents a SCSI optical drive capable of accepting SCSITask commands.
    ///
    /// Per Apple's SCSITaskLib.h and cdrtools (scsi-mac-iokit.c), the correct
    /// service for IOCreatePlugInInterfaceForService is:
    ///   1. IOSCSIPeripheralDeviceType05 — the in-kernel MMC optical drive driver.
    ///      This is the primary target for kIOMMCDeviceUserClientTypeID.
    ///   2. IOSCSIPeripheralDeviceNub — the nub created by the SCSI controller
    ///      driver before the Type05 driver loads. Can be used as a fallback.
    ///
    /// Returns the matching IOService (retained) or 0 if not found.
    /// The returned object may be the same as the input service.
    /// </summary>
    private static uint FindSCSITaskParent(uint service)
    {
        uint current = service;
        // Walk up to 10 levels to avoid infinite loops
        for (int depth = 0; depth < 10; depth++)
        {
            // Primary match: IOSCSIPeripheralDeviceType05 — the MMC optical drive driver.
            // IOCreatePlugInInterfaceForService with kIOMMCDeviceUserClientTypeID works on this.
            if (IOObjectConformsTo(current, "IOSCSIPeripheralDeviceType05"))
            {
                // Don't release this one — return it
                return current;
            }

            // Secondary match: IOSCSIPeripheralDeviceNub — the nub that the Type05
            // driver attaches to. It conforms to the SCSITaskAuthoringDevice protocol.
            // On some macOS versions or hardware configurations, the Type05 driver may
            // not be the direct parent in the walk, so checking the nub gives us a
            // wider match. Per cdrtools, this is the canonical service for SCSI tasks.
            if (IOObjectConformsTo(current, "IOSCSIPeripheralDeviceNub"))
            {
                return current;
            }

            var kr = IORegistryEntryGetParentEntry(current, "IOService", out var parent);

            // Release intermediate nodes (not the initial service which the caller owns)
            if (depth > 0) IOObjectRelease(current);

            if (kr != kIOReturnSuccess)
            {
                // Walk failed — current was already released (if intermediate) or is still
                // owned by caller (if depth == 0). Either way, nothing more to clean up.
                return 0;
            }

            current = parent;
        }

        // Walked the maximum depth without finding a match.
        // Release the last node we obtained (but not the initial service, which
        // the caller owns). Only release if current is a different object that we
        // acquired via IORegistryEntryGetParentEntry.
        if (current != service && current != 0)
            IOObjectRelease(current);

        return 0;
    }

    private static ScsiResult FailResult() => new()
    {
        Status = 0xFF,
        SenseData = Array.Empty<byte>(),
        DataTransferred = 0
    };

    /// <summary>
    /// Checks whether a BSD device name corresponds to an optical drive by looking
    /// for an IOSCSIPeripheralDeviceType05 or IOSCSIPeripheralDeviceNub (type 5)
    /// ancestor in the IOKit registry. This check is very fast (no SCSI commands,
    /// no Disk Arbitration operations) and is used by the discovery fallback scan
    /// to skip non-optical devices before attempting the expensive SCSI open sequence.
    /// </summary>
    internal static bool IsOpticalDriveBsdName(string bsdName)
    {
        var matchingDict = IOBSDNameMatching(kIOMasterPortDefault, 0, bsdName);
        if (matchingDict == IntPtr.Zero) return false;

        var kr = IOServiceGetMatchingServices(kIOMasterPortDefault, matchingDict, out var iterator);
        if (kr != kIOReturnSuccess) return false;

        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    // Walk up the parent chain looking for optical drive services
                    uint current = service;
                    bool foundOptical = false;

                    for (int depth = 0; depth < 10; depth++)
                    {
                        if (IOObjectConformsTo(current, "IOSCSIPeripheralDeviceType05"))
                        {
                            foundOptical = true;
                            break;
                        }
                        if (IOObjectConformsTo(current, "IOSCSIPeripheralDeviceNub"))
                        {
                            // Check device type on the nub
                            var devType = ReadRegistryIntProperty(current, "Peripheral Device Type", 0);
                            if (devType == 5)
                            {
                                foundOptical = true;
                                break;
                            }
                        }

                        kr = IORegistryEntryGetParentEntry(current, "IOService", out var parent);
                        // Release intermediate parent nodes we obtained (not the initial
                        // service, which is owned by the outer IOIteratorNext loop).
                        if (current != service) IOObjectRelease(current);
                        if (kr != kIOReturnSuccess) { current = 0; break; }
                        current = parent;
                    }

                    // Release the last parent we stopped on (if it's not the initial service
                    // and not already released). Both the found and not-found paths need this.
                    if (current != service && current != 0)
                        IOObjectRelease(current);

                    if (foundOptical) return true;
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        return false;
    }

    /// <summary>
    /// Resolves an IORegistry path (e.g., "IOService:/AppleACPIPlatformExpert/.../IOSCSIPeripheralDeviceType05")
    /// to the BSD device name (e.g., "/dev/disk2") if media is currently present.
    ///
    /// When a drive is discovered without media, the DevicePath stored in DiscDrive is an IORegistry path.
    /// If the user later inserts media and attempts operations like eject, blank, or format, we need the
    /// BSD name for Disk Arbitration operations (unmount, claim) and for diskutil commands.
    ///
    /// This method walks the IORegistry tree from the Type05 service down to find the IOMedia child
    /// that has a "BSD Name" property (only exists when media is present).
    /// </summary>
    /// <param name="ioRegistryPath">An IORegistry path starting with "IOService:".</param>
    /// <returns>The full BSD device path (e.g., "/dev/disk2"), or null if no media is present.</returns>
    internal static string? ResolveBsdNameFromIoRegistryPath(string ioRegistryPath)
    {
        if (string.IsNullOrEmpty(ioRegistryPath) ||
            !ioRegistryPath.StartsWith("IOService:", StringComparison.Ordinal))
            return null;

        var service = IORegistryEntryFromPath(kIOMasterPortDefault, ioRegistryPath);
        if (service == 0) return null;

        try
        {
            // The BSD name is a property of the IOMedia descendant of the Type05 service.
            // Search children recursively to find it.
            var bsdName = ReadRegistryStringProperty(
                service, "BSD Name", kIORegistryIterateRecursively);
            if (!string.IsNullOrEmpty(bsdName))
                return $"/dev/{bsdName}";

            return null;
        }
        finally
        {
            IOObjectRelease(service);
        }
    }

    // -----------------------------------------------------------------------
    // IOKit-based optical drive enumeration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads a CFString value and returns it as a managed string.
    /// Returns null if the CFString pointer is null or the conversion fails.
    /// </summary>
    private static string? ReadCFString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;
        var len = CFStringGetLength(cfString);
        if (len <= 0) return null;
        // CFStringGetLength returns UTF-16 code units. Each code unit can expand
        // to up to 4 bytes in UTF-8 (surrogate pairs), plus a null terminator.
        var bufSize = len * 4 + 1;
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            if (CFStringGetCString(cfString, buf, bufSize, kCFStringEncodingUTF8))
                return Marshal.PtrToStringUTF8(buf);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Reads an IORegistry string property from the specified entry or its
    /// ancestors/descendants, depending on the search options.
    /// Returns null if the property is not found.
    /// </summary>
    private static string? ReadRegistryStringProperty(uint entry, string propertyName, uint searchOptions)
    {
        var key = CFStringCreateWithCString(kCFAllocatorDefault, propertyName, kCFStringEncodingUTF8);
        if (key == IntPtr.Zero) return null;
        try
        {
            IntPtr valueRef;
            if (searchOptions == 0)
            {
                // Read directly from this entry only
                valueRef = IORegistryEntryCreateCFProperty(entry, key, kCFAllocatorDefault, 0);
            }
            else
            {
                // Search in the specified direction
                valueRef = IORegistryEntrySearchCFProperty(entry, "IOService", key, kCFAllocatorDefault, searchOptions);
            }
            if (valueRef == IntPtr.Zero) return null;
            try
            {
                return ReadCFString(valueRef);
            }
            finally
            {
                CFRelease(valueRef);
            }
        }
        finally
        {
            CFRelease(key);
        }
    }

    /// <summary>
    /// Enumerates all optical drives on macOS via IOKit service matching.
    /// Uses the IOSCSIPeripheralDeviceType05 class which represents MMC optical devices.
    ///
    /// This approach is more reliable than scanning /dev/diskN BSD device nodes because:
    ///   1. IOKit always knows about connected optical drives regardless of media state
    ///   2. Drives without media don't have BSD names (no /dev/diskN) and would be missed
    ///   3. Scanning /dev/diskN triggers expensive unmount/claim operations on non-optical devices
    ///   4. The /dev/diskN number is dynamic and can correspond to system disks
    ///
    /// For each drive found, returns the BSD name (if media is present and an IOMedia child
    /// exists), vendor identification, and product identification from the IORegistry.
    /// These properties are populated by the kernel SCSI driver during device enumeration
    /// and do not require exclusive access or SCSI passthrough to read.
    /// </summary>
    /// <returns>
    /// List of tuples: (bsdName, vendor, product) for each optical drive found.
    /// bsdName is null when no media is present (the drive has no IOMedia child).
    /// </returns>
    internal static List<(string? BsdName, string Vendor, string Product, string? IoRegistryPath)> EnumerateOpticalDriveServices()
    {
        var drives = new List<(string? BsdName, string Vendor, string Product, string? IoRegistryPath)>();

        var matching = IOServiceMatching("IOSCSIPeripheralDeviceType05");
        if (matching == IntPtr.Zero) return drives;

        // IOServiceGetMatchingServices consumes the matching dictionary (releases it)
        var kr = IOServiceGetMatchingServices(kIOMasterPortDefault, matching, out var iterator);
        if (kr != kIOReturnSuccess) return drives;

        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    // Read vendor/product from the IOSCSIPeripheralDeviceNub parent.
                    // The nub stores INQUIRY data that the kernel obtained during
                    // device enumeration: "Vendor Identification" and "Product Identification".
                    // Search parents because the nub is the immediate parent of Type05.
                    var vendor = ReadRegistryStringProperty(
                        service, "Vendor Identification",
                        kIORegistryIterateParents | kIORegistryIterateRecursively)?.Trim();
                    var product = ReadRegistryStringProperty(
                        service, "Product Identification",
                        kIORegistryIterateParents | kIORegistryIterateRecursively)?.Trim();

                    // Also try reading from the entry itself (some driver stacks
                    // put vendor/product directly on the IOSCSIPeripheralDeviceType05)
                    if (string.IsNullOrWhiteSpace(vendor))
                        vendor = ReadRegistryStringProperty(service, "Vendor Identification", 0)?.Trim();
                    if (string.IsNullOrWhiteSpace(product))
                        product = ReadRegistryStringProperty(service, "Product Identification", 0)?.Trim();

                    // Get BSD name by searching children recursively.
                    // The BSD name is a property of the IOMedia descendant, which only
                    // exists when media is present in the drive. Path:
                    //   IOSCSIPeripheralDeviceType05 → IOBlockStorageServices
                    //     → IOBlockStorageDriver → IOMedia (has "BSD Name")
                    var bsdName = ReadRegistryStringProperty(
                        service, "BSD Name", kIORegistryIterateRecursively);

                    // Get IORegistry path as fallback device identifier.
                    // When no BSD name is available (no media), the IORegistry path
                    // provides a stable device identifier like:
                    //   IOService:/AppleACPIPlatformExpert/.../IOSCSIPeripheralDeviceType05
                    string? ioRegPath = null;
                    try
                    {
                        var pathBuf = new System.Text.StringBuilder(512);
                        if (IORegistryEntryGetPath(service, "IOService", pathBuf) == kIOReturnSuccess)
                            ioRegPath = pathBuf.ToString();
                    }
                    catch { /* best effort */ }

                    drives.Add((
                        BsdName: bsdName,
                        Vendor: vendor ?? "Unknown",
                        Product: product ?? "Unknown",
                        IoRegistryPath: ioRegPath
                    ));
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        // If primary IOSCSIPeripheralDeviceType05 matching found no drives, try matching
        // IOSCSIPeripheralDeviceNub — the nub that sits above the Type05 driver in the
        // IOKit hierarchy. On some hardware configurations (USB drives during initialization,
        // external drives on Apple Silicon), the Type05 driver may not have loaded yet, but
        // the nub already exists. We filter by "Peripheral Device Type" = 5 (optical/MMC).
        if (drives.Count == 0)
        {
            EnumerateViaDeviceNub(drives);
        }

        // Fallback: try block storage service subclasses (media must be present).
        // Per dvdisaster and Apple conventions, optical drives can also be
        // discovered via their block storage service subclasses when media is present:
        //   IOBDServices (Blu-ray), IODVDServices (DVD), IOCompactDiscServices (CD)
        // These classes only exist when media is present, but they provide an
        // alternative path if the Type05 matching fails.
        if (drives.Count == 0)
        {
            EnumerateViaBlockStorageServices(drives);
        }

        return drives;
    }

    /// <summary>
    /// Reads an IORegistry integer (SInt32) property from the specified entry or its
    /// ancestors/descendants, depending on the search options.
    /// Returns null if the property is not found or is not a number.
    /// </summary>
    private static int? ReadRegistryIntProperty(uint entry, string propertyName, uint searchOptions)
    {
        var key = CFStringCreateWithCString(kCFAllocatorDefault, propertyName, kCFStringEncodingUTF8);
        if (key == IntPtr.Zero) return null;
        try
        {
            IntPtr valueRef;
            if (searchOptions == 0)
                valueRef = IORegistryEntryCreateCFProperty(entry, key, kCFAllocatorDefault, 0);
            else
                valueRef = IORegistryEntrySearchCFProperty(entry, "IOService", key, kCFAllocatorDefault, searchOptions);
            if (valueRef == IntPtr.Zero) return null;
            try
            {
                if (CFNumberGetValue(valueRef, kCFNumberSInt32Type, out var intValue))
                    return intValue;
                return null;
            }
            finally
            {
                CFRelease(valueRef);
            }
        }
        finally
        {
            CFRelease(key);
        }
    }

    /// <summary>
    /// Fallback discovery via IOSCSIPeripheralDeviceNub matching.
    /// The nub is created by the SCSI controller driver before the Type05 driver
    /// loads. This catches drives that are still initializing (e.g., USB drives
    /// during hotplug on Apple Silicon). We filter by "Peripheral Device Type" = 5
    /// (SCSI MMC/optical devices per SPC-5 §6.4.2).
    /// </summary>
    private static void EnumerateViaDeviceNub(
        List<(string? BsdName, string Vendor, string Product, string? IoRegistryPath)> drives)
    {
        var matching = IOServiceMatching("IOSCSIPeripheralDeviceNub");
        if (matching == IntPtr.Zero) return;

        var kr = IOServiceGetMatchingServices(kIOMasterPortDefault, matching, out var iterator);
        if (kr != kIOReturnSuccess) return;

        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    // Check "Peripheral Device Type" property — must be 5 for optical drives.
                    // Per SPC-5, device type 5 = CD/DVD device (MMC command set).
                    var devType = ReadRegistryIntProperty(service, "Peripheral Device Type", 0);
                    if (devType != 5) continue;

                    var vendor = ReadRegistryStringProperty(service, "Vendor Identification", 0)?.Trim();
                    var product = ReadRegistryStringProperty(service, "Product Identification", 0)?.Trim();

                    // Get BSD name from child IOMedia (if media is present)
                    var bsdName = ReadRegistryStringProperty(
                        service, "BSD Name", kIORegistryIterateRecursively);

                    // Get IORegistry path for drives without media (no BSD name).
                    // The IORegistry path enables SCSI passthrough via IOKit even
                    // without media, allowing capability probing (INQUIRY, GET CONFIGURATION).
                    string? nubIoRegPath = null;
                    try
                    {
                        var pathBuf = new System.Text.StringBuilder(512);
                        if (IORegistryEntryGetPath(service, "IOService", pathBuf) == kIOReturnSuccess)
                            nubIoRegPath = pathBuf.ToString();
                    }
                    catch { /* best effort */ }

                    drives.Add((
                        BsdName: bsdName,
                        Vendor: vendor ?? "Unknown",
                        Product: product ?? "Unknown",
                        IoRegistryPath: nubIoRegPath
                    ));
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }
    }

    /// <summary>
    /// Fallback discovery via IOKit block storage service classes.
    /// When media is present, optical drives expose IOCompactDiscServices,
    /// IODVDServices, or IOBDServices in the IOKit registry. These classes
    /// are children of IOSCSIPeripheralDeviceType05 and carry the BSD name.
    /// We walk up from these services to find vendor/product info.
    /// </summary>
    private static void EnumerateViaBlockStorageServices(
        List<(string? BsdName, string Vendor, string Product, string? IoRegistryPath)> drives)
    {
        var serviceClasses = new[] { "IOBDServices", "IODVDServices", "IOCompactDiscServices" };
        var seenBsdNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var className in serviceClasses)
        {
            var matching = IOServiceMatching(className);
            if (matching == IntPtr.Zero) continue;

            var kr = IOServiceGetMatchingServices(kIOMasterPortDefault, matching, out var iterator);
            if (kr != kIOReturnSuccess) continue;

            try
            {
                uint service;
                while ((service = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        // Read BSD name from children (IOMedia descendants)
                        var bsdName = ReadRegistryStringProperty(
                            service, "BSD Name", kIORegistryIterateRecursively);

                        // Also try reading from the entry itself
                        bsdName ??= ReadRegistryStringProperty(service, "BSD Name", 0);

                        // Skip duplicates
                        if (bsdName != null && !seenBsdNames.Add(bsdName)) continue;

                        // Read vendor/product from parent services
                        var vendor = ReadRegistryStringProperty(
                            service, "Vendor Identification",
                            kIORegistryIterateParents | kIORegistryIterateRecursively)?.Trim();
                        var product = ReadRegistryStringProperty(
                            service, "Product Identification",
                            kIORegistryIterateParents | kIORegistryIterateRecursively)?.Trim();

                        // Fallback: try IORegistry device characteristics
                        if (string.IsNullOrWhiteSpace(vendor))
                            vendor = ReadRegistryStringProperty(
                                service, "Vendor Name",
                                kIORegistryIterateParents | kIORegistryIterateRecursively)?.Trim();
                        if (string.IsNullOrWhiteSpace(product))
                            product = ReadRegistryStringProperty(
                                service, "Product Name",
                                kIORegistryIterateParents | kIORegistryIterateRecursively)?.Trim();

                        // Get IORegistry path for capability probing when no BSD name
                        string? bsIoRegPath = null;
                        try
                        {
                            var pathBuf = new System.Text.StringBuilder(512);
                            if (IORegistryEntryGetPath(service, "IOService", pathBuf) == kIOReturnSuccess)
                                bsIoRegPath = pathBuf.ToString();
                        }
                        catch { /* best effort */ }

                        drives.Add((
                            BsdName: bsdName,
                            Vendor: vendor ?? "Unknown",
                            Product: product ?? "Unknown",
                            IoRegistryPath: bsIoRegPath
                        ));
                    }
                    finally
                    {
                        IOObjectRelease(service);
                    }
                }
            }
            finally
            {
                IOObjectRelease(iterator);
            }
        }
    }
}
