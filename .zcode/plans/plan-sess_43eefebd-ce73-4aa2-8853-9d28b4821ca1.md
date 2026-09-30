# Vault — full rework of the password page and its database layer

## 1. New database schema (no compatibility, old data dropped)

`Models/PasswordModels.cs` → git-mv to `Models/VaultModels.cs`, contents replaced:

- **`VaultSite`** — one LiteDB document per site, collection **`"vault"`**:
  - `Id` (ObjectId), `Site` (name), `Accounts` (embedded list), `CreatedAt`, `UpdatedAt`
- **`VaultAccount`** — embedded document per account:
  - `Id` (ObjectId), `Username`, `Email`, `Password` (multi-line), `OtpSecret` (canonical base32), `Secret` (extra hidden field), `Note` (free text), `CreatedAt`, `UpdatedAt`
- `VaultListItem : ObservableObject` — per-account UI projection (no secrets beyond the live TOTP code), carries `SiteId`/`AccountId`, `Email`, `HasPassword/HasTotp/HasSecret/HasNote`, display and relative-time helpers
- `VaultSiteGroup` — grouped source for the list: site id/name + account items + count

`DatabaseService.cs`: delete the password methods (lines 423-465) and add vault equivalents on collection `"vault"` with `EnsureIndex` on `Site` — `GetVaultSites`, `GetVaultSite`, `UpsertVaultSite`, `UpsertVaultSites` (bulk import), `DeleteVaultSite` — same `lock (Gate)` pattern as notes/bookmarks. The legacy `"passwords"` collection is **dropped** when encountered: existing saved passwords intentionally do not carry over.

## 2. Service layer rework

`Services/PasswordService.cs` → `VaultService.cs`, rewritten around the site→accounts aggregate:
- `ListAsync(filter)`: flatten, filter across site/username/email, group case-insensitively by site (alphabetical groups, accounts by username then recency), return grouped items
- `GetAccountAsync(siteId, accountId)`, `SaveAccountAsync`: saving routes to the matching site document (case-insensitive), creating it when new; account updates are load-modify-save on the aggregate with `UpdatedAt` stamps on both levels
- `DeleteAccountAsync`: removes the account; deleting the last account of a site removes the site document
- `GetTotpSecretsAsync`: flattened accountId → secret map for the live codes
- `ImportCsvAsync`: browser CSV rows grouped into site documents; dedupe fingerprint (site+username+password) still prevents re-import duplicates; returns (imported, skipped)
- `ExportCsvAsync`: flatten back to Chrome-compatible CSV (`ToBrowserCsv`)

`Services/PasswordCsv.cs` → `VaultCsv.cs` (parse/export logic unchanged, type renames). `PasswordGeneratorService` and `TotpService` keep their names — they still generate passwords and codes.

## 3. Page rename + shell

- `Views/PasswordPage.xaml(.cs)` → `VaultPage.xaml(.cs)`; `PasswordPageViewModel.cs` → `VaultPageViewModel.cs`
- `MainViewModel.cs`: sidebar item "Vault" (keep the lock glyph), `Tag = nameof(VaultPage)`
- `MainWindow.xaml.cs`: type map entry, search placeholder "Search vault", and cache the page instance like `_chatPage` (AGENTS.md page-caching rule) so vault state survives navigation
- String sweep: "New account", "Delete account?", unencrypted-warning text, tooltips, empty state

## 4. UI/UX redesign

**List** (grouped by site — multiple accounts per site are the native view):
- Grouped `ListView` via `CollectionViewSource`; group header = letter avatar, site name, "N accounts", and a "+" button that opens a new-entry editor with that site pre-filled
- Account card: username + email, live TOTP chip, small badges for OTP/Secret/Note, copy-username / copy-password quick actions
- Toolbar: accent "New account" button + import/export/generator; subtitle "N accounts across M sites"
- Real empty state: icon, "Your vault is empty", add-account CTA + import hint

**Editor** (SettingsPage-style section cards):
- Header: back, "VAULT ENTRY", big inline site-name box, accent **Save** button (replacing the unlabeled save icon), delete with existing confirm dialog, saving indicator, created/updated line (computed today but never displayed)
- Sections: **Account** (site, username, email) · **Password** (kept multi-line masked input, copy/reveal/generate, new strength meter) · **OTP** (secret + Scan QR + live code, existing) · **Secret** (hidden, reveal + copy) · **Note** (multi-line)
- Back with unsaved changes asks "Discard changes?" (dirty check)

## 5. ViewModel

- Grouped `Entries`, site/account counts, `EditEmail`/`EditSecret`/`EditNote`, password-strength observables, dirty tracking, per-account TOTP refresh
- Validation: site required; at least one of username/email/password/otp/secret/note required; OTP secret must parse (existing rule)

## 6. Sweep + build

- Grep for `PasswordPage|PasswordService|PasswordDocument|PasswordCsv|"passwords"` leftovers and fix all references
- `dotnet build` the solution and fix any errors

**Note:** existing saved passwords in the `"passwords"` collection are deleted as part of this rework — say the word if you'd rather keep a one-time migration into the new schema.
