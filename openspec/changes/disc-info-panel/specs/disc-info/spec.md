## ADDED Requirements

### Requirement: Multi-drive selection with auto-detect

The Disc Info view SHALL provide a combobox listing all available optical drives detected by the system. When the view loads, the system SHALL auto-select the drive that already contains media (disc present). If no drive has media, the system SHALL select the first available drive.

#### Scenario: Auto-select drive with media
- **GIVEN** the system has 2 optical drives: D: (empty) and E: (with disc)
- **WHEN** the Disc Info view opens
- **THEN** drive E: SHALL be auto-selected

#### Scenario: No media in any drive
- **GIVEN** no optical drive contains media
- **WHEN** the Disc Info view opens
- **THEN** the first available drive SHALL be selected

#### Scenario: User switches drive
- **GIVEN** a drive is selected
- **WHEN** the user picks a different drive from the combobox
- **THEN** all Disc Info panels SHALL refresh for the newly selected drive

---

### Requirement: Display general disc information

The system SHALL read and display the following disc properties for any inserted optical disc: Media Type (Current Profile), Disc Status (Empty/Incomplete/Appendable/Closed), Erasable flag, Sessions count, Tracks count, Free Sectors, Free Space in bytes and MM:SS:FF format, Next Writable Address, MID (Media ID code), Manufacturer Name, Supported Read Speeds, and Current Read Speed.

#### Scenario: Display general info for blank DVD+R
- **GIVEN** a blank DVD+R is inserted in the selected drive
- **WHEN** the Disc Info view refreshes
- **THEN** the system SHALL show Media Type "DVD+R", Disc Status "Empty", Free Sectors, Free Space, MID, and Supported Read Speeds

#### Scenario: Display general info for written CD-R
- **GIVEN** a written CD-R is inserted
- **WHEN** the Disc Info view refreshes
- **THEN** the system SHALL show Media Type "CD-R", Disc Status, Sessions, Tracks, Free/Used space, and MID with Manufacturer

---

### Requirement: Display ATIP information for CD media

When a CD, CD-R, or CD-RW disc is inserted, the system SHALL read and display ATIP (Absolute Time In Pregroove) information including: Disc ID, Manufacturer, Start Time of LeadIn, and Last Possible Start Time of LeadOut.

#### Scenario: Display ATIP for CD-R
- **GIVEN** a CD-R disc is inserted
- **WHEN** the Disc Info view refreshes
- **THEN** the ATIP Information section SHALL show Disc ID, Manufacturer, LeadIn start time, and LeadOut last possible start time

#### Scenario: No ATIP for DVD
- **GIVEN** a DVD disc is inserted
- **WHEN** the Disc Info view refreshes
- **THEN** the ATIP Information section SHALL be hidden or show "Not applicable"

---

### Requirement: Display Physical Format Information for DVD media

When a DVD (DVD±R, DVD±RW, DVD±R DL) disc is inserted, the system SHALL read and display Physical Format Information including: Book Type, Part Version, Disc Size, Number of Layers, Track Path (PTP/OTP), Linear Density, Track Density, First Physical Sector of Data Area, Last Physical Sector of Data Area, and Last Physical Sector in Layer 0 (for dual-layer). For DVD+R media, the system SHALL read both ADIP (L0 and L1 for DL) and Last Recorded fields. For DVD-R media, the system SHALL read Pre-recorded Information (Manufacturer ID) and Recording Management Area Information.

#### Scenario: Display Physical Format for DVD+R DL
- **GIVEN** a DVD+R DL disc is inserted
- **WHEN** the Disc Info view refreshes
- **THEN** the Physical Format Information section SHALL show Book Type, 2 layers, Track Path "Opposite Track Path (OTP)", and per-layer sector boundaries

#### Scenario: Display Physical Format for DVD-R
- **GIVEN** a DVD-R disc is inserted
- **WHEN** the Disc Info view refreshes
- **THEN** the system SHALL show Pre-recorded Manufacturer ID and Physical Format Information (Last Recorded) with Book Type

---

### Requirement: Display drive information

The system SHALL display drive hardware information in the Disc Info view: Vendor, Model, Firmware Revision, Serial Number, Buffer Size, Supported Write Speeds, and Supported Read Speeds. These fields already exist in the DiscDrive model but SHALL be consolidated in the new view.

#### Scenario: Display drive info
- **GIVEN** an optical drive is selected
- **WHEN** the Disc Info view refreshes
- **THEN** the Drive Information section SHALL show Vendor, Firmware, Serial, Buffer, Write Speeds, and Read Speeds

---

### Requirement: MID-to-Manufacturer name lookup

The system SHALL maintain a built-in lookup table mapping known Media ID codes (MID) to human-readable manufacturer names. When a disc's MID is read, the system SHALL display the manufacturer name (e.g., "MCC 004" → "Mitsubishi"). If the MID is not found in the lookup table, the system SHALL display the raw MID code.

#### Scenario: Known MID resolves to manufacturer name
- **GIVEN** a disc with MID "MCC 004" is inserted
- **WHEN** the Disc Info view refreshes
- **THEN** the Manufacturer field SHALL display "Mitsubishi"

#### Scenario: Unknown MID shows raw code
- **GIVEN** a disc with an unrecognized MID code
- **WHEN** the Disc Info view refreshes
- **THEN** the Manufacturer field SHALL display the raw MID code
