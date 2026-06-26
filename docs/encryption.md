---
layout: default
title: Disc Image Encryption
permalink: /encryption
---

# Disc Image Encryption

Open Burning Suite supports AES-256-CBC encryption and decryption for disc image files, allowing you to protect sensitive backups with a password.

---

## OBS Encrypted Format (.obse)

Encrypted images use the `.obse` (Open Burning Suite Encrypted) extension with the following header structure:

| Offset | Size | Field |
|:-------|:-----|:------|
| 0 | 4 bytes | Magic: `OBSE` |
| 4 | 2 bytes | Format version (`0x0001`) |
| 6 | 2 bytes | KDF iteration exponent (iterations = 2^value, min 65536) |
| 8 | 32 bytes | PBKDF2 salt |
| 40 | 16 bytes | AES-256-CBC initialization vector (IV) |
| 56 | 32 bytes | HMAC-SHA256 of encrypted payload |
| 88 | 8 bytes | Original file size (little-endian uint64) |
| 96 | N bytes | AES-256-CBC encrypted payload (PKCS7 padded) |

Total header: **96 bytes** before encrypted payload.

**Security features:**
- AES-256-CBC symmetric encryption
- PBKDF2 key derivation (RFC 8018) with configurable iteration count
- HMAC-SHA256 integrity verification
- Unique random salt and IV per encryption
- Cross-platform (uses only .NET built-in `System.Security.Cryptography`)

---

## Encrypting an Image

To encrypt a disc image before burning:

1. Go to **Burn / Write** view
2. Select your source image file
3. Enable the encryption option in the burn panel
4. Enter and confirm a strong password
5. The application creates an `.obse` file with the encrypted image
6. Proceed to burn the encrypted image to disc

> **Note:** Encrypting increases burn time due to the encryption pass. The original image is not modified.

---

## Decrypting an Image

When you select an `.obse` file for burning:

1. The application detects the encrypted format automatically
2. You are prompted to enter the decryption password
3. The image is decrypted in memory and burned to disc
4. The original `.obse` file remains unchanged

### PS3 Image Decryption

PlayStation 3 ISO images can be decrypted before burning using one of three key sources:

| Key Source | File Extension | Description |
|:-----------|:---------------|:------------|
| **IRD file** | `.ird` | Official IRD file containing the disc key (versions 6–9 supported) |
| **Disc key file** | `.dkey` | Plaintext disc key file |
| **Hex disc key** | — | 32-character hex string pasted directly |

The decryption panel appears automatically when a PS3 ISO is detected. The service uses AES-128-CBC with per-sector IV for sector-level decryption.

**PS3 Encryption Reference:**
- Sector size: 2048 bytes
- AES-128-CBC with per-sector IV
- IV = 16-byte array with sector number in last 8 bytes (big-endian)
- Odd-numbered regions: encrypted
- Even-numbered regions: unencrypted
- Region map: stored in sector 0 of the ISO
- Disc key derived from IRD Data1 field using AES-128-CBC with fixed key/IV

---

**Next:** [Settings →]({{ '/settings' | relative_url }})
