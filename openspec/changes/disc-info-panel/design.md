## Context

O Open Burning Suite já possui um `DiscoverView` que mostra informações básicas do drive e mídia, mas de forma espalhada e incompleta. O `DiscDrive` model já tem `VendorId`, `FirmwareRevision`, `SerialNumber`, `BufferSizeKiB`, `SupportedWriteSpeeds`, `SupportedReadSpeeds`, e `CurrentMedia` (`DiscMedia`). O `OpticalDrive` já implementa `ReadDiscStructure` com vários formatos (0x00 Physical, 0x04 Manufacturer, 0x06 BD Media ID). O `TxtMediaId` existe no AXAML mas nunca é populado com o MID real.

A feature de Disc Info vai extrair, organizar e exibir todos esses dados num painel dedicado.

## Goals / Non-Goals

**Goals:**
- Nova View `DiscInfoView` com layout limpo e seções organizadas por tipo de mídia
- Modelos novos: `DiscInfoResult`, `PhysicalFormatInfo`, `AtipInfo`, `MediaManufacturerLookup`
- `DiscMedia` expandido com campos novos (ManufacturerId, MediaId, etc.)
- `DiscInfoService` com métodos SCSI dedicados (ATIP, Physical Format, Read Disc Info estendido)
- MID-to-Manufacturer lookup table estática
- Multi-drive via combobox com auto-detecção do drive com mídia
- Reaproveitar `OpticalDrive` e `NativeDeviceDiscovery` existentes

**Non-Goals:**
- Real-time buffer monitoring durante burn (fica pra outra etapa)
- Layer break picker gráfico (fora de escopo)
- Verificação de ISO vs mídia (mencionado como expansão futura, não agora)
- Edição de informações da mídia

## Decisions

### 1. View separada vs expandir DiscoverView

**Decisão:** Nova View `DiscInfoView` + `DiscInfoViewModel`.

Alternativa considerada: adicionar seções no DiscoverView. Rejeitada porque:
- DiscoverView já tem 400+ linhas de AXAML e 679 de code-behind
- Disc Info tem informações demais pra um card no DiscoverView
- Uma view dedicada permite layout mais flexível e futuras expansões

Navegação: adicionar aba no TabControl existente do MainWindow, similar às abas "Write", "Read", "Discover".

### 2. Serviço dedicado vs estender OpticalDrive

**Decisão:** Novo `DiscInfoService` que encapsula as chamadas SCSI de alto nível.

```csharp
public class DiscInfoService
{
    public DiscInfoResult GetDiscInfo(OpticalDrive drive);
    public AtipInfo? ReadAtipInfo(OpticalDrive drive);
    public PhysicalFormatInfo? ReadPhysicalFormatInfo(OpticalDrive drive, byte layer);
    public string ReadManufacturerId(OpticalDrive drive);
    public DiscInfoExtended ReadDiscInfoExtended(OpticalDrive drive);
}
```

`OpticalDrive.cs` ganha métodos públicos `ReadAtipInfo()` e `ReadPhysicalFormatInfo()` que chamam `ReadDiscStructure` com os formatos apropriados. O `DiscInfoService` orquestra e monta o `DiscInfoResult`.

### 3. Modelos novos

```
DiscInfoResult (agregador)
├── DriveInfo: Vendorname, Firmware, Serial, Buffer, WriteSpeeds[], ReadSpeeds[]
├── MediaType: string
├── DiscStatus: string (Empty/Incomplete/Appendable/Closed)
├── IsErasable: bool
├── Sessions: int / Tracks: int
├── FreeSectors: long / FreeBytes: long / FreeTime: string (MM:SS:FF)
├── NextWritableAddress: long
├── Mid: string / ManufacturerName: string
├── SupportedReadSpeeds: string[] / CurrentReadSpeed: string
├── AtipInfo: AtipInfo? (só para CD)
└── PhysicalFormatInfo: PhysicalFormatInfo? (só para DVD/BD)

AtipInfo
├── DiscId: string
├── Manufacturer: string
├── LeadInStart: string
└── LeadOutLastPossible: string

PhysicalFormatInfo
├── BookType: string
├── PartVersion: int
├── DiscSize: string
├── LayerCount: int
├── TrackPath: string (PTP/OTP)
├── LinearDensity: string
├── TrackDensity: string
├── FirstPhysicalSector: long
├── LastPhysicalSector: long
└── LastSectorLayer0: long
```

### 4. MID lookup table

`Dictionary<string, string>` estático em `MediaManufacturerLookup.cs`. Populado com os ~50 MIDs mais comuns:
- MCC 00x → Mitsubishi
- CMC MAG → CMC Magnetics
- RICOHJPN → Ricoh
- SONY → Sony
- TDK → TDK
- YUDEN → Taiyo Yuden
- Prodisc → Prodisc
- INFOME → Infomedia
- MBIP... → Moser Baer
- UMEDISC → UME Disc
- ETC.

Para CDs, o MID vem do ATIP (formato `97m15s05f`) e tem lookup separado.

### 5. Multi-drive

Reaproveitar `DeviceManager.GetOpticalDrives()` já existente. O `DiscInfoViewModel` expõe `ObservableCollection<DiscDrive>` e um `SelectedDrive` bindado ao combobox. `AutoSelectDriveWithMedia()` itera pelos drives checando `drive.CurrentMedia != null`.

### 6. Disc time formatting (MM:SS:FF)

Método helper em `FormatHelper`: `SectorsToMsf(long sectors)` → string `"MM:SS:FF"`. Padrão CD: 75 frames/second, 60 seconds/minute.

## Risks / Trade-offs

- [SCSI commands variam por drive] → Tratar silenciosamente falhas de `ReadDiscStructure` (alguns drives não implementam formatos específicos). Mostrar "Not available" em vez de quebrar.
- [MID lookup desatualizada] → A tabela cobre os MIDs mais comuns mas nunca será completa. Mostrar o código bruto como fallback é aceitável.
- [CD ATIP] → A leitura de ATIP varia entre fabricantes de drive. Alguns drives não retornam ATIP via SCSI padrão. Fallback para "Not available".
- [Performance] → `ReadDiscStructure` pode levar alguns segundos por chamada. Executar em background (async) com indicador de loading na view.
