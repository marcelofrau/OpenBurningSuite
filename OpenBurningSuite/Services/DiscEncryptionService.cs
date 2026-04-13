// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenBurningSuite.Services;

/// <summary>
/// Provides AES-256-CBC encryption and decryption for disc image files.
///
/// Protects sensitive backups on optical media by encrypting the entire
/// disc image with a user-supplied password. Uses PBKDF2 (RFC 8018) for
/// key derivation from passwords and AES-256-CBC for symmetric encryption.
///
/// File format (OBS Encrypted Image):
///   - 4 bytes: magic "OBSE" (Open Burning Suite Encrypted)
///   - 2 bytes: format version (0x0001)
///   - 2 bytes: KDF iteration exponent (iterations = 2^value, min 2^16 = 65536)
///   - 32 bytes: salt for PBKDF2 key derivation
///   - 16 bytes: AES-256-CBC initialization vector (IV)
///   - 32 bytes: HMAC-SHA256 of the encrypted payload (for integrity verification)
///   - 8 bytes: original file size (little-endian uint64)
///   - N bytes: AES-256-CBC encrypted payload (PKCS7 padded)
///
/// Header total: 96 bytes before the encrypted payload.
///
/// Cross-platform compatible: works on Windows, Linux, and macOS.
/// Uses only .NET built-in cryptography (System.Security.Cryptography).
/// </summary>
public static class DiscEncryptionService
{
    /// <summary>Magic bytes identifying an OBS encrypted image file.</summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OBSE");

    /// <summary>Current format version.</summary>
    private const ushort FormatVersion = 1;

    /// <summary>Default PBKDF2 iteration count: 2^18 = 262144.</summary>
    private const int DefaultIterationExponent = 18;

    /// <summary>Minimum allowed iteration exponent (2^16 = 65536 iterations).</summary>
    private const int MinIterationExponent = 16;

    /// <summary>Maximum allowed iteration exponent (2^30 ≈ 1 billion iterations).</summary>
    private const int MaxIterationExponent = 30;

    /// <summary>Salt size in bytes (256 bits).</summary>
    private const int SaltSize = 32;

    /// <summary>AES key size in bytes (256 bits).</summary>
    private const int KeySize = 32;

    /// <summary>AES IV size in bytes (128 bits).</summary>
    private const int IvSize = 16;

    /// <summary>HMAC-SHA256 size in bytes.</summary>
    private const int HmacSize = 32;

    /// <summary>Total header size before encrypted data.</summary>
    private const int HeaderSize = 4 + 2 + 2 + SaltSize + IvSize + HmacSize + 8; // = 96 bytes

    /// <summary>Buffer size for streaming encryption/decryption (1 MB).</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>Progress reporting for encryption/decryption operations.</summary>
    public class EncryptionProgress
    {
        /// <summary>Percentage complete (0-100).</summary>
        public int PercentComplete { get; set; }

        /// <summary>Bytes processed so far.</summary>
        public long BytesProcessed { get; set; }

        /// <summary>Total bytes to process.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Current operation status message.</summary>
        public string StatusMessage { get; set; } = string.Empty;
    }

    // -----------------------------------------------------------------------
    //  Public API: Encrypt
    // -----------------------------------------------------------------------

