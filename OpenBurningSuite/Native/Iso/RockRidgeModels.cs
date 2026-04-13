// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Parsed POSIX file attributes from a Rock Ridge PX entry (RRIP §4.1.1).
/// </summary>
public sealed class RockRidgePosixAttributes
{
    /// <summary>Raw POSIX st_mode value (file type + permission bits).</summary>
    public uint Mode { get; set; }

    /// <summary>POSIX hard link count (st_nlink).</summary>
    public uint LinkCount { get; set; }

    /// <summary>POSIX user ID (st_uid).</summary>
    public uint Uid { get; set; }

    /// <summary>POSIX group ID (st_gid).</summary>
    public uint Gid { get; set; }

    /// <summary>POSIX serial number / inode (st_ino).</summary>
    public uint SerialNumber { get; set; }

    /// <summary>Whether the entry is a directory.</summary>
    public bool IsDirectory => (Mode & 0xF000) == RockRidgeExtensions.S_IFDIR;

    /// <summary>Whether the entry is a regular file.</summary>
    public bool IsRegularFile => (Mode & 0xF000) == RockRidgeExtensions.S_IFREG;

    /// <summary>Whether the entry is a symbolic link.</summary>
    public bool IsSymbolicLink => (Mode & 0xF000) == RockRidgeExtensions.S_IFLNK;

    /// <summary>Whether the entry is a block device.</summary>
    public bool IsBlockDevice => (Mode & 0xF000) == RockRidgeExtensions.S_IFBLK;

    /// <summary>Whether the entry is a character device.</summary>
    public bool IsCharDevice => (Mode & 0xF000) == RockRidgeExtensions.S_IFCHR;

    /// <summary>Whether the entry is a named pipe (FIFO).</summary>
    public bool IsFifo => (Mode & 0xF000) == RockRidgeExtensions.S_IFIFO;

    /// <summary>Whether the entry is a socket.</summary>
    public bool IsSocket => (Mode & 0xF000) == RockRidgeExtensions.S_IFSOCK;

    /// <summary>Permission bits only (lower 12 bits of mode).</summary>
    public uint Permissions => Mode & 0x0FFF;

    /// <summary>Returns the POSIX mode string (e.g., "drwxr-xr-x").</summary>
    public string ModeString
    {
        get
        {
            char typeChar = (Mode & 0xF000) switch
            {
                0x4000 => 'd', // S_IFDIR
                0x8000 => '-', // S_IFREG
                0xA000 => 'l', // S_IFLNK
                0x6000 => 'b', // S_IFBLK
                0x2000 => 'c', // S_IFCHR
                0x1000 => 'p', // S_IFIFO
                0xC000 => 's', // S_IFSOCK
                _ => '?'
            };

            var sb = new StringBuilder(10);
            sb.Append(typeChar);
            sb.Append((Mode & RockRidgeExtensions.S_IRUSR) != 0 ? 'r' : '-');
            sb.Append((Mode & RockRidgeExtensions.S_IWUSR) != 0 ? 'w' : '-');
            sb.Append((Mode & RockRidgeExtensions.S_ISUID) != 0
                ? ((Mode & RockRidgeExtensions.S_IXUSR) != 0 ? 's' : 'S')
                : ((Mode & RockRidgeExtensions.S_IXUSR) != 0 ? 'x' : '-'));
            sb.Append((Mode & RockRidgeExtensions.S_IRGRP) != 0 ? 'r' : '-');
            sb.Append((Mode & RockRidgeExtensions.S_IWGRP) != 0 ? 'w' : '-');
            sb.Append((Mode & RockRidgeExtensions.S_ISGID) != 0
                ? ((Mode & RockRidgeExtensions.S_IXGRP) != 0 ? 's' : 'S')
                : ((Mode & RockRidgeExtensions.S_IXGRP) != 0 ? 'x' : '-'));
            sb.Append((Mode & RockRidgeExtensions.S_IROTH) != 0 ? 'r' : '-');
            sb.Append((Mode & RockRidgeExtensions.S_IWOTH) != 0 ? 'w' : '-');
            sb.Append((Mode & RockRidgeExtensions.S_ISVTX) != 0
                ? ((Mode & RockRidgeExtensions.S_IXOTH) != 0 ? 't' : 'T')
                : ((Mode & RockRidgeExtensions.S_IXOTH) != 0 ? 'x' : '-'));

            return sb.ToString();
        }
    }
}

/// <summary>
/// Parsed POSIX device numbers from a Rock Ridge PN entry (RRIP §4.1.2).
/// </summary>
public sealed class RockRidgeDeviceNumber
{
    /// <summary>Device major number (dev_t high).</summary>
    public uint Major { get; set; }

    /// <summary>Device minor number (dev_t low).</summary>
    public uint Minor { get; set; }
}

/// <summary>
/// Parsed timestamps from a Rock Ridge TF entry (RRIP §4.1.6).
/// </summary>
public sealed class RockRidgeTimestamps
{
    /// <summary>File creation time, if present.</summary>
    public DateTime? Creation { get; set; }

    /// <summary>File modification time, if present.</summary>
    public DateTime? Modification { get; set; }

    /// <summary>File last-access time, if present.</summary>
    public DateTime? Access { get; set; }

    /// <summary>Attributes change time, if present.</summary>
    public DateTime? Attributes { get; set; }

    /// <summary>Backup time, if present.</summary>
    public DateTime? Backup { get; set; }

    /// <summary>Expiration time, if present.</summary>
    public DateTime? Expiration { get; set; }

