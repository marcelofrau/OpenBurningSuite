## 1. Models

- [x] 1.1 Criar `AtipInfo` model (DiscId, Manufacturer, LeadInStart, LeadOutLastPossible)
- [x] 1.2 Criar `PhysicalFormatInfo` model (BookType, PartVersion, DiscSize, LayerCount, TrackPath, LinearDensity, TrackDensity, FirstPhysicalSector, LastPhysicalSector, LastSectorLayer0)
- [x] 1.3 Criar `DiscInfoResult` model agregador com todas as seções (General, Atip, PhysicalFormat, Drive)
- [x] 1.4 Expandir `DiscMedia.cs` com novos campos: ManufacturerId, MediaId, ManufacturerName, IsErasable, FreeSectors, NextWritableAddress, SupportedReadSpeeds
- [x] 1.5 Criar `MediaManufacturerLookup` com Dictionary estático MID→Manufacturer (CD ATIP + DVD/BD)

## 2. SCSI / OpticalDrive

- [x] 2.1 Implementar `ReadAtipInfo()` em `OpticalDrive.cs` (já existia via `ReadAtip()`)
- [x] 2.2 Implementar `ReadDiscStructure()` existente + `ReadManufacturerId()` novo
- [x] 2.3 Implementar `ReadDiscInformationEx()` em `OpticalDrive.cs` (disc info estendido com sectors/time)
- [x] 2.4 Garantir que `ReadManufacturerId()` retorne MID para DVD (format 0x04) e BD (format 0x06)

## 3. DiscInfoService

- [x] 3.1 Criar `DiscInfoService` com método `GetDiscInfo(OpticalDrive drive, DiscDrive discDrive)` que orquestra todas as leituras
- [x] 3.2 Implementar parse do ATIP a partir do Read Disc Information + custom parsing
- [x] 3.3 Implementar parse do Physical Format Information byte array
- [x] 3.4 Implementar lógica de roteamento: CD→ATIP, DVD→PhysicalFormat + Manufacturer, BD→PhysicalFormat + MediaId
- [x] 3.5 Tratar falhas de SCSI silenciosamente (drives que não suportam certos comandos)

## 4. View / Code-Behind (adaptado — projeto não usa MVVM)

- [x] 4.1 Criar `DiscInfoView.axaml` com combo de drives + seções separadas (General, ATIP, Physical, Drive)
- [x] 4.2 `AutoSelectDriveWithMedia()` — percorre drives e seleciona primeiro com mídia
- [x] 4.3 `ProbeSelectedDriveAsync()` — async que chama `DiscInfoService` em `Task.Run()` e popula UI
- [x] 4.4 Loading indicator (PnlLoading) durante SCSI
- [x] 4.5 Seção "General Disc Information": Media Type, Status, Erasable, Sessions, Tracks, Free Space (bytes + MM:SS:FF), Next Writable Address, MID, Manufacturer, Supported Read Speeds, Current Read Speed
- [x] 4.6 Seção "ATIP Information" (visível só para CD): Disc ID, Manufacturer, LeadIn
- [x] 4.7 Seção "Physical Format Information" (visível só para DVD/BD): Book Type, Layers, Track Path, Sector info
- [x] 4.8 Seção "Drive Information": Vendor, Firmware, Serial, Buffer, Write/Read Speeds
- [x] 4.9 Log com timestamp e auto-truncation

## 5. Integração

- [x] 5.1 Adicionar sidebar button "Disc Info" no MainWindow.axaml
- [x] 5.2 Adicionar `<views:DiscInfoView>` no content panel do MainWindow.axaml
- [x] 5.3 Adicionar `OnDiscInfoClick` handler + `HideAllViews()` entry em MainWindow.axaml.cs
- [x] 5.4 Instanciar services no code-behind (`new DiscDiscoveryService()` + `new DiscInfoService()`)

## 6. MID Lookup Table

- [x] 6.1 Lookup de ATIP Manufacturers (TDK, Mitsubishi, RICOH, SONY, etc. — 26 entries)
- [x] 6.2 Lookup de DVD/BD MIDs (MCC, CMC, RICOHJPN, YUDEN, Prodisc, INFOME, UMEDISC, etc. — 130+ entries)