    /// <summary>
    /// Encrypts a disc image file with a password.
    /// </summary>
    /// <param name="inputPath">Path to the source disc image file (ISO, BIN, IMG, NRG, etc.).</param>
    /// <param name="outputPath">Path for the encrypted output file (.obse extension recommended).</param>
    /// <param name="password">User-supplied encryption password.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Null on success, error message on failure.</returns>
    public static async Task<string?> EncryptAsync(
        string inputPath, string outputPath, string password,
        IProgress<EncryptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() => Encrypt(inputPath, outputPath, password, progress, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Encrypts a disc image file with a password (synchronous).
    /// </summary>
    public static string? Encrypt(
        string inputPath, string outputPath, string password,
        IProgress<EncryptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            ValidateInputs(inputPath, password);

            long inputSize = new FileInfo(inputPath).Length;
            int iterations = 1 << DefaultIterationExponent;

            // Generate cryptographic random salt and IV
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] iv = RandomNumberGenerator.GetBytes(IvSize);

            // Derive encryption key and HMAC key from password
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] keyMaterial = DeriveKeyMaterial(passwordBytes, salt, iterations, KeySize + KeySize);
            byte[] encryptionKey = keyMaterial[..KeySize];
            byte[] hmacKey = keyMaterial[KeySize..];

            using var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, false);
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, BufferSize, false);

            // Write header placeholder (HMAC will be filled later)
            WriteHeader(outputStream, salt, iv, inputSize);

            // Encrypt the data
            progress?.Report(new EncryptionProgress
            {
                StatusMessage = "Encrypting disc image...",
                TotalBytes = inputSize
            });

            using var aes = CreateAes(encryptionKey, iv);
            using var encryptor = aes.CreateEncryptor();
            long encryptedDataStart = outputStream.Position;

            using (var cryptoStream = new CryptoStream(outputStream, encryptor, CryptoStreamMode.Write, true))
            {
                StreamCopyWithProgress(inputStream, cryptoStream, inputSize, progress, ct);
            }

            // Compute HMAC over the encrypted payload
            outputStream.Seek(encryptedDataStart, SeekOrigin.Begin);
            byte[] hmac = ComputeHmac(outputStream, hmacKey);

            // Write HMAC into header
            outputStream.Seek(4 + 2 + 2 + SaltSize + IvSize, SeekOrigin.Begin);
            outputStream.Write(hmac, 0, HmacSize);

            progress?.Report(new EncryptionProgress
            {
                PercentComplete = 100,
                BytesProcessed = inputSize,
                TotalBytes = inputSize,
                StatusMessage = "Encryption complete."
            });

            return null; // Success
        }
        catch (OperationCanceledException)
        {
            CleanupFile(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            CleanupFile(outputPath);
            return $"Encryption failed: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    //  Public API: Decrypt
    // -----------------------------------------------------------------------

    /// <summary>
    /// Decrypts an OBS-encrypted disc image file with a password.
    /// </summary>
    /// <param name="inputPath">Path to the encrypted file (.obse).</param>
    /// <param name="outputPath">Path for the decrypted output file.</param>
    /// <param name="password">User-supplied decryption password.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Null on success, error message on failure.</returns>
    public static async Task<string?> DecryptAsync(
        string inputPath, string outputPath, string password,
        IProgress<EncryptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() => Decrypt(inputPath, outputPath, password, progress, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Decrypts an OBS-encrypted disc image file (synchronous).
    /// </summary>
    public static string? Decrypt(
        string inputPath, string outputPath, string password,
        IProgress<EncryptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            ValidateInputs(inputPath, password);

            using var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, false);

            if (inputStream.Length < HeaderSize)
                return "File is too small to be a valid OBS encrypted image.";

            // Read and validate header
            var (salt, iv, storedHmac, originalSize, iterExp, error) = ReadHeader(inputStream);
            if (error != null) return error;

            int iterations = 1 << iterExp;

            // Derive keys
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] keyMaterial = DeriveKeyMaterial(passwordBytes, salt!, iterations, KeySize + KeySize);
            byte[] encryptionKey = keyMaterial[..KeySize];
            byte[] hmacKey = keyMaterial[KeySize..];

            // Verify HMAC
            progress?.Report(new EncryptionProgress
            {
                StatusMessage = "Verifying integrity...",
                TotalBytes = originalSize
            });

            long encryptedDataStart = inputStream.Position;
            byte[] computedHmac = ComputeHmac(inputStream, hmacKey);

            if (!CryptographicOperations.FixedTimeEquals(storedHmac, computedHmac))
                return "Decryption failed: incorrect password or corrupted file (HMAC mismatch).";

            // Decrypt the data
            progress?.Report(new EncryptionProgress
            {
                StatusMessage = "Decrypting disc image...",
                TotalBytes = originalSize
            });

            inputStream.Seek(encryptedDataStart, SeekOrigin.Begin);

            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
                FileShare.None, BufferSize, false);

            using var aes = CreateAes(encryptionKey, iv!);
            using var decryptor = aes.CreateDecryptor();
            using var cryptoStream = new CryptoStream(inputStream, decryptor, CryptoStreamMode.Read, true);

            StreamCopyWithProgress(cryptoStream, outputStream, originalSize, progress, ct);

            // Truncate to original size (remove PKCS7 padding overshoot)
            if (outputStream.Length > originalSize)
                outputStream.SetLength(originalSize);

            progress?.Report(new EncryptionProgress
            {
                PercentComplete = 100,
                BytesProcessed = originalSize,
                TotalBytes = originalSize,
                StatusMessage = "Decryption complete."
            });

            return null; // Success
        }
        catch (OperationCanceledException)
        {
            CleanupFile(outputPath);
            throw;
        }
        catch (CryptographicException)
        {
            CleanupFile(outputPath);
            return "Decryption failed: incorrect password or corrupted file.";
        }
        catch (Exception ex)
        {
            CleanupFile(outputPath);
            return $"Decryption failed: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    //  Public API: Verification and utilities
    // -----------------------------------------------------------------------

    /// <summary>
    /// Checks if a file is an OBS encrypted image by examining the magic bytes.
    /// </summary>
    public static bool IsEncryptedImage(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            if (new FileInfo(filePath).Length < HeaderSize) return false;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128, false);
            var magic = new byte[4];
            stream.ReadExactly(magic, 0, 4);
            return magic[0] == Magic[0] && magic[1] == Magic[1] &&
                   magic[2] == Magic[2] && magic[3] == Magic[3];
        }
        catch { return false; }
    }

    /// <summary>
    /// Reads metadata from an encrypted image without decrypting it.
    /// </summary>
    /// <returns>Original file size, or -1 on error.</returns>
    public static long GetOriginalSize(string filePath)
    {
        try
        {
            if (!IsEncryptedImage(filePath)) return -1;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128, false);
            var header = new byte[HeaderSize];
            stream.ReadExactly(header, 0, HeaderSize);

            long size = BitConverter.ToInt64(header, 4 + 2 + 2 + SaltSize + IvSize + HmacSize);
            return size > 0 ? size : -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Verifies the integrity of an encrypted image using the stored HMAC.
    /// Returns null if valid, error message if not.
    /// </summary>
    public static string? VerifyIntegrity(string filePath, string password)
    {
        try
        {
            if (!File.Exists(filePath))
                return "File not found.";

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, false);

            if (stream.Length < HeaderSize)
                return "File too small for OBS encrypted format.";

            var (salt, _, storedHmac, _, iterExp, error) = ReadHeader(stream);
            if (error != null) return error;

            int iterations = 1 << iterExp;
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] keyMaterial = DeriveKeyMaterial(passwordBytes, salt!, iterations, KeySize + KeySize);
            byte[] hmacKey = keyMaterial[KeySize..];

            byte[] computedHmac = ComputeHmac(stream, hmacKey);

            if (!CryptographicOperations.FixedTimeEquals(storedHmac, computedHmac))
                return "Integrity check failed: incorrect password or file is corrupted.";

            return null; // Valid
        }
        catch (Exception ex)
        {
            return $"Verification error: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the recommended encrypted file extension.
    /// </summary>
    public static string GetEncryptedExtension(string originalPath)
    {
        return Path.GetExtension(originalPath).ToLowerInvariant() + ".obse";
    }

    // -----------------------------------------------------------------------
    //  Internal: Header read/write
    // -----------------------------------------------------------------------

    private static void WriteHeader(Stream output, byte[] salt, byte[] iv, long originalSize)
    {
        output.Write(Magic, 0, 4);

        // Version (LE)
        output.WriteByte((byte)(FormatVersion & 0xFF));
        output.WriteByte((byte)((FormatVersion >> 8) & 0xFF));

        // Iteration exponent (LE)
        output.WriteByte((byte)(DefaultIterationExponent & 0xFF));
        output.WriteByte((byte)((DefaultIterationExponent >> 8) & 0xFF));

        // Salt
        output.Write(salt, 0, SaltSize);

        // IV
        output.Write(iv, 0, IvSize);

        // HMAC placeholder (will be overwritten after encryption)
        output.Write(new byte[HmacSize], 0, HmacSize);

        // Original file size (LE)
        var sizeBytes = BitConverter.GetBytes(originalSize);
        output.Write(sizeBytes, 0, 8);
    }

    private static (byte[]? salt, byte[]? iv, byte[]? hmac, long originalSize, int iterationExponent, string? error) ReadHeader(
        Stream input)
    {
        var header = new byte[HeaderSize];
        int read = 0;
        while (read < HeaderSize)
        {
            int r = input.Read(header, read, HeaderSize - read);
            if (r <= 0) return (null, null, null, 0, 0, "Could not read header.");
            read += r;
        }

        // Validate magic
        if (header[0] != Magic[0] || header[1] != Magic[1] ||
            header[2] != Magic[2] || header[3] != Magic[3])
            return (null, null, null, 0, 0, "Not an OBS encrypted file (invalid magic).");

        // Version
        ushort version = (ushort)(header[4] | (header[5] << 8));
        if (version > FormatVersion)
            return (null, null, null, 0, 0, $"Unsupported format version: {version}");

        // Iteration exponent — use stored value for correct key derivation
        ushort iterExp = (ushort)(header[6] | (header[7] << 8));
        // Clamp to a safe range to prevent excessive computation or weak KDF
        if (iterExp < MinIterationExponent) iterExp = MinIterationExponent;
        if (iterExp > MaxIterationExponent) iterExp = MaxIterationExponent;

        int offset = 8;
        byte[] salt = new byte[SaltSize];
        Buffer.BlockCopy(header, offset, salt, 0, SaltSize);
        offset += SaltSize;

        byte[] iv = new byte[IvSize];
        Buffer.BlockCopy(header, offset, iv, 0, IvSize);
        offset += IvSize;

        byte[] hmac = new byte[HmacSize];
        Buffer.BlockCopy(header, offset, hmac, 0, HmacSize);
        offset += HmacSize;

        long originalSize = BitConverter.ToInt64(header, offset);

        // Validate that originalSize is a reasonable positive value.
        // A corrupted header could produce a negative or zero value which would
        // cause downstream issues (infinite loops, SetLength failures, etc.).
        if (originalSize <= 0)
            return (null, null, null, 0, 0, "Invalid original file size in header (corrupted file?).");

        return (salt, iv, hmac, originalSize, iterExp, null);
    }

    // -----------------------------------------------------------------------
    //  Internal: Cryptographic operations
    // -----------------------------------------------------------------------

    private static byte[] DeriveKeyMaterial(byte[] password, byte[] salt, int iterations, int outputLength)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(outputLength);
    }

    private static Aes CreateAes(byte[] key, byte[] iv)
    {
        var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        return aes;
    }

    private static byte[] ComputeHmac(Stream dataStream, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = dataStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hmac.TransformBlock(buffer, 0, read, null, 0);
        }
        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hmac.Hash!;
    }

    private static void StreamCopyWithProgress(Stream source, Stream destination,
        long totalBytes, IProgress<EncryptionProgress>? progress, CancellationToken ct)
    {
        if (totalBytes <= 0) return;

        var buffer = new byte[BufferSize];
        long bytesProcessed = 0;

        while (bytesProcessed < totalBytes)
        {
            ct.ThrowIfCancellationRequested();

            int toRead = (int)Math.Min(buffer.Length, totalBytes - bytesProcessed);
            int read = source.Read(buffer, 0, toRead);
            if (read <= 0) break;

            destination.Write(buffer, 0, read);
            bytesProcessed += read;

            if (progress != null && bytesProcessed % (BufferSize * 10) < BufferSize)
            {
                int pct = totalBytes > 0 ? (int)(bytesProcessed * 100 / totalBytes) : 0;
                progress.Report(new EncryptionProgress
                {
                    PercentComplete = pct,
                    BytesProcessed = bytesProcessed,
                    TotalBytes = totalBytes,
                    StatusMessage = pct < 100 ? $"Processing: {pct}%..." : "Finalizing..."
                });
            }
        }
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static void ValidateInputs(string inputPath, string password)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Input file path is required.", nameof(inputPath));
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input file not found: {inputPath}");
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required.", nameof(password));
    }

    private static void CleanupFile(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignore cleanup errors */ }
    }
}
