## Why

O Open Burning Suite não expõe informações detalhadas da mídia e do drive óptico — dados como MID real do fabricante, ATIP (CD), Physical Format Information (DVD/BD), Supported Read Speeds, e book type. Ferramentas como ImgBurn e Nero oferecem um painel "Disc Info" completo que é essencial para diagnosticar mídias, escolher velocidades adequadas e entender o que o drive e o disco suportam.

## What Changes

- Nova View "Disc Info" dedicada com abas/seções organizadas por tipo de informação
- Model `DiscMedia` expandido com campos de manufacturer, MID, layers, write speeds da mídia, read speeds
- Suporte a multi-drive: combobox para selecionar drive + auto-detecção do drive com mídia
- Leitura e exibição de:

  **Geral (todas as mídias)**
  - Current Profile / Media Type
  - Disc Status (Empty, Incomplete, Closed)
  - Erasable?
  - Sessions / Tracks
  - Free Sectors / Free Space (bytes + MM:SS:FF)
  - Next Writable Address
  - MID + Manufacturer Name
  - Supported Read Speeds / Current Read Speed
  - Supported Write Speeds (já existe, reposicionar)

  **CD / CD-R / CD-RW**
  - ATIP Information: Disc ID, Manufacturer, Start Time of LeadIn, Last Possible Start Time of LeadOut

  **DVD / DVD±R / DVD±RW / DVD±R DL**
  - Pre-recorded Information (Manufacturer ID)
  - Recording Management Area Information
  - Physical Format Information (ADIP L0/L1 + Last Recorded):
    Book Type, Part Version, Disc Size, Number of Layers, Track Path (PTP/OTP),
    Linear Density, Track Density, First/Last Physical Sector of Data Area,
    Last Physical Sector in Layer 0

  **Drive Info (sempre presente)**
  - Vendor, Model, Firmware, Serial, Buffer Size (já existe, consolidar)
  - Supported Write Speeds, Supported Read Speeds

- `DiscContentDetectionService` estendido com serviço dedicado para extrair todos esses dados via SCSI (Read Disc Structure, Read Disc Information, Read Track Information, ATIP via Read Disc Information + custom parsing)

## Capabilities

### New Capabilities
- `disc-info`: Painel dedicado com informações detalhadas do disco e do drive óptico, suporte a múltiplos drives, auto-detecção do drive com mídia

### Modified Capabilities
- (nenhuma spec existente — primeira spec do projeto)

## Impact

- **Models/DiscMedia.cs**: adicionar ManufacturerId, MediaId, ManufacturerName, LayerCount, TrackPath, BookType, SupportedReadSpeeds, SupportedWriteSpeeds (media), FreeSectors, NextWritableAddress, DiscTimeFormatted, IsErasable, AtipInfo
- **Models/DiscDrive.cs**: nenhuma mudança (já tem os campos necessários)
- **Models/**: novo modelo `AtipInfo`, novo modelo `PhysicalFormatInfo`, novo modelo `DiscInfoResult` (agregador)
- **Services/**: novo `DiscInfoService` ou estender `DiscContentDetectionService`
- **Views/**: nova `DiscInfoView.axaml` + `DiscInfoView.axaml.cs`
- **ViewModels/**: novo `DiscInfoViewModel`
- **Helpers/TabHelper.cs** ou similar: adicionar aba "Disc Info" no MainWindow ou navegação dedicada
- **Native/OpticalDrive.cs**: expor métodos `ReadAtipInfo()`, `ReadPhysicalFormatInfo()`, `ReadDiscInformationEx()`
