# PCCAST Library System

A web-based school library management application for tracking book inventory, student borrowing records, returns, overdue fines, and dashboard statistics.

Built with **C#**, **ASP.NET Core Razor Pages**, **Entity Framework Core**, and **SQLite**.

---

## Features

- **Inventory Tracking**: Physical copy cataloging with individual accession numbers, shelf locations, and categories. Real-time availability derived from active loan records.
- **Student Records**: Student management with grade/year levels, sections, and active/inactive status. Deactivation blocks new checkouts while preserving complete historical records.
- **Borrowing & Return Workflows**: Step-by-step loan issuance with 7-day default due dates, administrator overrides, and return check-in.
- **Automated Fine Calculations**: Computed in Philippine Standard Time (`Asia/Manila`, UTC+8). Snapshots daily fine rates per transaction, counts overdue calendar days, and separates estimated unreturned fines from finalized fines.
- **Manual Payment Recording**: Records full or partial payments against finalized fines on returned books with duplicate submission and overpayment guards.
- **Interactive Dashboard**: Summary metrics, monthly borrowing trends, inventory availability breakdown, top-borrowed titles, and overdue loan alerts.
- **Security & Authorization**: Single administrator account secured with ASP.NET Core cookie authentication, salted password hashing, rate-limited login, anti-forgery protection, and strict Content Security Policy.
- **Theme Support**: Built-in Light, Dark, and System color schemes with zero external CSS/JS dependencies.

---

## Architecture and Scope

- **Three application tables**:
  - `Books`: Individual physical copies with unique accession numbers (`NOCASE`), shelf locations, and catalog metadata.
  - `Students`: Student records with unique student numbers (`NOCASE`), year levels, sections, and active/inactive status.
  - `Borrowers`: Borrowing transaction records linking a student and a physical book copy. Captures loan dates, due dates (Philippine calendar dates), snapshot daily fine rates, return timestamps, finalized fines, and cumulative payments.
- **Single Administrator Account**: Authenticated via ASP.NET Core Cookie Authentication. No `Users` table; administrator credentials are stored outside source control using salted PBKDF2 password hashes.
- **Philippine Standard Time (PST, UTC+8)**: All due dates, overdue day counts, and fine assessments use the Philippine calendar date (`Asia/Manila`, UTC+8 with no DST).
- **Fine Engine**: A centralized `FineService` provides fine calculations across the dashboard, loan details, and return workflows.
- **Concurrency & Integrity**: The database enforces integrity at the schema level using a SQLite filtered unique index (`UX_Borrowers_ActiveLoanPerBook`), foreign keys, and check constraints (`CK_Borrowers_Money`, `CK_Borrowers_ReturnState`).
- **Offline & Self-Contained**: Dependency-free frontend (vanilla JavaScript and CSS) with light, dark, and system themes, responsive layouts, SVG charts, and SVG icons.

---

## Prerequisites

- **Windows 10 / 11** or **Windows Server 2019+** (also runs on Linux / macOS with .NET 10)
- **.NET 10.0 SDK** (or later supported .NET LTS release)
- PowerShell 5.1+ or PowerShell 7+

Verify your installation:

```powershell
dotnet --version
```

---

## Quick Start Guide

### 1. Clone the Repository

```powershell
git clone https://github.com/dellpogie01/PCCAST_Library_System.git
cd PCCAST_Library_System
```

### 2. Configure Administrator Credentials

Administrator credentials are kept out of source control. For local development, set your credentials using .NET User Secrets:

```powershell
cd src\PccastLibrary.Web

# Option A: Use the default development administrator credentials
dotnet user-secrets set "Admin:Username" "librarian"
dotnet user-secrets set "Admin:PasswordHash" "AQAAAAIAAYagAAAAEPcOIju7WyRKELXt2TXZYEl5YHuyYP30SmTHxmXKBzhrjZT2+ACcogp8XzaEWYiP+Q=="

# (This configures Username: librarian / Password: PccastAdmin2026!)
```

```powershell
# Option B: Use your own custom password
dotnet run -- hash-password "YourCustomPassword123"
dotnet user-secrets set "Admin:Username" "librarian"
dotnet user-secrets set "Admin:PasswordHash" "<PASTE_HASH_OUTPUT_HERE>"
```

### 3. Initialize Database & Seed Sample Demo Data (Optional)