    /// <summary>Effective time, if present.</summary>
    public DateTime? Effective { get; set; }

    /// <summary>Whether long-form (17-byte) timestamps were used.</summary>
    public bool IsLongForm { get; set; }
}

/// <summary>
/// Parsed symbolic link target from a Rock Ridge SL entry (RRIP §4.1.3).
/// </summary>
public sealed class RockRidgeSymlink
{
    /// <summary>The resolved symbolic link target path.</summary>
    public string Target { get; set; } = string.Empty;
}

/// <summary>
/// Parsed sparse file metadata from a Rock Ridge SF entry (RRIP §4.1.7).
/// </summary>
public sealed class RockRidgeSparseFile
{
    /// <summary>High 32 bits of the virtual file size.</summary>
    public uint VirtualSizeHigh { get; set; }

    /// <summary>Low 32 bits of the virtual file size.</summary>
    public uint VirtualSizeLow { get; set; }

    /// <summary>Sparse table depth (number of indirection levels).</summary>
    public byte TableDepth { get; set; }

    /// <summary>Gets the full 64-bit virtual file size.</summary>
    public long VirtualSize => ((long)VirtualSizeHigh << 32) | VirtualSizeLow;
}

/// <summary>
/// Aggregate of all parsed Rock Ridge / SUSP entries for a single directory record.
/// </summary>
public sealed class RockRidgeEntry
{
    /// <summary>Whether SUSP is active (SP entry found in root).</summary>
    public bool SuspActive { get; set; }

    /// <summary>Whether Rock Ridge extensions were identified (ER entry with RRIP_1991A).</summary>
    public bool RockRidgeIdentified { get; set; }

    /// <summary>RR flags byte (from RR entry), or null if no RR entry was present.</summary>
    public byte? RrFlags { get; set; }

    /// <summary>POSIX file attributes from the PX entry, or null if not present.</summary>
    public RockRidgePosixAttributes? PosixAttributes { get; set; }

    /// <summary>Alternate (POSIX) name from NM entries, or null if not present.</summary>
    public string? AlternateName { get; set; }

    /// <summary>Symbolic link target from SL entries, or null if not present.</summary>
    public RockRidgeSymlink? Symlink { get; set; }

    /// <summary>Timestamps from the TF entry, or null if not present.</summary>
    public RockRidgeTimestamps? Timestamps { get; set; }

    /// <summary>Device number from the PN entry, or null if not present.</summary>
    public RockRidgeDeviceNumber? DeviceNumber { get; set; }

    /// <summary>Child link LBA from the CL entry, or null if not a relocated child stub.</summary>
    public uint? ChildLink { get; set; }

    /// <summary>Parent link LBA from the PL entry, or null if not present.</summary>
    public uint? ParentLink { get; set; }

    /// <summary>Whether this directory is marked as relocated (RE entry present).</summary>
    public bool IsRelocated { get; set; }

    /// <summary>Sparse file metadata from the SF entry, or null if not present.</summary>
    public RockRidgeSparseFile? SparseFile { get; set; }

    /// <summary>LEN_SKP value from the SP entry (bytes to skip before SUSP data).</summary>
    public int SkipLength { get; set; }

    /// <summary>Extension identifiers found in ER entries.</summary>
    public List<string> ExtensionIdentifiers { get; set; } = new();

    /// <summary>
    /// Gets the effective file name — the alternate name if present, otherwise null.
    /// </summary>
    public string? EffectiveName => AlternateName;
}

/// <summary>
/// Reads and parses Rock Ridge Interchange Protocol (RRIP / IEEE P1282) extension records
/// and System Use Sharing Protocol (SUSP / IEEE 1281) entries from ISO 9660 directory records.
/// </summary>
/// <remarks>
/// This reader can parse SUSP/RRIP entries from raw ISO 9660 directory record System Use Areas,
/// including following Continuation Entries (CE) to continuation areas. It supports:
/// <list type="bullet">
///   <item>SUSP: SP (Sharing Protocol), CE (Continuation), ST (Terminator), PD (Padding), ER (Extensions Reference), ES (Extension Selector)</item>
///   <item>RRIP: PX (POSIX Attributes), PN (Device Number), SL (Symbolic Link), NM (Alternate Name),
///         CL (Child Link), PL (Parent Link), RE (Relocated), RR (Rock Ridge Indicator), TF (Timestamps)</item>
/// </list>

/// <summary>
/// Represents a single file or directory entry with full path and Rock Ridge metadata,
/// as returned by <see cref="RockRidgeReader.ReadAllEntries"/>.
/// </summary>
public sealed class RockRidgeFileEntry
{
    /// <summary>Full path from root (e.g., "/usr/bin/myapp").</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Original ISO 9660 file identifier.</summary>
    public string Iso9660Name { get; set; } = string.Empty;

    /// <summary>Whether this entry is a directory.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>Parsed Rock Ridge metadata.</summary>
    public RockRidgeEntry RockRidge { get; set; } = new();

    /// <summary>
    /// Gets the effective POSIX file name (Rock Ridge alternate name, or ISO 9660 name).
    /// </summary>
    public string EffectiveName => RockRidge.AlternateName ?? Iso9660Name;

    /// <summary>Gets the POSIX mode string if available (e.g., "drwxr-xr-x").</summary>
    public string? ModeString => RockRidge.PosixAttributes?.ModeString;

    /// <summary>Gets the symbolic link target if this entry is a symlink.</summary>
    public string? SymlinkTarget => RockRidge.Symlink?.Target;
}
