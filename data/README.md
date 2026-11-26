# Your data files

Put your own Excel (`.xlsx`, `.xlsb`, `.xls`) and CSV files here. Everything in this folder except this README is
ignored by git, so customer data is never committed.

Name the files so their type is clear, then run the VS Code task **data: upload files in ./data**
(or `.\scripts\upload-folder.ps1`) while the API is running:

| Put in the file name | Loaded as | Required columns |
| --- | --- | --- |
| `cis` or `master` (e.g. `cis_master_2026-11.xlsx`) | CIS customer master: one row per meter | `MeterId` (+ optional `BuildingId`, `Latitude`, `Longitude`, `Address`, `NSP`, `Manufacturer`, `Zone`, `InstallationDate`, `DtId`) |
| `mdm`, `comm` or `log` (e.g. `mdm_comm_2026-11-20.csv`) | MDM communication log: one row per meter per day | `MeterId`, `LogDate`, `IsCommunicating` |

CIS files are always loaded before MDM files. Rows that fail validation are written next to the file as
`<name>.errors.csv`. You can also drag the files onto the **Data ingestion** page in the web app instead.
