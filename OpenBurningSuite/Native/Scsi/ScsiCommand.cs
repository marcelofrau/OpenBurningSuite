// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>Describes a SCSI command to send to a device.</summary>
public sealed class ScsiCommand
{
    /// <summary>Command Descriptor Block (CDB) bytes.</summary>
    public byte[] Cdb { get; }

    /// <summary>Data transfer direction.</summary>
    public ScsiDataDirection Direction { get; }

    /// <summary>Buffer for data transfer (read or write).</summary>
    public byte[] DataBuffer { get; }

    /// <summary>Timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 30_000;

    public ScsiCommand(byte[] cdb, ScsiDataDirection direction, int dataLength = 0)
    {
        Cdb = cdb ?? throw new ArgumentNullException(nameof(cdb));
        Direction = direction;
        DataBuffer = dataLength > 0 ? new byte[dataLength] : Array.Empty<byte>();
    }

    public ScsiCommand(byte[] cdb, ScsiDataDirection direction, byte[] dataBuffer)
    {
        Cdb = cdb ?? throw new ArgumentNullException(nameof(cdb));
        Direction = direction;
        DataBuffer = dataBuffer ?? Array.Empty<byte>();
    }
}
