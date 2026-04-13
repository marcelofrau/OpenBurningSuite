<div align="center">

# Running Open Burning Suite on Linux

Step-by-step guide for installing and running Open Burning Suite on Linux.

</div>

## Prerequisites

- A 64-bit Linux distribution (x64 or ARM64)
- An optical disc drive (for burning, reading, and verification operations)

## Required System Packages

Open Burning Suite uses [Avalonia UI](https://avaloniaui.net/) which requires a few system libraries for rendering.

### Ubuntu / Debian / Linux Mint

```bash
sudo apt install libicu-dev libfontconfig1 libx11-6 libice6 libsm6
```

### Fedora / CentOS Stream / RHEL / Rocky Linux / AlmaLinux / Oracle Linux

```bash
sudo dnf install libicu fontconfig libX11 libICE libSM
```

### Arch Linux

```bash
sudo pacman -S icu fontconfig libx11 libice libsm
```

### Alpine Linux

```bash
sudo apk add icu-libs fontconfig libx11 libice libsm
```

### openSUSE / SUSE Linux Enterprise Server (SLES)

```bash
sudo zypper install libicu-devel fontconfig libX11-6 libICE6 libSM6
```

## Optional: FFmpeg

[FFmpeg](https://ffmpeg.org/) is required for video authoring features (DVD-Video, Blu-ray, BDAV, Blu-ray 3D, VCD, SVCD). It is **not** needed for disc burning, reading, building data/audio images, or verification.

Install FFmpeg and ensure it is available on your system PATH:

```bash
# Ubuntu / Debian / Linux Mint
sudo apt install ffmpeg

# Fedora / CentOS Stream / RHEL
sudo dnf install ffmpeg

# Arch Linux
sudo pacman -S ffmpeg

# Alpine Linux
sudo apk add ffmpeg

# openSUSE / SLES
sudo zypper install ffmpeg
```

## Downloading a Release

Download the release that matches your architecture from the [Releases](https://github.com/SvenGDK/OpenBurningSuite/releases) page.

### Portable ZIPs

| File | Description |
|---|---|
| `linux-x64.zip` | Linux 64-bit (Intel/AMD) |
| `linux-arm64.zip` | Linux ARM64 |

### DEB Installers

| File | Description |
|---|---|
| `linux-x64-Installer.deb` | Linux 64-bit (Intel/AMD) |
| `linux-arm64-Installer.deb` | Linux ARM64 |

### RPM Packages

| File | Description |
|---|---|
| `linux-x64.rpm` | Linux 64-bit (Intel/AMD) |
| `linux-arm64.rpm` | Linux ARM64 |

### APK Packages (Alpine Linux)

| File | Description |
|---|---|
| `linux-x64.apk` | Linux 64-bit (Intel/AMD) |
| `linux-arm64.apk` | Linux ARM64 |

### Pacman Packages (Arch Linux)

| File | Description |
|---|---|
| `linux-x64.pkg.tar.zst` | Linux 64-bit (Intel/AMD) |
| `linux-arm64.pkg.tar.zst` | Linux ARM64 |

### AppImage

| File | Description |
|---|---|
| `linux-x64.AppImage` | Linux 64-bit (Intel/AMD) |
| `linux-arm64.AppImage` | Linux ARM64 |

### Snap

| File | Description |
|---|---|
| `linux-x64.snap` | Linux 64-bit (Intel/AMD) |
| `linux-arm64.snap` | Linux ARM64 |

### Flatpak

| File | Description |
|---|---|
| `linux-x64.flatpak` | Linux 64-bit (Intel/AMD) |
| `linux-arm64.flatpak` | Linux ARM64 |

## Installing from a Portable ZIP

1. Extract the downloaded ZIP:

```bash
unzip linux-x64.zip -d OpenBurningSuite
```

2. Navigate to the extracted directory:

```bash
cd OpenBurningSuite
```

3. Make the binary executable:

```bash
chmod +x OpenBurningSuite
```

4. Run the application:

```bash
./OpenBurningSuite
```

> **Note:** When using the portable ZIP, you may need to run with `sudo` for SCSI access to optical drives (see [SCSI Access & Permissions](#scsi-access--permissions)).

## Installing from a DEB Package

```bash
sudo dpkg -i linux-x64-Installer.deb
```

Then launch from the application menu or run:

```bash
openburningsuite
```

> The DEB package automatically sets the `CAP_SYS_RAWIO` capability on the binary and installs udev rules for optical drive access. See [Post-Install: What the Packages Configure](#post-install-what-the-packages-configure).

## Installing from an RPM Package

### Fedora / CentOS Stream / RHEL / Rocky Linux / AlmaLinux / Oracle Linux

```bash
sudo dnf install linux-x64.rpm
```

### openSUSE / SUSE Linux Enterprise Server (SLES)

```bash
sudo zypper install linux-x64.rpm
```

Then launch from the application menu or run:

```bash
openburningsuite
```

> The RPM package automatically sets the `CAP_SYS_RAWIO` capability and installs udev rules. See [Post-Install: What the Packages Configure](#post-install-what-the-packages-configure).

## Installing from an APK Package (Alpine Linux)

```bash
sudo apk add --allow-untrusted linux-x64.apk
```

Then launch from the application menu or run:

```bash
openburningsuite
```

> The APK package includes a post-install script that sets `CAP_SYS_RAWIO` and reloads udev rules. Alpine requires the `libcap` package for `setcap`.

## Installing from a Pacman Package (Arch Linux)

```bash
sudo pacman -U linux-x64.pkg.tar.zst
```

Then launch from the application menu or run:

```bash
openburningsuite
```

> The Pacman package installs an alpm hook that automatically sets `CAP_SYS_RAWIO` and reloads udev rules after install and upgrade. Arch Linux uses the `optical` group (not `cdrom`) for optical device access.

## Installing from an AppImage

1. Make the AppImage executable:

```bash
chmod +x linux-x64.AppImage
```

2. Run the AppImage:

```bash
./linux-x64.AppImage
```

No installation is required — AppImage bundles everything into a single file.

> **Note:** AppImages do not configure system capabilities or udev rules. You may need to run with `sudo` or configure permissions manually. See [SCSI Access & Permissions](#scsi-access--permissions).

## Installing from a Snap

```bash
sudo snap install --dangerous linux-x64.snap
```

Then launch from the application menu or run:

```bash
snap run openburningsuite
```

> The Snap package requests the `optical-drive`, `raw-usb`, `block-devices`, and `hardware-observe` plugs for SCSI passthrough access. You may need to connect these plugs manually after installation:
>
> ```bash
> sudo snap connect openburningsuite:optical-drive
> sudo snap connect openburningsuite:raw-usb
> sudo snap connect openburningsuite:block-devices
> sudo snap connect openburningsuite:hardware-observe
> ```

## Installing from a Flatpak

```bash
flatpak install --user linux-x64.flatpak
```

Then launch from the application menu or run:

```bash
flatpak run io.github.svengdk.OpenBurningSuite
```

> The Flatpak is configured with `devices=all` access for SCSI passthrough to optical drives.

## SCSI Access & Permissions

Open Burning Suite communicates directly with optical drives using native **SCSI/MMC commands** via the Linux `SG_IO` ioctl interface. This requires appropriate permissions on `/dev/sr*` (optical block devices) and `/dev/sg*` (SCSI generic devices).

### Option 1: Run with `sudo`

The simplest approach for disc operations:

```bash
sudo openburningsuite
# Or for portable/AppImage:
sudo ./OpenBurningSuite
```

### Option 2: User Group Membership

Add your user to the `cdrom` and/or `optical` groups (depending on your distribution):

```bash
# Ubuntu / Debian / Fedora / Alpine / openSUSE
sudo usermod -aG cdrom $USER

# Arch Linux (uses 'optical' group)
sudo usermod -aG optical $USER
```

Log out and back in for the changes to take effect.

### Option 3: Linux Capabilities (Recommended for Package Installs)

The DEB, RPM, APK, and Pacman packages automatically grant the `CAP_SYS_RAWIO` capability to the binary, allowing SCSI passthrough without root. If you need to set this manually:

```bash
sudo setcap cap_sys_rawio+ep /usr/lib/openburningsuite/OpenBurningSuite
```

### Ensure the SCSI Generic Module is Loaded

Some distributions may not load the `sg` kernel module by default:

```bash
sudo modprobe sg
```

To load it automatically at boot, add it to `/etc/modules` or create a file in `/etc/modules-load.d/`:

```bash
echo "sg" | sudo tee /etc/modules-load.d/sg.conf
```

## Post-Install: What the Packages Configure

The DEB, RPM, APK, and Pacman packages perform the following post-install steps:

1. **`CAP_SYS_RAWIO` capability** — Grants the binary permission to send SCSI commands (SG_IO ioctl) to optical drives without requiring root.

2. **Udev rules** (`/etc/udev/rules.d/99-openburningsuite.rules`) — Ensures `/dev/sr*` and `/dev/sg*` devices are accessible to the `cdrom` group (or `optical` group on Arch Linux) with mode `0660`:

   ```
   SUBSYSTEM=="block", KERNEL=="sr[0-9]*", MODE="0660", GROUP="cdrom"
   SUBSYSTEM=="scsi_generic", KERNEL=="sg[0-9]*", MODE="0660", GROUP="cdrom"
   ```

3. **Udev reload** — Triggers a udev rule reload so the new permissions take effect immediately.

> **Note:** Portable ZIP and AppImage distributions do not include these configurations. You will need to set up permissions manually or run with `sudo`.

## Troubleshooting

### Application does not start

Make sure all required system packages are installed (see [Required System Packages](#required-system-packages)).

### "No drives detected"

- Ensure your optical drive is properly connected and recognized by the OS.
- Check that device nodes exist: `ls -la /dev/sr* /dev/sg*`
- Verify the `sg` module is loaded: `lsmod | grep sg`
- If not loaded: `sudo modprobe sg`

### "Permission denied" when accessing drive

- Run with `sudo`, or ensure your user is in the `cdrom`/`optical` group, or verify that `CAP_SYS_RAWIO` is set on the binary.
- Check device permissions: `ls -la /dev/sr0 /dev/sg*`
- For Snap installs, ensure the required plugs are connected (see [Installing from a Snap](#installing-from-a-snap)).

### Missing libSkiaSharp

If you see an error about `libSkiaSharp`, install the OpenGL library for your distribution:

```bash
# Ubuntu / Debian / Linux Mint
sudo apt install libgl1-mesa-glx

# Fedora / CentOS Stream / RHEL / Rocky Linux / AlmaLinux / Oracle Linux
sudo dnf install mesa-libGL

# Arch Linux
sudo pacman -S mesa

# Alpine Linux
sudo apk add mesa-gl

# openSUSE / SLES
sudo zypper install Mesa-libGL1
```

### Fonts look incorrect

Install a common font package:

```bash
# Ubuntu / Debian / Linux Mint
sudo apt install fonts-liberation

# Fedora / CentOS Stream / RHEL / Rocky Linux / AlmaLinux / Oracle Linux
sudo dnf install liberation-fonts

# Arch Linux
sudo pacman -S ttf-liberation

# Alpine Linux
sudo apk add font-liberation

# openSUSE / SLES
sudo zypper install liberation-fonts
```