Database migrations run automatically on startup. To seed sample books, students, and active/overdue loans for testing:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run -- seed-demo
```

The database file will be created in `src\PccastLibrary.Web\App_Data\pccast-library.db` (outside the public `wwwroot` directory).

### 4. Run the Web Application

```powershell
dotnet run
```

When started, open your web browser to:
- **URL**: `http://localhost:5185` (or `https://localhost:7284`)
- **Username**: `librarian`
- **Password**: `PccastAdmin2026!` *(or your custom password)*

*(To stop the server, press `Ctrl + C` in the PowerShell window).*

---

## Running Automated Tests

The test suite includes 55 unit and end-to-end integration tests covering fine calculation boundary conditions, Philippine midnight transitions, database constraints, concurrent borrowing races, idempotency of returns, partial payments, and cookie authentication security.

Run all tests from the repository root:

```powershell
dotnet test
```

---

## Administrator Credential Management & Recovery

- **Where credentials live**:
  - **Local Development**: In .NET User Secrets (`secrets.json` stored in the user profile directory outside the repository).
  - **Production Deployment**: In environment variables or host configuration (`Admin__Username` and `Admin__PasswordHash`).
- **Changing the password**:
  1. Generate a new hash: `dotnet run -- hash-password "NewPasswordHere"`
  2. Update the `Admin:PasswordHash` user secret or the `Admin__PasswordHash` environment variable.
  3. Restart the application.
- **Account recovery**: If credentials are lost, generate a new hash on the server and update the configuration. No database edits are necessary.

---

## Database Backups

Because the application uses SQLite with Write-Ahead Logging (WAL mode), file copies while the application is running could capture incomplete transactions. Use the built-in backup command:

```powershell
cd src\PccastLibrary.Web
dotnet run -- backup "C:\backups\library-backup.db"
```

This uses SQLite's online `VACUUM INTO` command to generate an atomic, consistent database snapshot without locking out active users.

---

## Deployment & Production Considerations

### 1. Persistent Storage Prerequisite
SQLite requires a persistent, writable local filesystem with file-locking support. **Do not deploy this application on ephemeral cloud containers** (such as stateless container tiers) unless a persistent disk volume is mounted for the database file. Many free hosting platforms offer only ephemeral storage where the database file would be wiped upon every restart.

To point the database to a persistent storage mount:
```powershell
$env:Database__Path = "D:\AppData\pccast-library.db"
```

### 2. Environment Variables in Production
In production, set the following environment variables:
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Admin__Username = "librarian"
$env:Admin__PasswordHash = "AQAAAAIAAYagAAAAE..."
$env:Security__RequireSecureCookies = "true"
$env:Security__HttpsRedirection = "true"
```

### 3. Reverse Proxy Configuration
If hosting behind Nginx, Apache, or IIS with HTTPS termination, enable forwarded headers:
```powershell
$env:Security__UseForwardedHeaders = "true"
```

---

## System Business Rules

### Fine Calculation Rules
1. **Default Rate**: ₱5.00 (`500` centavos) per overdue calendar day per physical copy.
2. **Snapshotting**: The daily rate is snapshotted into `Borrowers.DailyFineRateCentavos` at the moment of borrowing. Future configuration changes never alter existing loans.
3. **Due Date Grace**: No fine is charged on the due date itself.
4. **Calendar Days**: Overdue days begin on the next Philippine calendar day and count every day (including weekends and holidays).
5. **Accrual Cessation**: Fine accrual permanently stops on the calendar day the book is returned.
6. **Finalized vs. Estimated**: Unreturned loans display estimated fines (`≈ ₱XX.00 est.`). Once returned, the fine is finalized and recorded in `Borrowers.AssessedFineCentavos`.
7. **Payments**: Payments are recorded against finalized fines on returned loans. Partial payments are supported; overpayment and negative values are strictly rejected.

### Inventory & Student Rules
1. **Physical Copies**: Each `Books` row is one physical copy identified by an accession number. Multiple copies of the same title have different accession numbers.
2. **Archiving**: A book copy cannot be archived while it is currently on loan.
3. **Deactivation**: Inactive students cannot borrow new books, but may return any books they currently hold.
4. **History Preservation**: Books and students are never deleted from the database to ensure borrowing and fine audit trails remain intact.

---

## Third-Party Components & Licenses

- All NuGet dependencies (ASP.NET Core, Entity Framework Core SQLite, xUnit, Microsoft.AspNetCore.Mvc.Testing) are licensed under permissive open-source licenses (MIT / Apache 2.0).
- SVG icons are based on Feather Icons (MIT License).
- See `THIRD-PARTY-NOTICES.md` for complete notices and license text.
